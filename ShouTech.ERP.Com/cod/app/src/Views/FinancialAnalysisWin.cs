using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Financial analysis window (also serves the Tools.AI entry): ratios are
/// computed live and deterministically from the same RptQuery data the
/// standard reports use — trial balance (accounts table balances),
/// income statement (journal lines by account-code prefix: 1 assets,
/// 2 liabilities, 3 equity, 4 cost of sales, 5 revenue, 6 expenses) and
/// customer balances (receivables). Every ratio row shows its inputs, so
/// no number is a black box. This is deterministic analysis, labeled as
/// such — it is not a machine-learning model.
/// </summary>
public sealed class FinancialAnalysisWin : BaseWin
{
    public sealed class RatioRow
    {
        public string Ratio { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Inputs { get; set; } = string.Empty;
    }

    private readonly List<RatioRow> _rows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DatePicker _from = new();
    private readonly DatePicker _to = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _note = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public FinancialAnalysisWin()
    {
        Width = 1060;
        Height = 620;
        MinWidth = 860;
        MinHeight = 480;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.FinancialAnalysisView))
        {
            MessageBox.Show(this, T("FinAnalysis.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _from.SelectedDate = new DateTime(DateTime.Today.Year, 1, 1);
        _to.SelectedDate = DateTime.Today;
        await RefreshAsync();
    }

    protected override async void OnLanguageChanged() => await RefreshAsync();

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_refreshButton, _closeButton));

        var content = new DockPanel();
        var filter = F2Ui.CreateFormGrid(2);
        filter.Margin = new Thickness(0, 0, 0, 8);
        F2Ui.AddField(_labels, filter, 0, 0, "FinAnalysis.From", _from);
        F2Ui.AddField(_labels, filter, 0, 1, "FinAnalysis.To", _to);
        DockPanel.SetDock(filter, Dock.Top);
        content.Children.Add(filter);

        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_note, Dock.Top);
        content.Children.Add(_note);

        F2Ui.ConfigureGrid(_grid);
        _grid.Columns.Add(F2Ui.TextCol(_headers, "FinAnalysis.Ratio", nameof(RatioRow.Ratio), 210));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "FinAnalysis.Value", nameof(RatioRow.Value), 120, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "FinAnalysis.Inputs", nameof(RatioRow.Inputs), 640));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("FinAnalysis.Title");
        _note.Text = T("FinAnalysis.DeterministicNote");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _refreshButton, _closeButton }, T);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var from = _from.SelectedDate ?? new DateTime(DateTime.Today.Year, 1, 1);
            var to = _to.SelectedDate ?? DateTime.Today;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var trial = await RptQuery.TrialBalanceAsync(from, to, cts.Token);
            var income = await RptQuery.IncomeStatementAsync(from, to, cts.Token);
            var customers = await RptQuery.CustomerBalancesAsync(cts.Token);

            decimal SumTrial(char prefix) => trial
                .Where(r => Code(r).StartsWith(prefix))
                .Sum(r => Dec(r, "Balance"));
            decimal SumIncome(char prefix) => income
                .Where(r => Code(r).StartsWith(prefix))
                .Sum(r => Dec(r, "Amount"));

            var assets = SumTrial('1');
            var liabilities = SumTrial('2');
            // Liability/equity balances are credit-natured in the trial balance
            // (stored balance may be negative); use magnitudes for ratios.
            liabilities = Math.Abs(liabilities);
            var equity = Math.Abs(SumTrial('3'));
            var revenue = SumIncome('5');
            var cogs = SumIncome('4');
            var otherExpenses = SumIncome('6');
            var receivables = customers.Sum(r => Math.Max(0m, Dec(r, "Balance")));
            var inventory = trial
                .Where(r => Code(r).StartsWith("13", StringComparison.Ordinal) ||
                            (NameEn(r).Contains("invent", StringComparison.OrdinalIgnoreCase) ||
                             NameEn(r).Contains("stock", StringComparison.OrdinalIgnoreCase)))
                .Sum(r => Dec(r, "Balance"));
            var periodDays = Math.Max(1, (to.Date - from.Date).Days + 1);
            var netIncome = revenue - cogs - otherExpenses;

            _rows.Clear();
            _rows.Add(new RatioRow
            {
                Ratio = T("FinAnalysis.CurrentRatio"),
                Value = liabilities == 0 ? "n/a" : F2Ui.Fmt(assets / liabilities),
                Inputs = $"{T("FinAnalysis.Assets")} (1*) = {F2Ui.Fmt(assets)}; {T("FinAnalysis.Liabilities")} (2*) = {F2Ui.Fmt(liabilities)}; {T("FinAnalysis.NoCurrentSplit")}"
            });
            _rows.Add(new RatioRow
            {
                Ratio = T("FinAnalysis.DebtEquity"),
                Value = equity == 0 ? "n/a" : F2Ui.Fmt(liabilities / equity),
                Inputs = $"{T("FinAnalysis.Liabilities")} = {F2Ui.Fmt(liabilities)}; {T("FinAnalysis.Equity")} (3*) = {F2Ui.Fmt(equity)}"
            });
            _rows.Add(new RatioRow
            {
                Ratio = T("FinAnalysis.GrossMargin"),
                Value = revenue == 0 ? "n/a" : Percent((revenue - cogs) / revenue),
                Inputs = $"{T("FinAnalysis.Revenue")} (5*) = {F2Ui.Fmt(revenue)}; {T("FinAnalysis.Cogs")} (4*) = {F2Ui.Fmt(cogs)}"
            });
            _rows.Add(new RatioRow
            {
                Ratio = T("FinAnalysis.NetMargin"),
                Value = revenue == 0 ? "n/a" : Percent(netIncome / revenue),
                Inputs = $"{T("FinAnalysis.NetIncome")} = {F2Ui.Fmt(netIncome)}; {T("FinAnalysis.Revenue")} = {F2Ui.Fmt(revenue)}; {T("FinAnalysis.OtherExpenses")} (6*) = {F2Ui.Fmt(otherExpenses)}"
            });
            _rows.Add(new RatioRow
            {
                Ratio = T("FinAnalysis.ReceivablesDays"),
                Value = revenue == 0 ? "n/a" : F2Ui.Fmt(receivables / revenue * periodDays),
                Inputs = $"{T("FinAnalysis.Receivables")} = {F2Ui.Fmt(receivables)}; {T("FinAnalysis.Revenue")} = {F2Ui.Fmt(revenue)}; {T("FinAnalysis.PeriodDays")} = {periodDays}"
            });
            _rows.Add(new RatioRow
            {
                Ratio = T("FinAnalysis.InventoryTurnover"),
                Value = inventory == 0 ? "n/a" : F2Ui.Fmt(cogs / inventory),
                Inputs = $"{T("FinAnalysis.Cogs")} = {F2Ui.Fmt(cogs)}; {T("FinAnalysis.Inventory")} = {F2Ui.Fmt(inventory)}"
            });

            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
            _statusBar.Text = $"{from:yyyy-MM-dd} - {to:yyyy-MM-dd}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Code(IReadOnlyDictionary<string, object?> row)
        => Convert.ToString(row.GetValueOrDefault("Code"), CultureInfo.InvariantCulture) ?? string.Empty;

    private static string NameEn(IReadOnlyDictionary<string, object?> row)
        => Convert.ToString(row.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal Dec(IReadOnlyDictionary<string, object?> row, string key)
        => row.GetValueOrDefault(key) is { } v ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : 0m;

    private static string Percent(decimal value)
        => (value * 100m).ToString("N2", CultureInfo.CurrentCulture) + " %";
}
