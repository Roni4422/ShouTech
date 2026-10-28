using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShouTech.App;
using ShouTech.App.Common;
using ShouTech.App.Services;

namespace ShouTech.App.Views
{
    /// <summary>
    /// Warehouse card (v2).
    /// Inherits <see cref="BaseWin"/> for language, RTL/LTR, theme, and geometry persistence.
    /// Data contract: <see cref="MatRepo.WarehouseRec"/> and the warehouses table.
    /// All user-visible strings come from language resources via T("WhsWin.*").
    /// </summary>
    public partial class WhsWin : BaseWin
    {
        private const string ErrSaveFailed      = "WHS-001";
        private const string ErrDeleteFailed    = "WHS-002";
        private const string ErrLoadFailed      = "WHS-003";
        private const string ErrRepoUnavailable = "WHS-004";
        private const string ErrValidation      = "WHS-005";

        private readonly List<MatRepo.WarehouseRec> _items = new();
        private MatRepo.WarehouseRec? _current;
        private int _index = -1;

        private bool _isNew;
        private bool _isLoading;
        private bool _isBusy;
        private bool _repoAvailable = true;
        private bool _suppressTree;
        private bool _advanced;
        private bool _editing;
        private bool _dirty;
        private bool _suppressDirty;

        // -----------------------------------------------------------------
        // BaseWin overrides
        // -----------------------------------------------------------------

        protected override string PrefKey => "WhsWin";

        protected override void ApplyLocalizedText()
        {
            Title = T("WhsWin.Title");

            SafeSet(LblAutoCode,  T("WhsWin.AutoCode"));
            SafeSet(LblCardNo,    T("WhsWin.CardNo"));
            SafeSet(LblName,      T("WhsWin.Name"));
            SafeSet(LblNameEn,    T("WhsWin.NameLatin"));
            SafeSet(LblParent,    T("WhsWin.Parent"));
            SafeSet(LblAdvanced,  T("WhsWin.Advanced"));
            SafeSet(LblTreeTitle, T("WhsWin.TreeTitle"));
            SafeSet(LblAddress,   T("WhsWin.Address"));
            SafeSet(LblCity,      T("WhsWin.City"));
            SafeSet(LblRegion,    T("WhsWin.Region"));
            SafeSet(LblCountry,   T("WhsWin.Country"));
            SafeSet(LblPhone,     T("WhsWin.Phone"));
            SafeSet(LblLocation,  T("WhsWin.Location"));
            SafeSet(LblNotes,     T("WhsWin.Notes"));

            if (ChkDefault != null) ChkDefault.Content = T("WhsWin.IsDefault");
            if (ChkActive  != null) ChkActive.Content  = T("WhsWin.IsActive");

            SafeSet(BtnNew,    T("WhsWin.New"));
            SafeSet(BtnEdit,   T("WhsWin.Edit"));
            SafeSet(BtnSave,   T("WhsWin.Save"));
            SafeSet(BtnDelete, T("WhsWin.Delete"));
            SafeSet(BtnClose,  T("WhsWin.Close"));

            if (CtxEdit != null)        CtxEdit.Header        = T("WhsWin.CtxEdit");
            if (CtxDelete != null)      CtxDelete.Header      = T("WhsWin.CtxDelete");
            if (CtxStockCount != null)  CtxStockCount.Header  = T("WhsWin.CtxStockCount");
            if (CtxStockAdjust != null) CtxStockAdjust.Header = T("WhsWin.CtxStockAdjust");

            if (BtnFirst != null) BtnFirst.ToolTip = T("Common.First");
            if (BtnPrev != null)  BtnPrev.ToolTip  = T("Common.Prev");
            if (BtnNext != null)  BtnNext.ToolTip  = T("Common.Next");
            if (BtnLast != null)  BtnLast.ToolTip  = T("Common.Last");
            if (TglAdvanced != null) TglAdvanced.ToolTip = T("WhsWin.AdvancedHint");
            if (TxtAutoCode != null) TxtAutoCode.ToolTip = T("WhsWin.AutoCodeHint");

            RefreshModeBadge();
            RefreshStatusBar();
            RefreshPositionLabel();

            // Rebuild parent combo so the "(No Parent)" caption follows the language.
            if (CmbParent != null && !_isLoading)
            {
                var selectedCode = (CmbParent.SelectedItem as MatRepo.WarehouseRec)?.Code;
                FillParentCombo(_current?.Code);
                if (selectedCode.HasValue && selectedCode.Value > 0)
                {
                    CmbParent.SelectedItem = _items
                        .FirstOrDefault(x => x.Code == selectedCode.Value);
                }
                else if (CmbParent.Items.Count > 0)
                {
                    CmbParent.SelectedIndex = 0;
                }


            }
        }

        protected override void OnLanguageChanged()
        {
            if (!_isLoading && _items.Count > 0)
                BuildTree(_current?.Id);
        }

        // -----------------------------------------------------------------
        // Constructor
        // -----------------------------------------------------------------

        public WhsWin()
        {
            try
            {
                InitializeComponent();
                VisuId.ApplyWindow(this);

                Loaded += (_, _) =>
                {
                    try
                    {
                        ReloadAll();
                        ApplyLocalizedText();
                    }
                    catch (Exception ex)
                    {
                        ShowToast(false, T("WhsWin.LoadFailed"), ErrLoadFailed, ex.Message);
                    }
                };

                Closing += OnClosingDirtyCheck;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    T("WhsWin.LoadFailed") + "\n\n" + ex.Message,
                    T("Common.Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // -----------------------------------------------------------------
        // Dirty tracking
        // -----------------------------------------------------------------

        private void MarkDirty()
        {
            if (_suppressDirty || _isLoading || (!_editing && !_isNew)) return;
            if (_dirty) return;
            _dirty = true;
            UpdateTitleDirty();
            SafeEnable(BtnSave, _repoAvailable);
        }

        private void ClearDirty()
        {
            _dirty = false;
            UpdateTitleDirty();
        }

        private void UpdateTitleDirty()
        {
            var baseTitle = T("WhsWin.Title");
            Title = _dirty ? baseTitle + " *" : baseTitle;
        }

        private void Field_Changed(object sender, RoutedEventArgs e) => MarkDirty();
        private void Field_Changed(object sender, TextChangedEventArgs e) => MarkDirty();
        private void Field_Changed(object sender, SelectionChangedEventArgs e) => MarkDirty();

        private void OnClosingDirtyCheck(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_dirty) return;

            var result = MessageBox.Show(
                this,
                T("WhsWin.UnsavedChanges"),
                T("Common.Confirm"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                if (!ValidateInput()) { e.Cancel = true; return; }
                try
                {
                    if (_isNew) SaveNewWarehouse();
                    else SaveExistingWarehouse();
                    ClearDirty();
                }
                catch
                {
                    e.Cancel = true;
                }
            }
        }

        // -----------------------------------------------------------------
        // Keyboard shortcuts
        // -----------------------------------------------------------------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (BtnSave?.IsEnabled == true) Save_Click(BtnSave, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F2)
            {
                if (BtnEdit?.IsEnabled == true) Edit_Click(BtnEdit, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_editing || _isNew)
                {
                    if (_dirty && !AskConfirm("WhsWin.DiscardChanges"))
                        return;

                    ClearDirty();
                    if (_items.Count > 0) ShowAt(_index >= 0 ? _index : 0);
                    else PrepareNew();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
            }
        }

        // -----------------------------------------------------------------
        // Load / Reload
        // -----------------------------------------------------------------

        private void ReloadAll()
        {
            _isLoading = true;
            _suppressDirty = true;
            try
            {
                LoadItems();
                BuildTree();
                FillParentCombo();

                if (_items.Count == 0)
                    PrepareNew();
                else
                    ShowAt(_items.Count - 1);  // last created (highest Id)
            }
            finally
            {
                _isLoading = false;
                _suppressDirty = false;
                ClearDirty();
                RefreshStatusBar();
            }
        }

        private void LoadItems()
        {
            _items.Clear();
            try
            {
                var list = MatRepo.LoadWarehouses();
                if (list != null)
                {
                    _items.AddRange(list
                        .Where(x => x != null && !x.IsDeleted)
                        .OrderBy(x => x.Id));
                }
                _repoAvailable = true;
            }
            catch (Exception ex)
            {
                _repoAvailable = false;
                ShowToast(false, T("WhsWin.LoadFailed"), ErrLoadFailed, ex.Message);
            }
        }

        // -----------------------------------------------------------------
        // Parent combo + Tree
        // -----------------------------------------------------------------

        private void FillParentCombo(long? excludeCode = null)
        {
            if (CmbParent == null) return;

            var noParent = new MatRepo.WarehouseRec
            {
                Id = 0,
                Code = 0,
                Name = T("WhsWin.NoParent"),
                IsActive = true,
                IsDeleted = false
            };

            var options = new List<MatRepo.WarehouseRec> { noParent };
            options.AddRange(_items.Where(x =>
                x.IsActive &&
                (!excludeCode.HasValue || x.Code != excludeCode.Value)));

            CmbParent.ItemsSource = null;
            CmbParent.ItemsSource = options;
            CmbParent.SelectedIndex = 0;
        }

        private long? GetParentCode()
        {
            if (CmbParent?.SelectedItem is not MatRepo.WarehouseRec parent)
                return null;
            return parent.Code > 0 ? parent.Code : null;
        }

        private void BuildTree(long? selectId = null)
        {
            if (TreeWhs == null) return;

            _suppressTree = true;
            try
            {
                TreeWhs.Items.Clear();

                var roots = _items
                    .Where(x => !x.ParentCode.HasValue || x.ParentCode.Value == 0
                                || !_items.Any(p => p.Code == x.ParentCode.Value))
                    .OrderBy(x => x.Code);

                foreach (var root in roots)
                {
                    var node = CreateNode(root);
                    AttachChildren(node, root.Code);
                    TreeWhs.Items.Add(node);
                }

                if (selectId.HasValue)
                    SelectTreeNode(selectId.Value);
            }
            finally
            {
                _suppressTree = false;
            }
        }

        private static TreeViewItem CreateNode(MatRepo.WarehouseRec rec)
        {
            var header = rec.Display;
            if (!rec.IsActive)
                header += " [off]";

            return new TreeViewItem
            {
                Header = header,
                Tag = rec,
                IsExpanded = true,
                FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12.5)
            };
        }

        private void AttachChildren(TreeViewItem parent, long parentCode)
        {
            foreach (var child in _items
                .Where(x => x.ParentCode == parentCode)
                .OrderBy(x => x.Code))
            {
                var node = CreateNode(child);
                AttachChildren(node, child.Code);
                parent.Items.Add(node);
            }
        }

        private void TreeWhs_SelectedItemChanged(
            object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressTree || _isLoading || _isBusy) return;
            if (e.NewValue is not TreeViewItem item) return;
            if (item.Tag is not MatRepo.WarehouseRec rec) return;

            if (_dirty)
            {
                if (!AskConfirm("WhsWin.DiscardChanges"))
                {
                    if (_current != null) SelectTreeNode(_current.Id);
                    return;
                }
                ClearDirty();
            }

            var idx = _items.FindIndex(x => x.Id == rec.Id);
            if (idx >= 0) ShowAt(idx);
        }

        private void SelectTreeNode(long id)
        {
            _suppressTree = true;
            try
            {
                var node = FindNode(TreeWhs.Items, id);
                if (node != null)
                {
                    node.IsSelected = true;
                    node.BringIntoView();
                }
            }
            finally
            {
                _suppressTree = false;
            }
        }

        private static TreeViewItem? FindNode(ItemCollection items, long id)
        {
            foreach (TreeViewItem node in items)
            {
                if (node.Tag is MatRepo.WarehouseRec rec && rec.Id == id)
                    return node;
                var found = FindNode(node.Items, id);
                if (found != null) return found;
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Show / Prepare
        // -----------------------------------------------------------------

        private void ShowAt(int index)
        {
            if (index < 0 || index >= _items.Count) return;

            _suppressDirty = true;
            try
            {
                _index = index;
                _current = _items[index];
                _isNew = false;
                _editing = false;

                BindRecord(_current);
                SetEditable(false);
                SelectTreeNode(_current.Id);
                RefreshModeBadge();
                RefreshPositionLabel();
                ClearFieldError();
                ClearDirty();
            }
            finally
            {
                _suppressDirty = false;
            }
        }

        private void BindRecord(MatRepo.WarehouseRec rec)
        {
            // Always Western digits (0-9), never Arabic-Indic numerals.
            SafeSet(TxtAutoCode, rec.Code > 0 ? FormatWestern(rec.Code) : "");
            SafeSet(TxtCardNo, rec.CardNo > 0 ? FormatWestern(rec.CardNo) : "");
            SafeSet(TxtName,    rec.Name ?? "");
            SafeSet(TxtNameEn,  rec.NameEn ?? "");
            SafeSet(TxtAddress, rec.Address ?? "");
            SafeSet(TxtCity,    rec.City ?? "");
            SafeSet(TxtRegion,  rec.Region ?? "");
            SafeSet(TxtCountry, rec.Country ?? "");
            SafeSet(TxtPhone,   rec.Phone ?? "");
            SafeSet(TxtAisle,   rec.Aisle ?? "");
            SafeSet(TxtRack,    rec.Rack ?? "");
            SafeSet(TxtShelf,   rec.Shelf ?? "");
            SafeSet(TxtNotes,   rec.Notes ?? "");

            if (ChkDefault != null) ChkDefault.IsChecked = rec.IsDefault;
            if (ChkActive  != null) ChkActive.IsChecked  = rec.IsActive;

            FillParentCombo(rec.Code);
            if (CmbParent != null)
            {
                if (rec.ParentCode.HasValue && rec.ParentCode.Value > 0)
                {
                    var match = _items.FirstOrDefault(x => x.Code == rec.ParentCode.Value);
                    CmbParent.SelectedItem = match
                        ?? CmbParent.Items.Cast<MatRepo.WarehouseRec>().FirstOrDefault();
                }
                else
                {
                    CmbParent.SelectedIndex = 0;
                }
            }

            SafeEnable(BtnDelete, true);
            SafeEnable(BtnEdit, true);
        }

        private void PrepareNew()
        {
            _suppressDirty = true;
            try
            {
                _isNew = true;
                _editing = true;
                _current = null;
                _index = -1;

                SafeSet(TxtAutoCode, "");
                SafeSet(TxtCardNo, SuggestNextCardNo());
                SafeSet(TxtName, "");
                SafeSet(TxtNameEn, "");
                SafeSet(TxtAddress, "");
                SafeSet(TxtCity, "");
                SafeSet(TxtRegion, "");
                SafeSet(TxtCountry, "");
                SafeSet(TxtPhone, "");
                SafeSet(TxtAisle, "");
                SafeSet(TxtRack, "");
                SafeSet(TxtShelf, "");
                SafeSet(TxtNotes, "");

                if (ChkDefault != null) ChkDefault.IsChecked = false;
                if (ChkActive  != null) ChkActive.IsChecked  = true;

                FillParentCombo();
                if (CmbParent != null) CmbParent.SelectedIndex = 0;

                SetEditable(true);
                SafeEnable(BtnDelete, false);
                SafeEnable(BtnEdit, false);
                RefreshModeBadge();
                RefreshPositionLabel();
                ClearFieldError();
                ClearDirty();

                try { TxtCardNo?.Focus(); TxtCardNo?.SelectAll(); } catch { }
            }
            finally
            {
                _suppressDirty = false;
            }
        }

        private string SuggestNextCardNo()
        {
            long max = 0;
            foreach (var x in _items)
                if (x.CardNo > max) max = x.CardNo;
            return (max + 1).ToString(CultureInfo.InvariantCulture);
        }

        // -----------------------------------------------------------------
        // Edit mode
        // -----------------------------------------------------------------

        private void SetEditable(bool editable)
        {
            _editing = editable;

            SafeReadOnly(TxtCardNo,  !editable);
            SafeReadOnly(TxtName,    !editable);
            SafeReadOnly(TxtNameEn,  !editable);
            SafeReadOnly(TxtAddress, !editable);
            SafeReadOnly(TxtCity,    !editable);
            SafeReadOnly(TxtRegion,  !editable);
            SafeReadOnly(TxtCountry, !editable);
            SafeReadOnly(TxtPhone,   !editable);
            SafeReadOnly(TxtAisle,   !editable);
            SafeReadOnly(TxtRack,    !editable);
            SafeReadOnly(TxtShelf,   !editable);
            SafeReadOnly(TxtNotes,   !editable);

            if (CmbParent  != null) CmbParent.IsEnabled  = editable;
            if (ChkDefault != null) ChkDefault.IsEnabled = editable;
            if (ChkActive  != null) ChkActive.IsEnabled  = editable;

            SafeEnable(BtnSave, editable && _repoAvailable);
            SafeEnable(BtnEdit, !editable && _current != null);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _isBusy) return;
            SetEditable(true);
            RefreshModeBadge();
            try { TxtName?.Focus(); } catch { }
        }

        private void Advanced_Toggled(object sender, RoutedEventArgs e)
        {
            _advanced = TglAdvanced?.IsChecked == true;
            if (PnlAdvanced != null)
                PnlAdvanced.Visibility = _advanced ? Visibility.Visible : Visibility.Collapsed;
        }

        // -----------------------------------------------------------------
        // Validation
        // -----------------------------------------------------------------

        private bool ValidateInput()
        {
            ClearFieldError();

            var cardText = (TxtCardNo?.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cardText)
                || !long.TryParse(cardText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var cardNo)
                || cardNo <= 0)
            {
                ShowFieldError(TxtCardNo, T("WhsWin.CardNoInvalid"));
                return false;
            }

            var currentId = _isNew ? (long?)null : _current?.Id;
            if (_items.Any(x =>
                    x.CardNo == cardNo &&
                    (!currentId.HasValue || x.Id != currentId.Value)))
            {
                ShowFieldError(TxtCardNo, T("WhsWin.CardNoDuplicate"));
                return false;
            }

            if (string.IsNullOrWhiteSpace(TxtName?.Text))
            {
                ShowFieldError(TxtName, T("WhsWin.NameRequired"));
                return false;
            }

            if (_current != null
                && CmbParent?.SelectedItem is MatRepo.WarehouseRec parent
                && parent.Code > 0
                && parent.Code == _current.Code)
            {
                ShowFieldError(null, T("WhsWin.InvalidParent"));
                return false;
            }

            return true;
        }

        private void ShowFieldError(TextBox? box, string message)
        {
            if (TxtFieldError != null)
            {
                TxtFieldError.Text = message;
                TxtFieldError.Visibility = Visibility.Visible;
            }

            if (box != null)
            {
                box.BorderBrush = TryFindResource("ErrorBrush") as Brush
                                  ?? Brushes.IndianRed;
                try { box.Focus(); box.SelectAll(); } catch { }

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try { box.ClearValue(Border.BorderBrushProperty); } catch { }
                };
                timer.Start();
            }

            ShowToast(false, message, ErrValidation);
        }

        private void ClearFieldError()
        {
            if (TxtFieldError != null)
            {
                TxtFieldError.Text = "";
                TxtFieldError.Visibility = Visibility.Collapsed;
            }
        }

        // -----------------------------------------------------------------
        // Save / Delete / New
        // -----------------------------------------------------------------

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (!_repoAvailable)
            {
                ShowToast(false, T("WhsWin.RepoUnavailable"), ErrRepoUnavailable);
                return;
            }

            if (!ValidateInput()) return;

            _isBusy = true;
            try
            {
                if (_isNew)
                    SaveNewWarehouse();
                else
                    SaveExistingWarehouse();

                ClearDirty();
                ShowToast(true, _isNew ? T("WhsWin.Saved") : T("WhsWin.Updated"));
            }
            catch (Exception ex)
            {
                ShowToast(false, T("WhsWin.NotSaved"), ErrSaveFailed, ex.Message);
            }
            finally
            {
                _isBusy = false;
                RefreshStatusBar();
            }
        }

        private void SaveNewWarehouse()
        {
            var rec = BuildRecFromForm();
            var saved = MatRepo.SaveWarehouse(rec, isNew: true);

            _items.Add(saved);
            _items.Sort((a, b) => a.Code.CompareTo(b.Code));

            FillParentCombo();
            BuildTree(saved.Id);

            var idx = _items.FindIndex(x => x.Id == saved.Id);
            ShowAt(idx >= 0 ? idx : _items.Count - 1);
        }

        private void SaveExistingWarehouse()
        {
            if (_current == null) return;

            var rec = BuildRecFromForm();
            rec.Id = _current.Id;
            rec.Code = _current.Code;

            var saved = MatRepo.SaveWarehouse(rec, isNew: false);

            var idx = _items.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) _items[idx] = saved;

            FillParentCombo(saved.Code);
            BuildTree(saved.Id);
            ShowAt(idx >= 0 ? idx : _index);
        }

        private MatRepo.WarehouseRec BuildRecFromForm()
        {
            long.TryParse((TxtCardNo?.Text ?? "").Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var cardNo);

            return new MatRepo.WarehouseRec
            {
                CardNo     = cardNo,
                Name     = (TxtName?.Text ?? "").Trim(),
                NameEn     = (TxtNameEn?.Text ?? "").Trim(),
                ParentCode = GetParentCode(),
                IsDefault  = ChkDefault?.IsChecked == true,
                IsActive   = ChkActive?.IsChecked != false,
                Address    = NullIfEmpty(TxtAddress?.Text),
                City       = NullIfEmpty(TxtCity?.Text),
                Region     = NullIfEmpty(TxtRegion?.Text),
                Country    = NullIfEmpty(TxtCountry?.Text),
                Phone      = NullIfEmpty(TxtPhone?.Text),
                Aisle      = NullIfEmpty(TxtAisle?.Text),
                Rack       = NullIfEmpty(TxtRack?.Text),
                Shelf      = NullIfEmpty(TxtShelf?.Text),
                Notes      = NullIfEmpty(TxtNotes?.Text)
            };
        }

        private static string? NullIfEmpty(string? s)
        {
            var t = s?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_dirty && !AskConfirm("WhsWin.DiscardChanges"))
                return;

            ClearDirty();
            PrepareNew();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _current == null || _isNew) return;

            var msg = FormatLoc(
                T("WhsWin.DeleteConfirm"),
                _current.Code,
                _current.Name ?? "");

            var result = MessageBox.Show(
                this, msg, T("WhsWin.DeleteConfirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            _isBusy = true;
            try
            {
                MatRepo.SoftDeleteWarehouse(_current.Id);
                _items.RemoveAll(x => x.Id == _current.Id);

                FillParentCombo();
                BuildTree();

                if (_items.Count == 0) PrepareNew();
                else ShowAt(_items.Count - 1);

                ClearDirty();
                ShowToast(true, T("WhsWin.Deleted"));
            }
            catch (Exception ex)
            {
                ShowToast(false, T("WhsWin.NotDeleted"), ErrDeleteFailed, ex.Message);
            }
            finally
            {
                _isBusy = false;
                RefreshStatusBar();
            }
        }

        // -----------------------------------------------------------------
        // Navigation
        // -----------------------------------------------------------------


        private void First_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _items.Count == 0) return;
            if (_dirty && !AskConfirm("WhsWin.DiscardChanges")) return;
            ClearDirty();
            ShowAt(0);
        }

        private void Last_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _items.Count == 0) return;
            if (_dirty && !AskConfirm("WhsWin.DiscardChanges")) return;
            ClearDirty();
            ShowAt(_items.Count - 1);
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _items.Count == 0) return;
            if (_dirty && !AskConfirm("WhsWin.DiscardChanges")) return;
            ClearDirty();

            if (_isNew) { ShowAt(_items.Count - 1); return; }
            ShowAt(_index <= 0 ? _items.Count - 1 : _index - 1);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _items.Count == 0) return;
            if (_dirty && !AskConfirm("WhsWin.DiscardChanges")) return;
            ClearDirty();

            if (_isNew) { ShowAt(0); return; }
            ShowAt(_index >= _items.Count - 1 ? 0 : _index + 1);
        }


        // -----------------------------------------------------------------
        // Tree context menu
        // -----------------------------------------------------------------

        private MatRepo.WarehouseRec? GetTreeContextWarehouse()
        {
            if (TreeWhs?.SelectedItem is TreeViewItem item
                && item.Tag is MatRepo.WarehouseRec rec)
                return rec;
            return _current;
        }

        private void TreeCtxMenu_Opened(object sender, RoutedEventArgs e)
        {
            var has = GetTreeContextWarehouse() != null && !_isNew;
            if (CtxEdit != null)        CtxEdit.IsEnabled = has;
            if (CtxDelete != null)      CtxDelete.IsEnabled = has && _repoAvailable;
            var canPostStock = has && GetTreeContextWarehouse()?.IsActive == true;
            if (CtxStockCount != null)  CtxStockCount.IsEnabled = canPostStock;
            if (CtxStockAdjust != null) CtxStockAdjust.IsEnabled = canPostStock;
        }

        private void CtxEdit_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetTreeContextWarehouse();
            if (rec == null) return;
            var idx = _items.FindIndex(x => x.Id == rec.Id);
            if (idx < 0) return;
            if (_dirty && !AskConfirm("WhsWin.DiscardChanges")) return;
            ClearDirty();
            ShowAt(idx);
            Edit_Click(sender, e);
        }

        private void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetTreeContextWarehouse();
            if (rec == null) return;
            var idx = _items.FindIndex(x => x.Id == rec.Id);
            if (idx >= 0)
            {
                ShowAt(idx);
                Delete_Click(sender, e);
            }
        }

        private void CtxStockCount_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetTreeContextWarehouse();
            if (rec?.IsActive != true) return;
            OpenStockCount(this, rec.Id);
        }

        private void CtxStockAdjust_Click(object sender, RoutedEventArgs e)
        {
            var rec = GetTreeContextWarehouse();
            if (rec?.IsActive != true) return;
            OpenStockCount(this, rec.Id, isAdjustment: true);
        }

        public static bool OpenStockCount(
            Window? owner,
            long? warehouseId = null,
            bool isAdjustment = false)
        {
            try
            {
                var warehouses = MatRepo.LoadWarehouses()
                    .Where(x => x.IsActive && !x.IsDeleted)
                    .OrderBy(x => x.Id)
                    .ToList();
                if (warehouses.Count == 0)
                    throw new InvalidOperationException("There are no active warehouses.");

                if (warehouseId is long fixedId && warehouses.All(x => x.Id != fixedId))
                    throw new InvalidOperationException("The selected warehouse is unavailable or inactive.");

                var picker = new ComboBox
                {
                    ItemsSource = warehouses,
                    DisplayMemberPath = nameof(MatRepo.WarehouseRec.Display),
                    SelectedValuePath = nameof(MatRepo.WarehouseRec.Id),
                    SelectedValue = warehouseId ?? warehouses[0].Id,
                    IsEnabled = warehouseId is null,
                    MinWidth = 260,
                    Height = 28,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                picker.SetResourceReference(StyleProperty, "VisuComboBoxStyle");
                var pickerShell = new Border
                {
                    MinWidth = 260,
                    Height = 28,
                    BorderBrush = System.Windows.Application.Current?.TryFindResource("BorderBrush") as Brush,
                    Background = System.Windows.Application.Current?.TryFindResource("FieldBackgroundBrush") as Brush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = picker,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                var reason = new TextBox { MinWidth = 360, Height = 28, MaxLength = 500 };
                reason.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false,
                    CanUserAddRows = false,
                    CanUserDeleteRows = false,
                    IsReadOnly = false,
                    SelectionMode = DataGridSelectionMode.Single,
                    SelectionUnit = DataGridSelectionUnit.FullRow,
                    Margin = new Thickness(0, 8, 0, 8),
                };
                grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
                grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
                grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
                grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
                var rows = new List<StockCountRow>();
                System.ComponentModel.ICollectionView? rowsView = null;
                long? selectedMaterialId = null;
                long? loadedWarehouseId = null;
                var materialFilter = new TextBox { MinWidth = 260, Height = 28 };
                materialFilter.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
                var summary = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
                bool TryDecimal(string text, out decimal value)
                    => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

                void RefreshRowsView()
                {
                    rowsView?.Refresh();
                    grid.Items.Refresh();
                    UpdateSummary();
                }

                void UpdateSummary()
                {
                    var changed = 0;
                    var invalid = 0;
                    decimal variance = 0;
                    foreach (var row in rows)
                    {
                        if (!TryDecimal(row.CountedQtyText, out var counted) ||
                            !TryDecimal(row.UnitCostText, out var cost) ||
                            counted < 0 || cost < 0)
                        {
                            invalid++;
                            continue;
                        }
                        if (counted != row.CurrentQty)
                            changed++;
                        variance += (counted - row.CurrentQty) * cost;
                    }

                    var visible = rowsView?.Cast<object>().Count() ?? rows.Count;
                    summary.Text = FormatLoc(
                        T("Inventory.StockCountSummary"),
                        FormatWestern(rows.Count),
                        FormatWestern(visible),
                        FormatWestern(changed),
                        AppFormat.FormatNumber(variance, "N2"),
                        FormatWestern(invalid));
                }

                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Reports.Col.MaterialCode"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.Code)),
                    IsReadOnly = true,
                    Width = 110
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Reports.Col.MaterialName"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.Name)),
                    IsReadOnly = true,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Inventory.StockCountUnit"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.Unit)),
                    IsReadOnly = true,
                    Width = 80
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Inventory.StockCountCurrent"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.CurrentQtyText)),
                    IsReadOnly = true,
                    Width = 115
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Inventory.StockCountQty"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.CountedQtyText))
                    {
                        UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                    },
                    EditingElementStyle = CreateGridEditorStyle(),
                    Width = 120
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Inventory.StockCountDifference"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.DifferenceText)),
                    IsReadOnly = true,
                    Width = 115
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Inventory.StockCountUnitCost"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.UnitCostText))
                    {
                        UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                    },
                    EditingElementStyle = CreateGridEditorStyle(),
                    Width = 120
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("Inventory.StockCountDifferenceValue"),
                    Binding = new System.Windows.Data.Binding(nameof(StockCountRow.DifferenceValueText)),
                    IsReadOnly = true,
                    Width = 140
                });

                void LoadRows(long selectedId)
                {
                    rows = MatRepo.LoadStockRows(selectedId)
                        .Select(x => new StockCountRow
                        {
                            MaterialId = x.MaterialId,
                            Code = x.Code,
                            Name = x.Name,
                            NameEn = x.NameEn,
                            Unit = x.Unit,
                            CurrentQty = x.Quantity,
                            CountedQtyText = AppFormat.FormatNumber(x.Quantity, "0.0000"),
                            UnitCostText = AppFormat.FormatNumber(x.AverageCost, "0.000000"),
                            StartingCost = x.AverageCost
                        })
                        .ToList();
                    rowsView = CollectionViewSource.GetDefaultView(rows);
                    rowsView.Filter = item =>
                    {
                        if (item is not StockCountRow row)
                            return false;
                        if (selectedMaterialId is long selectedId)
                            return row.MaterialId == selectedId;
                        var query = materialFilter.Text.Trim();
                        return query.Length == 0 ||
                               row.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               row.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase);
                    };
                    grid.ItemsSource = rowsView;
                    loadedWarehouseId = selectedId;
                    UpdateSummary();
                }

                LoadRows(Convert.ToInt64(picker.SelectedValue, CultureInfo.InvariantCulture));

                var dialog = new StockCountDialog(owner)
                {
                    Width = 980,
                    Height = 560,
                    MinWidth = 760,
                    MinHeight = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.CanResize
                };

                long? priorWarehouseId = loadedWarehouseId;
                var changingWarehouse = false;
                picker.SelectionChanged += (_, _) =>
                {
                    if (changingWarehouse || picker.SelectedValue is not long selectedId)
                        return;
                    if (rows.Any(x =>
                            !TryDecimal(x.CountedQtyText, out var counted) || counted != x.CurrentQty ||
                            !TryDecimal(x.UnitCostText, out var cost) || cost != x.StartingCost))
                    {
                        var answer = MessageBox.Show(
                            dialog,
                            T("Inventory.StockCountSwitchPrompt"),
                            T("Common.Warning"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes)
                        {
                            changingWarehouse = true;
                            picker.SelectedValue = priorWarehouseId;
                            changingWarehouse = false;
                            return;
                        }
                    }

                    try
                    {
                        LoadRows(selectedId);
                        priorWarehouseId = selectedId;
                    }
                    catch (Exception ex)
                    {
                        AppHost.Logging?.LogError(
                            "InventoryCount",
                            $"[INV-CNT-002] Loading stock count rows failed for warehouse {selectedId}.",
                            ex);
                        MessageBox.Show(dialog, $"{T("Inventory.StockCountLoadFailed")}\n[INV-CNT-002]", T("Common.Error"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        changingWarehouse = true;
                        picker.SelectedValue = priorWarehouseId;
                        changingWarehouse = false;
                    }
                };

                var layout = new Grid { Margin = new Thickness(8) };
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var warehouseRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var warehouseLabel = new TextBlock
                {
                    Text = T("Reports.Col.Warehouse"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                warehouseRow.Children.Add(warehouseLabel);
                warehouseRow.Children.Add(pickerShell);
                Grid.SetRow(warehouseRow, 0);
                layout.Children.Add(warehouseRow);

                var instruction = new TextBlock
                {
                    Text = T("Inventory.StockCountHint"),
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var materialSearchRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                materialSearchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                materialSearchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                materialSearchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var materialSearchLabel = new TextBlock
                {
                    Text = T("Inventory.MaterialFilter"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                materialFilter.ToolTip = T("Inventory.MaterialFilterHint");
                var materialLookup = new Button
                {
                    Content = "...",
                    MinWidth = 32,
                    Height = 28,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = T("Inventory.SearchMaterial")
                };
                materialLookup.SetResourceReference(StyleProperty, "VisuButtonStyle");
                Grid.SetColumn(materialFilter, 1);
                Grid.SetColumn(materialLookup, 2);
                materialSearchRow.Children.Add(materialSearchLabel);
                materialSearchRow.Children.Add(materialFilter);
                materialSearchRow.Children.Add(materialLookup);
                Grid.SetRow(materialSearchRow, 1);
                layout.Children.Add(materialSearchRow);

                var information = new StackPanel();
                information.Children.Add(instruction);
                information.Children.Add(summary);
                Grid.SetRow(information, 2);
                layout.Children.Add(information);

                Grid.SetRow(grid, 3);
                layout.Children.Add(grid);

                var reasonRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var reasonLabel = new TextBlock
                {
                    Text = T("Inventory.StockCountReason"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(reason, 1);
                reasonRow.Children.Add(reasonLabel);
                reasonRow.Children.Add(reason);
                Grid.SetRow(reasonRow, 4);
                layout.Children.Add(reasonRow);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var post = new Button
                {
                    Content = T("Inventory.StockCountPost"),
                    MinWidth = 105,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 0),
                    IsDefault = true
                };
                post.SetResourceReference(StyleProperty, "VisuAccentButtonStyle");
                var cancel = new Button
                {
                    Content = T("WhsWin.Close"),
                    MinWidth = 76,
                    Height = 28,
                    IsCancel = true
                };
                cancel.SetResourceReference(StyleProperty, "VisuButtonStyle");
                materialFilter.TextChanged += (_, _) =>
                {
                    selectedMaterialId = null;
                    RefreshRowsView();
                };
                materialLookup.Click += (_, _) =>
                {
                    var material = SerWin.PickMaterial(dialog, materialFilter.Text);
                    if (material == null)
                        return;
                    selectedMaterialId = material.Id;
                    var name = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
                        ? material.Name
                        : string.IsNullOrWhiteSpace(material.NameEn) ? material.Name : material.NameEn;
                    materialFilter.Text = $"{material.Code} - {name}";
                    selectedMaterialId = material.Id;
                    RefreshRowsView();
                    var row = rows.FirstOrDefault(x => x.MaterialId == material.Id);
                    if (row != null)
                    {
                        grid.SelectedItem = row;
                        grid.ScrollIntoView(row);
                    }
                };
                grid.CellEditEnding += (_, _) => dialog.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        grid.Items.Refresh();
                        UpdateSummary();
                    }),
                    DispatcherPriority.Background);
                void ShowCountValidation(string message)
                {
                    AppHost.Logging?.LogError("InventoryCount", $"[INV-CNT-001] {message}");
                    MessageBox.Show(
                        dialog,
                        $"{message}\n[INV-CNT-001]",
                        T("Common.Warning"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                post.Click += (_, _) =>
                {
                    grid.CommitEdit(DataGridEditingUnit.Cell, true);
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                    if (string.IsNullOrWhiteSpace(reason.Text))
                    {
                        ShowCountValidation(T("Inventory.StockCountReasonRequired"));
                        reason.Focus();
                        return;
                    }
                    if (rows.Count == 0 || grid.HasItems is false)
                    {
                        ShowCountValidation(T("Inventory.StockCountNoMaterials"));
                        return;
                    }

                    try
                    {
                        var lines = new List<StkPost.StockCountLine>(rows.Count);
                        foreach (var row in rows)
                        {
                            if (!TryDecimal(row.CountedQtyText, out var countedQty) ||
                                !TryDecimal(row.UnitCostText, out var unitCost))
                            {
                                ShowCountValidation($"{row.Code}: {T("Inventory.StockCountQtyCostInvalid")}");
                                return;
                            }
                            lines.Add(new StkPost.StockCountLine(
                                row.MaterialId, row.Code, countedQty, unitCost));
                        }
                        var changed = StkPost.PostCount(
                            loadedWarehouseId ?? throw new InvalidOperationException("Select a warehouse."),
                            DateTime.Today,
                            reason.Text,
                            lines);
                        MessageBox.Show(
                            dialog,
                            T("Inventory.StockCountPosted").Replace(
                                "{0}",
                                FormatWestern(changed),
                                StringComparison.Ordinal),
                            T("Application.Success"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        dialog.DialogResult = true;
                    }
                    catch (Exception ex)
                    {
                        AppHost.Logging?.LogError(
                            "InventoryCount",
                            "[INV-CNT-003] Posting the stock count failed.",
                            ex);
                        MessageBox.Show(dialog, $"{T("Inventory.StockCountPostFailed")}\n[INV-CNT-003]", T("Common.Error"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                buttons.Children.Add(post);
                buttons.Children.Add(cancel);
                Grid.SetRow(buttons, 5);
                layout.Children.Add(buttons);
                dialog.Content = layout;
                dialog.SetLocalizationAction(() =>
                {
                    dialog.Title = isAdjustment
                        ? T("Inventory.InventoryAdjustment")
                        : T("Inventory.StockCountTitle");
                    grid.Columns[0].Header = T("Reports.Col.MaterialCode");
                    grid.Columns[1].Header = T("Reports.Col.MaterialName");
                    grid.Columns[2].Header = T("Inventory.StockCountUnit");
                    grid.Columns[3].Header = T("Inventory.StockCountCurrent");
                    grid.Columns[4].Header = T("Inventory.StockCountQty");
                    grid.Columns[5].Header = T("Inventory.StockCountDifference");
                    grid.Columns[6].Header = T("Inventory.StockCountUnitCost");
                    grid.Columns[7].Header = T("Inventory.StockCountDifferenceValue");
                    warehouseLabel.Text = T("Reports.Col.Warehouse");
                    materialSearchLabel.Text = T("Inventory.MaterialFilter");
                    materialLookup.ToolTip = T("Inventory.SearchMaterial");
                    materialFilter.ToolTip = T("Inventory.MaterialFilterHint");
                    instruction.Text = T("Inventory.StockCountHint");
                    UpdateSummary();
                    reasonLabel.Text = T("Inventory.StockCountReason");
                    post.Content = T("Inventory.StockCountPost");
                    cancel.Content = T("WhsWin.Close");
                });

                return dialog.ShowDialog() == true;
            }
            catch (Exception ex)
            {
                AppHost.Logging?.LogError(
                    "InventoryCount",
                    "[INV-CNT-002] Opening the stock count window failed.",
                    ex);
                MessageBox.Show(owner, $"{T("Inventory.StockCountLoadFailed")}\n[INV-CNT-002]", T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private sealed class StockCountRow
        {
            public long MaterialId { get; init; }
            public string Code { get; init; } = "";
            public string Name { get; init; } = "";
            public string NameEn { get; init; } = "";
            public string Unit { get; init; } = "";
            public decimal CurrentQty { get; init; }
            public string CurrentQtyText => AppFormat.FormatNumber(CurrentQty, "N4");
            public string CountedQtyText { get; set; } = "";
            public string UnitCostText { get; set; } = "";
            public decimal StartingCost { get; init; }
            public string DifferenceText
                => TryParse(CountedQtyText, out var qty)
                    ? AppFormat.FormatNumber(qty - CurrentQty, "0.0000")
                    : "";
            public string DifferenceValueText
                => TryParse(CountedQtyText, out var qty) && TryParse(UnitCostText, out var cost)
                    ? AppFormat.FormatNumber((qty - CurrentQty) * cost, "N2")
                    : "";

            private static bool TryParse(string value, out decimal result)
                => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        }

        private static Style CreateGridEditorStyle()
        {
            var style = new Style(
                typeof(TextBox),
                System.Windows.Application.Current?.TryFindResource("VisuTextBoxStyle") as Style);
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 24d));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(1, 0, 1, 0)));
            return style;
        }

        private sealed class StockCountDialog : BaseWin
        {
            private Action? _applyLocalizedText;

            protected override string PrefKey => "StockCountWin";

            public StockCountDialog(Window? owner)
            {
                Owner = owner ?? System.Windows.Application.Current?.MainWindow;
            }

            public void SetLocalizationAction(Action action)
            {
                _applyLocalizedText = action;
                ApplyLocalizedText();
            }

            protected override void ApplyLocalizedText()
                => _applyLocalizedText?.Invoke();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // -----------------------------------------------------------------
        // Status / Mode / Position
        // -----------------------------------------------------------------

        private void RefreshModeBadge()
        {
            if (TxtMode == null) return;

            string key;
            if (_isNew) key = "WhsWin.ModeNew";
            else if (_editing) key = "WhsWin.ModeEdit";
            else key = "WhsWin.ModeView";

            TxtMode.Text = T(key);
        }

        private void RefreshPositionLabel()
        {
            if (TxtPosition == null) return;

            if (_isNew || _items.Count == 0)
                TxtPosition.Text = "-";
            else
                TxtPosition.Text = FormatPosition(_index + 1, _items.Count);
        }

        private void RefreshStatusBar()
        {
            try
            {
                var user = AppState.CurrentUser?.Username;
                if (string.IsNullOrWhiteSpace(user))
                    user = Environment.UserName;

                var db = AppState.CurrentDatabaseName;
                if (string.IsNullOrWhiteSpace(db))
                    db = "-";

                SafeSet(TxtStatus,
                    FormatLoc(T("WhsWin.StatusUserDb"), user, db));
            }
            catch
            {
                SafeSet(TxtStatus, "");
            }
        }

        // -----------------------------------------------------------------
        // Toast
        // -----------------------------------------------------------------

        private void ShowToast(
            bool success,
            string message,
            string? errorCode = null,
            string? detail = null)
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
                    FlowDirection = FlowDirection,
                    FontFamily = ShouTech.App.Services.VisuId.ResolveFontFamily("ApplicationFontFamily", "Segoe UI"),
                    FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12)
                };

                var bg = success
                    ? (TryFindResource("SuccessBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)))
                    : (TryFindResource("ErrorBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));

                var text = message;
                if (!string.IsNullOrWhiteSpace(errorCode))
                    text += $"  [{errorCode}]";
                if (!string.IsNullOrWhiteSpace(detail))
                    text += "\n" + detail;

                var border = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = bg,
                    Padding = new Thickness(16, 10, 16, 10),
                    Child = new TextBlock
                    {
                        Text = text,
                        Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360
                    }
                };

                toast.Content = border;
                toast.Loaded += (_, _) =>
                {
                    try
                    {
                        toast.Left = Left + (ActualWidth - toast.ActualWidth) / 2;
                        toast.Top  = Top + ActualHeight - toast.ActualHeight - 60;
                    }
                    catch { }
                };

                toast.Show();

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(success ? 2.2 : 3.5) };
                timer.Tick += (_, _) => { timer.Stop(); try { toast.Close(); } catch { } };
                timer.Start();
            }
            catch
            {
                // Toast must never crash the form.
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------


        /// <summary>
        /// Formats a localized template. Suppresses CA1863 because format strings
        /// come from language resources and change at runtime with the UI language.
        /// </summary>
#pragma warning disable CA1863
        private static string FormatLoc(string format, params object[] args)
        {
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch (FormatException) { return format; }
        }
#pragma warning restore CA1863

        private static void SafeSet(TextBlock? tb, string value)
        {
            if (tb != null) tb.Text = value ?? "";
        }

        private static void SafeSet(TextBox? tb, string value)
        {
            if (tb != null) tb.Text = value ?? "";
        }

        private static void SafeSet(Button? btn, string value)
        {
            if (btn != null) btn.Content = value ?? "";
        }

        private static void SafeEnable(UIElement? el, bool enabled)
        {
            if (el != null) el.IsEnabled = enabled;
        }

        private static void SafeReadOnly(TextBox? box, bool readOnly)
        {
            if (box == null) return;
            box.IsReadOnly = readOnly;
            box.IsTabStop = !readOnly;
        }
    }

                public sealed class MatMergeWin : BaseWin
                {
                    private const string ErrLoad = "INV-TRF-003";
                    private const string ErrPost = "INV-TRF-005";

                    private readonly ComboBox _source = new();
                    private readonly ComboBox _target = new();
                    private readonly TextBox _reason = new();
                    private readonly DataGrid _grid = new();
                    private readonly TextBlock _hint = new();
                    private readonly Button _post = new();
                    private readonly Button _cancel = new();
                    private readonly List<TransferRow> _rows = new();
                    private List<MatRepo.WarehouseRec> _warehouses = new();
                    private readonly TextBox _sourceMaterial = new();
                    private readonly TextBox _targetMaterial = new();
                    private readonly TextBlock _sourceMaterialLabel = new();
                    private readonly TextBlock _targetMaterialLabel = new();
                    private readonly Button _sourceLookup = new();
                    private readonly Button _targetLookup = new();
                    private long? _sourceMaterialId;
                    private long? _targetMaterialId;
                    private bool _isMovingHistory;

                    protected override string PrefKey => "MaterialHistoryMoveWin";

                    public MatMergeWin(Window? owner)
                    {
                        Owner = owner ?? System.Windows.Application.Current?.MainWindow;
                        WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        Width = 440;
                        Height = 208;
                        MinWidth = 410;
                        MinHeight = 190;
                        ResizeMode = ResizeMode.NoResize;
                        SetResourceReference(FontFamilyProperty, "ApplicationFontFamily");
                        SetResourceReference(FontSizeProperty, "ApplicationFontSize");
                        VisuId.ApplyWindow(this);
                        BuildMaterialMoveLayout();
                        ApplyLocalizedText();
                    }

                    protected override void ApplyLocalizedText()
                    {
                        Title = T("Inventory.MaterialMoveTitle");
                        _sourceMaterialLabel.Text = T("Inventory.MaterialMoveFrom");
                        _targetMaterialLabel.Text = T("Inventory.MaterialMoveTo");
                        _sourceMaterial.ToolTip = T("Inventory.MaterialFilterHint");
                        _targetMaterial.ToolTip = T("Inventory.MaterialFilterHint");
                        _sourceLookup.ToolTip = T("Inventory.SearchMaterial");
                        _targetLookup.ToolTip = T("Inventory.SearchMaterial");
                        _post.Content = T("Common.OK");
                        _cancel.Content = T("Common.Cancel");
                    }

                    public static void Open(Window? owner)
                    {
                        var window = new MatMergeWin(owner);
                        window.ShowDialog();
                    }

                    private void BuildMaterialMoveLayout()
                    {
                        var root = new Grid { Margin = new Thickness(12, 10, 12, 8) };
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                        AddMaterialPickerRow(root, 0, _sourceMaterialLabel, _sourceMaterial, _sourceLookup,
                            (_, _) => PickMaterial(_sourceMaterial, true));
                        AddMaterialPickerRow(root, 1, _targetMaterialLabel, _targetMaterial, _targetLookup,
                            (_, _) => PickMaterial(_targetMaterial, false));
                        _sourceMaterial.Margin = new Thickness(0, 0, 0, 6);

                        var buttons = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 4, 0, 0)
                        };
                        _post.MinWidth = 82;
                        _post.Height = 28;
                        _post.Margin = new Thickness(0, 0, 4, 0);
                        _post.SetResourceReference(StyleProperty, "VisuAccentButtonStyle");
                        _post.IsDefault = true;
                        _post.Click += MoveHistory_Click;
                        _cancel.MinWidth = 76;
                        _cancel.Height = 28;
                        _cancel.SetResourceReference(StyleProperty, "VisuButtonStyle");
                        _cancel.IsCancel = true;
                        _cancel.Click += (_, _) => Close();
                        buttons.Children.Add(_post);
                        buttons.Children.Add(_cancel);
                        Grid.SetRow(buttons, 2);
                        root.Children.Add(buttons);

                        _sourceMaterial.TextChanged += (_, _) => _sourceMaterialId = null;
                        _targetMaterial.TextChanged += (_, _) => _targetMaterialId = null;
                        Content = root;
                    }

                    private static void AddMaterialPickerRow(
                        Grid root,
                        int row,
                        TextBlock label,
                        TextBox input,
                        Button lookup,
                        RoutedEventHandler pick)
                    {
                        var line = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                        label.VerticalAlignment = VerticalAlignment.Center;
                        label.Margin = new Thickness(0, 0, 7, 0);
                        Grid.SetColumn(label, 0);
                        line.Children.Add(label);

                        input.Height = 28;
                        input.Padding = new Thickness(6, 2, 6, 2);
                        input.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
                        Grid.SetColumn(input, 1);
                        line.Children.Add(input);

                        lookup.Content = "...";
                        lookup.Width = 30;
                        lookup.Height = 28;
                        lookup.Margin = new Thickness(4, 0, 0, 0);
                        lookup.SetResourceReference(StyleProperty, "VisuButtonStyle");
                        lookup.Click += pick;
                        Grid.SetColumn(lookup, 2);
                        line.Children.Add(lookup);
                        Grid.SetRow(line, row);
                        root.Children.Add(line);
                    }

                    private void PickMaterial(TextBox input, bool source)
                    {
                        var material = SerWin.PickMaterial(this, input.Text);
                        if (material is null)
                            return;
                        input.Text = $"{material.Code} — {DisplayMaterialName(material)}";
                        if (source)
                            _sourceMaterialId = material.Id;
                        else
                            _targetMaterialId = material.Id;
                    }

                    private static string DisplayMaterialName(MatRepo.MatRec material)
                        => string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase) ||
                           string.IsNullOrWhiteSpace(material.NameEn)
                            ? material.Name
                            : material.NameEn;

                    private void MoveHistory_Click(object sender, RoutedEventArgs e)
                    {
                        if (_isMovingHistory)
                            return;
                        long? sourceId;
                        long? targetId;
                        try
                        {
                            sourceId = ResolveMaterialId(_sourceMaterial, true);
                            targetId = ResolveMaterialId(_targetMaterial, false);
                        }
                        catch (Exception ex)
                        {
                            ShowFailure("INV-MRG-005", ex, "Loading materials for history transfer failed.");
                            return;
                        }
                        if (sourceId is not long ||
                            targetId is not long ||
                            sourceId == targetId)
                        {
                            ShowFailure("INV-MRG-001");
                            return;
                        }

                        _isMovingHistory = true;
                        _post.IsEnabled = false;
                        try
                        {
                            var result = StkPost.MoveMaterialHistory(sourceId.Value, targetId.Value);
                            AppHost.Logging?.LogInfo(
                                "MaterialHistoryMove",
                                $"[INV-MRG-000] Moved {result.DocumentLines} document lines and {result.Movements} stock movements from material {sourceId} to {targetId} for company '{AppState.CurrentCompanyCode}'.");
                            MessageBox.Show(
                                this,
                                T("Inventory.MaterialMoveCompleted").Replace(
                                    "{0}",
                                    result.Total.ToString(CultureInfo.InvariantCulture),
                                    StringComparison.Ordinal),
                                T("Application.Success"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            DialogResult = true;
                        }
                        catch (StkPost.MaterialHistoryMoveException ex)
                        {
                            ShowFailure(ex.ErrorCode, ex, ex.Message);
                        }
                        catch (Exception ex)
                        {
                            ShowFailure("INV-MRG-005", ex, "Moving material history failed.");
                        }
                        finally
                        {
                            _isMovingHistory = false;
                            _post.IsEnabled = true;
                        }
                    }

                    private long? ResolveMaterialId(TextBox input, bool source)
                    {
                        var selectedId = source ? _sourceMaterialId : _targetMaterialId;
                        if (selectedId is not null)
                            return selectedId;
                        var query = input.Text.Trim();
                        if (query.Length == 0)
                            return null;

                        var materials = MatRepo.LoadAll();
                        var matches = materials.Where(material =>
                                material.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                material.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                material.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                material.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                material.SearchName.Contains(query, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (matches.Count == 1)
                        {
                            var match = matches[0];
                            input.Text = $"{match.Code} — {DisplayMaterialName(match)}";
                            if (source)
                                _sourceMaterialId = match.Id;
                            else
                                _targetMaterialId = match.Id;
                            return match.Id;
                        }

                        PickMaterial(input, source);
                        return source ? _sourceMaterialId : _targetMaterialId;
                    }

                    private void BuildLayout()
                    {
                        var root = new Grid { Margin = new Thickness(10) };
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                        _hint.Margin = new Thickness(0, 0, 0, 5);
                        _hint.TextWrapping = TextWrapping.Wrap;
                        Grid.SetRow(_hint, 0);
                        root.Children.Add(_hint);

                        var warehouses = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                        warehouses.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        warehouses.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        warehouses.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        warehouses.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var sourceLabel = new TextBlock
                        {
                            Name = "SourceLabel",
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };
                        var targetLabel = new TextBlock
                        {
                            Name = "TargetLabel",
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(12, 0, 6, 0)
                        };
                        _source.Height = 28;
                        _source.BorderThickness = new Thickness(0);
                        _source.Background = Brushes.Transparent;
                        _target.Height = 28;
                        _target.BorderThickness = new Thickness(0);
                        _target.Background = Brushes.Transparent;
                        var sourceShell = CreateFieldShell(_source);
                        sourceShell.Margin = new Thickness(0, 0, 10, 0);
                        var targetShell = CreateFieldShell(_target);
                        _target.Margin = new Thickness(0);
                        Grid.SetColumn(sourceLabel, 0);
                        Grid.SetColumn(sourceShell, 1);
                        Grid.SetColumn(targetLabel, 2);
                        Grid.SetColumn(targetShell, 3);
                        warehouses.Children.Add(sourceLabel);
                        warehouses.Children.Add(sourceShell);
                        warehouses.Children.Add(targetLabel);
                        warehouses.Children.Add(targetShell);
                        warehouses.Tag = (sourceLabel, targetLabel);
                        Grid.SetRow(warehouses, 1);
                        root.Children.Add(warehouses);

                        _grid.AutoGenerateColumns = false;
                        _grid.CanUserAddRows = false;
                        _grid.CanUserDeleteRows = false;
                        _grid.SelectionMode = DataGridSelectionMode.Single;
                        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
                        _grid.Margin = new Thickness(0, 0, 0, 5);
                        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
                        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
                        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
                        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
                        _grid.SetResourceReference(BackgroundProperty, "SurfaceBrush");
                        _grid.Columns.Add(new DataGridTextColumn
                        {
                            Binding = new System.Windows.Data.Binding(nameof(TransferRow.Code)),
                            IsReadOnly = true,
                            Width = 125
                        });
                        _grid.Columns.Add(new DataGridTextColumn
                        {
                            Binding = new System.Windows.Data.Binding(nameof(TransferRow.Name)),
                            IsReadOnly = true,
                            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                        });
                        _grid.Columns.Add(new DataGridTextColumn
                        {
                            Binding = new System.Windows.Data.Binding(nameof(TransferRow.AvailableText)),
                            IsReadOnly = true,
                            Width = 150
                        });
                        _grid.Columns.Add(new DataGridTextColumn
                        {
                            Binding = new System.Windows.Data.Binding(nameof(TransferRow.QuantityText))
                            {
                                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                            },
                            EditingElementStyle = CreateGridEditorStyle(),
                            Width = 160
                        });
                        Grid.SetRow(_grid, 2);
                        root.Children.Add(_grid);

                        _reason.Height = 28;
                        _reason.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
                        var reasonRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                        reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        reasonRow.Children.Add(new TextBlock
                        {
                            Name = "ReasonLabel",
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        });
                        _reason.MaxLength = 500;
                        Grid.SetColumn(_reason, 1);
                        reasonRow.Children.Add(_reason);
                        reasonRow.Tag = reasonRow.Children[0];
                        Grid.SetRow(reasonRow, 3);
                        root.Children.Add(reasonRow);

                        var buttons = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right
                        };
                        _post.MinWidth = 112;
                        _post.Height = 28;
                        _post.Margin = new Thickness(0, 0, 4, 0);
                        _post.SetResourceReference(StyleProperty, "VisuAccentButtonStyle");
                        _post.IsDefault = true;
                        _post.Click += Post_Click;
                        _cancel.MinWidth = 76;
                        _cancel.Height = 28;
                        _cancel.SetResourceReference(StyleProperty, "VisuButtonStyle");
                        _cancel.IsCancel = true;
                        _cancel.Click += (_, _) => Close();
                        buttons.Children.Add(_post);
                        buttons.Children.Add(_cancel);
                        Grid.SetRow(buttons, 4);
                        root.Children.Add(buttons);
                        Content = root;

                    }

                    private Border CreateFieldShell(Control control)
                        => new()
                        {
                            Height = 28,
                            BorderBrush = TryFindResource("BorderBrush") as Brush,
                            Background = TryFindResource("FieldBackgroundBrush") as Brush,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Child = control
                        };

                    private Style CreateGridEditorStyle()
                    {
                        var style = new Style(
                            typeof(TextBox),
                            TryFindResource("VisuTextBoxStyle") as Style);
                        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 24d));
                        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(1, 0, 1, 0)));
                        return style;
                    }

                    private void LoadMaterials()
                    {
                        if (_source.SelectedItem is not MatRepo.WarehouseRec warehouse)
                            return;
                        try
                        {
                            _rows.Clear();
                            _rows.AddRange(MatRepo.LoadStockRows(warehouse.Id)
                                .Where(x => x.Quantity > 0)
                                .Select(x => new TransferRow
                                {
                                    MaterialId = x.MaterialId,
                                    Code = x.Code,
                                    Name = x.Name,
                                    Available = x.Quantity
                                }));
                            _grid.ItemsSource = null;
                            _grid.ItemsSource = _rows;
                            _post.IsEnabled = true;
                            _hint.Text = _rows.Count == 0
                                ? T("Inventory.TransferNoStock")
                                : T("Inventory.TransferHint");
                        }
                        catch (Exception ex)
                        {
                            _post.IsEnabled = false;
                            ShowFailure(ErrLoad, ex, "Loading material balances for transfer failed.");
                        }
                    }

                    private void Post_Click(object sender, RoutedEventArgs e)
                    {
                        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
                        _grid.CommitEdit(DataGridEditingUnit.Row, true);

                        if (_source.SelectedItem is not MatRepo.WarehouseRec source ||
                            _target.SelectedItem is not MatRepo.WarehouseRec target ||
                            source.Id == target.Id)
                        {
                            ShowFailure("INV-TRF-001");
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(_reason.Text))
                        {
                            ShowValidation(T("Inventory.TransferReasonRequired"));
                            _reason.Focus();
                            return;
                        }

                        var lines = new List<StkPost.StockTransferLine>();
                        foreach (var row in _rows)
                        {
                            if (string.IsNullOrWhiteSpace(row.QuantityText))
                                continue;
                            if (!decimal.TryParse(row.QuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty))
                            {
                                ShowValidation($"{row.Code}: {T("Inventory.TransferQtyInvalid")}");
                                return;
                            }
                            if (qty == 0)
                                continue;
                            if (qty < 0 || qty > row.Available || decimal.Round(qty, 4) != qty)
                            {
                                ShowValidation($"{row.Code}: {T("Inventory.TransferQtyInvalid")}");
                                return;
                            }
                            lines.Add(new StkPost.StockTransferLine(row.MaterialId, row.Code, qty));
                        }

                        if (lines.Count == 0)
                        {
                            ShowValidation(T("Inventory.TransferNoQty"));
                            return;
                        }

                        try
                        {
                            var documentNo = StkPost.PostTransfer(
                                source.Id,
                                target.Id,
                                DateTime.Today,
                                _reason.Text,
                                lines);
                            AppHost.Logging?.LogInfo("InventoryTransfer", $"[INV-TRF-000] Transfer {documentNo} posted for company '{AppState.CurrentCompanyCode}'.");
                            MessageBox.Show(
                                this,
                                T("Inventory.TransferPosted").Replace("{0}", documentNo, StringComparison.Ordinal),
                                T("Application.Success"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            DialogResult = true;
                        }
                        catch (StkPost.StockTransferException ex)
                        {
                            ShowFailure(ex.ErrorCode, ex, ex.Message);
                        }
                        catch (Exception ex)
                        {
                            ShowFailure(ErrPost, ex, "Posting material transfer failed.");
                        }
                    }

                    private void UpdateWarehouseCaptions()
                    {
                        if (Content is not Grid root || root.Children.Count < 2 ||
                            root.Children[1] is not Grid warehouseGrid ||
                            warehouseGrid.Tag is not ValueTuple<TextBlock, TextBlock> labels)
                            return;
                        labels.Item1.Text = T("Inventory.TransferSource");
                        labels.Item2.Text = T("Inventory.TransferTarget");
                        if (root.Children[3] is Grid reasonGrid && reasonGrid.Tag is TextBlock reasonLabel)
                            reasonLabel.Text = T("Inventory.TransferReason");
                    }

                    private void ShowFailure(string code, Exception? exception = null, string? logMessage = null)
                    {
                        var message = code switch
                        {
                            "INV-MRG-001" => T("Inventory.MaterialMoveInvalid"),
                            "INV-MRG-002" => T("Inventory.MaterialMoveFailed"),
                            "INV-MRG-003" => T("Inventory.MaterialMoveUnitMismatch"),
                            "INV-MRG-004" => T("Inventory.MaterialMoveNoHistory"),
                            "INV-MRG-005" => T("Inventory.MaterialMoveFailed"),
                            "INV-TRF-001" => T("Inventory.TransferInvalid"),
                            "INV-TRF-002" => T("Inventory.TransferFailed"),
                            "INV-TRF-003" => T("Inventory.TransferLoadFailed"),
                            "INV-TRF-004" => T("Inventory.TransferInsufficientStock"),
                            _ => T("Inventory.TransferFailed")
                        };
                        MessageBox.Show(
                            this,
                            $"{message}\n[{code}]",
                            T("Common.Error"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        LogError(code, logMessage ?? "Stock transfer validation failed.", exception);
                    }

                    private void ShowValidation(string message)
                        => ShowFailure("INV-TRF-001", logMessage: message);

                    private static void LogError(string code, string message, Exception? exception = null)
                        => AppHost.Logging?.LogError(
                            "MaterialHistoryMove",
                            $"[{code}] {message}",
                            exception);

                    private sealed class TransferRow
                    {
                        public long MaterialId { get; init; }
                        public string Code { get; init; } = "";
                        public string Name { get; init; } = "";
                        public decimal Available { get; init; }
                        public string AvailableText => AppFormat.FormatNumber(Available, "N4");
                        public string QuantityText { get; set; } = "";
                    }
                }
}
