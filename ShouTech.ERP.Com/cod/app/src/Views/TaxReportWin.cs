using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Tax transaction register and period summary. The report uses source
/// document dates and keeps currencies separate to avoid invalid totals.
/// </summary>
public sealed class TaxReportWin : BaseWin
{
    private readonly DatePicker _from = new() { SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) };
    private readonly DatePicker _to = new() { SelectedDate = DateTime.Today };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false };
    private readonly TextBlock _status = new();
    private readonly TextBlock _totals = new();
    private readonly Button _refresh = new();
    private readonly Button _close = new();

    public TaxReportWin()
    {
        Title = T("TaxReport.Title");
        Width = 1100;
        Height = 680;
        MinWidth = 720;
        MinHeight = 440;
        BuildUi();
        Loaded += OnLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("TaxReport.Title");
        if (Content is DependencyObject root)
            ApplyLocalizedNode(root);
        foreach (var column in _grid.Columns)
            if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
                column.Header = T("TaxReport.Column." + column.SortMemberPath);
        if (IsLoaded)
            RefreshReport();
    }

    private void ApplyLocalizedNode(DependencyObject node)
    {
        if (node is TextBlock text && text.Tag is string textKey)
            text.Text = T(textKey);
        else if (node is Button button && button.Tag is string buttonKey)
            button.Content = T(buttonKey);

        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency)
                ApplyLocalizedNode(dependency);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.TaxReportView))
        {
            MessageBox.Show(this, T("TaxReport.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        RefreshReport();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        filters.Children.Add(new TextBlock { Text = T("TaxReport.From"), Tag = "TaxReport.From", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _from.Width = 140;
        filters.Children.Add(_from);
        filters.Children.Add(new TextBlock { Text = T("TaxReport.To"), Tag = "TaxReport.To", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        _to.Width = 140;
        filters.Children.Add(_to);
        _refresh.Content = T("Common.Refresh");
        _refresh.Tag = "Common.Refresh";
        _refresh.Margin = new Thickness(10, 0, 0, 0);
        _refresh.Click += (_, _) => RefreshReport();
        filters.Children.Add(_refresh);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(filters);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);

        _totals.TextWrapping = TextWrapping.Wrap;
        _totals.Margin = new Thickness(0, 8, 0, 6);
        DockPanel.SetDock(_totals, Dock.Bottom);
        root.Children.Add(_totals);

        _close.Content = T("Common.Close");
        _close.Tag = "Common.Close";
        _close.MinWidth = 88;
        _close.Margin = new Thickness(0, 4, 0, 0);
        _close.Click += (_, _) => Close();
        DockPanel.SetDock(_close, Dock.Bottom);
        root.Children.Add(_close);

        _grid.AutoGeneratingColumn += (_, e) =>
        {
            var key = "TaxReport.Column." + e.PropertyName;
            e.Column.Header = T(key);
        };
        root.Children.Add(_grid);
        Content = root;
    }

    private void RefreshReport()
    {
        try
        {
            if (_from.SelectedDate is not DateTime from || _to.SelectedDate is not DateTime to)
                throw new InvalidOperationException(T("TaxReport.DateRequired"));
            var rows = TaxReportingSvc.LoadPeriod(from, to);
            _grid.ItemsSource = rows;
            var settings = TaxSvc.LoadSettings();
            _status.Text = string.Format(CultureInfo.CurrentCulture,
                T("TaxReport.PeriodStatus"), settings?.CountryCode ?? string.Empty,
                from.ToString("d", CultureInfo.CurrentCulture),
                to.ToString("d", CultureInfo.CurrentCulture),
                rows.Sum(x => x.TransactionCount),
                rows.Sum(x => x.DocumentCount));

            var currencyTotals = rows.GroupBy(x => x.CurrencyCode).Select(group =>
            {
                var output = group.Where(x => x.Direction == "OUTPUT").Sum(x => x.TaxAmount);
                var input = group.Where(x => x.Direction == "INPUT" && x.IsRecoverable).Sum(x => x.TaxAmount);
                var nonRecoverable = group.Where(x => x.Direction == "INPUT" && !x.IsRecoverable).Sum(x => x.TaxAmount);
                return string.Format(CultureInfo.CurrentCulture, T("TaxReport.CurrencyTotals"),
                    group.Key, output, input, nonRecoverable, output - input);
            });
            _totals.Text = string.Join(Environment.NewLine, currencyTotals);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "TaxReportWin.RefreshReport");
            _status.Text = ex.Message;
            _grid.ItemsSource = Array.Empty<TaxReportingSvc.TaxPeriodRow>();
            _totals.Text = string.Empty;
        }
    }
}
