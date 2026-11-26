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
        string alternateRowBrushKey = "TableAlternateRowBrush", bool showRowNumbers = true,
        bool enableContextMenu = true)
    {
        ArgumentNullException.ThrowIfNull(grid);
        grid.AutoGenerateColumns = false;
        grid.AlternationCount = 2;
        // Tables behave like spreadsheets: cells, not entire rows, are active.
        grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.EnableRowVirtualization = true;
        grid.EnableColumnVirtualization = true;
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(grid, true);
        grid.HeadersVisibility = showRowNumbers ? DataGridHeadersVisibility.All : DataGridHeadersVisibility.Column;
        grid.RowHeaderWidth = showRowNumbers ? 38 : 0;
        grid.CanUserResizeRows = false;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, rowBrushKey);
        grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, alternateRowBrushKey);
        if (showRowNumbers)
            DataGridTableBehavior.SetShowRowNumbers(grid, true);
        DataGridTableBehavior.SetEnableCellInteraction(grid, true);
        if (enableContextMenu)
            DataGridTableBehavior.SetEnableStandardContextMenu(grid, true);
    }
}
