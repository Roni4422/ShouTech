using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain;

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
        // Owner grid spec: numbered row headers on every schedule
        // (start side; mirrors with FlowDirection) — centrally on.
        DataGridTableBehavior.SetShowRowNumbers(grid, true);
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
        // End-aligned through FlowAlign so the command bar mirrors with
        // the window (physical right in LTR, physical left in RTL).
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
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
        throw new InvalidOperationException(AppHost.Localization.GetString("Common.InvalidNumberFormat", "Invalid number format."));
    }

    /// <summary>Money display for the F2 windows — governed by the
    /// program-wide Options number settings (NumFmt.Current,
    /// OPTWIN_SECTIONS §3.2), not a local hardcoded pattern.</summary>
    public static string Fmt(decimal value) => NumFmt.FormatMoney(value);

    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Direction-aware ("start"/"end") horizontal alignment — the central
/// mirroring mechanism for layout clusters (owner directive 2026-11-24
/// 14:55: in Arabic the ENTIRE window mirrors).
///
/// WPF's HorizontalAlignment.Left/Right are PHYSICAL: a button cluster
/// pinned Right stays on the physical right while the rest of the
/// window mirrors around it. Binding the alignment to the element's
/// own (inherited) FlowDirection makes the cluster sit at the layout
/// END in both directions — physical right in LTR, physical left in
/// RTL — and re-evaluates live when the window's direction flips on a
/// language switch, because the inherited FlowDirection value changes.
///
/// Code:  FlowAlign.End(panel);  /  FlowAlign.Start(control);
/// XAML:  local:FlowAlign.AlignEnd="True"  /  local:FlowAlign.AlignStart="True"
/// (replacing the HorizontalAlignment="Right"/"Left" attribute).
///
/// Content alignment is deliberately NOT covered here: numeric grid
/// columns stay right-aligned in both directions (F2Ui.RightAlignStyle)
/// and styled text controls follow the application alignment resources
/// (LangDirSv.UpdateApplicationAlignmentResources).
/// </summary>
public static class FlowAlign
{
    private sealed class EndConverter : IValueConverter
    {
        public static readonly EndConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is FlowDirection.RightToLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class StartConverter : IValueConverter
    {
        public static readonly StartConverter Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is FlowDirection.RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Pins the element to the layout END (right in LTR, left in RTL), live with direction changes.</summary>
    public static void End(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetBinding(FrameworkElement.HorizontalAlignmentProperty,
            new Binding(nameof(FrameworkElement.FlowDirection)) { Source = element, Converter = EndConverter.Instance });
    }

    /// <summary>Pins the element to the layout START (left in LTR, right in RTL), live with direction changes.</summary>
    public static void Start(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetBinding(FrameworkElement.HorizontalAlignmentProperty,
            new Binding(nameof(FrameworkElement.FlowDirection)) { Source = element, Converter = StartConverter.Instance });
    }

    public static readonly DependencyProperty AlignEndProperty =
        DependencyProperty.RegisterAttached("AlignEnd", typeof(bool), typeof(FlowAlign),
            new PropertyMetadata(false, OnAlignEndChanged));

    public static bool GetAlignEnd(DependencyObject obj) => (bool)obj.GetValue(AlignEndProperty);

    public static void SetAlignEnd(DependencyObject obj, bool value) => obj.SetValue(AlignEndProperty, value);

    private static void OnAlignEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        if (e.NewValue is true)
            End(element);
        else
            BindingOperations.ClearBinding(element, FrameworkElement.HorizontalAlignmentProperty);
    }

    public static readonly DependencyProperty AlignStartProperty =
        DependencyProperty.RegisterAttached("AlignStart", typeof(bool), typeof(FlowAlign),
            new PropertyMetadata(false, OnAlignStartChanged));

    public static bool GetAlignStart(DependencyObject obj) => (bool)obj.GetValue(AlignStartProperty);

    public static void SetAlignStart(DependencyObject obj, bool value) => obj.SetValue(AlignStartProperty, value);

    private static void OnAlignStartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        if (e.NewValue is true)
            Start(element);
        else
            BindingOperations.ClearBinding(element, FrameworkElement.HorizontalAlignmentProperty);
    }
}
