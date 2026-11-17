using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;

namespace ShouTech.App.Views;

/// <summary>
/// Shared UI construction helpers for the F2 operations/system windows,
/// factored from the ProjWin/PayRecWin pattern (Explorer-style bottom command
/// bar, slim status bar, Al-Ameen schedule grids, labeled editors). Each
/// window keeps its own label/header registries so ApplyLocalizedText can
/// re-localize live, exactly like ProjWin.
/// </summary>
internal static class F2Ui
{
    public static readonly Style RightAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) }
    };

    public static readonly Style CenterAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center) }
    };

    public static void ConfigureGrid(DataGrid grid)
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

    public static DataGridTextColumn TextCol(
        List<(DataGridColumn Column, string Key)> headers,
        string key, string path, double width, bool numeric = false, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (numeric)
            column.ElementStyle = RightAlignStyle;
        else if (center)
            column.ElementStyle = CenterAlignStyle;
        headers.Add((column, key));
        return column;
    }

    public static StackPanel Labeled(
        List<(TextBlock Label, string Key)> labels,
        string key, Control control, double width = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        if (width > 0)
            panel.Width = width;
        var label = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        labels.Add((label, key));
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    public static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = 92;
        button.MinHeight = 32;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Padding = new Thickness(12, 3, 12, 3);
    }

    /// <summary>Explorer-style bottom command bar (flat, top divider), docked bottom.</summary>
    public static Border BottomBar(params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
            bar.Children.Add(b);
        var border = new Border
        {
            Child = bar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(border, Dock.Bottom);
        return border;
    }

    /// <summary>Slim status bar at the very bottom.</summary>
    public static Border StatusBar(TextBlock statusBar)
    {
        var border = new Border
        {
            Child = statusBar,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(border, Dock.Bottom);
        return border;
    }

    public static void ApplyTexts(
        List<(TextBlock Label, string Key)> labels,
        List<(DataGridColumn Column, string Key)> headers,
        IEnumerable<Button> buttons,
        Func<string, string> t)
    {
        foreach (var (label, key) in labels)
            label.Text = t(key);
        foreach (var (column, key) in headers)
            column.Header = t(key);
        foreach (var b in buttons)
            if (b.Tag is string key)
                b.Content = t(key);
    }

    public static decimal ParseDecimal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }

    public static string Fmt(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
