using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Fiscal roll-over window over the fiscal_years table. The src/
/// FYearSvc exists but has no repository implementation and is not wired in
/// AppHost, so the real operations run through OperationsService over the
/// same fiscal_years table the app itself writes: list years, create the
/// next year (day after the latest end date, same length), and close a year
/// (roll forward). The ledger is continuous, so account balances carry
/// forward automatically — closing only locks the year; the window states
/// this instead of pretending to post carry-forward journals.
/// </summary>
public sealed class RolloverWin : BaseWin
{
    protected override string? ScheduleTintName => "Cedar";
    private readonly OperationsService? _service;
    private readonly List<FiscalYearRow> _years = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _note = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _createButton = new();
    private readonly Button _closeYearButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public RolloverWin()
    {
        _service = AppHost.Operations;
        Width = 920;
        Height = 560;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.FiscalRollForward))
        {
            MessageBox.Show(this, T("Rollover.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_createButton, "Rollover.CreateNext");
        F2Ui.StyleButton(_closeYearButton, "Rollover.CloseYear");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _createButton.Click += async (_, _) => await CreateNextAsync();
        _closeYearButton.Click += async (_, _) => await CloseYearAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_createButton, _closeYearButton, _refreshButton, _closeButton));

        var content = new DockPanel();
        _note.Margin = new Thickness(0, 0, 0, 8);
        _note.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_note, Dock.Top);
        content.Children.Add(_note);

        F2Ui.ConfigureGrid(_grid);
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.Year", nameof(FiscalYearRow.FiscalYear), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.Start", nameof(FiscalYearRow.StartDate), 120, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.End", nameof(FiscalYearRow.EndDate), 120, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.Current", nameof(FiscalYearRow.IsCurrent), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.Closed", nameof(FiscalYearRow.IsClosed), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.ClosedBy", nameof(FiscalYearRow.ClosedBy), 130));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Rollover.ClosedAt", nameof(FiscalYearRow.ClosedAt), 160, center: true));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Rollover.Title");
        _note.Text = T("Rollover.BalancesNote");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _createButton, _closeYearButton, _refreshButton, _closeButton }, T);
        _statusBar.Text = $"{_years.Count}";
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Rollover.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetFiscalYearsAsync(CurrentCompanyCode);
            _years.Clear();
            _years.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _years;
            _statusBar.Text = $"{_years.Count}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CreateNextAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Rollover.ServiceUnavailable"));
            await _service.CreateNextFiscalYearAsync(CurrentCompanyCode, CurrentUserId);
            MessageBox.Show(this, T("Rollover.Created"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CloseYearAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Rollover.ServiceUnavailable"));
            if (_grid.SelectedItem is not FiscalYearRow row)
                throw new InvalidOperationException(T("Rollover.SelectYear"));
            if (row.IsClosed)
                throw new InvalidOperationException(T("Rollover.AlreadyClosed"));
            if (MessageBox.Show(this, T("Rollover.ConfirmClose"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.CloseFiscalYearAsync(CurrentCompanyCode, row.Id, CurrentUserId);
            MessageBox.Show(this, T("Rollover.ClosedOk"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
