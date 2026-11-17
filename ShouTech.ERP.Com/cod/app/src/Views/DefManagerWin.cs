using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Generic definition-set manager (ribbon completion F1): one window serves
/// every <see cref="DefSet"/> in the registry — voucher/security/payment/
/// invoice/order type definitions, payment methods, POS terminals, areas,
/// targets, discount rules, customer prices, handheld devices, device
/// routes, drivers, delivery prices, custom fields, units of measure and
/// material specs. Layout follows ProjWin: list grid + generated editor,
/// actions in the Explorer-style bottom command bar, slim status bar.
/// Every save/delete/activate is a real, permission-gated, transactional
/// write through <see cref="DefinitionService"/>.
/// </summary>
public sealed class DefManagerWin : BaseWin
{
    private readonly DefinitionService? _service;
    private readonly DefSet _set;
    private readonly List<DefRow> _rows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly List<(DefColumn Column, Control Editor)> _editors = new();

    private readonly DataGrid _grid = new();
    private readonly Grid _editorPanel = CreateFormGrid(1);
    private readonly CheckBox _isActive = new();
    private readonly TextBlock _header = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _toggleActiveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private string? _selectedId;
    private bool _selectedIsActive = true;

    /// <summary>Parameterless ctor (designer/safety): defaults to VoucherTypes.</summary>
    public DefManagerWin() : this(DefSets.VoucherTypes)
    {
    }

    public DefManagerWin(string setKey)
    {
        _set = DefSets.Get(setKey);
        _service = AppHost.Definitions;
        Width = 1180;
        Height = 720;
        MinWidth = 940;
        MinHeight = 580;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>Static factory used by ribbon routing: returns the manager window for a set key.</summary>
    public static DefManagerWin For(string setKey) => new(setKey);

    public string SetKey => _set.Key;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!CanView())
        {
            MessageBox.Show(this, T("Definitions.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        ApplyManageGating();
        await RefreshAsync();
    }

    private bool CanView() => AppHost.Permissions.Has(_set.PermissionView) || CanManage();

    private bool CanManage()
        => AppHost.Permissions.Has(_set.PermissionManage)
           || _set.ExtraManagePermissions.Any(p => AppHost.Permissions.Has(p));

    private void ApplyManageGating()
    {
        var canManage = CanManage();
        _saveButton.IsEnabled = canManage;
        _deleteButton.IsEnabled = canManage;
        _toggleActiveButton.IsEnabled = canManage && _set.HasActive;
        _toggleActiveButton.Visibility = _set.HasActive ? Visibility.Visible : Visibility.Collapsed;
        _isActive.IsEnabled = canManage;
        foreach (var (_, editor) in _editors)
            editor.IsEnabled = canManage;
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
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_toggleActiveButton, "Definitions.ToggleActive");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _toggleActiveButton.Click += async (_, _) => await ToggleActiveAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _toggleActiveButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
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

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _header.FontWeight = FontWeights.SemiBold;
        _header.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_header, Dock.Top);
        listPanel.Children.Add(_header);
        ConfigureGrid(_grid);
        foreach (var column in _set.Columns)
        {
            var gridColumn = new DataGridTextColumn
            {
                Binding = new Binding($"Values[{column.Name}]"),
                Width = new DataGridLength(column.Type is DefColumnType.Number or DefColumnType.Integer ? 110 : 150)
            };
            if (column.Type is DefColumnType.Number or DefColumnType.Integer)
                gridColumn.ElementStyle = RightAlignStyle;
            _headers.Add((gridColumn, $"Definitions.Field.{column.Name}"));
            _grid.Columns.Add(gridColumn);
        }
        if (_set.HasActive)
        {
            var activeColumn = new DataGridTextColumn
            {
                Binding = new Binding(nameof(DefRow.IsActive)),
                Width = new DataGridLength(80)
            };
            activeColumn.ElementStyle = CenterAlignStyle;
            _headers.Add((activeColumn, "Definitions.IsActive"));
            _grid.Columns.Add(activeColumn);
        }
        _grid.SelectionChanged += (_, _) => OnRowSelected();
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        // Editor generated from the descriptor columns, as rows of the
        // one-pair form grid (side labels, aligned edges — Phenix rule).
        // Editor heights come from the central styles via AddFormField.
        var editorRow = 0;
        foreach (var column in _set.Columns)
        {
            Control editor = column.Type switch
            {
                DefColumnType.Bool => new CheckBox { VerticalAlignment = VerticalAlignment.Center },
                DefColumnType.Date => new DatePicker(),
                _ => new TextBox()
            };
            _editors.Add((column, editor));
            AddFormField(_labels, _editorPanel, editorRow++, 0, $"Definitions.Field.{column.Name}", editor);
        }
        if (_set.HasActive)
        {
            AddFormField(_labels, _editorPanel, editorRow, 0, "Definitions.IsActive", _isActive);
            _isActive.IsChecked = true;
        }
        var editorScroll = new ScrollViewer { Content = _editorPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(editorScroll, 1);
        content.Children.Add(editorScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T($"Definitions.Set.{_set.Key}");
        _header.Text = T($"Definitions.Set.{_set.Key}");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _toggleActiveButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Definitions.ServiceUnavailable");
                return;
            }
            var rows = await _service.ListAsync(_set.Key, CurrentCompanyCode);
            _rows.Clear();
            _rows.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRowSelected()
    {
        if (_grid.SelectedItem is not DefRow row)
            return;
        _selectedId = row.Id;
        _selectedIsActive = row.IsActive;
        foreach (var (column, editor) in _editors)
        {
            row.Values.TryGetValue(column.Name, out var value);
            SetEditorValue(editor, column, value);
        }
        _isActive.IsChecked = row.IsActive;
        UpdateStatusBar();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Definitions.ServiceUnavailable"));
            if (!CanManage())
                throw new InvalidOperationException(T("Definitions.PermissionDenied"));
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, editor) in _editors)
                values[column.Name] = GetEditorValue(editor, column);
            if (_set.HasActive)
                values["is_active"] = _isActive.IsChecked == true;
            var id = await _service.SaveAsync(_set.Key, CurrentCompanyCode, _selectedId, values, CurrentUserId);
            _selectedId = id;
            MessageBox.Show(this, T("Definitions.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
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
            if (_service is null || _selectedId is null)
                return;
            if (!CanManage())
                throw new InvalidOperationException(T("Definitions.PermissionDenied"));
            // Shared global seed rows (company '') are never deleted from a
            // company session; only company-owned rows can be removed.
            var row = _rows.FirstOrDefault(r => r.Id == _selectedId);
            if (row is not null && row.CompanyId.Length == 0)
                throw new InvalidOperationException(T("Definitions.GlobalRowProtected"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.SoftDeleteAsync(_set.Key, CurrentCompanyCode, _selectedId, CurrentUserId);
            _selectedId = null;
            ClearEditor();
            MessageBox.Show(this, T("Definitions.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ToggleActiveAsync()
    {
        try
        {
            if (_service is null || _selectedId is null)
                return;
            if (!CanManage())
                throw new InvalidOperationException(T("Definitions.PermissionDenied"));
            await _service.SetActiveAsync(_set.Key, CurrentCompanyCode, _selectedId, !_selectedIsActive, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _selectedIsActive = true;
        _grid.SelectedItem = null;
        foreach (var (column, editor) in _editors)
            SetEditorValue(editor, column, null);
        _isActive.IsChecked = true;
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        _statusBar.Text = $"{_rows.Count} · {T($"Definitions.Set.{_set.Key}")}";
    }

    private static void SetEditorValue(Control editor, DefColumn column, object? value)
    {
        switch (editor)
        {
            case CheckBox check:
                check.IsChecked = value switch
                {
                    null => false,
                    bool b => b,
                    long l => l != 0,
                    int i => i != 0,
                    _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                };
                break;
            case DatePicker picker:
                picker.SelectedDate = value switch
                {
                    null => null,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed.ToDateTime(TimeOnly.MinValue),
                    _ => null
                };
                break;
            case TextBox text:
                text.Text = value switch
                {
                    null => string.Empty,
                    decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                };
                break;
        }
    }

    private static object? GetEditorValue(Control editor, DefColumn column)
    {
        return editor switch
        {
            CheckBox check => check.IsChecked == true,
            DatePicker picker => picker.SelectedDate.HasValue
                ? picker.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            TextBox text => string.IsNullOrWhiteSpace(text.Text) ? null : text.Text.Trim(),
            _ => null
        };
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

    private static readonly Style RightAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) }
    };

    private static readonly Style CenterAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center) }
    };


    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }
}
