using System;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Interaction;

namespace ShouTech.App.Services;

/// <summary>Application-wide policy for editable and read-only data tables.
/// Domain windows own their columns and persistence; this engine owns shared
/// Excel-like interaction, virtualization and visual defaults.</summary>
public static class GridEngine
{
    public static void ApplyStandard(DataGrid grid, string rowBrushKey = "TableSurfaceBrush",
        string alternateRowBrushKey = "TableAlternateRowBrush", bool enableContextMenu = true)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.AutoGenerateColumns = false;
        grid.AlternationCount = 2;
        grid.EnableRowVirtualization = true;
        grid.EnableColumnVirtualization = true;
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(grid, true);
        grid.HeadersVisibility = DataGridHeadersVisibility.All;
        grid.RowHeaderWidth = 38;
        grid.CanUserResizeRows = false;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, rowBrushKey);
        grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, alternateRowBrushKey);
        DataGridTableBehavior.SetShowRowNumbers(grid, true);
        DataGridTableBehavior.SetEnableStandardContextMenu(grid, enableContextMenu);
    }
}
