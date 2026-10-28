using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Interaction;

/// <summary>
/// Standard Excel-like table behavior used by every DataGrid in the ERP.
/// Provides row selection, a consistent right-click menu, row insertion/deletion,
/// row-height expansion and copy helpers without requiring per-window XAML.
/// </summary>
public static class DataGridTableBehavior
{
    public static readonly DependencyProperty EnableStandardContextMenuProperty =
        DependencyProperty.RegisterAttached(
            "EnableStandardContextMenu",
            typeof(bool),
            typeof(DataGridTableBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    private const double NormalRowHeight = 30d;
    private const double ExpandedRowHeight = 38d;

    public static void SetEnableStandardContextMenu(DependencyObject element, bool value) =>
        element.SetValue(EnableStandardContextMenuProperty, value);

    public static bool GetEnableStandardContextMenu(DependencyObject element) =>
        (bool)element.GetValue(EnableStandardContextMenuProperty);

    private static double GetNormalRowHeight(DataGrid grid) =>
        grid.TryFindResource("TableRowHeight") is double rowHeight ? rowHeight : NormalRowHeight;

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not true) return;

        // Selection must be row-based so the active record is visually obvious.
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.ContextMenuOpening += Grid_ContextMenuOpening;
        grid.PreviewMouseRightButtonDown += Grid_PreviewMouseRightButtonDown;
        grid.AddHandler(Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(Grid_PreviewKeyDown),
            handledEventsToo: true);
    }

    private static void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item == null) return;

        // Right-clicking a row makes it the active row before the menu opens.
        if (!grid.SelectedItems.Contains(row.Item))
            grid.SelectedItem = row.Item;
    }

    private static void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Under an input method (IME / text services, common with Arabic layouts)
        // the real key arrives as Key.ImeProcessed; resolve it so Enter is seen.
        var effectiveKey = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        if (effectiveKey == Key.Enter)
        {
            DiagEnter("received", sender as DataGrid, e,
                $"effectiveKey={effectiveKey}");
        }

        // View (read-only) mode: Enter still moves to the next cell in the row so
        // the table behaves the same whether or not the form is being edited.
        // Nothing is committed or edited here.
        if (effectiveKey == Key.Enter
            && sender is DataGrid browseGrid
            && browseGrid.IsReadOnly
            && browseGrid.IsEnabled
            && !IsColumnHeaderFocused())
        {
            // Focus is not required to be inside a cell: after the data is reloaded
            // (for example right after Save) it often sits on the row or on the grid
            // itself, and Enter must still move horizontally instead of falling
            // through to the DataGrid default (one row down).
            e.Handled = true;

            DiagEnter("browse (read-only): moving horizontally", browseGrid, e, "");

            var browseItem = browseGrid.CurrentCell.Item ?? browseGrid.SelectedItem;
            var browseColumn = browseGrid.CurrentCell.Column;

            browseGrid.Dispatcher.BeginInvoke(
                new Action(() => MoveToNextEditableCell(browseGrid, browseItem, browseColumn, edit: false)),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        if (sender is not DataGrid grid || grid.IsReadOnly || !grid.IsEnabled)
        {
            if (effectiveKey == Key.Enter)
                DiagEnter("ignored: grid null/read-only/disabled", sender as DataGrid, e, "");
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopySelection(grid);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            PasteClipboard(grid);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
        {
            CopySelection(grid);
            DeleteSelectedRows(grid);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            grid.SelectAll();
            e.Handled = true;
            return;
        }

        if (effectiveKey is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter))
            return;

        // Enter must never fall through to the DataGrid default (move to the row
        // below), even when keyboard focus is on the row or the grid itself and
        // not inside a cell (this happens whenever a target cell could not be
        // focused, for example because its column is virtualized).
        if (effectiveKey != Key.Enter)
        {
            if (Keyboard.FocusedElement is not DependencyObject focused) return;
            var cell = FindParent<DataGridCell>(focused);
            if (cell == null) return;
        }

        e.Handled = true;
        var key = effectiveKey;

        // Remember where the key was pressed. The DataGrid's own Enter handling
        // (commit + move to the row below) may still run before the action below,
        // so the target must be computed from this origin and never from
        // whatever CurrentCell happens to be by then.
        var originItem = grid.CurrentCell.Item ?? grid.SelectedItem;
        var originColumn = grid.CurrentCell.Column;

        grid.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                MoveCell(grid, key, originItem, originColumn);

                if (key == Key.Enter)
                    DiagEnter("moved", grid, null,
                        $"currentColumn={DescribeColumn(grid.CurrentCell.Column)}; " +
                        $"currentRow={grid.Items.IndexOf(grid.CurrentCell.Item)}");
            }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------
    //  Diagnostics: one log line per Enter stage so the real path is visible
    //  in log/log.log (search for "DGTB-Enter").
    // ------------------------------------------------------------------
    private static string DescribeColumn(DataGridColumn? column)
    {
        if (column == null) return "null";
        var header = column.Header?.ToString();
        if (column.Header is TextBlock tb) header = tb.Text;
        return $"#{column.DisplayIndex}:{header}";
    }

    private static void DiagEnter(string stage, DataGrid? grid, KeyEventArgs? e, string extra)
    {
        try
        {
            var focused = Keyboard.FocusedElement?.GetType().Name ?? "null";
            var details =
                $"stage={stage}; grid={grid?.Name}; focused={focused}; " +
                $"readOnly={grid?.IsReadOnly}; enabled={grid?.IsEnabled}; " +
                $"rtl={grid?.FlowDirection}; " +
                $"cell={DescribeColumn(grid?.CurrentCell.Column)}; " +
                $"rawKey={(e == null ? "-" : e.Key.ToString())}; {extra}";

            AppHost.Logging.LogLifecycle("DGTB-Enter", details);
        }
        catch { }
    }

    private static bool IsColumnHeaderFocused() =>
        Keyboard.FocusedElement is DependencyObject focused
        && FindParent<System.Windows.Controls.Primitives.DataGridColumnHeader>(focused) != null;

    private static void MoveCell(DataGrid grid, Key key, object? originItem, DataGridColumn? originColumn)
    {
        if (key == Key.Enter)
        {
            MoveToNextEditableCell(grid, originItem, originColumn);
            return;
        }

        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        if (columns.Count == 0 || grid.Items.Count == 0) return;

        var currentColumn = originColumn ?? grid.CurrentCell.Column;
        var currentItem = originItem ?? grid.CurrentCell.Item;
        if (currentColumn == null || currentItem == null) return;

        var col = columns.IndexOf(currentColumn);
        var row = grid.Items.IndexOf(currentItem);
        if (col < 0 || row < 0) return;

        var rtl = grid.FlowDirection == FlowDirection.RightToLeft;
        var nextCol = col;
        var nextRow = row;

        switch (key)
        {
            case Key.Left:
                nextCol += rtl ? 1 : -1;
                break;
            case Key.Right:
            case Key.Enter:
                nextCol += rtl ? -1 : 1;
                break;
            case Key.Up:
                nextRow--;
                break;
            case Key.Down:
                nextRow++;
                break;
        }

        if (nextCol < 0 || nextCol >= columns.Count ||
            nextRow < 0 || nextRow >= grid.Items.Count)
            return;

        var item = grid.Items[nextRow];
        var column = columns[nextCol];

        grid.SelectedItem = item;
        grid.CurrentCell = new DataGridCellInfo(item, column);
        grid.ScrollIntoView(item, column);

        grid.Dispatcher.BeginInvoke(
            new Action(() => FocusCurrentCell(grid, item, column)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Enter moves to the next editable cell in the same row and starts editing it.
    /// "Next" follows DisplayIndex, which already mirrors under RightToLeft, so the
    /// order matches the reading direction (right to left in Arabic, left to right
    /// otherwise). After the last editable cell it continues at the first editable
    /// cell of the next row; on the last row it stays where it is.
    /// </summary>
    private static void MoveToNextEditableCell(
        DataGrid grid, object? originItem, DataGridColumn? originColumn, bool edit = true)
    {
        // In view mode every column reports IsReadOnly, so all visible columns count.
        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible && (!edit || !c.IsReadOnly))
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        if (columns.Count == 0 || grid.Items.Count == 0) return;

        var currentColumn = originColumn ?? grid.CurrentCell.Column;
        var currentItem = originItem ?? grid.CurrentCell.Item ?? grid.SelectedItem;

        object item;
        DataGridColumn? column;

        if (currentItem == null)
        {
            // No current cell at all: start at the first editable cell.
            item = grid.Items[0];
            column = columns[0];
        }
        else
        {
            var row = grid.Items.IndexOf(currentItem);
            if (row < 0) return;

            item = currentItem;
            column = currentColumn == null
                ? columns[0]
                : columns.FirstOrDefault(c => c.DisplayIndex > currentColumn.DisplayIndex);

            if (column == null)
            {
                if (row + 1 >= grid.Items.Count) return;
                item = grid.Items[row + 1];
                column = columns[0];
            }
        }

        var targetItem = item;
        var targetColumn = column;

        grid.SelectedItem = targetItem;
        grid.CurrentCell = new DataGridCellInfo(targetItem, targetColumn);
        grid.ScrollIntoView(targetItem, targetColumn);

        grid.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (edit)
                    FocusAndBeginEdit(grid, targetItem, targetColumn);
                else
                    FocusCurrentCell(grid, targetItem, targetColumn);
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void FocusAndBeginEdit(DataGrid grid, object item, DataGridColumn column)
    {
        try
        {
            FocusCurrentCell(grid, item, column);

            // Focusing can reset the current cell; make sure it is the target.
            grid.CurrentCell = new DataGridCellInfo(item, column);

            if (!grid.BeginEdit())
            {
                // The cell container may not be generated yet (virtualized row or
                // column): retry once after layout has settled.
                grid.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        try
                        {
                            FocusCurrentCell(grid, item, column);
                            grid.CurrentCell = new DataGridCellInfo(item, column);
                            grid.BeginEdit();
                        }
                        catch { }
                    }),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch { }
    }

    private static bool FocusCurrentCell(DataGrid grid, object item, DataGridColumn column)
    {
        try
        {
            grid.UpdateLayout();

            // Realize the row and (with column virtualization) the column.
            grid.ScrollIntoView(item, column);
            grid.UpdateLayout();

            var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            if (row == null)
            {
                grid.ScrollIntoView(item);
                grid.UpdateLayout();
                row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            }

            if (row != null)
            {
                foreach (var cell in FindVisualChildren<DataGridCell>(row))
                {
                    if (ReferenceEquals(cell.Column, column))
                    {
                        cell.Focus();
                        return true;
                    }
                }
            }

            grid.Focus();
        }
        catch { }

        return false;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null) yield break;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void CopySelection(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible && !c.IsReadOnly)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        if (columns.Count == 0) return;

        var rows = grid.SelectedItems.Cast<object>().ToList();
        if (rows.Count == 0 && grid.CurrentCell.Item != null)
            rows.Add(grid.CurrentCell.Item);

        if (rows.Count == 0) return;

        var lines = rows.Select(row =>
            string.Join("\t", columns.Select(c => GetCellText(c, row))));

        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
        catch { }
    }

    private static void PasteClipboard(DataGrid grid)
    {
        if (!Clipboard.ContainsText()) return;

        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var raw = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(raw)) return;

        var rows = ParseClipboard(raw);
        if (rows.Count == 0) return;

        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible && !c.IsReadOnly)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        if (columns.Count == 0) return;

        var anchor = columns.IndexOf(grid.CurrentCell.Column);
        if (anchor < 0) anchor = 0;

        if (rows.Count > 1 && LooksLikeHeaderRow(rows[0], columns, anchor))
            rows.RemoveAt(0);

        if (rows.Count == 0) return;

        var itemType = GetItemType(grid.ItemsSource);
        if (itemType == null)
        {
            ShowPasteError(grid, "EXL-3001", "TargetTable");
            return;
        }

        var existing = grid.CurrentCell.Item;
        var startRow = existing == null ? grid.Items.Count : grid.Items.IndexOf(existing);
        if (startRow < 0) startRow = grid.Items.Count;

        var errors = new List<string>();
        var staged = new List<(int row, object? item, List<(PropertyInfo property, object? value)> values)>();

        for (var r = 0; r < rows.Count; r++)
        {
            var item = r == 0 && startRow < grid.Items.Count
                ? grid.Items[startRow]
                : CreateItem(itemType);

            if (item == null)
            {
                errors.Add(FormatPasteIssue("EXL-3001", $"row={r + 1}"));
                continue;
            }

            var values = new List<(PropertyInfo property, object? value)>();
            for (var c = 0; c < rows[r].Count; c++)
            {
                var targetIndex = anchor + c;
                if (targetIndex >= columns.Count)
                {
                    errors.Add(FormatPasteIssue("EXL-3002", $"row={r + 1}"));
                    break;
                }

                var property = GetBindingProperty(columns[targetIndex], itemType);
                if (property == null || !property.CanWrite)
                {
                    errors.Add(FormatPasteIssue("EXL-3003", $"row={r + 1};column={targetIndex + 1}"));
                    continue;
                }

                if (!TryConvertValue(rows[r][c], property.PropertyType, out var value))
                {
                    errors.Add(FormatPasteIssue("EXL-3004", $"row={r + 1};column={targetIndex + 1};value={rows[r][c]}"));
                    continue;
                }

                values.Add((property, value));
            }

            staged.Add((startRow + r, item, values));
        }

        if (errors.Count > 0)
        {
            ShowPasteError(grid, "EXL-3004", string.Join(Environment.NewLine, errors.Take(12)) +
                (errors.Count > 12 ? Environment.NewLine + $"... {errors.Count - 12} more errors." : ""));
            return;
        }

        if (grid.ItemsSource is not IList list || list.IsReadOnly || list.IsFixedSize)
        {
            ShowPasteError(grid, "EXL-3005", "TargetTable");
            return;
        }

        foreach (var entry in staged)
        {
            if (!list.Contains(entry.item))
                list.Add(entry.item);

            foreach (var (property, value) in entry.values)
                property.SetValue(entry.item, value);

            grid.SelectedItem = entry.item;
        }

        var last = staged.LastOrDefault().item;
        if (last != null)
        {
            var lastColumnIndex = Math.Min(anchor + rows[^1].Count - 1, columns.Count - 1);
            grid.CurrentCell = new DataGridCellInfo(last, columns[lastColumnIndex]);
            grid.ScrollIntoView(last, columns[lastColumnIndex]);
        }
    }

    private static bool LooksLikeHeaderRow(
        IReadOnlyList<string> row,
        IReadOnlyList<DataGridColumn> columns,
        int anchor)
    {
        var matches = 0;
        var checkedCells = 0;

        for (var i = 0; i < row.Count && anchor + i < columns.Count; i++)
        {
            var text = NormalizeHeader(row[i]);
            if (text.Length == 0) continue;

            checkedCells++;
            var column = columns[anchor + i];
            var binding = GetBindingPropertyName(column);
            var header = GetHeaderText(column);

            if (text == NormalizeHeader(binding) || text == NormalizeHeader(header))
                matches++;
        }

        return checkedCells > 0 && matches >= Math.Max(1, Math.Min(2, checkedCells));
    }

    private static string? GetBindingPropertyName(DataGridColumn column)
    {
        if (column is not DataGridBoundColumn bound ||
            bound.Binding is not Binding binding ||
            binding.Path == null)
            return null;

        return binding.Path.Path.Split('.').FirstOrDefault();
    }

    private static string GetHeaderText(DataGridColumn column)
    {
        return column.Header switch
        {
            string s => s,
            TextBlock tb => tb.Text,
            ContentControl cc when cc.Content is string s => s,
            _ => column.Header?.ToString() ?? string.Empty
        };
    }

    private static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var chars = value.Trim()
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray();

        return new string(chars).ToUpperInvariant();
    }

    private static List<List<string>> ParseClipboard(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized
            .Split('\n')
            .Select(line => line.EndsWith('\t')
                ? line[..^1]
                : line)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('\t').ToList())
            .ToList();
    }

    private static PropertyInfo? GetBindingProperty(DataGridColumn column, Type itemType)
    {
        if (column is not DataGridBoundColumn bound ||
            bound.Binding is not Binding binding ||
            binding.Path == null ||
            string.IsNullOrWhiteSpace(binding.Path.Path))
            return null;

        var path = binding.Path.Path.Split('.').FirstOrDefault();
        return string.IsNullOrWhiteSpace(path)
            ? null
            : itemType.GetProperty(path,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
    }

    private static object? CreateItem(Type itemType)
    {
        try { return Activator.CreateInstance(itemType); }
        catch { return null; }
    }

    private static bool TryConvertValue(string text, Type targetType, out object? value)
    {
        text = text.Trim();
        var nullable = Nullable.GetUnderlyingType(targetType);
        var type = nullable ?? targetType;

        if (text.Length == 0)
        {
            value = nullable != null || !type.IsValueType ? null : Activator.CreateInstance(type);
            return true;
        }

        if (type == typeof(string))
        {
            value = text;
            return true;
        }

        if (type == typeof(decimal))
        {
            if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var d) ||
                decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                    CultureInfo.CurrentCulture, out d))
            {
                value = d;
                return true;
            }
        }
        else if (type == typeof(int) &&
                 int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            value = i;
            return true;
        }
        else if (type == typeof(long) &&
                 long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            value = l;
            return true;
        }
        else if (type == typeof(double) &&
                 double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                     CultureInfo.InvariantCulture, out var dbl))
        {
            value = dbl;
            return true;
        }
        else if (type == typeof(Guid) && Guid.TryParse(text, out var g))
        {
            value = g;
            return true;
        }
        else if (type == typeof(DateTime) && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
        {
            value = dt;
            return true;
        }
        else if (type.IsEnum && Enum.TryParse(type, text, true, out var e))
        {
            value = e;
            return true;
        }

        value = null;
        return false;
    }

    private static string FormatPasteIssue(string code, string context)
    {
        var definition = ErrorCatalog.Resolve(code);
        return $"[{definition.Code}] {definition.Name} | {definition.CatalogRef} | {context}";
    }

    private static void ShowPasteError(DataGrid grid, string code, string details)
    {
        var definition = ErrorCatalog.Resolve(code);
        var message = $"[{definition.Code}] {definition.Name}\nCatalog: {definition.CatalogRef}\nSeverity: {definition.Severity}\n{definition.Message}";
        if (!string.IsNullOrWhiteSpace(details))
            message += $"\nDetails: {details}";

        var owner = Window.GetWindow(grid);
        var icon = string.Equals(definition.Severity, "Critical", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(definition.Severity, "Error", StringComparison.OrdinalIgnoreCase)
            ? MessageBoxImage.Error
            : MessageBoxImage.Warning;
        MessageBox.Show(owner, message, "Data Operation Error", MessageBoxButton.OK, icon);
    }

    private static void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        var menu = grid.ContextMenu;
        if (menu == null)
        {
            menu = new ContextMenu { FlowDirection = grid.FlowDirection };
            grid.ContextMenu = menu;
        }

        menu.FlowDirection = grid.FlowDirection;
        menu.Items.Clear();

        var selectedCount = grid.SelectedItems.Count;
        var canEdit = !grid.IsReadOnly && grid.IsEnabled;
        var canDelete = canEdit && CanModifyItems(grid.ItemsSource);
        var canInsert = canEdit && CanInsertItem(grid.ItemsSource);

        Add(menu, "DataGrid.Context.Select", "Select", (_, _) =>
        {
            if (grid.SelectedItem == null && grid.Items.Count > 0)
                grid.SelectedIndex = 0;
        }, selectedCount > 0 || grid.Items.Count > 0);

        Add(menu, "DataGrid.Context.SelectAll", "Select All", (_, _) => grid.SelectAll(), grid.Items.Count > 0);
        Add(menu, "DataGrid.Context.ClearSelection", "Clear Selection", (_, _) => grid.UnselectAll(), selectedCount > 0);

        menu.Items.Add(new Separator());

        Add(menu, "DataGrid.Context.InsertRow", "Insert Row", (_, _) => InsertRow(grid), canInsert);
        Add(menu, "DataGrid.Context.Delete", "Delete", (_, _) => DeleteSelectedRows(grid), canDelete && selectedCount > 0);

        menu.Items.Add(new Separator());

        Add(menu, "DataGrid.Context.Expand", "Expand Table", (_, _) => grid.RowHeight = ExpandedRowHeight,
            grid.Items.Count > 0 && grid.RowHeight < ExpandedRowHeight);
        var normalRowHeight = GetNormalRowHeight(grid);
        Add(menu, "DataGrid.Context.NormalSize", "Reset Row Height", (_, _) => grid.RowHeight = normalRowHeight,
            Math.Abs(grid.RowHeight - normalRowHeight) > 0.1);

        menu.Items.Add(new Separator());

        Add(menu, "DataGrid.Context.CopyRow", "Copy Row", (_, _) => CopySelectedRows(grid), selectedCount > 0);
        Add(menu, "DataGrid.Context.CopyCell", "Copy Cell", (_, _) => CopyCurrentCell(grid), grid.CurrentCell.Item != null);
        Add(menu, "DataGrid.Context.Paste", "Paste from Excel", (_, _) => PasteClipboard(grid), canEdit && Clipboard.ContainsText());
        Add(menu, "DataGrid.Context.FitColumns", "Fit Columns", (_, _) => FitColumns(grid), grid.Columns.Count > 0);
    }

    private static void Add(ContextMenu menu, string key, string fallback, RoutedEventHandler action, bool enabled)
    {
        var item = new MenuItem
        {
            Header = Loc.Get(key, fallback),
            IsEnabled = enabled
        };
        item.Click += action;
        menu.Items.Add(item);
    }

    private static bool CanModifyItems(object? source)
    {
        if (source is IList list) return !list.IsReadOnly && !list.IsFixedSize;
        return source is IEditableCollectionView editable && !editable.IsEditingItem;
    }

    private static bool CanInsertItem(object? source)
    {
        if (source is IList list)
        {
            if (list.IsReadOnly || list.IsFixedSize) return false;
            var itemType = GetItemType(source);
            return itemType != null && itemType.GetConstructor(Type.EmptyTypes) != null;
        }

        return source is IEditableCollectionView editable && editable.CanAddNew;
    }

    private static Type? GetItemType(object? source)
    {
        if (source == null) return null;
        var type = source.GetType();
        if (type.IsGenericType) return type.GetGenericArguments().FirstOrDefault();
        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
            ?.GetGenericArguments()[0];
    }

    private static void InsertRow(DataGrid grid)
    {
        if (!CanInsertItem(grid.ItemsSource)) return;

        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var source = grid.ItemsSource;
        if (source is IList list)
        {
            var type = GetItemType(source);
            if (type == null) return;

            object? item;
            try { item = Activator.CreateInstance(type); }
            catch { return; }
            if (item == null) return;

            var index = grid.SelectedIndex >= 0 ? grid.SelectedIndex + 1 : list.Count;
            index = Math.Clamp(index, 0, list.Count);
            list.Insert(index, item);
            grid.SelectedItem = item;
            grid.ScrollIntoView(item);
            return;
        }

        if (source is IEditableCollectionView editable && editable.CanAddNew)
        {
            var item = editable.AddNew();
            editable.CommitNew();
            grid.SelectedItem = item;
            grid.ScrollIntoView(item);
        }
    }

    private static void DeleteSelectedRows(DataGrid grid)
    {
        if (grid.IsReadOnly || grid.SelectedItems.Count == 0) return;

        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var selected = grid.SelectedItems.Cast<object>().ToList();
        if (grid.ItemsSource is IList list && !list.IsReadOnly && !list.IsFixedSize)
        {
            foreach (var item in selected)
                if (list.Contains(item)) list.Remove(item);
            return;
        }

        if (grid.ItemsSource is IEditableCollectionView editable)
        {
            foreach (var item in selected)
            {
                try { editable.Remove(item); }
                catch { /* Some views expose a read-only Remove operation. */ }
            }
        }
    }

    private static void CopySelectedRows(DataGrid grid)
    {
        var rows = grid.SelectedItems.Cast<object>().ToList();
        if (rows.Count == 0) return;

        var columns = grid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Where(c => c.Visibility == Visibility.Visible)
            .ToList();

        var lines = rows.Select(row => string.Join("\t", columns.Select(c => GetCellText(c, row))));
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private static void CopyCurrentCell(DataGrid grid)
    {
        if (grid.CurrentCell.Item == null) return;
        var column = grid.CurrentCell.Column;
        if (column == null) return;
        Clipboard.SetText(GetCellText(column, grid.CurrentCell.Item));
    }

    private static void FitColumns(DataGrid grid)
    {
        foreach (var column in grid.Columns.Where(c => c.Visibility == Visibility.Visible))
            column.Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells);
    }

    private static string GetCellText(DataGridColumn column, object row)
    {
        if (column is not DataGridBoundColumn bound) return string.Empty;
        if (bound.Binding is not Binding binding || binding.Path == null) return string.Empty;

        object? value = row;
        foreach (var part in binding.Path.Path.Split('.'))
        {
            if (value == null) return string.Empty;
            var property = value.GetType().GetProperty(part,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property == null) return string.Empty;
            value = property.GetValue(value);
        }

        return value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T result) return result;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
