using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>Read-only browser for immutable maintenance report snapshots.</summary>
internal sealed class MaintenanceHistoryWin : BaseWin
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Vertical };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Button _closeButton = new() { MinWidth = 100, Margin = new Thickness(4) };

    public MaintenanceHistoryWin()
    {
        Width = 980;
        Height = 620;
        MinWidth = 760;
        MinHeight = 460;
        BuildUi();
        Loaded += async (_, _) => await LoadHistoryAsync();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.ReportWindowTitle");
        _closeButton.Content = T("Maintenance.ReportClose");
    }

    private void BuildUi()
    {
        Title = T("Maintenance.ReportWindowTitle");
        var root = new DockPanel { Margin = new Thickness(12) };
        _closeButton.Click += (_, _) => Close();
        var bottom = F2Ui.BottomBar(_closeButton);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _items };
        Grid.SetColumn(listScroll, 0);
        body.Children.Add(listScroll);
        var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 0, 0), Content = _details };
        Grid.SetColumn(detailScroll, 1);
        body.Children.Add(detailScroll);
        root.Children.Add(body);
        Content = root;
    }

    private async Task LoadHistoryAsync()
    {
        _items.Children.Clear();
        if (AppHost.Operations is not { } operations)
        {
            _empty.Text = T("Maintenance.ServiceUnavailable");
            _items.Children.Add(_empty);
            return;
        }

        try
        {
            var rows = await operations.GetMaintenanceReportsAsync(CurrentCompanyCode, 300);
            if (rows.Count == 0)
            {
                _empty.Text = T("Maintenance.ReportEmpty");
                _items.Children.Add(_empty);
                return;
            }

            foreach (var report in rows)
            {
                var button = new Button
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(2),
                    Padding = new Thickness(8),
                    Content = $"{T("Maintenance.ReportNumber")} {report.ReportNo:D6}\n{report.RunAt}\n{report.Status} · {report.FailedCount} {T("Maintenance.ReportFailed")}"
                };
                button.Click += async (_, _) => await ShowReportAsync(report.ReportNo);
                _items.Children.Add(button);
            }
            await ShowReportAsync(rows[0].ReportNo);
        }
        catch (Exception ex)
        {
            _empty.Text = ex.Message;
            _items.Children.Add(_empty);
        }
    }

    private async Task ShowReportAsync(long reportNo)
    {
        if (AppHost.Operations is not { } operations)
            return;
        var report = await operations.GetMaintenanceReportAsync(CurrentCompanyCode, reportNo);
        if (report is null)
        {
            _details.Text = T("Maintenance.ReportEmpty");
            return;
        }

        var pretty = report.ReportJson;
        try
        {
            using var document = JsonDocument.Parse(report.ReportJson);
            pretty = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { }

        _details.Text =
            $"{T("Maintenance.ReportNumber")}: {report.ReportNo:D6}\n" +
            $"{T("Maintenance.ReportDate")}: {report.RunAt}\n" +
            $"{T("Maintenance.ReportUser")}: {report.CreatedBy}\n" +
            $"{T("Maintenance.ReportProvider")}: {report.Provider}\n" +
            $"{T("Maintenance.ReportStatus")}: {report.Status}\n" +
            $"{T("Maintenance.ReportChecks")}: {report.CheckCount} · {T("Maintenance.ReportPassed")}: {report.PassedCount} · " +
            $"{T("Maintenance.ReportWarnings")}: {report.WarningCount} · {T("Maintenance.ReportFailed")}: {report.FailedCount}\n\n{pretty}";
    }
}
