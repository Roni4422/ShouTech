using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Recurring sales schedules console (owner sales spec sections 3 + 5,
/// migration 106, workstream W11): lists the company's schedules with
/// status / next run / occurrences created, and creates new ones from a
/// chosen TEMPLATE sales document (a sales order or sales invoice, draft
/// or posted, picked by kind + document number). Actions: Create, Pause,
/// Resume, Cancel, Run Now (generate the pending occurrence immediately)
/// and Generate Due (the same headless sweep a future scheduler will
/// call). RBAC: SALES.RECURRING.VIEW gates the window, MANAGE every
/// mutation. All generation goes through RecurService — this window
/// never clones a document itself.
/// </summary>
public sealed class RecurWin : BaseWin
{
    protected override string? ScheduleTintName => "Kelp";

    private readonly RecurService? _service;
    private readonly List<RecurSchedule> _schedules = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();

    // Editor (create-from-template)
    private readonly ComboBox _templateKind = new();
    private readonly TextBox _templateDocNo = new();
    private readonly ComboBox _intervalType = new();
    private readonly TextBox _intervalCount = new();
    private readonly DatePicker _startDate = new();
    private readonly DatePicker _endDate = new();
    private readonly TextBox _maxOccurrences = new();
    private readonly CheckBox _autoPost = new();

    private readonly Button _createButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _runNowButton = new();
    private readonly Button _generateDueButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid _companyId;

    public RecurWin()
    {
        _service = AppHost.Recurring;
        Width = 1120;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.RecurringView))
        {
            MessageBox.Show(this, T("Recur.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        try
        {
            _companyId = AppHost.JournalStore is not null
                ? await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode)
                : Guid.Empty;
        }
        catch (Exception ex) { Report(ex, "RecurWin.OnLoaded"); }
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

        var bar = BottomBar(_createButton, _pauseButton, _resumeButton, _cancelButton,
            _runNowButton, _generateDueButton, _refreshButton, _closeButton);
        StyleButton(_createButton, "Recur.Create");
        StyleButton(_pauseButton, "Recur.Pause");
        StyleButton(_resumeButton, "Recur.Resume");
        StyleButton(_cancelButton, "Recur.CancelSchedule");
        StyleButton(_runNowButton, "Recur.RunNow");
        StyleButton(_generateDueButton, "Recur.GenerateDue");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _createButton.Click += async (_, _) => await CreateAsync();
        _pauseButton.Click += async (_, _) => await SetStatusAsync(pause: true);
        _resumeButton.Click += async (_, _) => await SetStatusAsync(pause: false);
        _cancelButton.Click += async (_, _) => await CancelAsync();
        _runNowButton.Click += async (_, _) => await RunNowAsync();
        _generateDueButton.Click += async (_, _) => await GenerateDueAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Recur.Col.Template", nameof(RecurSchedule.TemplateDocNo), 110));
        _grid.Columns.Add(TextCol("Recur.Col.Kind", nameof(RecurSchedule.TemplateDocKind), 55));
        _grid.Columns.Add(TextCol("Recur.Col.Interval", nameof(RecurSchedule.IntervalType), 80));
        _grid.Columns.Add(TextCol("Recur.Col.Every", nameof(RecurSchedule.IntervalCount), 55));
        _grid.Columns.Add(TextCol("Recur.Col.NextRun", nameof(RecurSchedule.NextRunDate), 90));
        _grid.Columns.Add(TextCol("Recur.Col.Occurrences", nameof(RecurSchedule.OccurrencesCreated), 80));
        _grid.Columns.Add(TextCol("Recur.Col.AutoPost", nameof(RecurSchedule.AutoPost), 65));
        _grid.Columns.Add(TextCol("Recur.Col.Status", nameof(RecurSchedule.Status), 80));
        _grid.Columns.Add(TextCol("Recur.Col.LastRun", nameof(RecurSchedule.LastRunStatus), 200));
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Recur.TemplateKind", _templateKind);
        AddFormField(_labels, form, 1, 0, "Recur.TemplateDocNo", _templateDocNo);
        AddFormField(_labels, form, 2, 0, "Recur.Col.Interval", _intervalType);
        AddFormField(_labels, form, 3, 0, "Recur.Col.Every", _intervalCount);
        AddFormField(_labels, form, 4, 0, "Recur.StartDate", _startDate);
        AddFormField(_labels, form, 5, 0, "Recur.EndDate", _endDate);
        AddFormField(_labels, form, 6, 0, "Recur.MaxOccurrences", _maxOccurrences);
        AddFormField(_labels, form, 7, 0, "Recur.Col.AutoPost", _autoPost);
        _templateKind.ItemsSource = new[] { CommercialDocKinds.SalesOrder, CommercialDocKinds.SalesInvoice };
        _templateKind.SelectedItem = CommercialDocKinds.SalesOrder;
        _intervalType.ItemsSource = RecurIntervalTypes.All;
        _intervalType.SelectedItem = RecurIntervalTypes.Monthly;
        _intervalCount.Text = "1";
        _startDate.SelectedDate = DateTime.Today;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Recur.Title");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[]
        {
            _createButton, _pauseButton, _resumeButton, _cancelButton,
            _runNowButton, _generateDueButton, _refreshButton, _closeButton
        })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    // ------------------------------------------------------------------
    // Data + actions
    // ------------------------------------------------------------------
    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Recur.ServiceUnavailable"); return; }
            var rows = await _service.GetSchedulesAsync(CurrentCompanyCode);
            _schedules.Clear();
            _schedules.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _schedules;
            _statusBar.Text = Tf("Recur.SchedulesStatus", _schedules.Count);
        }
        catch (Exception ex) { Report(ex, "RecurWin.RefreshAsync"); }
    }

    private async Task CreateAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Recur.ServiceUnavailable"));
            RequireManage();
            if (AppHost.Orders is null || _companyId == Guid.Empty)
                throw new InvalidOperationException(T("Recur.ServiceUnavailable"));
            var kind = _templateKind.SelectedItem as string ?? CommercialDocKinds.SalesOrder;
            var docNo = _templateDocNo.Text.Trim();
            if (docNo.Length == 0) throw new InvalidOperationException(T("Recur.TemplateRequired"));
            var template = await AppHost.Orders.FindByNumberAsync(CommercialDocSide.Sales, _companyId, kind, docNo)
                ?? throw new InvalidOperationException(Tf("Recur.TemplateNotFound", docNo));
            var start = _startDate.SelectedDate is { } s ? DateOnly.FromDateTime(s) : DateOnly.FromDateTime(DateTime.Today);
            var end = _endDate.SelectedDate is { } e ? DateOnly.FromDateTime(e) : (DateOnly?)null;
            var schedule = await _service.CreateScheduleAsync(CurrentCompanyCode, template.DocId,
                _intervalType.SelectedItem as string ?? RecurIntervalTypes.Monthly,
                ParseInt(_intervalCount.Text, 1), start, end,
                ParseIntOrNull(_maxOccurrences.Text), _autoPost.IsChecked == true, CurrentUserId);
            _statusBar.Text = Tf("Recur.CreatedStatus", schedule.TemplateDocNo, schedule.NextRunDate);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "RecurWin.CreateAsync"); }
    }

    private async Task SetStatusAsync(bool pause)
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Recur.ServiceUnavailable"));
            RequireManage();
            var schedule = Selected() ?? throw new InvalidOperationException(T("Recur.SelectSchedule"));
            if (pause)
                await _service.PauseScheduleAsync(CurrentCompanyCode, schedule.Id, CurrentUserId);
            else
                await _service.ResumeScheduleAsync(CurrentCompanyCode, schedule.Id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "RecurWin.SetStatusAsync"); }
    }

    private async Task CancelAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Recur.ServiceUnavailable"));
            RequireManage();
            var schedule = Selected() ?? throw new InvalidOperationException(T("Recur.SelectSchedule"));
            await _service.CancelScheduleAsync(CurrentCompanyCode, schedule.Id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "RecurWin.CancelAsync"); }
    }

    private async Task RunNowAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Recur.ServiceUnavailable"));
            RequireManage();
            var schedule = Selected() ?? throw new InvalidOperationException(T("Recur.SelectSchedule"));
            var summary = await _service.RunScheduleNowAsync(CurrentCompanyCode, schedule.Id,
                DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            _statusBar.Text = Tf("Recur.GeneratedStatus", summary.DocumentsCreated, summary.DocumentsPosted, summary.Failures);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "RecurWin.RunNowAsync"); }
    }

    private async Task GenerateDueAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Recur.ServiceUnavailable"));
            RequireManage();
            var summary = await _service.GenerateDueAsync(DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            _statusBar.Text = Tf("Recur.GeneratedStatus", summary.DocumentsCreated, summary.DocumentsPosted, summary.Failures);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "RecurWin.GenerateDueAsync"); }
    }

    // ------------------------------------------------------------------
    // Helpers (CommWin pattern)
    // ------------------------------------------------------------------
    private RecurSchedule? Selected() => _grid.SelectedItem as RecurSchedule;

    private static void RequireManage()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.RecurringManage))
            throw new InvalidOperationException(T("Recur.PermissionDenied"));
    }

    private void Report(Exception ex, string context)
    {
        AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static Border BottomBar(params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        foreach (var button in buttons) bar.Children.Add(button);
        var border = new Border
        {
            Child = bar,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
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

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        _headers.Add((column, key));
        return column;
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static int? ParseIntOrNull(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
