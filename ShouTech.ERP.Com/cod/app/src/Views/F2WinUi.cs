using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;

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

    // ================================================================
    // FORM PROPORTIONS — the Phenix layout rule (docs/WINDOW_PROPORTIONS.md),
    // shared with BaseWin.CreateFormGrid/AddFormField: one shared column
    // grid per form, labels BESIDE their fields in the FormLabelWidth
    // label column, star field columns, spans keep every field edge on
    // the shared grid. Labels register in the window's label list so
    // ApplyTexts localizes them exactly as before.
    // ================================================================

    /// <summary>Creates the one shared column grid of a form (label + field pairs).</summary>
    public static Grid CreateFormGrid(int pairs)
    {
        var grid = new Grid();
        for (var i = 0; i < pairs; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(VisuId.FormLabelWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        return grid;
    }

    /// <summary>Places a registered side label and its field into the form grid.</summary>
    public static void AddField(
        List<(TextBlock Label, string Key)> labels,
        Grid grid, int row, int pair, string key, Control control, int spanPairs = 1)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 8)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        labels.Add((label, key));
        Grid.SetRow(label, row);
        Grid.SetColumn(label, pair * 2);
        grid.Children.Add(label);
        PlaceControl(grid, row, pair, control, spanPairs);
    }

    /// <summary>Places a self-captioned control (CheckBox) into a field cell.</summary>
    public static void AddElement(Grid grid, int row, int pair, Control control, int spanPairs = 1)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PlaceControl(grid, row, pair, control, spanPairs);
    }

    private static void PlaceControl(Grid grid, int row, int pair, Control control, int spanPairs)
    {
        ApplyFieldStyle(control);
        control.Margin = new Thickness(0, 0, 12, 8);
        if (control is CheckBox or RadioButton)
            control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, pair * 2 + 1);
        if (spanPairs > 1)
            Grid.SetColumnSpan(control, spanPairs * 2 - 1);
        grid.Children.Add(control);
    }

    /// <summary>Applies the central input style for the control type (height 32).</summary>
    public static void ApplyFieldStyle(Control control)
    {
        switch (control)
        {
            case TextBox textBox:
                textBox.SetResourceReference(FrameworkElement.StyleProperty, "VisuTextBoxStyle");
                break;
            case ComboBox comboBox:
                comboBox.SetResourceReference(FrameworkElement.StyleProperty, "VisuComboBoxStyle");
                break;
            case DatePicker datePicker:
                datePicker.SetResourceReference(FrameworkElement.StyleProperty, "Win11DatePickerStyle");
                break;
            case PasswordBox passwordBox:
                passwordBox.SetResourceReference(FrameworkElement.StyleProperty, "VisuPasswordBoxStyle");
                break;
        }
    }

    public static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
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
            Padding = new Thickness(0, 8, 0, 0),
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
            Padding = new Thickness(2, 4, 2, 0),
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
