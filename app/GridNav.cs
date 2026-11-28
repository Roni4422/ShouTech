using System;
using System.Collections.Generic;

namespace ShouTech.App.Interaction;

/// <summary>
/// Pure cell-advance computation behind the Excel-style Enter navigation of
/// <see cref="DataGridTableBehavior"/>. Kept free of any WPF type so the
/// navigation rules can be unit-tested directly.
///
/// Columns are supplied in logical reading order (the grid's DisplayIndex
/// order). DisplayIndex order is the reading order in both layout directions:
/// under a RightToLeft FlowDirection WPF mirrors the visual placement, but
/// the first column in DisplayIndex order is still the first column read
/// (it appears at the right edge). "Forward" therefore always means "next in
/// reading order" and no left/right knowledge is needed here.
///
/// Rows and columns are addressed by index. As the current position, Column
/// may be -1 ("before the first column") or columnEditable.Count ("after the
/// last column") so callers can express "no current column" for a row.
/// </summary>
public static class GridCellNavigator
{
    /// <summary>A cell address: item row index + ordered-column index.</summary>
    public readonly record struct CellPosition(int Row, int Column);

    /// <summary>
    /// Moves in one arrow-key direction without wrapping. Read-only columns
    /// are skipped; if no editable cell exists before the grid edge, returns null.
    /// rowStep and columnStep must each be -1, 0, or +1 and cannot both be nonzero.
    /// </summary>
    public static CellPosition? MoveDirectional(
        IReadOnlyList<bool> columnEditable,
        int rowCount,
        CellPosition current,
        int rowStep,
        int columnStep)
    {
        ArgumentNullException.ThrowIfNull(columnEditable);
        if (columnEditable.Count == 0 || rowCount <= 0 ||
            current.Row < 0 || current.Row >= rowCount ||
            current.Column < 0 || current.Column >= columnEditable.Count ||
            Math.Abs(rowStep) + Math.Abs(columnStep) != 1)
            return null;

        var row = current.Row + rowStep;
        var column = current.Column + columnStep;
        while (row >= 0 && row < rowCount &&
               column >= 0 && column < columnEditable.Count)
        {
            if (columnEditable[column])
                return new CellPosition(row, column);

            row += rowStep;
            column += columnStep;
        }

        return null;
    }

    /// <summary>
    /// Computes the next cell to move to.
    /// </summary>
    /// <param name="columnEditable">
    /// One flag per visible column, in reading (DisplayIndex) order: true when
    /// the column may receive the move (an editable column in an entry grid;
    /// every visible column in a read-only browse grid).
    /// </param>
    /// <param name="rowCount">Number of item rows in the grid.</param>
    /// <param name="current">The cell the key was pressed in.</param>
    /// <param name="direction">+1 forward (Enter), -1 backward (Shift+Enter).</param>
    /// <returns>
    /// The target cell, or null when there is no further cell in that
    /// direction — the caller then stays where it is.
    /// </returns>
    public static CellPosition? Advance(
        IReadOnlyList<bool> columnEditable,
        int rowCount,
        CellPosition current,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(columnEditable);

        if (columnEditable.Count == 0 || rowCount <= 0)
            return null;

        if (current.Row < 0 || current.Row >= rowCount)
            return null;

        if (current.Column < -1 || current.Column > columnEditable.Count)
            return null;

        if (direction >= 0)
        {
            // Rest of the current row, in reading order.
            for (var col = current.Column + 1; col < columnEditable.Count; col++)
                if (columnEditable[col])
                    return new CellPosition(current.Row, col);

            // Then the first reachable cell of each following row.
            for (var row = current.Row + 1; row < rowCount; row++)
                for (var col = 0; col < columnEditable.Count; col++)
                    if (columnEditable[col])
                        return new CellPosition(row, col);

            return null;
        }

        // Backward: rest of the current row, against reading order.
        for (var col = current.Column - 1; col >= 0; col--)
            if (columnEditable[col])
                return new CellPosition(current.Row, col);

        // Then the last reachable cell of each preceding row.
        for (var row = current.Row - 1; row >= 0; row--)
            for (var col = columnEditable.Count - 1; col >= 0; col--)
                if (columnEditable[col])
                    return new CellPosition(row, col);

        return null;
    }
}
