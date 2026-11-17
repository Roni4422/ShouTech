using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Group master manager for party groups (Customers.CustomerGroups) and
/// sales-rep groups (SalesRep.SalesRepGroups). Both group tables are served
/// by MasterCardService (migration 031); this window is the list + editor
/// surface: New / Save / Delete (soft, guarded in the service) over real
/// rows — groups feed the CstCard / RepCard group lookups.
/// </summary>
public sealed class GroupManagerWin : BaseWin
{
    public enum GroupKind
    {
        Party,
        SalesRep
    }

    private readonly GroupKind _kind;
    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<GroupRow> _rows = new();
    private readonly TextBox _txtCode = new();
    private readonly TextBox _txtNameEn = new();
    private readonly TextBox _txtNameAr = new();
    private readonly CheckBox _chkActive = new() { IsChecked = true };
    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnClose = new();
    private readonly TextBlock _lblStatus = new();
    private Guid? _editingId;

    public GroupManagerWin(GroupKind kind)
    {
        _kind = kind;
        Width = 760;
        Height = 500;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        ApplyLocalizedText();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");
        StyleButton(_btnClose, "Common.Close");
        _btnNew.Click += (_, _) => ClearEditor();
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnDelete.Click += async (_, _) => await DeleteAsync();
        _btnClose.Click += (_, _) => Close();
        foreach (var b in new[] { _btnNew, _btnSave, _btnDelete, _btnClose })
            toolbar.Children.Add(b);

        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);

        var footer = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        footer.Child = _lblStatus;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Editor = one shared column grid (3 pairs + the Active field),
        // side labels, edges aligned (Phenix rule).
        var editor = CreateFormGrid(4);
        editor.Margin = new Thickness(0, 0, 0, 8);
        AddFormField(editor, 0, 0, "MasterCards.Code", _txtCode);
        AddFormField(editor, 0, 1, "MasterCards.NameEn", _txtNameEn);
        AddFormField(editor, 0, 2, "MasterCards.NameAr", _txtNameAr);
        AddFormField(editor, 0, 3, "MasterCards.Active", _chkActive);
        Grid.SetRow(editor, 0);
        body.Children.Add(editor);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.IsReadOnly = true;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");
        _grid.ItemsSource = _rows;
        _grid.Columns.Add(TextCol(nameof(GroupRow.Code), 130));
        _grid.Columns.Add(TextCol(nameof(GroupRow.NameEn), 240));
        _grid.Columns.Add(TextCol(nameof(GroupRow.NameAr), 240));
        _grid.Columns.Add(TextCol(nameof(GroupRow.ActiveText), 80));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is GroupRow row)
            {
                _editingId = row.Id;
                _txtCode.Text = row.Code;
                _txtNameEn.Text = row.NameEn;
                _txtNameAr.Text = row.NameAr;
                _chkActive.IsChecked = row.IsActive;
            }
        };
        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private async Task RefreshAsync()
    {
        _rows.Clear();
        if (AppHost.MasterCards is null)
        {
            _lblStatus.Text = T("MasterCards.ServiceUnavailable");
            return;
        }
        try
        {
            if (_kind == GroupKind.Party)
            {
                foreach (var group in await AppHost.MasterCards.GetPartyGroupsAsync(CurrentCompanyCode))
                    _rows.Add(new GroupRow { Id = group.Id, Code = group.Code, NameEn = group.NameEn, NameAr = group.NameAr ?? "", IsActive = group.IsActive });
            }
            else
            {
                foreach (var group in await AppHost.MasterCards.GetSalesRepGroupsAsync(CurrentCompanyCode))
                    _rows.Add(new GroupRow { Id = group.Id, Code = group.Code, NameEn = group.NameEn, NameAr = group.NameAr ?? "", IsActive = group.IsActive });
            }
            _lblStatus.Text = $"{T("DocSearch.Results")}: {_rows.Count}";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = ex.Message;
        }
    }

    private void ClearEditor()
    {
        _editingId = null;
        _txtCode.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _txtNameAr.Text = string.Empty;
        _chkActive.IsChecked = true;
        _grid.SelectedItem = null;
    }

    private async Task SaveAsync()
    {
        if (AppHost.MasterCards is null) return;
        var permission = _kind == GroupKind.Party ? PermCat.Customers.Update : PermCat.Representatives.Update;
        if (!AppHost.Permissions.Has(permission))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (_kind == GroupKind.Party)
            {
                var group = new PartyGroup
                {
                    Id = _editingId ?? Guid.NewGuid(),
                    Kind = PartyKinds.Customer,
                    Code = _txtCode.Text.Trim(),
                    NameEn = _txtNameEn.Text.Trim(),
                    NameAr = string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                    IsActive = _chkActive.IsChecked == true
                };
                _editingId = await AppHost.MasterCards.SavePartyGroupAsync(CurrentCompanyCode, group, CurrentUserId);
            }
            else
            {
                var group = new SalesRepGroup
                {
                    Id = _editingId ?? Guid.NewGuid(),
                    Code = _txtCode.Text.Trim(),
                    NameEn = _txtNameEn.Text.Trim(),
                    NameAr = string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                    IsActive = _chkActive.IsChecked == true
                };
                _editingId = await AppHost.MasterCards.SaveSalesRepGroupAsync(CurrentCompanyCode, group, CurrentUserId);
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteAsync()
    {
        if (AppHost.MasterCards is null || _editingId is null) return;
        var confirm = MessageBox.Show(this, T("MasterCards.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            if (_kind == GroupKind.Party)
                await AppHost.MasterCards.DeletePartyGroupAsync(CurrentCompanyCode, _editingId.Value, CurrentUserId);
            else
                await AppHost.MasterCards.DeleteSalesRepGroupAsync(CurrentCompanyCode, _editingId.Value, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void ApplyLocalizedText()
    {
        Title = T(_kind == GroupKind.Party ? "MasterCards.PartyGroupsTitle" : "MasterCards.RepGroupsTitle");
        if (_grid.Columns.Count >= 4)
        {
            _grid.Columns[0].Header = T("MasterCards.Code");
            _grid.Columns[1].Header = T("MasterCards.NameEn");
            _grid.Columns[2].Header = T("MasterCards.NameAr");
            _grid.Columns[3].Header = T("MasterCards.Active");
        }
        RefreshLocalizedContent(Content as DependencyObject);
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
    }


    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 8, 0);
        btn.MinWidth = VisuId.ButtonMinWidth;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static DataGridTextColumn TextCol(string path, double width)
        => new()
        {
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            Width = width,
            IsReadOnly = true
        };

    private sealed class GroupRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string NameAr { get; set; } = "";
        public bool IsActive { get; set; }
        public string ActiveText => IsActive ? "Yes" : "No";
    }
}
