using System.Collections.Generic;
using ShouTech.App.Interaction;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Excel-style Enter navigation: the pure advance computation behind
    /// DataGridTableBehavior (Enter = next editable cell in reading order,
    /// Shift+Enter = previous, wrapping across rows, read-only columns
    /// skipped, focus stays at the ends).
    ///
    /// The column flags are always supplied in DisplayIndex order, which is
    /// the reading order in LTR and RTL alike (under RightToLeft the grid
    /// mirrors visually, but the first DisplayIndex column is still read
    /// first), so one ordered walk proves both layout directions.
    /// </summary>
    public class GridCellNavigatorTests
    {
        private static GridCellNavigator.CellPosition Pos(int row, int col) => new(row, col);

        private static GridCellNavigator.CellPosition? Advance(
            bool[] editable, int rows, int row, int col, int direction)
            => GridCellNavigator.Advance(editable, rows, Pos(row, col), direction);

        [Theory]
        [InlineData(0, -1, 1, 2, 1, 0)]
        [InlineData(0, 1, 1, 0, 1, 2)]
        [InlineData(-1, 0, 1, 0, 0, 0)]
        [InlineData(1, 0, 1, 0, 2, 0)]
        public void DirectionalMove_SkipsReadOnlyColumns(
            int rowStep, int columnStep, int startRow, int startColumn,
            int expectedRow, int expectedColumn)
        {
            var editable = new[] { true, false, true };
            var actual = GridCellNavigator.MoveDirectional(
                editable, 3, Pos(startRow, startColumn), rowStep, columnStep);
            Assert.Equal(Pos(expectedRow, expectedColumn), actual);
        }

        [Fact]
        public void DirectionalMove_StopsAtGridEdge()
        {
            var editable = new[] { true, false, true };
            Assert.Null(GridCellNavigator.MoveDirectional(
                editable, 2, Pos(0, 0), 0, -1));
            Assert.Null(GridCellNavigator.MoveDirectional(
                editable, 2, Pos(1, 2), 1, 0));
        }

        [Fact]
        public void DirectionalMove_RejectsInvalidDirection()
        {
            Assert.Null(GridCellNavigator.MoveDirectional(
                new[] { true }, 1, Pos(0, 0), 1, 1));
        }

        [Fact]
        public void Forward_MovesToNextEditableCellInSameRow()
        {
            var editable = new[] { true, true, true };
            Assert.Equal(Pos(0, 1), Advance(editable, 3, 0, 0, +1));
            Assert.Equal(Pos(0, 2), Advance(editable, 3, 0, 1, +1));
        }

        [Fact]
        public void Forward_SkipsReadOnlyColumns()
        {
            var editable = new[] { true, false, false, true };
            Assert.Equal(Pos(0, 3), Advance(editable, 2, 0, 0, +1));
        }

        [Fact]
        public void Forward_WrapsToFirstEditableCellOfNextRow()
        {
            var editable = new[] { false, true, false };
            Assert.Equal(Pos(1, 1), Advance(editable, 3, 0, 1, +1));
            Assert.Equal(Pos(2, 1), Advance(editable, 3, 1, 1, +1));
        }

        [Fact]
        public void Forward_AtLastEditableCell_StaysPut()
        {
            var editable = new[] { true, false, true };
            Assert.Null(Advance(editable, 2, 1, 2, +1));
        }

        [Fact]
        public void Backward_MovesToPreviousEditableCellInSameRow()
        {
            var editable = new[] { true, true, true };
            Assert.Equal(Pos(0, 1), Advance(editable, 3, 0, 2, -1));
            Assert.Equal(Pos(0, 0), Advance(editable, 3, 0, 1, -1));
        }

        [Fact]
        public void Backward_SkipsReadOnlyColumns()
        {
            var editable = new[] { true, false, false, true };
            Assert.Equal(Pos(0, 0), Advance(editable, 2, 0, 3, -1));
        }

        [Fact]
        public void Backward_WrapsToLastEditableCellOfPreviousRow()
        {
            var editable = new[] { false, true, false };
            Assert.Equal(Pos(0, 1), Advance(editable, 3, 1, 1, -1));
            Assert.Equal(Pos(1, 1), Advance(editable, 3, 2, 1, -1));
        }

        [Fact]
        public void Backward_AtFirstEditableCell_StaysPut()
        {
            var editable = new[] { true, false, true };
            Assert.Null(Advance(editable, 2, 0, 0, -1));
        }

        [Fact]
        public void AllColumnsReadOnly_NoMoveEitherDirection()
        {
            var editable = new[] { false, false };
            Assert.Null(Advance(editable, 4, 1, 0, +1));
            Assert.Null(Advance(editable, 4, 1, 1, -1));
        }

        [Fact]
        public void NoColumnsOrNoRows_NoMove()
        {
            Assert.Null(Advance(new bool[0], 3, 0, -1, +1));
            Assert.Null(Advance(new[] { true }, 0, 0, -1, +1));
        }

        [Fact]
        public void FromBeforeFirstColumn_ForwardLandsOnFirstEditableCell()
        {
            // The adapter's "no current cell" start for Enter.
            var editable = new[] { false, true, true };
            Assert.Equal(Pos(0, 1), Advance(editable, 2, 0, -1, +1));
        }

        [Fact]
        public void FromAfterLastColumn_BackwardLandsOnLastEditableCell()
        {
            // The adapter's "no current cell" start for Shift+Enter.
            var editable = new[] { true, true, false };
            Assert.Equal(Pos(1, 1), Advance(editable, 2, 1, 3, -1));
        }

        [Fact]
        public void CurrentColumnReadOnly_AdvancesFromItsPosition()
        {
            // Enter pressed while a read-only column (e.g. line number) is
            // current: forward takes the next editable after it, backward
            // the previous editable before it.
            var editable = new[] { true, false, true };
            Assert.Equal(Pos(0, 2), Advance(editable, 2, 0, 1, +1));
            Assert.Equal(Pos(0, 0), Advance(editable, 2, 0, 1, -1));
        }

        [Fact]
        public void SingleEditableColumn_WalksDownAndUpTheRows()
        {
            var editable = new[] { false, true, false };
            Assert.Equal(Pos(1, 1), Advance(editable, 3, 0, 1, +1));
            Assert.Equal(Pos(2, 1), Advance(editable, 3, 1, 1, +1));
            Assert.Null(Advance(editable, 3, 2, 1, +1));
            Assert.Equal(Pos(0, 1), Advance(editable, 3, 1, 1, -1));
            Assert.Null(Advance(editable, 3, 0, 1, -1));
        }

        [Fact]
        public void Traversal_FollowsReadingOrder_ForLtrAndRtl()
        {
            // DisplayIndex order == reading order in both layout directions:
            // in an RTL grid the walk below is seen moving right-to-left, in
            // an LTR grid left-to-right, but the cell sequence is identical.
            var editable = new[] { true, false, true, true };
            var expected = new[]
            {
                Pos(0, 0), Pos(0, 2), Pos(0, 3),
                Pos(1, 0), Pos(1, 2), Pos(1, 3),
                Pos(2, 0), Pos(2, 2), Pos(2, 3)
            };

            var forward = new List<GridCellNavigator.CellPosition>();
            var cursor = Pos(0, -1);
            while (GridCellNavigator.Advance(editable, 3, cursor, +1) is { } next)
            {
                forward.Add(next);
                cursor = next;
            }
            Assert.Equal(expected, forward);

            var backward = new List<GridCellNavigator.CellPosition>();
            cursor = Pos(2, editable.Length);
            while (GridCellNavigator.Advance(editable, 3, cursor, -1) is { } prev)
            {
                backward.Add(prev);
                cursor = prev;
            }
            backward.Reverse();
            Assert.Equal(expected, backward);
        }
    }
}
