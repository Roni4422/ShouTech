using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Party picker (ribbon chunk B): searches parties by code, name or phone with
/// an optional kind filter. Double-click (or Select) returns the choice through
/// <see cref="SelectedPartyCode"/> / <see cref="SelectedPartyKind"/> with
/// DialogResult = true.
/// </summary>
public sealed class PartySearchWin : BaseWin
{
    protected override string? ScheduleTintName => "Mauve";
    private readonly TextBox _txtQuery = new();
    private readonly ComboBox _cbKind = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _searchButton = new();
    private readonly Button _selectButton = new();
    private readonly Button _closeButton = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly List<Party> _results = new();

    /// <summary>Code of the chosen party (set when the dialog is accepted).</summary>
    public string? SelectedPartyCode { get; private set; }

    /// <summary>Kind ("C"/"S") of the chosen party.</summary>
    public string? SelectedPartyKind { get; private set; }

    /// <summary>Optional kind pre-filter: "C", "S", or null for both.</summary>
    public string? KindFilter { get; set; }

    public PartySearchWin()
    {
        Width = 760;
        Height = 480;
        MinWidth = 620;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Customers.View))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _cbKind.SelectedIndex = KindFilter == PartyKinds.Supplier ? 2 : KindFilter == PartyKinds.Customer ? 1 : 0;
        await SearchAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var statusBorder = new Border
        {
            Child = _statusBar,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_selectButton, "MasterCards.Select");
        StyleButton(_closeButton, "Common.Close");
        _selectButton.Click += (_, _) => AcceptSelection();
        _closeButton.Click += (_, _) => { DialogResult = false; Close(); };
        bar.Children.Add(_selectButton);
        bar.Children.Add(_closeButton);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        var searchForm = CreateFormGrid(2);
        AddFormField(searchForm, 0, 0, "MasterCards.Search", _txtQuery);
        AddFormField(searchForm, 0, 1, "MasterCards.Kind", _cbKind);
        StyleButton(_searchButton, "MasterCards.Search");
        _searchButton.Margin = new Thickness(8, 0, 0, 0);
        _searchButton.VerticalAlignment = VerticalAlignment.Top;
        _searchButton.Click += async (_, _) => await SearchAsync();
        var searchPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_searchButton, Dock.Right);
        searchPanel.Children.Add(_searchButton);
        searchPanel.Children.Add(searchForm);
        _txtQuery.KeyDown += async (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
                await SearchAsync();
        };
        DockPanel.SetDock(searchPanel, Dock.Top);
        root.Children.Add(searchPanel);

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("MasterCards.Kind", nameof(Party.Kind), 70, center: true));
        _grid.Columns.Add(TextCol("CustCard.Code", nameof(Party.Code), 120));
        _grid.Columns.Add(TextCol("MasterCards.Name", nameof(Party.NameEn), 240));
        _grid.Columns.Add(TextCol("CustCard.Phone", nameof(Party.Phone), 130));
        _grid.Columns.Add(TextCol("MasterCards.Active", nameof(Party.IsActive), 80, center: true));
        _grid.MouseDoubleClick += (_, _) => AcceptSelection();
        root.Children.Add(_grid);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("MasterCards.PartySearchTitle");
        var selected = _cbKind.SelectedIndex;
        _cbKind.Items.Clear();
        _cbKind.Items.Add(T("MasterCards.All"));
        _cbKind.Items.Add(T("CustCard.Customer"));
        _cbKind.Items.Add(T("CustCard.Supplier"));
        _cbKind.SelectedIndex = selected < 0 ? 0 : selected;
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _searchButton, _selectButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        _statusBar.Text = $"{_results.Count}";
    }

    private string? SelectedKind() => _cbKind.SelectedIndex switch
    {
        1 => PartyKinds.Customer,
        2 => PartyKinds.Supplier,
        _ => null
    };

    private async Task SearchAsync()
    {
        try
        {
            var service = AppHost.MasterCards;
            if (service is null)
            {
                _statusBar.Text = T("MasterCards.ServiceUnavailable");
                return;
            }
            var rows = await service.SearchPartiesAsync(CurrentCompanyCode, SelectedKind(), _txtQuery.Text.Trim());
            _results.Clear();
            _results.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _results;
            _statusBar.Text = $"{_results.Count}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AcceptSelection()
    {
        if (_grid.SelectedItem is not Party party)
            return;
        SelectedPartyCode = party.Code;
        SelectedPartyKind = party.Kind;
        DialogResult = true;
        Close();
    }

    private static void ConfigureGrid(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.IsReadOnly = true;
        grid.CanUserSortColumns = true;
        grid.CanUserReorderColumns = true;
        grid.CanUserResizeColumns = true;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.RowHeaderWidth = 0;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.AlternationCount = 2;
        DataGridTableBehavior.SetEnableStandardContextMenu(grid, true);
    }

    private DataGridTextColumn TextCol(string key, string path, double width, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (center)
            column.ElementStyle = CenterAlignStyle;
        _headers.Add((column, key));
        return column;
    }

    private static readonly Style CenterAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center) }
    };


    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.Content = T(key);
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }
}
