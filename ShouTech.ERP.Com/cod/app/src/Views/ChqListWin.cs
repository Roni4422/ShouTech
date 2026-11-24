using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Cheque register (RS-4): filter by status / direction / due-soon, Al-Ameen
/// schedule grid (Cheque No | Direction | Party/Drawer | Bank | Amount | Due |
/// Status | Days-to-due), footer totals per status, and the lifecycle actions
/// (Deposit, Clear, Bounce, Endorse, Print Register) in the bottom command
/// bar. All transitions go through ChequeService (status order enforced,
/// GL posted/reversed through the journal engine). RBAC: VCH.SECURITY.*.
/// </summary>
public sealed class ChqListWin : BaseWin
{
    protected override string? ScheduleTintName => "Indigo";
    /// <summary>Grid row: a cheque plus its computed days-to-due.</summary>
    public sealed class ChequeRow
    {
        public ChequeRow(Cheque cheque) => Cheque = cheque;
        public Cheque Cheque { get; }
        public Guid Id => Cheque.Id;
        public string ChequeNo => Cheque.ChequeNo;
        public string Direction => Cheque.Direction;
        public string Party => Cheque.PartyName ?? Cheque.DrawerName ?? string.Empty;
        public string Bank => Cheque.BankName;
        public string Branch => Cheque.BranchName ?? string.Empty;
        public string Currency => Cheque.CurrencyCode;
        public string IssueText => Cheque.IssueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        public decimal Amount => Cheque.Amount;
        public string DueText => Cheque.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public string Status => Cheque.Status;
        public int DaysToDue => (Cheque.DueDate.ToDateTime(TimeOnly.MinValue) - DateTime.Today).Days;
    }

    private readonly List<ChequeRow> _rows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly ComboBox _statusFilter = new();
    private readonly ComboBox _directionFilter = new();
    private readonly CheckBox _dueSoon = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _refreshButton = new();
    private readonly Button _depositButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _bounceButton = new();
    private readonly Button _endorseButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _printButton = new();
    private readonly Button _closeButton = new();

    public ChqListWin()
    {
        Width = 1180;
        Height = 700;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Vouchers.SecuritiesView))
        {
            MessageBox.Show(this, T("Cheques.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
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

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        StyleButton(_depositButton, "Cheques.Deposit");
        StyleButton(_clearButton, "Cheques.Clear");
        StyleButton(_bounceButton, "Cheques.Bounce");
        StyleButton(_endorseButton, "Cheques.Endorse");
        StyleButton(_cancelButton, "Cheques.CancelCheque");
        StyleButton(_printButton, "Cheques.PrintRegister");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _depositButton.Click += async (_, _) => await DepositAsync();
        _clearButton.Click += async (_, _) => await ClearAsync();
        _bounceButton.Click += async (_, _) => await BounceAsync();
        _endorseButton.Click += async (_, _) => await EndorseAsync();
        _cancelButton.Click += async (_, _) => await CancelAsync();
        _printButton.Click += (_, _) => PrintRegister();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _depositButton, _clearButton, _bounceButton, _endorseButton, _cancelButton, _printButton, _refreshButton, _closeButton })
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

        var content = new DockPanel();
        var filters = CreateFormGrid(3);
        filters.Margin = new Thickness(0, 0, 0, 8);
        _statusFilter.ItemsSource = new[] { string.Empty }.Concat(ChequeStatuses.All).ToArray();
        _statusFilter.SelectedIndex = 0;
        _statusFilter.SelectionChanged += async (_, _) => await RefreshAsync();
        _directionFilter.ItemsSource = new[] { string.Empty, ChequeDirections.In, ChequeDirections.Out };
        _directionFilter.SelectedIndex = 0;
        _directionFilter.SelectionChanged += async (_, _) => await RefreshAsync();
        _dueSoon.Checked += async (_, _) => await RefreshAsync();
        _dueSoon.Unchecked += async (_, _) => await RefreshAsync();
        AddFormField(_labels, filters, 0, 0, "Vouchers.Status", _statusFilter);
        AddFormField(_labels, filters, 0, 1, "Cheques.Direction", _directionFilter);
        AddFormField(_labels, filters, 0, 2, "Cheques.DueSoon", _dueSoon);
        DockPanel.SetDock(filters, Dock.Top);
        content.Children.Add(filters);

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Vouchers.ChequeNo", nameof(ChequeRow.ChequeNo), 110));
        _grid.Columns.Add(TextCol("Cheques.Direction", nameof(ChequeRow.Direction), 80, center: true));
        _grid.Columns.Add(TextCol("Cheques.PartyDrawer", nameof(ChequeRow.Party), 190));
        _grid.Columns.Add(TextCol("Vouchers.Bank", nameof(ChequeRow.Bank), 150));
        _grid.Columns.Add(TextCol("BankCard.Branch", nameof(ChequeRow.Branch), 100));
        _grid.Columns.Add(TextCol("Vouchers.Currency", nameof(ChequeRow.Currency), 70, center: true));
        _grid.Columns.Add(TextCol("Cheques.IssueDate", nameof(ChequeRow.IssueText), 100, center: true));
        _grid.Columns.Add(TextCol("MfgOrder.Amount", nameof(ChequeRow.Amount), 110, numeric: true));
        _grid.Columns.Add(TextCol("Vouchers.DueDate", nameof(ChequeRow.DueText), 100, center: true));
        _grid.Columns.Add(TextCol("Vouchers.Status", nameof(ChequeRow.Status), 110, center: true));
        _grid.Columns.Add(TextCol("Cheques.DaysToDue", nameof(ChequeRow.DaysToDue), 100, numeric: true));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Cheques.RegisterTitle");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _depositButton, _clearButton, _bounceButton, _endorseButton, _cancelButton, _printButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (AppHost.Cheques is not { } service)
            {
                _statusBar.Text = T("Cheques.ServiceUnavailable");
                return;
            }
            var status = _statusFilter.SelectedItem as string;
            var direction = _directionFilter.SelectedItem as string;
            var rows = await service.GetAllAsync(CurrentCompanyCode,
                string.IsNullOrWhiteSpace(status) ? null : status,
                string.IsNullOrWhiteSpace(direction) ? null : direction);
            _rows.Clear();
            foreach (var cheque in rows)
            {
                // Due-soon is a maturity workflow: terminal cheques are
                // never "due soon" (CHQ-16).
                if (_dueSoon.IsChecked == true &&
                    (cheque.Status is ChequeStatuses.Cleared or ChequeStatuses.Bounced or ChequeStatuses.Endorsed or ChequeStatuses.Cancelled
                     || (cheque.DueDate.ToDateTime(TimeOnly.MinValue) - DateTime.Today).Days > 30))
                    continue;
                _rows.Add(new ChequeRow(cheque));
            }
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
            UpdateStatusBar();
        }
        catch (Exception ex) { ReportFailure("Refresh", ex); }
    }

    private void UpdateStatusBar()
    {
        if (_rows.Count == 0)
        {
            _statusBar.Text = "0";
            return;
        }
        var perStatus = _rows.GroupBy(r => r.Status)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}: {g.Count()} · {g.Sum(r => r.Amount):N2}");
        _statusBar.Text = $"{_rows.Count} · " + string.Join(" | ", perStatus);
    }

    private ChequeRow? SelectedRow() => _grid.SelectedItem as ChequeRow;

    private static void RequireManage()
    {
        if (!AppHost.Permissions.Has(PermCat.Vouchers.SecuritiesManage))
            throw new InvalidOperationException(T("Cheques.PermissionDenied"));
        if (AppHost.Cheques is null)
            throw new InvalidOperationException(T("Cheques.ServiceUnavailable"));
    }

    private async Task DepositAsync()
    {
        try
        {
            RequireManage();
            var row = SelectedRow() ?? throw new InvalidOperationException(T("Cheques.SelectSavedCheque"));
            var input = PromptDialog.Show(this, T("Cheques.Deposit"),
                (T("Cheques.DepositBankAccount"), row.Cheque.DepositBankAccountCode ?? string.Empty));
            if (input is null) return;
            await AppHost.Cheques!.DepositAsync(CurrentCompanyCode, row.Id, input[0], DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Lifecycle", ex); }
    }

    private async Task ClearAsync()
    {
        try
        {
            RequireManage();
            var row = SelectedRow() ?? throw new InvalidOperationException(T("Cheques.SelectSavedCheque"));
            string? bankCode = null;
            if (row.Cheque.Direction == ChequeDirections.Out)
            {
                // Outgoing clearance credits the paying bank (CHQ-04).
                var input = PromptDialog.Show(this, T("Cheques.Clear"),
                    (T("Cheques.DepositBankAccount"), row.Cheque.DepositBankAccountCode ?? string.Empty));
                if (input is null) return;
                bankCode = input[0];
            }
            await AppHost.Cheques!.ClearAsync(CurrentCompanyCode, row.Id, DateOnly.FromDateTime(DateTime.Today), CurrentUserId, bankCode);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Lifecycle", ex); }
    }

    private async Task BounceAsync()
    {
        try
        {
            RequireManage();
            var row = SelectedRow() ?? throw new InvalidOperationException(T("Cheques.SelectSavedCheque"));
            var input = PromptDialog.Show(this, T("Cheques.Bounce"),
                (T("Cheques.BounceReason"), string.Empty),
                (T("Cheques.BounceFees"), "0"));
            if (input is null) return;
            var fees = decimal.TryParse(input[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f : 0m;
            await AppHost.Cheques!.BounceAsync(CurrentCompanyCode, row.Id, DateOnly.FromDateTime(DateTime.Today),
                string.IsNullOrWhiteSpace(input[0]) ? null : input[0], fees, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Lifecycle", ex); }
    }

    private async Task EndorseAsync()
    {
        try
        {
            RequireManage();
            var row = SelectedRow() ?? throw new InvalidOperationException(T("Cheques.SelectSavedCheque"));
            var input = PromptDialog.Show(this, T("Cheques.Endorse"),
                (T("Cheques.EndorsedToAccount"), row.Cheque.EndorsedToAccountCode ?? string.Empty));
            if (input is null) return;
            await AppHost.Cheques!.EndorseAsync(CurrentCompanyCode, row.Id, input[0], DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Lifecycle", ex); }
    }

    private async Task CancelAsync()
    {
        try
        {
            RequireManage();
            var row = SelectedRow() ?? throw new InvalidOperationException(T("Cheques.SelectSavedCheque"));
            var input = PromptDialog.Show(this, T("Cheques.CancelCheque"),
                (T("Cheques.CancelReason"), string.Empty));
            if (input is null) return;
            await AppHost.Cheques!.CancelOutgoingAsync(CurrentCompanyCode, row.Id, CurrentUserId,
                string.IsNullOrWhiteSpace(input[0]) ? null : input[0], DateOnly.FromDateTime(DateTime.Today));
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Cancel", ex); }
    }

    /// <summary>Central error reporting (CHQ-18): every failure is
    /// logged with its catalog code (APP-2010, the ChqWin pattern)
    /// instead of a raw unlogged MessageBox.</summary>
    private void ReportFailure(string operation, Exception exception)
    {
        const string errorCode = "APP-2010";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var context = $"Operation={operation}; Window={GetType().Name}; Company={CurrentCompanyCode}";
        AppHost.Logging?.LogError("WindowPersistence",
            $"[{definition.Code}] {definition.Name} | {context} | {exception.Message}", exception);
        MessageBox.Show(this, $"[{definition.Code}] {exception.Message}", Title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------
    // Print register (JrnWin A4 pattern).
    // ------------------------------------------------------------------
    private void PrintRegister()
    {
        var root = new Border { Background = Brushes.White, Padding = new Thickness(40), Width = 794, Height = 1123 };
        var stack = new StackPanel();
        root.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = CurrentCompanyDisplay, FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))
        });
        stack.Children.Add(new TextBlock
        {
            Text = T("Cheques.RegisterTitle"), FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 10)
        });
        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var width in new[] { 100, 70, 150, 120, 90, 90, 100, 60 })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        void Cell(int row, int col, string text, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(3, 2, 3, 2), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col); table.Children.Add(tb);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, T("Vouchers.ChequeNo"), true); Cell(0, 1, T("Cheques.Direction"), true);
        Cell(0, 2, T("Cheques.PartyDrawer"), true); Cell(0, 3, T("Vouchers.Bank"), true);
        Cell(0, 4, T("MfgOrder.Amount"), true); Cell(0, 5, T("Vouchers.DueDate"), true);
        Cell(0, 6, T("Vouchers.Status"), true); Cell(0, 7, T("Cheques.DaysToDue"), true);
        var rowIndex = 1;
        foreach (var r in _rows)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, r.ChequeNo); Cell(rowIndex, 1, r.Direction); Cell(rowIndex, 2, r.Party);
            Cell(rowIndex, 3, r.Bank); Cell(rowIndex, 4, r.Amount.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 5, r.DueText); Cell(rowIndex, 6, r.Status);
            Cell(rowIndex, 7, r.DaysToDue.ToString(CultureInfo.InvariantCulture));
            rowIndex++;
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 3, T("Invoice.Total"), true);
        Cell(rowIndex, 4, _rows.Sum(r => r.Amount).ToString("N2", CultureInfo.InvariantCulture), true);
        stack.Children.Add(table);

        var paginator = new SinglePagePaginator(root);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, Title);
    }

    private sealed class SinglePagePaginator : DocumentPaginator
    {
        private readonly FrameworkElement _page;
        public SinglePagePaginator(FrameworkElement page) => _page = page;
        public override bool IsPageCountValid => true;
        public override int PageCount => 1;
        public override Size PageSize { get; set; } = new(794, 1123);
        public override IDocumentPaginatorSource Source => null!;
        public override DocumentPage GetPage(int pageNumber)
        {
            _page.Measure(PageSize);
            _page.Arrange(new Rect(PageSize));
            _page.UpdateLayout();
            return new DocumentPage(_page);
        }
    }

    /// <summary>Minimal modal prompt for lifecycle inputs (account codes, bounce reason/fees).</summary>
    private static class PromptDialog
    {
        public static string[]? Show(Window owner, string title, params (string Label, string Value)[] fields)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            var boxes = new List<TextBox>();
            foreach (var (label, value) in fields)
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
                var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 10) };
                boxes.Add(box);
                panel.Children.Add(box);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            FlowAlign.End(buttons);
            var ok = new Button { Content = T("Common.OK"), MinWidth = 84, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = T("Common.Cancel"), MinWidth = 84, IsCancel = true };
            ok.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            return dialog.ShowDialog() == true ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
        }
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

    private DataGridTextColumn TextCol(string key, string path, double width, bool numeric = false, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (numeric) column.ElementStyle = RightAlignStyle;
        else if (center) column.ElementStyle = CenterAlignStyle;
        _headers.Add((column, key));
        return column;
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
