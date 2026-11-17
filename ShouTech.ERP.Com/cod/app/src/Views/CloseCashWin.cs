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
/// POS cash closing window: terminal + business date; the expected amount is
/// computed live from the day's POS sales documents (non-deleted SAL totals
/// for the date) plus the opening float; the counted amount is typed, the
/// difference is computed (never typed), and Save persists one
/// pos_cash_closings row (migration 037). Past closings are listed below.
/// </summary>
public sealed class CloseCashWin : BaseWin
{
    private readonly OperationsService? _service;
    private readonly List<PosCashClosing> _closings = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBox _terminal = new();
    private readonly DatePicker _date = new();
    private readonly TextBox _opening = new();
    private readonly TextBox _counted = new();
    private readonly TextBox _notes = new();
    private readonly TextBlock _salesInfo = new();
    private readonly TextBlock _expected = new();
    private readonly TextBlock _difference = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private PosSalesSummary _sales = new(0, 0m);

    public CloseCashWin()
    {
        _service = AppHost.Operations;
        Width = 980;
        Height = 640;
        MinWidth = 820;
        MinHeight = 520;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Pos.SessionClose))
        {
            MessageBox.Show(this, T("CloseCash.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _date.SelectedDate = DateTime.Today;
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_saveButton, "CloseCash.Save");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _saveButton.Click += async (_, _) => await SaveAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_saveButton, _refreshButton, _closeButton));

        var content = new DockPanel();
        var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        form.Children.Add(F2Ui.Labeled(_labels, "CloseCash.Terminal", _terminal, 160));
        form.Children.Add(F2Ui.Labeled(_labels, "CloseCash.BusinessDate", _date, 150));
        form.Children.Add(F2Ui.Labeled(_labels, "CloseCash.Opening", _opening, 130));
        form.Children.Add(F2Ui.Labeled(_labels, "CloseCash.Counted", _counted, 130));
        form.Children.Add(F2Ui.Labeled(_labels, "CloseCash.Notes", _notes, 240));
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);

        var computed = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        _salesInfo.Margin = new Thickness(0, 0, 0, 2);
        _expected.FontWeight = FontWeights.SemiBold;
        _difference.FontWeight = FontWeights.SemiBold;
        computed.Children.Add(_salesInfo);
        computed.Children.Add(_expected);
        computed.Children.Add(_difference);
        DockPanel.SetDock(computed, Dock.Top);
        content.Children.Add(computed);

        _opening.TextChanged += (_, _) => UpdateComputed();
        _counted.TextChanged += (_, _) => UpdateComputed();
        _date.SelectedDateChanged += async (_, _) => await LoadSalesAsync();

        F2Ui.ConfigureGrid(_grid);
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.Terminal", nameof(PosCashClosing.TerminalCode), 110));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.BusinessDate", nameof(PosCashClosing.BusinessDate), 110, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.Opening", nameof(PosCashClosing.OpeningAmount), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.Expected", nameof(PosCashClosing.ExpectedAmount), 120, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.Counted", nameof(PosCashClosing.CountedAmount), 120, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.Difference", nameof(PosCashClosing.Difference), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.Status", nameof(PosCashClosing.Status), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "CloseCash.ClosedBy", nameof(PosCashClosing.ClosedBy), 120));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CloseCash.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _saveButton, _refreshButton, _closeButton }, T);
        UpdateComputed();
    }

    private DateOnly BusinessDate => _date.SelectedDate is DateTime d ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(DateTime.Today);

    private async Task RefreshAsync()
    {
        await LoadSalesAsync();
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("CloseCash.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetPosClosingsAsync(CurrentCompanyCode);
            _closings.Clear();
            _closings.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _closings;
            _statusBar.Text = $"{_closings.Count}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadSalesAsync()
    {
        try
        {
            if (_service is null)
                return;
            _sales = await _service.GetPosSalesSummaryAsync(CurrentCompanyCode, BusinessDate);
            UpdateComputed();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateComputed()
    {
        var opening = TryParse(_opening.Text);
        var counted = TryParse(_counted.Text);
        var expected = OperationsService.ComputeExpected(opening, _sales);
        var difference = OperationsService.ComputeDifference(counted, expected);
        _salesInfo.Text = $"{T("CloseCash.SalesSummary")}: {_sales.DocumentCount} · {F2Ui.Fmt(_sales.Total)}";
        _expected.Text = $"{T("CloseCash.Expected")}: {F2Ui.Fmt(expected)}";
        _difference.Text = $"{T("CloseCash.Difference")}: {F2Ui.Fmt(difference)}";
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("CloseCash.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Pos.SessionClose))
                throw new InvalidOperationException(T("CloseCash.PermissionDenied"));
            var opening = F2Ui.ParseDecimal(_opening.Text);
            var counted = F2Ui.ParseDecimal(_counted.Text);
            var expected = OperationsService.ComputeExpected(opening, _sales);
            var closing = new PosCashClosing
            {
                TerminalCode = _terminal.Text.Trim(),
                BusinessDate = BusinessDate,
                OpenedBy = CurrentUserId,
                ClosedBy = CurrentUserId,
                OpeningAmount = opening,
                ExpectedAmount = expected,
                CountedAmount = counted,
                Notes = F2Ui.NullIfBlank(_notes.Text)
            };
            await _service.SavePosClosingAsync(CurrentCompanyCode, closing, CurrentUserId);
            MessageBox.Show(this, T("CloseCash.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static decimal TryParse(string text)
        => decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var v) ||
           decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out v)
            ? v : 0m;
}
