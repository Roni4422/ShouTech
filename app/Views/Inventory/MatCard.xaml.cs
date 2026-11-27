using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ShouTech.App.Localization;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views;

// ====================================================================
//  MatUnitRow - one row in the Units & Prices grid
// ====================================================================
public sealed class MatUnitRow : INotifyPropertyChanged
{
    private int _lineNo;
    private string _unitName = "";
    private decimal? _factor;
    private decimal? _priceSale, _pricePurchase, _priceWholesale, _priceRetail,
        _priceSpecial, _priceExport, _priceDistributor;
    private string _barcode = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public int LineNo
    {
        get => _lineNo;
        set { if (_lineNo == value) return; _lineNo = value; Notify(nameof(LineNo)); }
    }

    public string UnitName
    {
        get => _unitName;
        set { var v = value ?? ""; if (_unitName == v) return; _unitName = v; Notify(nameof(UnitName)); }
    }

    public decimal? Factor
    {
        get => _factor;
        set
        {
            var v = (value is null || value < 0) ? null : value;
            if (_factor == v) return;
            _factor = v;
            Notify(nameof(Factor));
        }
    }

    public decimal? PriceSale
    {
        get => _priceSale;
        set { if (_priceSale == value) return; _priceSale = value; Notify(nameof(PriceSale)); }
    }

    public decimal? PricePurchase
    {
        get => _pricePurchase;
        set { if (_pricePurchase == value) return; _pricePurchase = value; Notify(nameof(PricePurchase)); }
    }

    public decimal? PriceWholesale
    {
        get => _priceWholesale;
        set { if (_priceWholesale == value) return; _priceWholesale = value; Notify(nameof(PriceWholesale)); }
    }

    public decimal? PriceRetail
    {
        get => _priceRetail;
        set { if (_priceRetail == value) return; _priceRetail = value; Notify(nameof(PriceRetail)); }
    }

    public decimal? PriceSpecial
    {
        get => _priceSpecial;
        set { if (_priceSpecial == value) return; _priceSpecial = value; Notify(nameof(PriceSpecial)); }
    }

    public decimal? PriceExport
    {
        get => _priceExport;
        set { if (_priceExport == value) return; _priceExport = value; Notify(nameof(PriceExport)); }
    }

    public decimal? PriceDistributor
    {
        get => _priceDistributor;
        set { if (_priceDistributor == value) return; _priceDistributor = value; Notify(nameof(PriceDistributor)); }
    }

    public string Barcode
    {
        get => _barcode;
        set { var v = value ?? ""; if (_barcode == v) return; _barcode = v; Notify(nameof(Barcode)); }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(UnitName)
        || Factor.HasValue
        || PriceSale.HasValue || PricePurchase.HasValue || PriceWholesale.HasValue
        || PriceRetail.HasValue || PriceSpecial.HasValue || PriceExport.HasValue
        || PriceDistributor.HasValue || !string.IsNullOrWhiteSpace(Barcode);

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ====================================================================
//  MatComboOption - combo item with a stable key + localized display
// ====================================================================
public sealed class MatComboOption
{
    public MatComboOption(string key, string display)
    {
        Key = key ?? string.Empty;
        Display = display ?? string.Empty;
    }

    public string Key { get; }
    public string Display { get; }

    public override string ToString() => Display;
}

public sealed class MatCategoryOption
{
    public MatCategoryOption(string key, string display, string code = "", string nameAr = "", string nameEn = "")
    {
        Key = key;
        Display = display;
        Code = code;
        Name = nameAr;
        NameEn = nameEn;
    }

    public string Key { get; }
    public string Display { get; }
    public string Code { get; }
    public string Name { get; }
    public string NameEn { get; }

    public override string ToString() => Display;
}

// ====================================================================
//  MatCard - Material Card window
// ====================================================================
public partial class MatCard : BaseWin, IInfoContextProvider
{
    protected override string? ScheduleTintName => "Amber";

    // ---------- State ----------
    private readonly ObservableCollection<MatUnitRow> _units = new();
    private readonly ObservableCollection<MatAttrRow> _attrs = new();
    private List<MatRepo.MatRec> _cache = new();
    private List<MatCategoryOption> _categoryOptions = new();
    private List<string> _unitSuggestions = new();
    private bool _filteringUnitCombo;
    private bool _unitComboNavigated;
    private int _index = -1;
    private bool _isNew;
    private bool _isEditing;
    private long _currentId;
    private MatRepo.MatRec? _loaded;

    // Global Information Window provider (INFO_WINDOW_STUDY): the
    // card's loaded material.
    public InfoRef? CurrentInfoRef
        => _currentId != 0 ? new InfoRef(InfoKind.Material, Id: _currentId) : null;

    public InfoContext? CurrentInfoContext => null;

    private static readonly Regex NumericInputRegex =
        new(@"^[0-9]*\.?[0-9]*$", RegexOptions.Compiled);

    private const int UnitWorkspaceRows = 200;

    // ================================================================
    //  Static constructor: numeric input filter for cells
    // ================================================================
    static MatCard()
    {
        EventManager.RegisterClassHandler(
            typeof(DataGridCell),
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler(OnAnyDataGridCellPreviewTextInput),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(TextBox),
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler(OnTextBoxPreviewTextInput),
            handledEventsToo: true);
    }

    private static void OnAnyDataGridCellPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Numeric-only cells are filtered by each TextBox handler registered below.
        // This cell-level hook exists so the class handler runs before the TextBox
        // handler and can veto input if necessary. Current implementation is a no-op.
    }

    private static void OnTextBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Only filter when the TextBox is inside a DataGrid cell whose column
        // is a numeric column. TextBoxes outside grids (regular form fields)
        // accept any input.
        var cell = FindAncestor<DataGridCell>(tb);
        if (cell == null) return;

        var column = cell.Column as DataGridTextColumn;
        if (column == null) return;

        // Identify numeric columns by binding path.
        var path = (column.Binding as System.Windows.Data.Binding)?.Path?.Path;
        if (string.IsNullOrEmpty(path)) return;

        var numericPaths = new[]
        {
            "Factor", "PriceSale", "PricePurchase", "PriceWholesale",
            "PriceRetail", "PriceSpecial", "PriceExport", "PriceDistributor"
        };

        if (!numericPaths.Contains(path)) return;

        if (!NumericInputRegex.IsMatch(e.Text))
            e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T t) return t;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static string ToWesternDigits(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= '\u0660' && chars[i] <= '\u0669')
                chars[i] = (char)('0' + (chars[i] - '\u0660'));
            else if (chars[i] >= '\u06F0' && chars[i] <= '\u06F9')
                chars[i] = (char)('0' + (chars[i] - '\u06F0'));
        }
        return new string(chars);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    // ================================================================
    //  Constructor
    // ================================================================
    public MatCard()
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);
        ApplyDirection();

        try
        {
            AppServiceHost.Localization.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) =>
            {
                try { AppServiceHost.Localization.LanguageChanged -= OnLanguageChanged; }
                catch (Exception exL287)
                {
                    AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL287, "MatCard.xaml.MatCard");
                }
                RestoreOwnerWindow();
            };
        }
        catch (Exception exL291)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL291, "MatCard.xaml.MatCard");
        }

        try
        {
            DgUnits.CanUserAddRows = false;
            DgAttributes.CanUserAddRows = true;
            // Unit grid already has a persisted LineNo data column; avoid
            // duplicating it with the shared row header. Attribute rows use
            // the standard row header and shared Excel-like interaction.
            GridEngine.ApplyStandard(DgUnits, showRowNumbers: false);
            GridEngine.ApplyStandard(DgAttributes);
            DgUnits.ItemsSource = _units;
            DgUnits.PreparingCellForEdit += DgUnits_PreparingCellForEdit;
            DgAttributes.ItemsSource = _attrs;
            _units.CollectionChanged += (_, _) => RenumberUnits();

            InitCombos();
            EnsureUnitRows(UnitWorkspaceRows);
            ReloadCache();

            if (_cache.Count > 0)
            {
                _index = 0;
                LoadCurrent();
            }
            else
            {
                ClearForm();
                SetEditMode(false);
            }

            ShowSection(0);
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.MatCard");
            MessageBox.Show(
                "Failed to load material data:\n" +
                ex.GetType().Name + ": " + ex.Message +
                (ex.InnerException != null ? "\n" + ex.InnerException.Message : ""),
                "Material Card",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            try
            {
                _cache = new();
                _index = -1;
                ClearForm();
                SetEditMode(false);
                ShowSection(0);
            }
            catch (Exception exL337)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL337, "MatCard.xaml.MatCard");
            }
        }
    }

    /// <summary>
    /// Opens the card at a specific material (used by MatDirWin's
    /// Open/double-click — previously the selection was discarded and the
    /// card always showed the first material in the cache).
    /// </summary>
    public void OpenMaterial(long materialId)
    {
        var idx = _cache.FindIndex(x => x.Id == materialId);
        if (idx < 0)
        {
            ReloadCache();
            idx = _cache.FindIndex(x => x.Id == materialId);
        }
        if (idx >= 0)
        {
            _index = idx;
            LoadCurrent();
        }
    }

    // ================================================================
    //  Restore owner window when this dialog closes
    // ================================================================
    private void RestoreOwnerWindow()
    {
        try
        {
            var owner = Owner;
            if (owner == null) return;
            if (owner.WindowState == WindowState.Minimized)
                owner.WindowState = WindowState.Normal;
            owner.Activate();
        }
        catch (Exception exL354)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL354, "MatCard.xaml.RestoreOwnerWindow");
        }
    }

    // ================================================================
    //  Localization + direction
    // ================================================================
    private void ApplyDirection()
    {
        // Central service sets Window.FlowDirection and Western digit rendering.
        // Every DataTrigger in the styles that binds to Window.FlowDirection
        // fires automatically, so no manual per-control alignment is needed.
        LangDirSv.ApplyToWindow(this, App.CurrentLanguageCode);

        // Window title (already localized through LocProps, kept here for
        // title bar refresh on live language switch).
        Title = L("Inventory.MaterialCard", "Material Card");

        // Localization pass over the visual tree (loc:LocProps.Key / ToolTipKey).
        try { UiLoczr.ApplyToWindow(this); } catch (Exception exL372) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL372, "MatCard.xaml.ApplyDirection"); }

        // Keep identifiers centered while allowing their reading direction to follow the UI language.
        if (TxtCode != null)
        {
            TxtCode.TextAlignment = TextAlignment.Center;
        }

        RefreshLocalizedTexts();
        UpdateNavigationArrows();
        UpdateStatusBar();
    }

    private static bool IsRtl => LangDirSv.IsRtl(App.CurrentLanguageCode);

    private static string L(string key, string fallback) => Loc.Get(key, fallback);

    private void RefreshLocalizedTexts()
    {
        Title = L("Inventory.MaterialCard", "Material Card");

        if (DgUnits != null && DgUnits.Columns.Count >= 11)
        {
            DgUnits.Columns[0].Header  = "#";
            DgUnits.Columns[1].Header  = L("MatCard.Unit",        "Unit");
            DgUnits.Columns[2].Header  = L("MatCard.Factor",      "Factor");
            DgUnits.Columns[3].Header  = L("MatCard.Sale",        "Sale");
            DgUnits.Columns[4].Header  = L("MatCard.Purchase",    "Purchase");
            DgUnits.Columns[5].Header  = L("MatCard.Wholesale",   "Wholesale");
            DgUnits.Columns[6].Header  = L("MatCard.Retail",      "Retail");
            DgUnits.Columns[7].Header  = L("MatCard.Special",     "Special");
            DgUnits.Columns[8].Header  = L("MatCard.Export",      "Export");
            DgUnits.Columns[9].Header  = L("MatCard.Distributor", "Distributor");
            DgUnits.Columns[10].Header = L("MatCard.Barcode",     "Barcode");
        }

        if (DgAttributes != null && DgAttributes.Columns.Count >= 3)
        {
            DgAttributes.Columns[0].Header = L("MatCard.AttrGroup", "Group");
            DgAttributes.Columns[1].Header = L("MatCard.AttrKey",   "Key");
            DgAttributes.Columns[2].Header = L("MatCard.AttrValue", "Value");
        }

        RefreshComboDisplays();
        InitCategories(GetComboKey(CmbCategory));
    }

    private void UpdateNavigationArrows()
    {
        var rtl = IsRtl;
        BtnNavFirst.Content = rtl ? "\u25B6|" : "|\u25C0";
        BtnNavPrev.Content  = rtl ? "\u25B6"  : "\u25C0";
        BtnNavNext.Content  = rtl ? "\u25C0"  : "\u25B6";
        BtnNavLast.Content  = rtl ? "|\u25C0" : "\u25B6|";

    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        try { Dispatcher.Invoke(ApplyDirection); }
        catch (Exception exL432) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL432, "MatCard.xaml.OnLanguageChanged"); ApplyDirection(); }
    }

    // ================================================================
    //  Combos
    // ================================================================
    private void InitCombos()
    {
        CmbItemType.ItemsSource = new[]
        {
            new MatComboOption("Stock",    L("MatCard.ItemType.Stock",    "Stock")),
            new MatComboOption("Service",  L("MatCard.ItemType.Service",  "Service")),
            new MatComboOption("NonStock", L("MatCard.ItemType.NonStock", "Non-stock")),
            new MatComboOption("Kit",      L("MatCard.ItemType.Kit",      "Kit"))
        };

        CmbStatus.ItemsSource = new[]
        {
            new MatComboOption("Active",   L("MatCard.Status.Active",   "Active")),
            new MatComboOption("Inactive", L("MatCard.Status.Inactive", "Inactive"))
        };

        CmbCostMethod.ItemsSource = new[]
        {
            new MatComboOption("WeightedAvg",  L("MatCard.CostMethod.WeightedAvg",  "Weighted Average")),
            new MatComboOption("FIFO",         L("MatCard.CostMethod.FIFO",         "FIFO")),
            new MatComboOption("LIFO",         L("MatCard.CostMethod.LIFO",         "LIFO")),
            new MatComboOption("Standard",     L("MatCard.CostMethod.Standard",     "Standard")),
            new MatComboOption("LastPurchase", L("MatCard.CostMethod.LastPurchase", "Last Purchase"))
        };

        CmbAbcClass.ItemsSource = new[]
        {
            new MatComboOption("A", "A"),
            new MatComboOption("B", "B"),
            new MatComboOption("C", "C")
        };

        CmbPriceCurrency.ItemsSource = new[]
        {
            new MatComboOption("SYP", "SYP"),
            new MatComboOption("USD", "USD"),
            new MatComboOption("EUR", "EUR"),
            new MatComboOption("TRY", "TRY"),
            new MatComboOption("SAR", "SAR"),
            new MatComboOption("AED", "AED")
        };

        CmbColor.ItemsSource = BuildColorOptions();
        CmbColor.SelectedIndex = 0;

        InitGroups();
        InitWarehouses();
        RefreshComboDisplays();
        InitCategories();
        InitUnitSuggestions();
    }

    // ----------------------------------------------------------------
    //  Units grid autocomplete (owner directive 2026-11-17): the unit
    //  cell's editor is an editable ComboBox fed from the saved units
    //  (units master + names already saved on materials). Typing filters
    //  by CONTAINS and opens the suggestion list.
    //
    //  Enter semantics (owner, refined 2026-11-17): the list opening is
    //  not a choice. If the user has NOT deliberately highlighted a
    //  suggestion (no arrow-key navigation, pointer not resting on a
    //  list item), Enter commits the TYPED text — a brand-new unit name
    //  is accepted exactly as typed — and then moves horizontally per
    //  the central grid rule. Only a deliberate highlight (arrows, or
    //  the pointer over an item) makes Enter accept the suggestion.
    //  Escape dismisses the list and leaves the typed text intact.
    //  _unitComboNavigated carries the "deliberate highlight" state for
    //  the current edit session: set by Up/Down, cleared by fresh
    //  typing (text input, Back, Delete) and at every edit start.
    // ----------------------------------------------------------------
    private void InitUnitSuggestions()
    {
        try
        {
            var names = new HashSet<string>(
                MatRepo.LoadUnitNameSuggestions(), StringComparer.OrdinalIgnoreCase);
            foreach (var row in _units)
                if (!string.IsNullOrWhiteSpace(row.UnitName))
                    names.Add(row.UnitName.Trim());
            _unitSuggestions = names
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.InitUnitSuggestions");
            _unitSuggestions = new List<string>();
        }
    }

    private void DgUnits_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        try
        {
            if (e.EditingElement is not ComboBox combo) return;
            // A new edit session starts un-navigated: until the user
            // arrows into the list or points at an item, Enter means
            // "take what I typed" (see UnitCombo_PreviewKeyDown).
            _unitComboNavigated = false;
            combo.ItemsSource = _unitSuggestions;
            combo.ApplyTemplate();
            if (combo.Template?.FindName("PART_TextBox", combo) is TextBox textBox
                && textBox.Tag is not ComboBox)
            {
                textBox.Tag = combo;
                textBox.TextChanged += UnitComboTextChanged;
                combo.PreviewKeyDown += UnitCombo_PreviewKeyDown;
                combo.PreviewTextInput += UnitCombo_PreviewTextInput;
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.DgUnits_PreparingCellForEdit");
        }
    }

    private void UnitComboTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filteringUnitCombo) return;
        if (sender is not TextBox textBox || textBox.Tag is not ComboBox combo) return;
        try
        {
            _filteringUnitCombo = true;
            var text = textBox.Text ?? string.Empty;
            if (text.Length == 0)
            {
                combo.ItemsSource = _unitSuggestions;
                combo.IsDropDownOpen = false;
                return;
            }

            var filtered = _unitSuggestions
                .Where(x => x.Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            combo.ItemsSource = filtered;
            if (!string.Equals(combo.Text, text, StringComparison.Ordinal))
                combo.Text = text;
            textBox.CaretIndex = text.Length;
            combo.IsDropDownOpen = filtered.Count > 0;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.UnitComboTextChanged");
        }
        finally
        {
            _filteringUnitCombo = false;
        }
    }

    private void UnitCombo_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Fresh typing re-filters the list; whatever is highlighted after
        // that is the automatic first match, not a choice — Enter must
        // take the typed text again.
        _unitComboNavigated = false;
    }

    // Runs in the tunnel AFTER the central grid behavior (attached at the
    // DataGrid) and BEFORE the ComboBox's own key handling: exactly the
    // slot where the "accept the highlighted suggestion" decision can
    // still be pre-empted without disturbing the behavior's deferred
    // commit + horizontal move, which it schedules for every Enter it
    // leaves to the ComboBox.
    private void UnitCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo) return;

        // Mirror the central behavior's key resolution (Arabic IME layouts
        // deliver the real key as ImeProcessed).
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        try
        {
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                    // Deliberate navigation into the suggestion list:
                    // Enter must now accept the highlighted suggestion.
                    _unitComboNavigated = true;
                    break;

                case Key.Back:
                case Key.Delete:
                    // Editing the text re-arms the "typed text wins" rule.
                    _unitComboNavigated = false;
                    break;

                case Key.Enter:
                    // Plain Enter only — Ctrl/Alt+Enter is not grid
                    // navigation (the central behavior's own definition;
                    // Shift+Enter stays plain and reverses its move).
                    if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
                        return;
                    if (!combo.IsDropDownOpen || _unitComboNavigated || IsPointerOverUnitSuggestion())
                        return;

                    // The list is open only because typing produced
                    // matches; its highlight is the automatic first match,
                    // which the user never chose. Close the list WITHOUT
                    // accepting it, so the typed text — possibly a
                    // brand-new unit name — is what the central behavior's
                    // deferred commit writes to the cell. Marking the key
                    // handled also keeps the DataGrid's own Enter (commit
                    // + one row down) from racing that horizontal move.
                    CloseUnitDropDownPreservingText(combo);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    if (!combo.IsDropDownOpen) return;

                    // Dismiss the suggestions only; the typed text stays
                    // in the editor. (With the list already closed, Escape
                    // keeps its standard grid meaning — cancel the edit.)
                    CloseUnitDropDownPreservingText(combo);
                    e.Handled = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.UnitCombo_PreviewKeyDown");
        }
    }

    private void CloseUnitDropDownPreservingText(ComboBox combo)
    {
        var typed = combo.Text ?? string.Empty;
        combo.IsDropDownOpen = false;
        if (string.Equals(combo.Text, typed, StringComparison.Ordinal)) return;

        // Insurance: if closing the list snapped the text back to a
        // previously committed value, restore what the user actually
        // typed. The filtering guard keeps the resulting TextChanged
        // from re-filtering and re-opening the list.
        _filteringUnitCombo = true;
        try
        {
            combo.Text = typed;
        }
        finally
        {
            _filteringUnitCombo = false;
        }
    }

    private static bool IsPointerOverUnitSuggestion()
    {
        // The pointer resting on a suggestion counts as highlighting it:
        // Enter then accepts the pointed item through the ComboBox's own
        // handling. Only this cell's drop-down can be open while the cell
        // is being edited, so a ComboBoxItem under the pointer is
        // necessarily one of its suggestions.
        return Mouse.DirectlyOver is DependencyObject over
            && FindAncestor<ComboBoxItem>(over) != null;
    }

    private void InitCategories(string? preferredValue = null)
	{
		preferredValue ??= GetComboKey(CmbCategory);

		// Owner semantics (2026-11-17): the Category combo lists the
		// categories managed through the Category Card window (CatWin). The
		// trace showed CatWin's cards live in material_groups — the
		// product_categories table behind the old source is orphaned (its
		// repository has no callers and no window populates it), so the combo
		// now reads the SAME MatRepo.LoadGroups() source CatWin manages,
		// presented in CatWin's tree order (parents before their children).
		// The selection still persists to materials.category, and the
		// stored-value fallback below keeps a legacy value (e.g. an old
		// product_categories id) displayed instead of silently cleared.
		List<MatRepo.MatGroupRec> cards = new();
		try
		{
			cards = MatRepo.LoadGroups() ?? new();
		}
		catch (Exception ex)
		{
			AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.InitCategories");
			cards = new();
		}

		var isArabic = string.Equals(App.CurrentLanguageCode?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
		_categoryOptions = OrderCardsAsTree(cards)
			.Where(card => !string.IsNullOrWhiteSpace(card.Code))
			.Select(card =>
			{
				var name = isArabic
					? (string.IsNullOrWhiteSpace(card.Name) ? card.NameEn : card.Name)
					: (string.IsNullOrWhiteSpace(card.NameEn) ? card.Name : card.NameEn);
				var display = string.IsNullOrWhiteSpace(name)
					? card.Code
					: $"{card.Code} - {name}";
				return new MatCategoryOption(card.Code, display, card.Code, card.Name, card.NameEn);
			})
			.ToList();

		if (!string.IsNullOrWhiteSpace(preferredValue) && !_categoryOptions.Any(option =>
				string.Equals(option.Key, preferredValue, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(option.Code, preferredValue, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(option.Name, preferredValue, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(option.NameEn, preferredValue, StringComparison.OrdinalIgnoreCase)))
		{
			_categoryOptions.Add(new MatCategoryOption(preferredValue, preferredValue));
		}

		CmbCategory.ItemsSource = _categoryOptions;
		SetCategory(preferredValue);
	}

	/// <summary>
	/// Orders group/category cards the way CatWin's tree presents them: each
	/// root followed by its children (depth-first, siblings in store order).
	/// Cards whose parent is missing from the set keep their store order at
	/// the end instead of being dropped.
	/// </summary>
	private static List<MatRepo.MatGroupRec> OrderCardsAsTree(List<MatRepo.MatGroupRec> cards)
	{
		var alive = cards.Where(c => c != null && !c.IsDeleted).ToList();
		var codes = new HashSet<string>(
			alive.Select(c => c.Code).Where(c => !string.IsNullOrWhiteSpace(c)),
			StringComparer.OrdinalIgnoreCase);
		var ordered = new List<MatRepo.MatGroupRec>();
		var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void Place(MatRepo.MatGroupRec card)
		{
			if (!placed.Add(card.Code)) return;
			ordered.Add(card);
			foreach (var child in alive.Where(c =>
					!string.IsNullOrWhiteSpace(c.ParentCode)
					&& string.Equals(c.ParentCode, card.Code, StringComparison.OrdinalIgnoreCase)))
				Place(child);
		}

		foreach (var root in alive.Where(c =>
				string.IsNullOrWhiteSpace(c.ParentCode) || !codes.Contains(c.ParentCode!)))
			Place(root);
		foreach (var rest in alive)
			Place(rest);

		return ordered;
	}

    private void SetCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CmbCategory.SelectedIndex = -1;
            return;
        }

        var option = _categoryOptions.FirstOrDefault(item =>
            string.Equals(item.Key, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Code, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.NameEn, value, StringComparison.OrdinalIgnoreCase));

        CmbCategory.SelectedValue = option?.Key;
    }

    // ----------------------------------------------------------------
    //  Color palette - user-selectable, persisted in materials.color
    // ----------------------------------------------------------------
    private static readonly string[] ColorKeys =
    {
        "None", "White", "Black", "Red", "Green", "Blue",
        "Yellow", "Orange", "Brown", "Gray", "Silver", "Gold"
    };

    private static List<MatComboOption> BuildColorOptions() =>
        ColorKeys
            .Select(k => new MatComboOption(k, L("MatCard.Color." + k, k)))
            .ToList();

    // ----------------------------------------------------------------
    //  Warehouses - default-warehouse combo
    // ----------------------------------------------------------------
    private void InitWarehouses()
    {
        var warehouses = new List<MatComboOption>();
        try
        {
            var list = MatRepo.LoadWarehouses();
            if (list != null)
            {
                var lang = App.CurrentLanguageCode?.Trim().ToLowerInvariant();
                foreach (var w in list)
                {
                    if (w == null || w.IsDeleted) continue;
                    var code = w.Code.ToString(CultureInfo.InvariantCulture);
                    var name = lang == "ar"
                        ? (string.IsNullOrWhiteSpace(w.Name) ? w.NameEn : w.Name)
                        : (string.IsNullOrWhiteSpace(w.NameEn) ? w.Name : w.NameEn);
                    var display = string.IsNullOrWhiteSpace(name)
                        ? code
                        : code + " - " + name;
                    warehouses.Add(new MatComboOption(code, display));
                }
            }
        }
        catch (Exception exL597)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL597, "MatCard.xaml.InitWarehouses");
            // The warehouse store may be unavailable; the combo stays editable
            // so the user can still type a warehouse code manually.
        }
        CmbDefaultWarehouse.ItemsSource = warehouses;
    }

    private void InitGroups(string? preferredValue = null)
    {
        // Owner semantics (2026-11-17): the Group combo lists the
        // operational material groups managed through the Material Groups
        // window (MatGroupWin) — MatRepo.LoadGroups(). The old wholesale
        // "[Auto]" fallback (every distinct materials.group_code as a
        // competing source) is removed; a stored value missing from the
        // list survives only as a single fallback entry, mirroring
        // InitCategories, so it displays and re-saves instead of clearing.
        preferredValue ??= GetComboKey(CmbGroup);

        var groups = new List<MatComboOption>();
        try
        {
            var list = MatRepo.LoadGroups();
            if (list != null)
            {
                var lang = App.CurrentLanguageCode?.Trim().ToLowerInvariant();
                foreach (var item in list)
                {
                    if (item == null || item.IsDeleted) continue;
                    var code = item.Code;
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var name = lang == "ar"
                        ? (string.IsNullOrWhiteSpace(item.Name) ? item.NameEn : item.Name)
                        : (string.IsNullOrWhiteSpace(item.NameEn) ? item.Name : item.NameEn);
                    var display = string.IsNullOrWhiteSpace(name) ? code : code + " - " + name;
                    groups.Add(new MatComboOption(code, display));
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.InitGroups");
        }

        if (!string.IsNullOrWhiteSpace(preferredValue)
            && !groups.Any(g => string.Equals(g.Key, preferredValue, StringComparison.OrdinalIgnoreCase)))
        {
            groups.Add(new MatComboOption(preferredValue, preferredValue));
        }

        CmbGroup.ItemsSource = groups;
        SetCombo(CmbGroup, preferredValue);
    }

    private void RefreshComboDisplays()
    {
        RefreshStaticCombo(CmbItemType, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stock"]    = L("MatCard.ItemType.Stock",    "Stock"),
            ["Service"]  = L("MatCard.ItemType.Service",  "Service"),
            ["NonStock"] = L("MatCard.ItemType.NonStock", "Non-stock"),
            ["Kit"]      = L("MatCard.ItemType.Kit",      "Kit")
        });

        RefreshStaticCombo(CmbStatus, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Active"]   = L("MatCard.Status.Active",   "Active"),
            ["Inactive"] = L("MatCard.Status.Inactive", "Inactive")
        });

        RefreshStaticCombo(CmbCostMethod, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WeightedAvg"]  = L("MatCard.CostMethod.WeightedAvg",  "Weighted Average"),
            ["FIFO"]         = L("MatCard.CostMethod.FIFO",         "FIFO"),
            ["LIFO"]         = L("MatCard.CostMethod.LIFO",         "LIFO"),
            ["Standard"]     = L("MatCard.CostMethod.Standard",     "Standard"),
            ["LastPurchase"] = L("MatCard.CostMethod.LastPurchase", "Last Purchase")
        });

        var colorLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ColorKeys)
            colorLabels[key] = L("MatCard.Color." + key, key);
        RefreshStaticCombo(CmbColor, colorLabels);
    }

    private static void RefreshStaticCombo(ComboBox combo, IReadOnlyDictionary<string, string> labels)
    {
        var selected = GetComboKey(combo);
        var options = labels.Select(x => new MatComboOption(x.Key, x.Value)).ToList();
        combo.ItemsSource = options;
        if (!string.IsNullOrWhiteSpace(selected)) SetCombo(combo, selected);
    }

    // ================================================================
    //  Grid row management
    // ================================================================
    private void EnsureUnitRows(int count)
    {
        while (_units.Count < count)
            _units.Add(new MatUnitRow { LineNo = _units.Count + 1 });
    }

    private void RenumberUnits()
    {
        for (var i = 0; i < _units.Count; i++)
            _units[i].LineNo = i + 1;
    }

    private void ReloadCache()
    {
        try
        {
            _cache = MatRepo.LoadAll() ?? new();
        }
        catch (Exception exL721)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL721, "MatCard.xaml.ReloadCache");
            _cache = new();
        }
    }

    private void LoadCurrent()
    {
        if (_index < 0 || _index >= _cache.Count)
        {
            ClearForm();
            return;
        }

        try
        {
            MatRepo.MatRec? full = null;
            try { full = MatRepo.LoadById(_cache[_index].Id, true); }
            catch (Exception exL739)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL739, "MatCard.xaml.LoadCurrent");
            }

            var m = full ?? _cache[_index];
            _loaded = m;
            _currentId = m.Id;
            _isNew = false;
            PublishInfoSelection();

            BindToUi(m);
            SetEditMode(false);
            UpdateNav();
            UpdateStatusBar();
            TxtMeta.Text = "ID: " + m.Id;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.LoadCurrent");
            MessageBox.Show(this,
                "Unable to load material:\n" + ex.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            ClearForm();
            SetEditMode(false);
        }
    }

    private void UpdateNav()
    {
        // The navigator now carries the item-number field where the old
        // "1 / 4" position text used to sit (owner directive 2026-11-17):
        // keep it showing the current record's number on every navigation.
        try
        {
            TxtItemNo.Text = _cache.Count == 0 || _index < 0 || _index >= _cache.Count
                ? string.Empty
                : _cache[_index].Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exL769)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL769, "MatCard.xaml.UpdateNav");
        }
    }

    /// <summary>
    /// Enter in the navigator's item-number field jumps straight to the record
    /// carrying that number inside the same record set the navigator pages
    /// through. A number with no matching record leaves the current record in
    /// place and surfaces a catalog-coded warning (no silent failure).
    /// </summary>
    private void TxtItemNo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        e.Handled = true;
        try
        {
            var text = TxtItemNo.Text?.Trim();
            if (string.IsNullOrEmpty(text)
                || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                UpdateNav();
                return;
            }

            var idx = _cache.FindIndex(x => x.Id == number);
            if (idx < 0)
            {
                AppHost.Errors.Report(ErrorCodes.NavigationFailed,
                    new InvalidOperationException($"No material record carries number {number}."),
                    "MatCard.xaml.TxtItemNo_KeyDown");
                ShowWarn("MatCard.RecordNumberNotFound");
                UpdateNav();
                return;
            }

            _index = idx;
            LoadCurrent();
        }
        catch (Exception exNav)
        {
            AppHost.Errors.Report(ErrorCodes.NavigationFailed, exNav, "MatCard.xaml.TxtItemNo_KeyDown");
        }
    }

    private void ShowSection(int index)
    {
        if (PanelDesc == null) return;

        PanelDesc.Visibility     = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelUnits.Visibility    = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelStock.Visibility    = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelAccounts.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelExtra.Visibility    = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        var on  = (Style)FindResource("SecBtnOn");
        var off = (Style)FindResource("SecBtn");

        NavDesc.Style     = index == 0 ? on : off;
        NavUnits.Style    = index == 1 ? on : off;
        NavStock.Style    = index == 2 ? on : off;
        NavAccounts.Style = index == 3 ? on : off;
        NavExtra.Style    = index == 4 ? on : off;
    }

    private void NavSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string tag) return;
        if (!int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var section)) return;
        ShowSection(section);
    }

    // ================================================================
    //  Clear / Bind / Collect
    // ================================================================
    private void ClearForm()
    {
        _currentId = 0;
        _loaded = null;

        TxtCode.Clear();
        TxtItemNo.Clear();
        TxtName.Clear();
        CmbCategory.SelectedIndex = -1;
        TxtBrand.Clear();
        TxtModel.Clear();
        TxtSizeSpec.Clear();
        TxtManufacturer.Clear();
        TxtOriginCountry.Clear();
        TxtSource.Clear();
        TxtAltCode.Clear();
        TxtGtin.Clear();
        TxtSupplierCode.Clear();
        TxtNotes.Clear();
        TxtMinQty.Text = "0";
        TxtMaxQty.Text = "0";
        TxtCost.Text = "0";
        TxtTaxRate.Text = "0";
        TxtQtyOnHand.Text = "0";
        TxtReorderQty.Text = "0";
        TxtSafetyStock.Text = "0";
        TxtShelfBin.Clear();
        TxtLeadTimeDays.Text = "0";
        TxtWeightNet.Text = "0";
        TxtWeightGross.Text = "0";
        TxtShelfLifeDays.Text = "0";
        TxtImagePath.Clear();
        TxtAccInventory.Clear();
        TxtAccSales.Clear();
        TxtAccSalesReturn.Clear();
        TxtAccCogs.Clear();
        TxtAccPurchase.Clear();
        TxtAccPurchaseReturn.Clear();
        TxtAccExpense.Clear();
        TxtAccWip.Clear();

        ChkInactive.IsChecked = false;
        ChkIsStocked.IsChecked = true;
        ChkIsPurchased.IsChecked = true;
        ChkIsSold.IsChecked = true;
        ChkIsProduced.IsChecked = false;
        ChkIsService.IsChecked = false;
        ChkHasSerial.IsChecked = false;
        ChkHasExpiry.IsChecked = false;
        ChkHasBatch.IsChecked = false;
        ChkIsTaxable.IsChecked = false;
        ChkPriceIncludesTax.IsChecked = false;
        ChkAllowNegative.IsChecked = false;
        ChkTrackLocation.IsChecked = false;
        ChkQualityCheck.IsChecked = false;
        ChkAllowSale.IsChecked = true;
        ChkAllowPurchase.IsChecked = true;
        ChkAllowReturn.IsChecked = true;
        ChkSalesBlocked.IsChecked = false;

        SetCombo(CmbStatus, "Active");
        SetCombo(CmbItemType, "Stock");
        SetCombo(CmbColor, "None");
        CmbGroup.SelectedIndex = -1;
        CmbGroup.Text = "";
        CmbAbcClass.SelectedIndex = -1;
        CmbPriceCurrency.SelectedIndex = -1;
        CmbCostMethod.SelectedIndex = -1;
        CmbDefaultWarehouse.SelectedIndex = -1;
        CmbDefaultWarehouse.Text = "";

        _units.Clear();
        EnsureUnitRows(UnitWorkspaceRows);
        _attrs.Clear();
        ImgPreview.Source = null;
        TxtMeta.Text = "";
    }

    private static decimal D(string? value)
    {
        if (decimal.TryParse(value?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        if (decimal.TryParse(value?.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out result))
            return result;
        return 0;
    }

    private static int I(string? value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static string S(string? value) => value?.Trim() ?? "";

    private static string? SN(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void BindToUi(MatRepo.MatRec m)
    {
        TxtCode.Text = m.Code;
        TxtItemNo.Text = m.Id > 0 ? m.Id.ToString(CultureInfo.InvariantCulture) : "";
        TxtName.Text = m.Name;
        SetCombo(CmbGroup, m.GroupCode);
        SetCombo(CmbStatus, m.Status);
        SetCombo(CmbItemType, m.ItemType);
        ChkInactive.IsChecked =
            string.Equals(m.Status, "Inactive", StringComparison.OrdinalIgnoreCase);
        SetCategory(m.Category);
        TxtBrand.Text = m.Brand ?? "";
        TxtModel.Text = m.Model ?? "";
        SetCombo(CmbColor, string.IsNullOrWhiteSpace(m.Color) ? "None" : m.Color);
        TxtSizeSpec.Text = m.SizeSpec ?? "";
        TxtManufacturer.Text = m.Manufacturer ?? "";
        TxtOriginCountry.Text = m.OriginCountry ?? "";
        TxtSource.Text = m.Source ?? "";
        TxtAltCode.Text = m.AltCode ?? "";
        TxtGtin.Text = m.Gtin ?? "";
        TxtSupplierCode.Text = m.SupplierCode ?? "";
        SetCombo(CmbAbcClass, m.AbcClass);
        TxtNotes.Text = m.Notes ?? "";

        ChkIsStocked.IsChecked = m.IsStocked;
        ChkIsPurchased.IsChecked = m.IsPurchased;
        ChkIsSold.IsChecked = m.IsSold;
        ChkIsProduced.IsChecked = m.IsProduced;
        ChkIsService.IsChecked = m.IsService;

        TxtMinQty.Text = m.MinQty.ToString(CultureInfo.InvariantCulture);
        TxtMaxQty.Text = m.MaxQty.ToString(CultureInfo.InvariantCulture);
        SetCombo(CmbPriceCurrency, m.PriceCurrency);
        TxtCost.Text = m.Cost.ToString(CultureInfo.InvariantCulture);
        SetCombo(CmbCostMethod, m.CostMethod);
        TxtTaxRate.Text = m.TaxRate.ToString(CultureInfo.InvariantCulture);
        ChkIsTaxable.IsChecked = m.IsTaxable;
        ChkPriceIncludesTax.IsChecked = m.PriceIncludesTax;
        ChkHasSerial.IsChecked = m.HasSerial;
        ChkHasExpiry.IsChecked = m.HasExpiry;
        ChkHasBatch.IsChecked = m.HasBatch;
        TxtShelfLifeDays.Text = m.ShelfLifeDays.ToString(CultureInfo.InvariantCulture);

        _units.Clear();
        var restoredUnits = false;
        if (!string.IsNullOrWhiteSpace(m.UnitsJson))
        {
            try
            {
                var savedUnits = JsonSerializer.Deserialize<List<MatUnitRow>>(m.UnitsJson);
                if (savedUnits is { Count: > 0 })
                {
                    foreach (var unit in savedUnits)
                        _units.Add(unit);
                    RenumberUnits();
                    restoredUnits = true;
                }
            }
            catch (JsonException exL960)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL960, "MatCard.xaml.BindToUi");
                // Unrecognized payloads fall back to the legacy unit columns below.
            }
        }

        if (!restoredUnits)
        {
            decimal? N(decimal v) => v == 0 ? (decimal?)null : v;

            void AddUnit(string? name, decimal factor, string? barcode, bool first)
            {
                if (string.IsNullOrWhiteSpace(name) && !first) return;
                _units.Add(new MatUnitRow
                {
                    LineNo = _units.Count + 1,
                    UnitName = name ?? "",
                    Factor = factor > 0 ? factor : (decimal?)null,
                    Barcode = barcode ?? ""
                });
            }

            AddUnit(m.Unit1, m.Unit1Factor, m.Unit1Barcode, true);
            AddUnit(m.Unit2, m.Unit2Factor, m.Unit2Barcode, false);
            AddUnit(m.Unit3, m.Unit3Factor, m.Unit3Barcode, false);
            AddUnit(m.Unit4, m.Unit4Factor, m.Unit4Barcode, false);
            AddUnit(m.Unit5, m.Unit5Factor, m.Unit5Barcode, false);

            // Legacy price fields map onto unit #1
            var u1 = _units.FirstOrDefault();
            if (u1 != null)
            {
                u1.PriceSale        = N(m.PriceList);
                u1.PriceRetail      = N(m.PriceRetail);
                u1.PriceWholesale   = N(m.PriceWholesale);
                u1.PriceSpecial     = N(m.PriceSpecial);
                u1.PriceExport      = N(m.PriceExport);
                u1.PriceDistributor = N(m.PriceDistributor);
            }
        }

        EnsureUnitRows(UnitWorkspaceRows);

        TxtQtyOnHand.Text = m.QtyOnHand.ToString(CultureInfo.InvariantCulture);
        TxtReorderQty.Text = m.ReorderQty.ToString(CultureInfo.InvariantCulture);
        TxtSafetyStock.Text = m.SafetyStock.ToString(CultureInfo.InvariantCulture);
        SetCombo(CmbDefaultWarehouse, m.DefaultWarehouse);
        TxtShelfBin.Text = m.ShelfBin ?? "";
        TxtLeadTimeDays.Text = m.LeadTimeDays.ToString(CultureInfo.InvariantCulture);
        TxtWeightNet.Text = m.WeightNet.ToString(CultureInfo.InvariantCulture);
        TxtWeightGross.Text = m.WeightGross.ToString(CultureInfo.InvariantCulture);
        ChkAllowNegative.IsChecked = m.AllowNegative;
        ChkTrackLocation.IsChecked = m.TrackLocation;
        ChkQualityCheck.IsChecked = m.QualityCheckRequired;

        TxtAccInventory.Text = m.AccInventory ?? "";
        TxtAccSales.Text = m.AccSales ?? "";
        TxtAccSalesReturn.Text = m.AccSalesReturn ?? "";
        TxtAccCogs.Text = m.AccCogs ?? "";
        TxtAccPurchase.Text = m.AccPurchase ?? "";
        TxtAccPurchaseReturn.Text = m.AccPurchaseReturn ?? "";
        TxtAccExpense.Text = m.AccExpense ?? "";
        TxtAccWip.Text = m.AccWip ?? "";

        ChkAllowSale.IsChecked = m.AllowSale;
        ChkAllowPurchase.IsChecked = m.AllowPurchase;
        ChkAllowReturn.IsChecked = m.AllowReturn;
        ChkSalesBlocked.IsChecked = m.SalesBlocked;

        TxtImagePath.Text = m.ImagePath ?? "";
        LoadImage(m.ImagePath);

        _attrs.Clear();
        if (m.Attributes is { Count: > 0 })
        {
            foreach (var a in m.Attributes)
                _attrs.Add(a);
        }

        NormalizeServiceControls();
    }

    private void Inactive_Changed(object sender, RoutedEventArgs e)
    {
        if (CmbStatus == null || CmbStatus.ItemsSource == null) return;
        SetCombo(CmbStatus, ChkInactive.IsChecked == true ? "Inactive" : "Active");
    }

    private void Status_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var status = GetComboKey(CmbStatus);
        if (string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
            ChkInactive.IsChecked = false;
        else if (string.Equals(status, "Inactive", StringComparison.OrdinalIgnoreCase))
            ChkInactive.IsChecked = true;
    }

    // ----------------------------------------------------------------
    //  Service-material consistency (owner rule, 2026-11-17): the item
    //  type, the Service flag and the Stocked flag are three views of
    //  one fact — a service material is NON-STOCK. Each control's own
    //  gesture wins and brings the other two into agreement, so the
    //  card can never present (or save) a "service but stock-tracked"
    //  material. Collect applies the same normalization to the record
    //  itself, and the posting layer refuses stock effects for service
    //  materials regardless of which view a legacy row carries.
    // ----------------------------------------------------------------
    private bool _syncingServiceState;

    private void ItemType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingServiceState) return;
        _syncingServiceState = true;
        try
        {
            var isService = string.Equals(
                GetComboKey(CmbItemType), "Service", StringComparison.OrdinalIgnoreCase);
            ChkIsService.IsChecked = isService;
            if (isService) ChkIsStocked.IsChecked = false;
        }
        finally { _syncingServiceState = false; }
    }

    private void IsService_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingServiceState) return;
        _syncingServiceState = true;
        try
        {
            if (ChkIsService.IsChecked == true)
            {
                SetCombo(CmbItemType, "Service");
                ChkIsStocked.IsChecked = false;
            }
            else if (string.Equals(GetComboKey(CmbItemType), "Service", StringComparison.OrdinalIgnoreCase))
            {
                SetCombo(CmbItemType, "Stock");
            }
        }
        finally { _syncingServiceState = false; }
    }

    private void IsStocked_Checked(object sender, RoutedEventArgs e)
    {
        // XAML applies IsChecked="True" while InitializeComponent is still
        // constructing controls in document order. ChkIsService and/or
        // CmbItemType may not have been created when this handler first runs.
        if (_syncingServiceState || ChkIsService is null || CmbItemType is null)
            return;

        _syncingServiceState = true;
        try
        {
            // Explicitly marking the material as stocked ends service state.
            ChkIsService.IsChecked = false;
            if (string.Equals(GetComboKey(CmbItemType), "Service", StringComparison.OrdinalIgnoreCase))
                SetCombo(CmbItemType, "Stock");
        }
        finally { _syncingServiceState = false; }
    }

    /// <summary>
    /// Brings the three service controls into agreement for a record being
    /// displayed: a legacy row whose type says Service but whose flags
    /// disagree (or vice versa) is shown — and will re-save — as a service
    /// material: type Service, Service on, Stocked off.
    /// </summary>
    private void NormalizeServiceControls()
    {
        if (_syncingServiceState) return;
        var isService = ChkIsService.IsChecked == true
            || string.Equals(GetComboKey(CmbItemType), "Service", StringComparison.OrdinalIgnoreCase);
        if (!isService) return;
        _syncingServiceState = true;
        try
        {
            SetCombo(CmbItemType, "Service");
            ChkIsService.IsChecked = true;
            ChkIsStocked.IsChecked = false;
        }
        finally { _syncingServiceState = false; }
    }

    private MatRepo.MatRec Collect()
    {
        var m = _loaded != null ? CloneSparse(_loaded) : new MatRepo.MatRec();

        m.Id = _currentId;
        m.Code = S(TxtCode.Text);
        m.Name = S(TxtName.Text);
        m.GroupCode = SN(GetComboKey(CmbGroup));
        m.Status = GetComboKey(CmbStatus) ?? "Active";
        m.ItemType = GetComboKey(CmbItemType) ?? "Stock";
        m.Category = GetComboKey(CmbCategory);
        m.Brand = SN(TxtBrand.Text);
        m.Model = SN(TxtModel.Text);
        m.Color = GetComboKey(CmbColor);
        m.SizeSpec = SN(TxtSizeSpec.Text);
        m.Manufacturer = SN(TxtManufacturer.Text);
        m.OriginCountry = SN(TxtOriginCountry.Text);
        m.Source = SN(TxtSource.Text);
        m.AltCode = SN(TxtAltCode.Text);
        m.Gtin = SN(TxtGtin.Text);
        m.SupplierCode = SN(TxtSupplierCode.Text);
        m.AbcClass = SN(GetComboKey(CmbAbcClass));
        m.Notes = S(TxtNotes.Text);

        m.IsStocked = ChkIsStocked.IsChecked == true;
        m.IsPurchased = ChkIsPurchased.IsChecked == true;
        m.IsSold = ChkIsSold.IsChecked == true;
        m.IsProduced = ChkIsProduced.IsChecked == true;
        m.IsService = ChkIsService.IsChecked == true;

        // Service-material rule (owner, 2026-11-17): any service indication
        // normalizes all three stored views — a service material is
        // non-stock and must never reach the posting layer as tracked.
        if (string.Equals(m.ItemType, "Service", StringComparison.OrdinalIgnoreCase) || m.IsService)
        {
            m.ItemType = "Service";
            m.IsService = true;
            m.IsStocked = false;
        }

        m.MinQty = D(TxtMinQty.Text);
        m.MaxQty = D(TxtMaxQty.Text);
        m.PriceCurrency = S(GetComboKey(CmbPriceCurrency));
        m.Cost = D(TxtCost.Text);
        m.CostMethod = S(GetComboKey(CmbCostMethod));
        m.TaxRate = D(TxtTaxRate.Text);
        m.IsTaxable = ChkIsTaxable.IsChecked == true;
        m.PriceIncludesTax = ChkPriceIncludesTax.IsChecked == true;
        m.HasSerial = ChkHasSerial.IsChecked == true;
        m.HasExpiry = ChkHasExpiry.IsChecked == true;
        m.HasBatch = ChkHasBatch.IsChecked == true;
        m.ShelfLifeDays = I(TxtShelfLifeDays.Text);

        var active = _units.Where(u => u.HasContent).ToList();
        for (var i = 0; i < active.Count; i++) active[i].LineNo = i + 1;

        var u1 = active.ElementAtOrDefault(0);
        var u2 = active.ElementAtOrDefault(1);
        var u3 = active.ElementAtOrDefault(2);
        var u4 = active.ElementAtOrDefault(3);
        var u5 = active.ElementAtOrDefault(4);

        decimal Z(decimal? v) => v ?? 0m;

        m.Unit1 = u1?.UnitName ?? ""; m.Unit1Factor = Z(u1?.Factor); m.Unit1Barcode = SN(u1?.Barcode);
		m.Unit2 = u2?.UnitName ?? ""; m.Unit2Factor = Z(u2?.Factor); m.Unit2Barcode = SN(u2?.Barcode);
		m.Unit3 = u3?.UnitName ?? ""; m.Unit3Factor = Z(u3?.Factor); m.Unit3Barcode = SN(u3?.Barcode);
		m.Unit4 = u4?.UnitName ?? ""; m.Unit4Factor = Z(u4?.Factor); m.Unit4Barcode = SN(u4?.Barcode);
		m.Unit5 = u5?.UnitName ?? ""; m.Unit5Factor = Z(u5?.Factor); m.Unit5Barcode = SN(u5?.Barcode);
        m.DefaultUnit = m.Unit1;

        m.PriceList        = Z(u1?.PriceSale);
        m.PriceRetail      = Z(u1?.PriceRetail);
        m.PriceWholesale   = Z(u1?.PriceWholesale);
        m.PriceSpecial     = Z(u1?.PriceSpecial);
        m.PriceExport      = Z(u1?.PriceExport);
        m.PriceDistributor = Z(u1?.PriceDistributor);
        // PriceMin has no field on this card: it is preserved from the
        // loaded record via CloneSparse. (It was previously overwritten
        // with the constant 0 on every save — silent data loss.)
        m.UnitsJson = JsonSerializer.Serialize(active);

        // The section's Cost field (TxtCost, already collected above) is
        // authoritative for materials.cost. Unit 1's purchase price keeps
        // its own store (units_json) — it previously overwrote the Cost
        // field's value on every save, so the field never round-tripped
        // whenever the grid's purchase cell was filled.

        m.ReorderQty = D(TxtReorderQty.Text);
        m.SafetyStock = D(TxtSafetyStock.Text);
        m.DefaultWarehouse = SN(GetComboKey(CmbDefaultWarehouse));
        m.ShelfBin = SN(TxtShelfBin.Text);
        m.LeadTimeDays = I(TxtLeadTimeDays.Text);
        m.WeightNet = D(TxtWeightNet.Text);
        m.WeightGross = D(TxtWeightGross.Text);
        m.AllowNegative = ChkAllowNegative.IsChecked == true;
        m.TrackLocation = ChkTrackLocation.IsChecked == true;
        m.QualityCheckRequired = ChkQualityCheck.IsChecked == true;

        m.AccInventory = SN(TxtAccInventory.Text);
        m.AccSales = SN(TxtAccSales.Text);
        m.AccSalesReturn = SN(TxtAccSalesReturn.Text);
        m.AccCogs = SN(TxtAccCogs.Text);
        m.AccPurchase = SN(TxtAccPurchase.Text);
        m.AccPurchaseReturn = SN(TxtAccPurchaseReturn.Text);
        m.AccExpense = SN(TxtAccExpense.Text);
        m.AccWip = SN(TxtAccWip.Text);

        m.AllowSale = ChkAllowSale.IsChecked == true;
        m.AllowPurchase = ChkAllowPurchase.IsChecked == true;
        m.AllowReturn = ChkAllowReturn.IsChecked == true;
        m.SalesBlocked = ChkSalesBlocked.IsChecked == true;

        m.ImagePath = SN(TxtImagePath.Text);

        // Renumber attribute order by grid position (same treatment as the
        // unit rows' LineNo): rows added in the grid carry SortOrder 0 and
        // would otherwise jump to the top on reload, which orders by
        // sort_order.
        var attrs = _attrs.Where(a => !string.IsNullOrWhiteSpace(a.Key)).ToList();
        for (var i = 0; i < attrs.Count; i++) attrs[i].SortOrder = i + 1;
        m.Attributes = attrs;

        return m;
    }

    private static MatRepo.MatRec CloneSparse(MatRepo.MatRec src)
    {
        return new MatRepo.MatRec
        {
            Id = src.Id,
            ShortName = src.ShortName, SearchName = src.SearchName,
            OldCode = src.OldCode, CustomerCode = src.CustomerCode,
            ManufacturerCode = src.ManufacturerCode, HsCode = src.HsCode,
            SkuExternal = src.SkuExternal, PosName = src.PosName,
            SubCategory = src.SubCategory, Grade = src.Grade,
            Class1 = src.Class1, Class2 = src.Class2, Class3 = src.Class3,
            Class4 = src.Class4, Class5 = src.Class5,
            ProductLine = src.ProductLine,
            Season = src.Season, LifecycleStatus = src.LifecycleStatus,
            MakeOrBuy = src.MakeOrBuy, StockStage = src.StockStage,
            WeightUnit = src.WeightUnit, Volume = src.Volume, VolumeUnit = src.VolumeUnit,
            Length = src.Length, Width = src.Width, Height = src.Height, DimUnit = src.DimUnit,
            Area = src.Area, AreaUnit = src.AreaUnit,
            PackQty = src.PackQty, UnitsPerParcel = src.UnitsPerParcel,
            BrandOwner = src.BrandOwner, WarrantyMonths = src.WarrantyMonths,
            CatalogueRef = src.CatalogueRef, WebUrl = src.WebUrl, Tags = src.Tags,
            PurchaseUnit = src.PurchaseUnit, SalesUnit = src.SalesUnit,
            StandardCost = src.StandardCost, LastPurchaseCost = src.LastPurchaseCost,
            AvgCost = src.AvgCost, CostCurrency = src.CostCurrency,
            OverheadPct = src.OverheadPct, LandedCost = src.LandedCost,
            LastPurchaseDate = src.LastPurchaseDate,
            PriceMin = src.PriceMin,
            PriceOnline = src.PriceOnline, MarginTargetPct = src.MarginTargetPct,
            DiscountMaxPct = src.DiscountMaxPct, AllowInvoiceDisc = src.AllowInvoiceDisc,
            LotSize = src.LotSize, OrderMultiple = src.OrderMultiple,
            EconomicOrderQty = src.EconomicOrderQty,
            ReplenishmentMethod = src.ReplenishmentMethod,
            AlertBeforeExpiryDays = src.AlertBeforeExpiryDays, HasLot = src.HasLot,
            TaxCode = src.TaxCode, TaxAccount = src.TaxAccount, TaxType = src.TaxType,
            IsZeroRated = src.IsZeroRated, CommissionPct = src.CommissionPct,
            BonusQty = src.BonusQty, BonusItemCode = src.BonusItemCode,
            MinSaleQty = src.MinSaleQty, MaxSaleQty = src.MaxSaleQty,
            ImagePath2 = src.ImagePath2, AttachmentPath = src.AttachmentPath,
            Description = src.Description,
            InternalNotes = src.InternalNotes, SpecSheet = src.SpecSheet,
            LabelText = src.LabelText, UnitsJson = src.UnitsJson,
            // QtyOnHand is stock-maintained and read-only in this window, but
            // MatRepo.Save binds it — omitting it here made every card save
            // overwrite materials.qty_on_hand with 0 (data loss).
            QtyOnHand = src.QtyOnHand,
            PricesJson = src.PricesJson, AttributesJson = src.AttributesJson,
            ExtraBarcodes = src.ExtraBarcodes?.ToList() ?? new(),
            PriceLines = src.PriceLines?.ToList() ?? new(),
            Suppliers = src.Suppliers?.ToList() ?? new()
        };
    }

    private static string? GetComboKey(ComboBox? combo)
    {
        if (combo == null) return null;
        if (combo.SelectedItem is MatComboOption option) return option.Key;
        if (combo.SelectedItem is MatCategoryOption categoryOption) return categoryOption.Key;
        if (combo.SelectedItem != null)
        {
            var selected = combo.SelectedItem.ToString();
            return string.IsNullOrWhiteSpace(selected) ? null : ExtractCode(selected);
        }
        return string.IsNullOrWhiteSpace(combo.Text) ? null : ExtractCode(combo.Text);
    }

    private static void SetCombo(ComboBox? combo, string? value)
    {
        if (combo == null) return;
        if (string.IsNullOrWhiteSpace(value)) { combo.SelectedIndex = -1; combo.Text = ""; return; }

        var key = value.Trim();
        var match = combo.Items.Cast<object>().FirstOrDefault(item =>
        {
            if (item is MatComboOption option)
                return string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase);
            if (item is MatCategoryOption cat)
                return string.Equals(cat.Key, key, StringComparison.OrdinalIgnoreCase);
            return string.Equals(item?.ToString(), key, StringComparison.OrdinalIgnoreCase);
        });

        if (match != null) { combo.SelectedItem = match; return; }

        // Stored-value fallback (same pattern as InitCategories/InitGroups):
        // a value outside the built-in option list — e.g. a currency, status
        // or color written by an import or another module — must still
        // display and round-trip. On a non-editable combo the old behavior
        // (SelectedIndex=-1 + Text) showed nothing and Collect then saved
        // null, silently clearing the stored value on every save.
        if (combo.ItemsSource is IEnumerable<object> current
            && current.All(i => i is MatComboOption))
        {
            var options = current.Cast<MatComboOption>().ToList();
            var fallback = new MatComboOption(key, key);
            options.Add(fallback);
            combo.ItemsSource = options;
            combo.SelectedItem = fallback;
            return;
        }

        combo.SelectedIndex = -1;
        combo.Text = key;
    }

    private static string? ExtractCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var index = value.IndexOf(" -", StringComparison.Ordinal);
        return index > 0 ? value[..index].Trim() : value;
    }

    // ================================================================
    //  Edit mode
    // ================================================================
    private void SetEditMode(bool editing)
    {
        _isEditing = editing;
        var readOnly = !editing;

        foreach (var tb in FindVisualChildren<TextBox>(this))
        {
            if (tb.Name == "TxtQtyOnHand") { tb.IsReadOnly = true; continue; }
            if (tb.Name == "TxtCode" && !_isNew) { tb.IsReadOnly = true; continue; }
            if (tb.Name == "TxtGoto") { tb.IsReadOnly = false; continue; }
            if (tb.Name == "TxtItemNo") { tb.IsReadOnly = false; continue; }
            tb.IsReadOnly = readOnly;
        }

        foreach (var cb in FindVisualChildren<ComboBox>(this))
            cb.IsEnabled = editing;

        foreach (var ck in FindVisualChildren<CheckBox>(this))
            ck.IsEnabled = editing;

        DgUnits.IsReadOnly = readOnly;
        DgAttributes.IsReadOnly = readOnly;

        BtnSave.IsEnabled = editing;
        BtnUnitAdd.IsEnabled = editing;
        BtnUnitRemove.IsEnabled = editing;
        BtnCancel.IsEnabled = editing;
        BtnEdit.IsEnabled = !editing && _currentId != 0;
        BtnDelete.IsEnabled = !editing && _currentId != 0;
        BtnNew.IsEnabled = !editing;
    }

    private void AddUnit_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing) return;
        DgUnits.CommitEdit(DataGridEditingUnit.Cell, true);
        DgUnits.CommitEdit(DataGridEditingUnit.Row, true);

        var row = new MatUnitRow { LineNo = _units.Count + 1 };
        _units.Add(row);
        DgUnits.SelectedItem = row;
        DgUnits.ScrollIntoView(row);

        if (DgUnits.Columns.Count > 1)
        {
            DgUnits.CurrentCell = new DataGridCellInfo(row, DgUnits.Columns[1]);
            DgUnits.BeginEdit();
        }
    }

    private void RemoveUnit_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing || DgUnits.SelectedItem is not MatUnitRow selected) return;
        DgUnits.CommitEdit(DataGridEditingUnit.Cell, true);
        DgUnits.CommitEdit(DataGridEditingUnit.Row, true);
        _units.Remove(selected);
        EnsureUnitRows(UnitWorkspaceRows);
        RenumberUnits();
    }

    // ================================================================
    //  Commands
    // ================================================================
    private void New_Click(object sender, RoutedEventArgs e)
    {
        _isNew = true;
        _currentId = 0;
        _loaded = null;
        ClearForm();
        try
        {
            TxtCode.Text = ToWesternDigits(MatRepo.NextCode());
        }
        catch (Exception exL1320)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL1320, "MatCard.xaml.New_Click");
            TxtCode.Clear();
            SetEditMode(false);
            MessageBox.Show(this,
                L("MatCard.ErrNumberGeneration", "A new material number could not be generated. Check the company database and numbering configuration, then try again."),
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        TxtCode.IsReadOnly = false;
        SetEditMode(true);
        ShowSection(0);
        TxtName.Focus();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId == 0) return;
        SetEditMode(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isNew) { ClearForm(); SetEditMode(false); }
        else LoadCurrent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditing) return;

        if (string.IsNullOrWhiteSpace(TxtCode.Text) ||
            string.IsNullOrWhiteSpace(TxtName.Text))
        {
            MessageBox.Show(this,
                L("MatCard.ErrRequired", "Code and name are required."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (!TryCommitGridEdits()) return;

            var record = Collect();
            var saved = MatRepo.Save(record, _isNew);

            _isNew = false;
            _currentId = saved.Id;
            ReloadCache();
            InitUnitSuggestions();
            _index = Math.Max(0, _cache.FindIndex(x => x.Id == saved.Id));
            LoadCurrent();

            MessageBox.Show(this,
                L("MatCard.Saved", "Saved successfully."),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.Save_Click");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryCommitGridEdits()
    {
        if (DgUnits.CommitEdit(DataGridEditingUnit.Cell, true)
            && DgUnits.CommitEdit(DataGridEditingUnit.Row, true)
            && DgAttributes.CommitEdit(DataGridEditingUnit.Cell, true)
            && DgAttributes.CommitEdit(DataGridEditingUnit.Row, true))
            return true;

        MessageBox.Show(this,
            L("MatCard.ErrGridValidation", "Please correct the invalid table values before saving."),
            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void ExcelExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryCommitGridEdits()) return;

            var record = Collect();
            var dialog = new SaveFileDialog
            {
                Title = "Export Material Card",
                Filter = "Excel Workbook (.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(record.Code) ? "Material.xlsx" : record.Code + ".xlsx"
            };

            if (dialog.ShowDialog(this) != true) return;
            MaterialExcelExchange.Export(dialog.FileName, record);
        }
        catch (Exception ex) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.ExcelExport_Click"); MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExcelImport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import Material Card",
                Filter = "Excel Workbook (.xlsx)|*.xlsx",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true) return;

            var imported = MaterialExcelExchange.Import(dialog.FileName);
            if (imported == null)
            {
                MessageBox.Show(this, "The workbook does not contain a valid ShouTech Material Card map.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existing = imported.Id > 0 ? MatRepo.LoadById(imported.Id, true) : null;
            if (existing == null && !string.IsNullOrWhiteSpace(imported.Code))
                existing = MatRepo.LoadAll().FirstOrDefault(x => string.Equals(x.Code, imported.Code, StringComparison.OrdinalIgnoreCase));

            imported.Id = existing?.Id ?? 0;
            _currentId = imported.Id;
            _isNew = existing == null;
            _loaded = imported;
            _isEditing = true;

            LoadCurrent();

            MessageBox.Show(this,
                _isNew
                    ? "Material imported into the form. Review it and click Save to create the material."
                    : "Material imported into the form. Review the changes and click Save to update the material.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.ExcelImport_Click"); MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId == 0) return;

        if (MessageBox.Show(this,
                L("MatCard.ConfirmDelete", "Delete or disable this material?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            MatRepo.SoftDelete(_currentId);
            ReloadCache();
            if (_cache.Count == 0) { ClearForm(); SetEditMode(false); }
            else { _index = Math.Min(_index, _cache.Count - 1); LoadCurrent(); }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MatCard.xaml.Delete_Click");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NavFirst_Click(object sender, RoutedEventArgs e)
    {
        if (_cache.Count == 0) return;
        _index = 0;
        LoadCurrent();
    }

    private void NavPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_index > 0) { _index--; LoadCurrent(); }
    }

    private void NavPrevTen_Click(object sender, RoutedEventArgs e)
    {
        if (_cache.Count == 0) return;
        _index = Math.Max(0, _index - 10);
        LoadCurrent();
    }

    private void NavNext_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _cache.Count - 1) { _index++; LoadCurrent(); }
    }

    private void NavNextTen_Click(object sender, RoutedEventArgs e)
    {
        if (_cache.Count == 0) return;
        _index = Math.Min(_cache.Count - 1, _index + 10);
        LoadCurrent();
    }

    private void NavLast_Click(object sender, RoutedEventArgs e)
    {
        if (_cache.Count == 0) return;
        _index = _cache.Count - 1;
        LoadCurrent();
    }

    // ================================================================
    //  Quick navigation
    // ================================================================
    private void Goto_Click(object sender, RoutedEventArgs e)
    {
        NavigateToQuery(TxtGoto.Text);
    }

    private void TxtGoto_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToQuery(TxtGoto.Text);
            e.Handled = true;
        }
    }

    private void NavigateToQuery(string? query)
	{
		var q = (query ?? "").Trim();
		if (q.Length == 0) return;

		// Try direct match on Code first
		var idx = _cache.FindIndex(x =>
			string.Equals(x.Code, q, StringComparison.OrdinalIgnoreCase));
		if (idx >= 0)
		{
			_index = idx;
			LoadCurrent();
			TxtCode.Focus();
			return;
		}

		// Otherwise open the search window
		var items = _cache.Select(m =>
		{
			string code     = m.Code         ?? "";
			string name     = m.Name       ?? "";
			string group    = m.GroupCode    ?? "";
			string gtin     = m.Gtin         ?? "";
			string altCode  = m.AltCode      ?? "";
			string supplier = m.SupplierCode ?? "";

			return new SerWinItem
			{
				Payload    = m,
				Id         = m.Id,
				SearchText = string.Join(" ", code, name, group, gtin, altCode, supplier),
				Values = new Dictionary<string, object?>
				{
					["Code"]   = m.Code     ?? "",
					["Name"]   = m.Name   ?? "",
					["Group"]  = m.GroupCode ?? "",
					["Status"] = m.Status   ?? ""
				}
			};
		}).ToList();

		var cols = new List<SerWinColumn>          // <-- was SerWinCol
		{
			new() { Key = "Code",   Header = L("MatCard.Code",        "Code"),   Width = 90 },
			new() { Key = "Name",   Header = L("MatCard.Name",        "Name"),   Fill = true },
			new() { Key = "Group",  Header = L("MatCard.Group",       "Group"),  Width = 90 },
			new() { Key = "Status", Header = L("MatCard.StatusLabel", "Status"), Width = 80 }
		};

		var picked = SerWin.Pick(
			owner: this,
			title: L("Inventory.SearchMaterial", "Search Material"),
			items: items,
			columns: cols,
			initialQuery: q);

		if (picked?.Payload is not MatRepo.MatRec mat) return;

		idx = _cache.FindIndex(x => x.Id == mat.Id);
		if (idx >= 0)
		{
			_index = idx;
			LoadCurrent();
			TxtCode.Focus();
		}
	}

    // ================================================================
    //  Color VisuId
    // ================================================================
    private void Color_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The selected color is persisted with the material record on save
        // (see Collect); no live UI side effects are required here.
    }

    // ================================================================
    //  Status bar (company + user)
    // ================================================================
    private void UpdateStatusBar()
    {
        try
        {
            var company = GetCurrentCompanyName();
            var user = GetCurrentUsername();

            if (TxtStatusCompany != null)
                TxtStatusCompany.Text = string.IsNullOrWhiteSpace(company) ? "\u2014" : company;
            if (TxtStatusUser != null)
                TxtStatusUser.Text = string.IsNullOrWhiteSpace(user) ? "\u2014" : user;
        }
        catch (Exception exL1609)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL1609, "MatCard.xaml.UpdateStatusBar");
        }
    }

    private static string GetCurrentCompanyName()
    {
        try
        {
            var t = typeof(AppState);
            foreach (var name in new[] { "CurrentCompanyName", "CompanyName" })
            {
                var p = t.GetProperty(name,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static);
                if (p != null)
                {
                    var v = p.GetValue(null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!;
                }
            }
        }
        catch (Exception exL1629)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL1629, "MatCard.xaml.GetCurrentCompanyName");
        }
        return AppState.CurrentCompanyCode ?? "";
    }

    private static string GetCurrentUsername()
    {
        try
        {
            // AppState.CurrentUser is a UserInfo OBJECT: stringifying it
            // renders the type name ("...Services.UserInfo") into the
            // status bar. Read the display name off the object instead —
            // the same value ShellContext.CurrentUserName exposes.
            var user = AppState.CurrentUser;
            if (user != null)
            {
                if (!string.IsNullOrWhiteSpace(user.FullName)) return user.FullName;
                if (!string.IsNullOrWhiteSpace(user.Username)) return user.Username;
            }
        }
        catch (Exception exL1654)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL1654, "MatCard.xaml.GetCurrentUsername");
        }
        return "";
    }

    // ================================================================
    //  Image
    // ================================================================
    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            TxtImagePath.Text = dialog.FileName;
            LoadImage(dialog.FileName);
        }
    }

    private void LoadImage(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ImgPreview.Source = null;
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            ImgPreview.Source = bitmap;
        }
        catch (Exception exL1692)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL1692, "MatCard.xaml.LoadImage");
            ImgPreview.Source = null;
        }
    }

    // ================================================================
    //  Print barcode
    // ================================================================
    private void PrintBarcode_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing)
        {
            DgUnits.CommitEdit(DataGridEditingUnit.Cell, true);
            DgUnits.CommitEdit(DataGridEditingUnit.Row, true);
        }

        var code = _units.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.Barcode))?.Barcode;
        if (string.IsNullOrWhiteSpace(code)) code = TxtGtin.Text;
        if (string.IsNullOrWhiteSpace(code)) code = TxtCode.Text;
        code = code?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show(this,
                L("MatCard.NoBarcode", "No barcode or code available to print."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = TxtName.Text.Trim();

        var price = _units.FirstOrDefault(u => u.PriceSale is > 0)?.PriceSale;
        var currency = GetComboKey(CmbPriceCurrency);

        var printed = ShouTech.App.Common.BarcodeLabelPrinter.Print(
            this,
            L("MatCard.BarcodePrint", "Print barcode") + " - " + code,
            name,
            code,
            ShouTech.App.Common.BarcodeLabelPrinter.FormatPrice(price, currency));

        if (printed)
        {
            MessageBox.Show(this,
                L("MatCard.BarcodePrinted", "The barcode label was sent to the printer."),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ================================================================
    //  Close
    // ================================================================
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void ApplyLocalizedText()
    {
        RefreshLocalizedTexts();
    }
}
// ====================================================================
//  MaterialExcelExchange - stub
//  Replace this class with the real implementation once a clean copy
//  is available. Currently used by MatCard.ExcelExport_Click and
//  MatCard.ExcelImport_Click.
// ====================================================================
internal static class MaterialExcelExchange
{
    public static void Export(string path, MatRepo.MatRec record)
    {
        MessageBox.Show(
            "Excel export is temporarily unavailable.",
            "Material Card",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public static MatRepo.MatRec? Import(string path)
    {
        MessageBox.Show(
            "Excel import is temporarily unavailable.",
            "Material Card",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return null;
    }
}