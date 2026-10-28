using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ShouTech.App;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Views
{
    /// <summary>
    /// Category card window. Persists to the current company database
    /// (AppState.CurrentCompanyCode). Status bar shows user and database name.
    /// User-visible strings come from Resources/Lang via Loc (CatWin.* keys).
    /// Notifications use a Windows-style toast with error catalog codes.
    ///
    /// Alignment of fields, labels, buttons, and tree items follows the
    /// window language automatically through the central visual identity
    /// styles (DataTriggers bound to Window.FlowDirection). No content-based
    /// direction switching is performed in this file.
    /// </summary>
    public partial class CatWin : BaseWin
    {
        // Error catalog codes (CAT-xxx)
        private const string ErrSaveFailed      = "CAT-001";
        private const string ErrDeleteFailed    = "CAT-002";
        private const string ErrLoadFailed      = "CAT-003";
        private const string ErrSearchFailed    = "CAT-004";
        private const string ErrImageFailed     = "CAT-005";
        private const string ErrRepoUnavailable = "CAT-006";

        private readonly List<MatRepo.MatGroupRec> _items = new();
        private readonly List<MatRepo.WarehouseRec> _warehouses = new();
        private MatRepo.MatGroupRec? _current;
        private string? _pendingImagePath;
        private bool _isNew;
        private bool _isEditing;
        private bool _isLoading;
        private bool _isBusy;
        private bool _repoAvailable = true;
        private bool _suppressTreeSelect;

        /// <summary>Localized string under CatWin.* with English fallback.</summary>
        private static string L(string key, string fallback)
            => Loc.Get("CatWin." + key, fallback);

        private static string Lf(string key, string fallback, params object[] args)
        {
            var fmt = L(key, fallback);
            try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
            catch { return fmt; }
        }

        public CatWin()
        {
            try
            {
                InitializeComponent();
                VisuId.ApplyWindow(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    L("InitFailed", "Could not initialize the category card window.") + "\n\n" + ex.Message,
                    L("Title", "Category Card"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;

            UiLoczr.ApplyToWindow(this);
            RefreshStatusBar();

            try
            {
                LoadWarehouses();
                ReloadAll();
            }
            catch (Exception ex)
            {
                ShowToast(false, L("LoadFailed", "Could not load category cards."), ErrLoadFailed, ex.Message);
                PrepareNew();
            }
        }

        protected override void ApplyLocalizedText()
        {
            // Captions are localized by UiLoczr.ApplyToWindow using the
            // loc:LocProps.Key / ToolTipKey attributes on the XAML elements.
            // Nothing additional is required here.
        }

        protected override void OnLanguageChanged()
        {
            base.OnLanguageChanged();
            RefreshStatusBar();
        }

        // ================================================================
        //  Status bar
        // ================================================================
        /// <summary>
        /// Status bar always shows user and database name.
        /// Save/error messages use toast only - never overwrite this bar.
        /// </summary>
        private void RefreshStatusBar()
        {
            try
            {
                var user = AppState.CurrentUser?.Username;
                if (string.IsNullOrWhiteSpace(user))
                    user = Environment.UserName;

                var db = AppState.CurrentDatabaseName;
                if (string.IsNullOrWhiteSpace(db))
                {
                    try
                    {
                        var cs = DatabaseRuntime.GetConnectionString();
                        db = ExtractDbName(cs) ?? "-";
                    }
                    catch
                    {
                        db = "-";
                    }
                }

                SafeSet(TxtStatus, Lf("StatusUserDb", "User: {0}  |  Database: {1}", user, db));
            }
            catch
            {
                SafeSet(TxtStatus, Lf("StatusUserDb", "User: {0}  |  Database: {1}", "-", "-"));
            }
        }

        private static string? ExtractDbName(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return null;
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim();
                if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    var val = kv[1].Trim();
                    if (val.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                        val.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                        return Path.GetFileName(val);
                    return val;
                }
            }
            return null;
        }

        // ================================================================
        //  Data loading
        // ================================================================
        private void ReloadAll()
        {
            if (_isBusy) return;
            _isBusy = true;
            _isLoading = true;
            try
            {
                LoadCards();
                BuildTree();

                if (_items.Count == 0)
                {
                    PrepareNew();
                }
                else
                {
                    // Keep the tree in numeric-code order, but open the most
                    // recently updated card (falling back to creation time).
                    var latest = _items
                        .Select((item, index) => new { item, index, stamp = GetActivityTimestamp(item) })
                        .OrderByDescending(x => x.stamp.HasValue)
                        .ThenByDescending(x => x.stamp)
                        .ThenByDescending(x => x.item.CardNo)
                        .First();
                    ShowCard(latest.index);
                }
            }
            finally
            {
                _isLoading = false;
                _isBusy = false;
                RefreshStatusBar();
            }
        }

        private void LoadWarehouses()
        {
            _warehouses.Clear();
            try
            {
                var list = MatRepo.LoadWarehouses();
                if (list != null)
                    _warehouses.AddRange(list);
            }
            catch
            {
                // Loading failed - leave the list empty. The category card can
                // still be viewed and edited; the warehouse field simply stays
                // unselected. Never fabricate a synthetic warehouse record.
            }

            if (CmbWarehouse == null) return;

            CmbWarehouse.ItemsSource = null;
            CmbWarehouse.ItemsSource = _warehouses;

            if (_warehouses.Count == 0)
            {
                CmbWarehouse.SelectedItem = null;
                return;
            }

            var def =
                _warehouses.FirstOrDefault(w => w.IsDefault)
                ?? _warehouses[0];

            CmbWarehouse.SelectedItem = def;
        }

        private void LoadCards()
        {
            _items.Clear();
            _repoAvailable = true;
            try
            {
                var records = MatRepo.LoadGroups();
                if (records == null) return;

                foreach (var item in records
                             .Where(x => x != null && !x.IsDeleted)
                             .OrderBy(x => IsNumericCode(x.Code) ? 0 : 1)
                             .ThenBy(x => NumericCodeValue(x.Code))
                             .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x.CardNo))
                {
                    _items.Add(item);
                }
            }
            catch
            {
                _repoAvailable = false;
                _items.Clear();
            }
        }

        private void BuildTree(long? selectId = null)
        {
            if (TreeGroups == null) return;

            _suppressTreeSelect = true;
            try
            {
                TreeGroups.Items.Clear();

                var byCode = _items
                    .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                    .GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var roots = _items
                    .Where(x => string.IsNullOrWhiteSpace(x.ParentCode) ||
                                !byCode.ContainsKey(x.ParentCode.Trim()))
                    .OrderBy(x => IsNumericCode(x.Code) ? 0 : 1)
                    .ThenBy(x => NumericCodeValue(x.Code))
                    .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.CardNo)
                    .ToList();

                TreeViewItem? toSelect = null;

                foreach (var root in roots)
                {
                    var node = CreateTreeNode(root);
                    TreeGroups.Items.Add(node);
                    if (selectId.HasValue && root.Id == selectId.Value)
                        toSelect = node;
                    AttachChildren(node, root.Code, byCode, selectId, ref toSelect);
                }

                if (toSelect != null)
                {
                    toSelect.IsSelected = true;
                    toSelect.BringIntoView();
                }
            }
            finally
            {
                _suppressTreeSelect = false;
            }
        }

        private static TreeViewItem CreateTreeNode(MatRepo.MatGroupRec rec)
        {
            var label = string.IsNullOrWhiteSpace(rec.Code)
                ? (rec.Name ?? "")
                : $"{rec.Code} - {rec.Name}";

            return new TreeViewItem
            {
                Header = label,
                Tag = rec,
                IsExpanded = true
            };
        }

        private void AttachChildren(
            TreeViewItem parentNode,
            string? parentCode,
            Dictionary<string, MatRepo.MatGroupRec> byCode,
            long? selectId,
            ref TreeViewItem? toSelect)
        {
            if (string.IsNullOrWhiteSpace(parentCode)) return;

            var children = _items
                .Where(x => !string.IsNullOrWhiteSpace(x.ParentCode) &&
                            string.Equals(x.ParentCode.Trim(), parentCode.Trim(),
                                StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => IsNumericCode(x.Code) ? 0 : 1)
                .ThenBy(x => NumericCodeValue(x.Code))
                .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.CardNo);

            foreach (var child in children)
            {
                var node = CreateTreeNode(child);
                parentNode.Items.Add(node);
                if (selectId.HasValue && child.Id == selectId.Value)
                    toSelect = node;
                AttachChildren(node, child.Code, byCode, selectId, ref toSelect);
            }
        }

        private void TreeGroups_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressTreeSelect || _isLoading || _isBusy) return;
            if (e.NewValue is not TreeViewItem item) return;
            if (item.Tag is not MatRepo.MatGroupRec rec) return;

            var idx = _items.FindIndex(x => x.Id == rec.Id);
            if (idx >= 0)
                ShowCard(idx);
        }

        // ================================================================
        //  Card rendering
        // ================================================================
        private void ShowCard(int index)
        {
            if (_items.Count == 0)
            {
                PrepareNew();
                return;
            }

            index = Math.Clamp(index, 0, _items.Count - 1);
            _current = _items[index];
            _isNew = false;
            _isEditing = false;
            _pendingImagePath = _current.ImagePath;

            SafeSet(TxtCardNo, FormatNumber(_current.CardNo));
            SafeSet(TxtCode, _current.Code);
            SafeSet(TxtName, _current.Name);
            SafeSet(TxtNameEn, _current.NameEn);
            SafeSet(TxtSpecs, _current.GeneralSpecs);
            SelectWarehouse(_current.Warehouse);
            LoadImagePreview(_current.ImagePath);
            SafeSet(TxtMode, L("ModeView", "View"));

            SetFieldsEnabled(false);
            SafeEnable(BtnDelete, true);
            SafeEnable(BtnSave, false);

            SelectTreeNode(_current.Id);
            RefreshStatusBar();
        }

        private void PrepareNew()
        {
            _current = null;
            _isNew = true;
            _isEditing = true;
            _pendingImagePath = null;

            SafeSet(TxtCardNo, GetNextCardNoPreviewText());
            SafeSet(TxtCode, "");
            SafeSet(TxtName, "");
            SafeSet(TxtNameEn, "");
            SafeSet(TxtSpecs, "");
            SelectDefaultWarehouse();
            ClearImagePreview();
            SafeSet(TxtMode, L("ModeNew", "New"));

            SetFieldsEnabled(true);
            SafeEnable(BtnDelete, false);
            SafeEnable(BtnSave, _repoAvailable);

            ClearTreeSelection();
            RefreshStatusBar();
            try { TxtCode?.Focus(); } catch { }
        }

        private string GetNextCardNoPreviewText()
        {
            if (!_repoAvailable) return string.Empty;
            try
            {
                return MatRepo.GetNextGroupCardNoPreview()
                    .ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                ShowToast(false,
                    L("CardNumberPreviewFailed", "Could not determine the next card number."),
                    ErrLoadFailed,
                    ex.Message);
                return string.Empty;
            }
        }

        private static DateTime? GetActivityTimestamp(MatRepo.MatGroupRec item)
        {
            var raw = !string.IsNullOrWhiteSpace(item.UpdatedAt)
                ? item.UpdatedAt
                : item.CreatedAt;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var value)
                ? value
                : null;
        }

        private string GetNextItemCode()
        {
            var maxCode = _items
                .Select(x => x.Code)
                .Where(IsNumericCode)
                .Select(NumericCodeValue)
                .Where(value => value >= 0 && value < long.MaxValue)
                .DefaultIfEmpty(0L)
                .Max();

            return (maxCode + 1L).ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsNumericCode(string? code)
            => long.TryParse(code?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
               && value >= 0;

        private static long NumericCodeValue(string? code)
            => long.TryParse(code?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : long.MaxValue;

        // ================================================================
        //  Warehouse selection
        // ================================================================
        /// <summary>
        /// Attempts to match a stored warehouse reference (numeric code, card
        /// number, or name) against the loaded warehouses of the current company.
        /// </summary>
        private void SelectWarehouse(string? codeOrName)
        {
            if (CmbWarehouse == null || _warehouses.Count == 0)
                return;

            MatRepo.WarehouseRec? match = null;

            if (!string.IsNullOrWhiteSpace(codeOrName))
            {
                var needle = codeOrName.Trim();

                match = _warehouses.FirstOrDefault(w =>
                    string.Equals(
                        w.Code.ToString(CultureInfo.InvariantCulture),
                        needle,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        w.CardNo.ToString(CultureInfo.InvariantCulture),
                        needle,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(w.Name, needle, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(w.NameEn, needle, StringComparison.OrdinalIgnoreCase));
            }

            CmbWarehouse.SelectedItem =
                match
                ?? _warehouses.FirstOrDefault(w => w.IsDefault)
                ?? _warehouses[0];
        }

        private void SelectDefaultWarehouse()
        {
            if (CmbWarehouse == null || _warehouses.Count == 0)
                return;

            CmbWarehouse.SelectedItem =
                _warehouses.FirstOrDefault(w => w.IsDefault)
                ?? _warehouses[0];
        }

        /// <summary>
        /// Returns the value that will be stored in the category record's
        /// warehouse reference field. Prefers the human-readable Arabic name,
        /// falling back to the numeric system code as a string.
        /// </summary>
        private string? GetSelectedWarehouseValue()
        {
            if (CmbWarehouse?.SelectedItem is not MatRepo.WarehouseRec w)
                return null;

            if (!string.IsNullOrWhiteSpace(w.Name))
                return w.Name.Trim();

            if (!string.IsNullOrWhiteSpace(w.NameEn))
                return w.NameEn.Trim();

            return w.Code.ToString(CultureInfo.InvariantCulture);
        }

        // ================================================================
        //  Edit state
        // ================================================================
        private void SetFieldsEnabled(bool enabled)
        {
            try
            {
                if (TxtCardNo != null) TxtCardNo.IsEnabled = true;
                if (TxtCode != null) TxtCode.IsEnabled = enabled && _repoAvailable;
                if (TxtName != null) TxtName.IsEnabled = enabled && _repoAvailable;
                if (TxtNameEn != null) TxtNameEn.IsEnabled = enabled && _repoAvailable;
                if (CmbWarehouse != null) CmbWarehouse.IsEnabled = enabled && _repoAvailable;
                if (TxtSpecs != null) TxtSpecs.IsEnabled = enabled && _repoAvailable;
                if (BtnPickImage != null) BtnPickImage.IsEnabled = enabled && _repoAvailable;
                if (BtnClearImage != null) BtnClearImage.IsEnabled = enabled && _repoAvailable;
            }
            catch { }
        }

        private void SelectTreeNode(long id)
        {
            if (TreeGroups == null) return;
            _suppressTreeSelect = true;
            try
            {
                var node = FindTreeNode(TreeGroups.Items, id);
                if (node != null)
                {
                    node.IsSelected = true;
                    node.BringIntoView();
                }
            }
            finally
            {
                _suppressTreeSelect = false;
            }
        }

        private void ClearTreeSelection()
        {
            if (TreeGroups == null) return;
            _suppressTreeSelect = true;
            try
            {
                if (TreeGroups.SelectedItem is TreeViewItem selected)
                    selected.IsSelected = false;
            }
            catch { }
            finally
            {
                _suppressTreeSelect = false;
            }
        }

        private static TreeViewItem? FindTreeNode(ItemCollection items, long id)
        {
            foreach (var obj in items)
            {
                if (obj is not TreeViewItem node) continue;
                if (node.Tag is MatRepo.MatGroupRec rec && rec.Id == id)
                    return node;
                var found = FindTreeNode(node.Items, id);
                if (found != null) return found;
            }
            return null;
        }

        // ================================================================
        //  Image
        // ================================================================
        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = L("PickImageTitle", "Choose Item Image"),
                    Filter = L("ImageFilter", "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"),
                    CheckFileExists = true
                };
                if (dlg.ShowDialog(this) != true) return;

                _pendingImagePath = dlg.FileName;
                LoadImagePreview(_pendingImagePath);
            }
            catch (Exception ex)
            {
                ShowToast(false, L("ImageFailed", "Could not load the image."), ErrImageFailed, ex.Message);
            }
        }

        private void ClearImage_Click(object sender, RoutedEventArgs e)
        {
            _pendingImagePath = null;
            ClearImagePreview();
        }

        private void LoadImagePreview(string? path)
        {
            try
            {
                if (ImgPreview == null) return;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    ImgPreview.Source = null;
                    return;
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 160;
                bmp.EndInit();
                bmp.Freeze();
                ImgPreview.Source = bmp;
            }
            catch
            {
                if (ImgPreview != null) ImgPreview.Source = null;
            }
        }

        private void ClearImagePreview()
        {
            try { if (ImgPreview != null) ImgPreview.Source = null; } catch { }
        }

        // ================================================================
        //  Validation
        // ================================================================
        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(TxtCode?.Text))
            {
                ShowToast(false, L("CodeRequired", "Item code is required."), ErrSaveFailed);
                try { TxtCode?.Focus(); } catch { }
                return false;
            }

            if (string.IsNullOrWhiteSpace(TxtName?.Text))
            {
                ShowToast(false, L("NameRequired", "Item name is required."), ErrSaveFailed);
                try { TxtName?.Focus(); } catch { }
                return false;
            }

            var code = (TxtCode?.Text ?? string.Empty).Trim();
            if (IsCodeDuplicate(code, _isNew ? null : _current?.Id))
            {
                ShowToast(false, L("CodeDuplicate", "Item code already exists."), ErrSaveFailed);
                try { TxtCode?.Focus(); } catch { }
                return false;
            }

            return true;
        }

        private bool IsCodeDuplicate(string code, long? excludeId)
        {
            try
            {
                return _items.Any(x =>
                    string.Equals(x.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase) &&
                    (!excludeId.HasValue || x.Id != excludeId.Value));
            }
            catch
            {
                return false;
            }
        }

        // ================================================================
        //  Save / Delete
        // ================================================================
        private void SaveCurrent(bool prepareNewAfter)
        {
            if (_isBusy) return;
            if (!_repoAvailable)
            {
                ShowToast(false, L("RepoUnavailable", "Repository is currently unavailable."), ErrRepoUnavailable);
                return;
            }
            if (!ValidateInput()) return;

            _isBusy = true;
            try
            {
                if (_isNew || _current == null)
                {
                    var record = new MatRepo.MatGroupRec
                    {
                        Code = (TxtCode?.Text ?? string.Empty).Trim(),
                        Name = (TxtName?.Text ?? string.Empty).Trim(),
                        NameEn = (TxtNameEn?.Text ?? string.Empty).Trim(),
                        Warehouse = GetSelectedWarehouseValue(),
                        GeneralSpecs = (TxtSpecs?.Text ?? string.Empty).Trim(),
                        ImagePath = _pendingImagePath,
                        ParentCode = null,
                        Status = "Active",
                        IsDeleted = false
                    };

                    var saved = MatRepo.SaveGroup(record, true);
                    if (saved == null)
                        throw new InvalidOperationException(L("CreateFailed", "Could not create the item."));

                    LoadCards();
                    BuildTree(saved.Id);

                    if (prepareNewAfter)
                    {
                        PrepareNew();
                    }
                    else
                    {
                        var idx = _items.FindIndex(x => x.Id == saved.Id);
                        if (idx >= 0) ShowCard(idx);
                        else PrepareNew();
                    }

                    ShowToast(true, L("Saved", "Item saved successfully."));
                }
                else
                {
                    _current.Code = (TxtCode?.Text ?? string.Empty).Trim();
                    _current.Name = (TxtName?.Text ?? string.Empty).Trim();
                    _current.NameEn = (TxtNameEn?.Text ?? string.Empty).Trim();
                    _current.Warehouse = GetSelectedWarehouseValue();
                    _current.GeneralSpecs = (TxtSpecs?.Text ?? string.Empty).Trim();
                    _current.ImagePath = _pendingImagePath;

                    MatRepo.SaveGroup(_current, false);

                    var id = _current.Id;
                    LoadCards();
                    BuildTree(id);

                    var idx = _items.FindIndex(x => x.Id == id);
                    if (idx >= 0) ShowCard(idx);

                    ShowToast(true, L("Updated", "Item updated successfully."));
                }
            }
            catch (Exception ex)
            {
                ShowToast(false, L("NotSaved", "Item was not saved."), ErrSaveFailed, ex.Message);
            }
            finally
            {
                _isBusy = false;
                RefreshStatusBar();
            }
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            PrepareNew();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _isNew || _current == null || !_repoAvailable) return;
            _isEditing = true;
            SetFieldsEnabled(true);
            SafeEnable(BtnSave, true);
            SafeSet(TxtMode, L("ModeEdit", "Edit"));
            try { TxtCode?.Focus(); } catch { }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_isNew && !_isEditing) return;
            SaveCurrent(prepareNewAfter: false);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _isNew || _current == null) return;
            if (!_repoAvailable)
            {
                ShowToast(false, L("RepoUnavailable", "Repository is currently unavailable."), ErrRepoUnavailable);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                Lf("DeleteConfirm", "Delete item {0} - {1}?", _current.Code, _current.Name),
                L("DeleteConfirmTitle", "Confirm Delete"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes) return;

            _isBusy = true;
            try
            {
                MatRepo.SoftDeleteGroup(_current.Id);
                LoadCards();
                BuildTree();

                if (_items.Count == 0)
                    PrepareNew();
                else
                    ShowCard(0);

                ShowToast(true, L("Deleted", "Item deleted."));
            }
            catch (Exception ex)
            {
                ShowToast(false, L("NotDeleted", "Item was not deleted."), ErrDeleteFailed, ex.Message);
            }
            finally
            {
                _isBusy = false;
                RefreshStatusBar();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            try
            {
                LoadWarehouses();
                ReloadAll();
                ShowToast(true, L("Refreshed", "Refreshed."));
            }
            catch (Exception ex)
            {
                ShowToast(false, L("RefreshFailed", "Refresh failed."), ErrLoadFailed, ex.Message);
            }
        }

        // ================================================================
        //  Search
        // ================================================================
        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                RunSearch();
            }
        }

        private void RunSearch()
        {
            if (_isLoading || _isBusy) return;

            var term = (TxtSearch?.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(term))
            {
                ShowToast(false, L("SearchEmpty", "Enter search text (code or name)."), ErrSearchFailed);
                return;
            }

            try
            {
                var idx = _items.FindIndex(x =>
                    (x.Code != null && x.Code.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (x.Name != null && x.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (x.NameEn != null && x.NameEn.Contains(term, StringComparison.OrdinalIgnoreCase)));

                if (idx >= 0)
                {
                    ShowCard(idx);
                    ShowToast(true, Lf("SearchFound", "Found: {0}", _items[idx].Code));
                }
                else
                {
                    ShowToast(false, L("SearchNotFound", "No results found."), ErrSearchFailed);
                }
            }
            catch (Exception ex)
            {
                ShowToast(false, L("SearchFailed", "Search failed."), ErrSearchFailed, ex.Message);
            }
        }

        // ================================================================
        //  Toast notifications
        // ================================================================
        /// <summary>
        /// Floating toast. Success = green; failure = red with error catalog code.
        /// </summary>
        private void ShowToast(bool success, string message, string? errorCode = null, string? detail = null)
        {
            try
            {
                var toast = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Owner = this,
                    FlowDirection = this.FlowDirection,
                    FontFamily = ShouTech.App.Services.VisuId.ResolveFontFamily("ApplicationFontFamily", "Segoe UI"),
                    FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12)
                };

                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 10, 14, 10),
                    Background = success
                        ? ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessBrush", "#198754")
                        : ShouTech.App.Services.VisuId.ResolveBrushResource("ErrorBrush", "#C0392B"),
                    BorderBrush = success
                        ? ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessDarkBrush", "#146C43")
                        : ShouTech.App.Services.VisuId.ResolveBrushResource("ErrorDarkBrush", "#922B21"),
                    BorderThickness = new Thickness(1),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 12,
                        ShadowDepth = 2,
                        Opacity = 0.35,
                        Color = Colors.Black
                    }
                };

                var panel = new StackPanel { Orientation = Orientation.Vertical };

                panel.Children.Add(new TextBlock
                {
                    Text = (success ? "\u2713  " : "\u2715  ") + message,
                    Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360
                });

                if (!success && !string.IsNullOrWhiteSpace(errorCode))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = Lf("ErrorCode", "Error code: {0}", errorCode),
                        Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnErrorBrush", "#FFD0D0"),
                        FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11),
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = detail.Length > 180
                            ? string.Concat(detail.AsSpan(0, 180), "...")
                            : detail,
                        Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextMutedOnDarkBrush", "#F0F0F0"),
                        FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(10),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360,
                        Margin = new Thickness(0, 3, 0, 0),
                        Opacity = 0.9
                    });
                }

                border.Child = panel;
                toast.Content = border;

                toast.Loaded += (_, __) =>
                {
                    try
                    {
                        if (IsVisible)
                        {
                            toast.Left = Left + ActualWidth - toast.ActualWidth - 16;
                            toast.Top = Top + ActualHeight - toast.ActualHeight - 48;
                        }
                        else
                        {
                            toast.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        }
                    }
                    catch { }
                };

                toast.Show();

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(success ? 2.4 : 4.0) };
                timer.Tick += (_, __) =>
                {
                    timer.Stop();
                    try { toast.Close(); } catch { }
                };
                timer.Start();
            }
            catch
            {
                try
                {
                    var text = message;
                    if (!success && !string.IsNullOrWhiteSpace(errorCode))
                        text += "\n" + Lf("ErrorCode", "Error code: {0}", errorCode);
                    if (!string.IsNullOrWhiteSpace(detail))
                        text += "\n" + detail;
                    MessageBox.Show(this, text, L("Title", "Category Card"),
                        MessageBoxButton.OK,
                        success ? MessageBoxImage.Information : MessageBoxImage.Error);
                }
                catch { }
            }
        }

        // ================================================================
        //  Small helpers
        // ================================================================
        private static string FormatNumber(long value) =>
            value.ToString(CultureInfo.InvariantCulture);

        private static void SafeSet(TextBox? box, string? text)
        {
            if (box == null) return;
            try { box.Text = text ?? ""; } catch { }
        }

        private static void SafeSet(TextBlock? block, string? text)
        {
            if (block == null) return;
            try { block.Text = text ?? ""; } catch { }
        }

        private static void SafeEnable(UIElement? el, bool enabled)
        {
            if (el == null) return;
            try { el.IsEnabled = enabled; } catch { }
        }
    }
}