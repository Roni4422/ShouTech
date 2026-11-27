using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShouTech.App.Views;

internal sealed class MaintenanceChecksWin : BaseWin
{
    private sealed record CheckRow(string LabelKey, Func<Task<string>> Execute, TextBlock Label, TextBlock Detail, TextBlock State);

    private readonly IReadOnlyList<(string LabelKey, Func<Task<string>> Execute)> _checks;
    private readonly IProgress<double> _progress;
    private readonly StackPanel _rows = new() { Orientation = Orientation.Vertical };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _closeButton = new();
    private readonly List<CheckRow> _items = new();
    private bool _started;
    private bool _finished;

    public int FailedCount { get; private set; }

    public MaintenanceChecksWin(
        IReadOnlyList<(string LabelKey, Func<Task<string>> Execute)> checks,
        IProgress<double> progress)
    {
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        Title = T("Maintenance.ChecksWindowTitle");
        Width = 760;
        Height = 520;
        MinWidth = 600;
        MinHeight = 380;
        BuildUi();
        Loaded += async (_, _) => await ExecuteChecksAsync();
        Closing += (_, e) => { if (_started && !_finished) e.Cancel = true; };
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        F2Ui.StyleButton(_closeButton, "Maintenance.ChecksClose");
        _closeButton.IsEnabled = false;
        _closeButton.Click += (_, _) => Close();
        var bottom = F2Ui.BottomBar(_closeButton);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rows
        };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 0);
        Grid.SetRow(_summary, 1);
        body.Children.Add(scroll);
        body.Children.Add(_summary);
        root.Children.Add(body);
        Content = root;

        foreach (var check in _checks)
        {
            var label = new TextBlock { Text = T(check.LabelKey), Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold };
            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black, Margin = new Thickness(0, 4, 0, 0) };
            var state = new TextBlock { Text = T("Maintenance.CheckRunning"), Foreground = Brushes.Gray, Margin = new Thickness(8, 0, 0, 0) };
            var heading = new DockPanel();
            DockPanel.SetDock(state, Dock.Right);
            heading.Children.Add(state);
            heading.Children.Add(label);
            var body = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            body.Children.Add(heading);
            body.Children.Add(detail);
            var border = new Border { Child = body, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _rows.Children.Add(border);
            _items.Add(new CheckRow(check.LabelKey, check.Execute, label, detail, state));
        }
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.ChecksWindowTitle");
        F2Ui.ApplyTexts(new List<(TextBlock Label, string Key)>(),
            new List<(DataGridColumn, string)>(), new[] { _closeButton }, T);
        foreach (var item in _items)
            item.Label.Text = T(item.LabelKey);
    }

    private async Task ExecuteChecksAsync()
    {
        if (_started)
            return;
        _started = true;
        var completed = 0;

        foreach (var item in _items)
        {
            item.Label.Foreground = Brushes.DodgerBlue;
            item.State.Text = T("Maintenance.CheckRunning");
            item.State.Foreground = Brushes.DodgerBlue;
            try
            {
                var detail = await item.Execute();
                item.Detail.Text = Tf("Maintenance.CheckPassed", T(item.LabelKey), detail);
                item.State.Text = "✓";
                item.State.Foreground = Brushes.Gray;
            }
            catch (Exception ex)
            {
                FailedCount++;
                item.Detail.Text = Tf("Maintenance.CheckFailed", T(item.LabelKey), ex.Message);
                item.State.Text = "!";
                item.State.Foreground = Brushes.DarkRed;
            }
            finally
            {
                // The active item is highlighted only while its check is running.
                item.Label.Foreground = Brushes.Black;
                completed++;
                _progress.Report(completed * 100d / Math.Max(1, _items.Count));
            }
        }

        _summary.Text = FailedCount == 0
            ? T("Maintenance.ChecksCompleted")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                T("Maintenance.ChecksCompletedWithErrors"), FailedCount);
        _finished = true;
        _closeButton.IsEnabled = true;
    }
}
