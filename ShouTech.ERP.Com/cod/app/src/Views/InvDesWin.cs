using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Invoice Designer (services phase B3): banded print designs for
/// the six printable document kinds, stored per company in
/// doc_designs (migration 116) and applied by InvPrn on the real
/// print/PDF path. The design surface edits the band toggles, the
/// lines-band columns (field / caption / width / visibility, with
/// reorder) and the header color; the preview renders a sample
/// document through InvPrn.BuildPreviewPage — the production page
/// builder, not a mock. Designs export/import as encrypted .stid
/// files (DesignFileSvc). QR placement is intentionally absent: the
/// repo has no QR encoder (recorded in SERVICES_GAP_INVENTORY).
/// </summary>
public sealed class InvDesWin : BaseWin
{
    protected override string? ScheduleTintName => "Mint";

    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly ComboBox _kind = new();
    private readonly ListBox _designs = new();
    private readonly TextBox _name = new();
    private readonly TextBox _headerColor = new();
    private readonly CheckBox _showAddress = new();
    private readonly CheckBox _showParty = new();
    private readonly CheckBox _showMeta = new();
    private readonly CheckBox _showTax = new();
    private readonly CheckBox _showWords = new();
    private readonly CheckBox _showNotes = new();
    private readonly CheckBox _showSignatures = new();
    private readonly CheckBox _showBarcode = new();
    private readonly DataGrid _columnsGrid = new();
    private readonly Border _previewHost = new();
    private readonly TextBlock _statusBar = new();

    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _defaultButton = new();
    private readonly Button _previewButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _importButton = new();
    private readonly Button _upButton = new();
    private readonly Button _downButton = new();
    private readonly Button _addColumnButton = new();
    private readonly Button _removeColumnButton = new();
    private readonly Button _closeButton = new();

    private readonly ObservableCollection<DocDesignColumn> _columns = new();
    private IReadOnlyList<DocDesignListItem> _items = Array.Empty<DocDesignListItem>();
    private int? _editingId;
    private Guid _companyId;
    private string? _companyAddress;

    public InvDesWin()
    {
        Width = 1180;
        Height = 720;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    // CA1863: cache CompositeFormat instances for repeated format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    private static string FormatLocalized(string pattern, params object?[] args)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;
        var compositeFormat = CompositeFormats.GetOrAdd(
            pattern, static value => CompositeFormat.Parse(value));
        return string.Format(CultureInfo.CurrentCulture, compositeFormat, args);
    }

    private static int UserId => int.TryParse(AppState.CurrentUser?.Id, out var id) ? id : 0;

    private sealed record KindChoice(DocDesignKind Kind, string Display)
    {
        public override string ToString() => Display;
    }

    private DocDesignKind SelectedKind =>
        _kind.SelectedItem is KindChoice choice ? choice.Kind : DocDesignKind.SalesInvoice;

    private string KindKey => DocDesignKinds.Key(SelectedKind);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Reporting.DesignerView))
        {
            MessageBox.Show(this, T("InvDesigner.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        _kind.ItemsSource = DocDesignKinds.All
            .Select(k => new KindChoice(k, T("InvDesigner.Kind" + k)))
            .ToList();
        _kind.SelectedIndex = 0;

        var companies = await CoLocalStore.LoadAsync();
        var rec = companies.FirstOrDefault(c =>
            string.Equals(c.Code, CurrentCompanyCode, StringComparison.OrdinalIgnoreCase));
        if (rec != null)
        {
            _companyId = rec.CompanyId;
            var cityCountry = string.Join(", ",
                new[] { rec.City, rec.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var parts = new[] { rec.AddressLine1, rec.AddressLine2, cityCountry }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var address = string.Join("\n", parts);
            _companyAddress = address.Length == 0 ? null : address;
        }

        LoadDesignIntoForm(DocDesign.BuiltInDefault(), null);
        await RefreshListAsync();
        RefreshPreview();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_newButton, "Common.New");
        F2Ui.StyleButton(_saveButton, "Common.Save");
        F2Ui.StyleButton(_copyButton, "InvDesigner.Copy");
        F2Ui.StyleButton(_deleteButton, "Common.Delete");
        F2Ui.StyleButton(_defaultButton, "InvDesigner.SetDefault");
        F2Ui.StyleButton(_previewButton, "InvDesigner.Preview");
        F2Ui.StyleButton(_exportButton, "InvDesigner.ExportDesign");
        F2Ui.StyleButton(_importButton, "InvDesigner.ImportDesign");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => { LoadDesignIntoForm(DocDesign.BuiltInDefault(), null); RefreshPreview(); };
        _saveButton.Click += async (_, _) => await SaveAsync();
        _copyButton.Click += async (_, _) => await CopyAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _defaultButton.Click += async (_, _) => await SetDefaultAsync();
        _previewButton.Click += (_, _) => RefreshPreview();
        _exportButton.Click += (_, _) => ExportDesign();
        _importButton.Click += async (_, _) => await ImportDesignAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_newButton, _saveButton, _copyButton, _deleteButton,
            _defaultButton, _previewButton, _exportButton, _importButton, _closeButton));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });

        // Left: kind + design list + band form.
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var form = F2Ui.CreateFormGrid(1);
        _kind.SelectionChanged += async (_, _) => await RefreshListAsync();
        F2Ui.AddField(_labels, form, 0, 0, "InvDesigner.Kind", _kind);
        F2Ui.AddField(_labels, form, 1, 0, "InvDesigner.Name", _name);
        F2Ui.AddField(_labels, form, 2, 0, "InvDesigner.HeaderColor", _headerColor);
        F2Ui.AddField(_labels, form, 3, 0, "InvDesigner.ShowCompanyAddress", _showAddress);
        F2Ui.AddField(_labels, form, 4, 0, "InvDesigner.ShowParty", _showParty);
        F2Ui.AddField(_labels, form, 5, 0, "InvDesigner.ShowMeta", _showMeta);
        F2Ui.AddField(_labels, form, 6, 0, "InvDesigner.ShowTax", _showTax);
        F2Ui.AddField(_labels, form, 7, 0, "InvDesigner.ShowWords", _showWords);
        F2Ui.AddField(_labels, form, 8, 0, "InvDesigner.ShowNotes", _showNotes);
        F2Ui.AddField(_labels, form, 9, 0, "InvDesigner.ShowSignatures", _showSignatures);
        F2Ui.AddField(_labels, form, 10, 0, "InvDesigner.ShowBarcode", _showBarcode);
        DockPanel.SetDock(form, Dock.Top);
        left.Children.Add(form);
        _designs.SelectionChanged += async (_, _) => await OnDesignSelectedAsync();
        left.Children.Add(_designs);
        Grid.SetRow(left, 0);
        Grid.SetColumn(left, 0);
        Grid.SetRowSpan(left, 2);
        content.Children.Add(left);

        // Right top: lines-band columns editor.
        var columnsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var columnButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        F2Ui.StyleButton(_upButton, "InvDesigner.MoveUp");
        F2Ui.StyleButton(_downButton, "InvDesigner.MoveDown");
        F2Ui.StyleButton(_addColumnButton, "InvDesigner.AddColumn");
        F2Ui.StyleButton(_removeColumnButton, "InvDesigner.RemoveColumn");
        _upButton.Click += (_, _) => MoveColumn(-1);
        _downButton.Click += (_, _) => MoveColumn(1);
        _addColumnButton.Click += (_, _) => _columns.Add(new DocDesignColumn
        {
            FieldKey = "Description",
            Caption = "Description",
            Width = 1,
            Visible = true
        });
        _removeColumnButton.Click += (_, _) =>
        {
            if (_columnsGrid.SelectedItem is DocDesignColumn col)
                _columns.Remove(col);
        };
        columnButtons.Children.Add(_upButton);
        columnButtons.Children.Add(_downButton);
        columnButtons.Children.Add(_addColumnButton);
        columnButtons.Children.Add(_removeColumnButton);
        DockPanel.SetDock(columnButtons, Dock.Top);
        columnsPanel.Children.Add(columnButtons);

        F2Ui.ConfigureGrid(_columnsGrid);
        _columnsGrid.AutoGenerateColumns = false;
        _columnsGrid.CanUserAddRows = false;
        _columnsGrid.CanUserDeleteRows = false;
        _columnsGrid.ItemsSource = _columns;
        columnsPanel.Children.Add(_columnsGrid);
        Grid.SetRow(columnsPanel, 0);
        Grid.SetColumn(columnsPanel, 1);
        content.Children.Add(columnsPanel);

        // Right bottom: live preview through the production renderer.
        _previewHost.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE8, 0xEE, 0xF3));
        Grid.SetRow(_previewHost, 1);
        Grid.SetColumn(_previewHost, 1);
        content.Children.Add(_previewHost);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("InvDesigner.Title");
        var buttons = new[]
        {
            _newButton, _saveButton, _copyButton, _deleteButton, _defaultButton,
            _previewButton, _exportButton, _importButton, _closeButton,
            _upButton, _downButton, _addColumnButton, _removeColumnButton
        };
        F2Ui.ApplyTexts(_labels, _headers, buttons, T);

        _columnsGrid.Columns.Clear();
        _columnsGrid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = T("InvDesigner.Field"),
            Width = new DataGridLength(140),
            ItemsSource = DocDesign.LineFieldKeys,
            SelectedItemBinding = new Binding(nameof(DocDesignColumn.FieldKey))
        });
        _columnsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = T("InvDesigner.Caption"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(DocDesignColumn.Caption))
        });
        _columnsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = T("InvDesigner.Width"),
            Width = new DataGridLength(70),
            Binding = new Binding(nameof(DocDesignColumn.Width))
        });
        _columnsGrid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = T("InvDesigner.Visible"),
            Width = new DataGridLength(60),
            Binding = new Binding(nameof(DocDesignColumn.Visible))
        });
        RefreshListDisplay();
    }

    private void RefreshListDisplay()
    {
        var selectedId = (_designs.SelectedItem as DesignListEntry)?.Item.DesignID;
        _designs.ItemsSource = _items
            .Select(i => new DesignListEntry(i, i.DesignName + (i.IsDefault ? " " + T("InvDesigner.DefaultSuffix") : string.Empty)))
            .ToList();
        if (selectedId is int id)
            _designs.SelectedItem = (_designs.ItemsSource as List<DesignListEntry>)?
                .FirstOrDefault(e => e.Item.DesignID == id);
    }

    private sealed record DesignListEntry(DocDesignListItem Item, string Display)
    {
        public override string ToString() => Display;
    }

    private void MoveColumn(int delta)
    {
        if (_columnsGrid.SelectedItem is not DocDesignColumn col)
            return;
        var index = _columns.IndexOf(col);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _columns.Count)
            return;
        _columns.Move(index, target);
        _columnsGrid.SelectedItem = col;
    }

    private async Task RefreshListAsync()
    {
        try
        {
            _items = await DocDesignRepo.ListAsync(_companyId, KindKey);
            RefreshListDisplay();
            _statusBar.Text = $"{_items.Count} · {KindKey}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "InvDesWin.RefreshListAsync");
            _statusBar.Text = ex.Message;
        }
    }

    private async Task OnDesignSelectedAsync()
    {
        try
        {
            if (_designs.SelectedItem is not DesignListEntry entry)
                return;
            var payload = await DocDesignRepo.LoadPayloadAsync(entry.Item.DesignID);
            var design = payload is null ? null : DocDesign.FromJson(payload);
            if (design is null)
                throw new InvalidOperationException(T("InvDesigner.DesignImportFailed"));
            LoadDesignIntoForm(design, entry.Item.DesignID);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadDesignIntoForm(DocDesign design, int? id)
    {
        _editingId = id;
        _name.Text = design.Name;
        _headerColor.Text = design.HeaderBackColor;
        _showAddress.IsChecked = design.ShowCompanyAddress;
        _showParty.IsChecked = design.ShowPartyBlock;
        _showMeta.IsChecked = design.ShowDocMeta;
        _showTax.IsChecked = design.ShowTaxBlock;
        _showWords.IsChecked = design.ShowAmountInWords;
        _showNotes.IsChecked = design.ShowNotes;
        _showSignatures.IsChecked = design.ShowSignatures;
        _showBarcode.IsChecked = design.ShowBarcode;
        _columns.Clear();
        foreach (var col in design.Columns)
        {
            _columns.Add(new DocDesignColumn
            {
                FieldKey = col.FieldKey,
                Caption = col.Caption,
                Width = col.Width,
                Visible = col.Visible
            });
        }
    }

    /// <summary>Reads the form back into a design; validates the
    /// name and every column field against the line-field catalog.</summary>
    private DocDesign DesignFromForm()
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(T("InvDesigner.NameRequired"));
        foreach (var col in _columns)
        {
            if (!DocDesign.IsKnownField(col.FieldKey))
                throw new InvalidOperationException(
                    FormatLocalized(T("InvDesigner.UnknownField"), col.FieldKey));
        }

        return new DocDesign
        {
            Name = name,
            HeaderBackColor = string.IsNullOrWhiteSpace(_headerColor.Text) ? "#51708E" : _headerColor.Text.Trim(),
            ShowCompanyAddress = _showAddress.IsChecked == true,
            ShowPartyBlock = _showParty.IsChecked == true,
            ShowDocMeta = _showMeta.IsChecked == true,
            ShowTaxBlock = _showTax.IsChecked == true,
            ShowAmountInWords = _showWords.IsChecked == true,
            ShowNotes = _showNotes.IsChecked == true,
            ShowSignatures = _showSignatures.IsChecked == true,
            ShowBarcode = _showBarcode.IsChecked == true,
            Columns = _columns.Select(c => new DocDesignColumn
            {
                FieldKey = c.FieldKey,
                Caption = c.Caption,
                Width = c.Width <= 0 ? 1 : c.Width,
                Visible = c.Visible
            }).ToList()
        };
    }

    private static bool CanManage =>
        AppHost.Permissions.Has(PermCat.Reporting.DesignerManage);

    private async Task SaveAsync()
    {
        try
        {
            if (!CanManage)
                throw new InvalidOperationException(T("InvDesigner.PermissionDenied"));
            var design = DesignFromForm();
            var makeDefault = _items.Count == 0 && _editingId is null;
            _editingId = await DocDesignRepo.SaveAsync(
                _companyId, KindKey, design, makeDefault, _editingId, UserId);
            _statusBar.Text = T("InvDesigner.Saved");
            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CopyAsync()
    {
        try
        {
            if (!CanManage)
                throw new InvalidOperationException(T("InvDesigner.PermissionDenied"));
            var design = DesignFromForm();
            design.Name += " (2)";
            _editingId = await DocDesignRepo.SaveAsync(
                _companyId, KindKey, design, makeDefault: false, existingId: null, UserId);
            _name.Text = design.Name;
            _statusBar.Text = T("InvDesigner.Saved");
            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (!CanManage)
                throw new InvalidOperationException(T("InvDesigner.PermissionDenied"));
            if (_editingId is not int id)
                throw new InvalidOperationException(T("InvDesigner.SelectFirst"));
            await DocDesignRepo.DeleteAsync(id, UserId);
            _editingId = null;
            _statusBar.Text = T("InvDesigner.Deleted");
            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SetDefaultAsync()
    {
        try
        {
            if (!CanManage)
                throw new InvalidOperationException(T("InvDesigner.PermissionDenied"));
            if (_editingId is not int id)
                throw new InvalidOperationException(T("InvDesigner.SelectFirst"));
            await DocDesignRepo.SetDefaultAsync(id, _companyId, KindKey, UserId);
            _statusBar.Text = T("InvDesigner.DefaultSet");
            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshPreview()
    {
        try
        {
            var design = DesignFromForm();
            var page = InvPrn.BuildPreviewPage(
                BuildSampleDoc(SelectedKind), CurrentCompanyDisplay, design, _companyAddress);
            _previewHost.Child = new Viewbox
            {
                Child = page,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(8)
            };
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
        }
    }

    /// <summary>Sample document for the preview — clearly sample
    /// data, rendered through the production page builder.</summary>
    private static InvDoc BuildSampleDoc(DocDesignKind kind)
    {
        var docKind = DocDesignKinds.DocumentKind(kind);
        var purchase = kind is DocDesignKind.PurchaseInvoice or DocDesignKind.PurchaseReturn;
        var doc = new InvDoc
        {
            Kind = docKind,
            DocNo = $"{docKind switch
            {
                InvDocKind.SalesInvoice => "SAL",
                InvDocKind.PurchaseInvoice => "PUR",
                InvDocKind.SalesReturn => "SRT",
                InvDocKind.PurchaseReturn => "PRT",
                InvDocKind.StockIn => "ENT",
                _ => "EXT"
            }}-1001",
            DocDate = DateTime.Today,
            PartyCode = purchase ? "S-100" : "C-100",
            PartyName = purchase ? "Sample Supplier" : "Sample Customer",
            WarehouseName = "Main Warehouse",
            CurrencyCode = "SAR",
            Notes = "Sample notes for preview."
        };
        doc.Lines.Add(new InvLine
        {
            LineNo = 1,
            MaterialCode = "M-001",
            Description = "Sample material A",
            Unit = "pcs",
            Qty = 2,
            UnitPrice = 150m,
            TaxRate = 15m,
            WarehouseName = "Main Warehouse"
        });
        doc.Lines.Add(new InvLine
        {
            LineNo = 2,
            MaterialCode = "M-002",
            Description = "Sample material B",
            Unit = "pcs",
            Qty = 3,
            UnitPrice = 40m,
            DiscountPct = 10m,
            TaxRate = 15m,
            WarehouseName = "Main Warehouse"
        });
        return doc;
    }

    private void ExportDesign()
    {
        try
        {
            var design = DesignFromForm();
            var dialog = new SaveFileDialog
            {
                Filter = DesignFileSvc.FilterFor(DesignFileSvc.Kind.Invoice),
                DefaultExt = DesignFileSvc.ExtensionFor(DesignFileSvc.Kind.Invoice),
                AddExtension = true,
                OverwritePrompt = true,
                FileName = design.Name + DesignFileSvc.ExtensionFor(DesignFileSvc.Kind.Invoice)
            };
            if (dialog.ShowDialog(this) != true)
                return;
            if (!DesignFileSvc.Export(dialog.FileName, DesignFileSvc.Kind.Invoice,
                    design.Name, design.ToJson()))
                throw new InvalidOperationException(T("InvDesigner.DesignExportFailed"));
            _statusBar.Text = T("InvDesigner.DesignExported");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ImportDesignAsync()
    {
        try
        {
            if (!CanManage)
                throw new InvalidOperationException(T("InvDesigner.PermissionDenied"));
            var dialog = new OpenFileDialog
            {
                Filter = DesignFileSvc.FilterFor(DesignFileSvc.Kind.Invoice)
            };
            if (dialog.ShowDialog(this) != true)
                return;
            var json = DesignFileSvc.Import(dialog.FileName, DesignFileSvc.Kind.Invoice, out var designName);
            var design = json is null ? null : DocDesign.FromJson(json);
            if (design is null)
                throw new InvalidOperationException(T("InvDesigner.DesignImportFailed"));
            if (!string.IsNullOrWhiteSpace(designName))
                design.Name = designName;
            _editingId = await DocDesignRepo.SaveAsync(
                _companyId, KindKey, design, makeDefault: false, existingId: null, UserId);
            LoadDesignIntoForm(design, _editingId);
            _statusBar.Text = T("InvDesigner.DesignImported");
            await RefreshListAsync();
            RefreshPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
