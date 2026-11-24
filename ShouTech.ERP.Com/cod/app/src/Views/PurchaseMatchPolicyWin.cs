using ShouTech.Domain;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Company-level purchase three-way-match settings and recent decision review.
/// Sensitive writes are routed through PurchaseMatchService, which checks
/// dedicated permissions and writes the audit trail.
/// </summary>
public sealed class PurchaseMatchPolicyWin : BaseWin
{
    private readonly PurchaseMatchService? _service;
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _requireReceipt = new();
    private readonly TextBox _priceWarning = NumberBox();
    private readonly TextBox _priceBlock = NumberBox();
    private readonly TextBox _taxWarning = NumberBox();
    private readonly TextBox _taxBlock = NumberBox();
    private readonly TextBox _baseWarning = NumberBox();
    private readonly TextBox _baseBlock = NumberBox();
    private readonly TextBox _releaseReason = new();
    private readonly DataGrid _history = new();
    private readonly TextBox _matchDetails = new();
    private readonly TextBlock _status = new();
    private readonly Button _save = new();
    private readonly Button _refresh = new();
    private readonly Button _release = new();
    private readonly Button _reevaluate = new();
    private readonly Button _close = new();
    private readonly List<EvaluationRow> _rows = new();
    private static readonly JsonSerializerOptions MatchDetailsJsonOptions = new() { WriteIndented = true };
    private PurchaseMatchPolicy? _policy;
    private bool _canManage;
    private bool _canRelease;

    public PurchaseMatchPolicyWin()
    {
        _service = AppHost.PurchaseMatch;
        Title = T("PurchaseMatch.Title");
        Width = 1120;
        Height = 720;
        // Screen-fit cap (verify_screenfit.py): <= 684x384. The whole
        // middle band (settings + history) scrolls vertically when
        // squeezed instead of clipping; the status and command bars stay
        // docked at the bottom, always visible.
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _canManage = AppHost.Permissions.Has(PermCat.Purchasing.MatchPolicyManage);
        _canRelease = AppHost.Permissions.Has(PermCat.Purchasing.MatchOverride);
        if (_service is null || string.IsNullOrWhiteSpace(CurrentCompanyCode))
        {
            MessageBox.Show(this, T("PurchaseMatch.ServiceUnavailable"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        if (!_canManage && !_canRelease &&
            !AppHost.Permissions.Has(PermKey.PurchView) &&
            !AppHost.Permissions.Has(PermCat.Purchasing.ReportsView))
        {
            MessageBox.Show(this, T("PurchaseMatch.PermissionDenied"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        SetPolicyControlsEnabled(_canManage);
        _save.IsEnabled = _canManage;
        _release.IsEnabled = _canRelease;
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var statusBorder = new Border
        {
            Child = _status,
            Padding = new Thickness(4, 6, 4, 2),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);

        var commands = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        StyleButton(_save, "PurchaseMatch.Save");
        StyleButton(_refresh, "Common.Refresh");
        StyleButton(_release, "PurchaseMatch.Release");
        StyleButton(_reevaluate, "PurchaseMatch.Reevaluate");
        StyleButton(_close, "Common.Close");
        _save.Click += async (_, _) => await SaveAsync();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _release.Click += async (_, _) => await ReleaseAsync();
        _reevaluate.Click += async (_, _) => await ReevaluateAsync();
        _close.Click += (_, _) => Close();
        foreach (var button in new[] { _save, _refresh, _release, _reevaluate, _close })
            commands.Children.Add(button);
        DockPanel.SetDock(commands, Dock.Bottom);
        root.Children.Add(commands);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var settings = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        settings.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var settingsPanel = new StackPanel();
        settingsPanel.Children.Add(_enabled);
        settingsPanel.Children.Add(_requireReceipt);
        var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < 3; i++)
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 2; i++)
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddThresholdField(fields, 0, 0, "PurchaseMatch.PriceWarning", _priceWarning);
        AddThresholdField(fields, 1, 0, "PurchaseMatch.PriceBlock", _priceBlock);
        AddThresholdField(fields, 2, 0, "PurchaseMatch.TaxWarning", _taxWarning);
        AddThresholdField(fields, 0, 1, "PurchaseMatch.TaxBlock", _taxBlock);
        AddThresholdField(fields, 1, 1, "PurchaseMatch.BaseWarning", _baseWarning);
        AddThresholdField(fields, 2, 1, "PurchaseMatch.BaseBlock", _baseBlock);
        settingsPanel.Children.Add(fields);
        settings.Child = settingsPanel;
        Grid.SetRow(settings, 0);
        content.Children.Add(settings);

        var historyPanel = new DockPanel();
        var historyHeader = new TextBlock { Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeights.SemiBold };
        historyHeader.SetBinding(TextBlock.TextProperty, new Binding());
        historyHeader.Text = T("PurchaseMatch.RecentEvaluations");
        DockPanel.SetDock(historyHeader, Dock.Top);
        historyPanel.Children.Add(historyHeader);

        _history.AutoGenerateColumns = false;
        _history.IsReadOnly = true;
        _history.SelectionMode = DataGridSelectionMode.Single;
        _history.SelectionUnit = DataGridSelectionUnit.FullRow;
        _history.CanUserAddRows = false;
        _history.Columns.Add(Column("PurchaseMatch.ColInvoice", nameof(EvaluationRow.InvoiceDocId), 95));
        _history.Columns.Add(Column("PurchaseMatch.ColDecision", nameof(EvaluationRow.Decision), 105));
        _history.Columns.Add(Column("PurchaseMatch.ColVersion", nameof(EvaluationRow.EvaluationVersion), 75));
        _history.Columns.Add(Column("PurchaseMatch.ColReasons", nameof(EvaluationRow.Reasons), 2));
        _history.Columns.Add(Column("PurchaseMatch.ColEvaluatedBy", nameof(EvaluationRow.EvaluatedBy), 130));
        _history.Columns.Add(Column("PurchaseMatch.ColDate", nameof(EvaluationRow.EvaluatedAt), 155));
        _history.ItemsSource = _rows;
        _history.SelectionChanged += (_, _) =>
        {
            DisplayMatchDetails();
            UpdateReevaluateAvailability();
        };
        historyPanel.Children.Add(_history);

        _matchDetails.IsReadOnly = true;
        _matchDetails.AcceptsReturn = true;
        _matchDetails.TextWrapping = TextWrapping.Wrap;
        _matchDetails.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _matchDetails.MinHeight = 100;
        _matchDetails.MaxHeight = 170;
        _matchDetails.Margin = new Thickness(0, 8, 0, 0);
        _matchDetails.SetResourceReference(Control.BackgroundProperty, "InputBackgroundBrush");
        _matchDetails.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _matchDetails.ToolTip = T("PurchaseMatch.MatchDetails");
        historyPanel.Children.Add(_matchDetails);

        var releasePanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var reasonLabel = new TextBlock
        {
            Text = T("PurchaseMatch.ReleaseReason"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(reasonLabel, Dock.Left);
        releasePanel.Children.Add(reasonLabel);
        _releaseReason.MinWidth = 260;
        _releaseReason.MaxLength = 1000;
        _releaseReason.SetResourceReference(Control.BackgroundProperty, "InputBackgroundBrush");
        _releaseReason.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        releasePanel.Children.Add(_releaseReason);
        DockPanel.SetDock(releasePanel, Dock.Bottom);
        historyPanel.Children.Add(releasePanel);

        Grid.SetRow(historyPanel, 1);
        content.Children.Add(historyPanel);
        // The settings/history band is the flexible middle: wrap it in a
        // vertical ScrollViewer so small heights degrade to scroll, never
        // clip (the history grid's fixed columns scroll horizontally
        // inside the grid itself).
        var contentScroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(contentScroll);
        Content = root;
        ApplyLocalizedText();
    }

    private static DataGridTextColumn Column(string key, string path, double width)
        => new()
        {
            Header = T(key),
            Binding = new Binding(path),
            Width = width == 2 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width)
        };

    private static void AddThresholdField(Grid grid, int column, int row, string key, TextBox input)
    {
        var panel = new StackPanel { Margin = new Thickness(5, 3, 12, 3) };
        panel.Children.Add(new TextBlock { Text = T(key), Margin = new Thickness(0, 0, 0, 3) });
        input.MinWidth = 110;
        input.MaxLength = 24;
        input.SetResourceReference(Control.BackgroundProperty, "InputBackgroundBrush");
        input.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        panel.Children.Add(input);
        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, row);
        grid.Children.Add(panel);
    }

    private static TextBox NumberBox() => new() { Text = "0", HorizontalContentAlignment = HorizontalAlignment.Left };

    private static void StyleButton(Button button, string key)
    {
        button.Content = T(key);
        button.MinWidth = 88;
        button.MinHeight = 30;
        button.Margin = new Thickness(4, 0, 0, 0);
        button.Padding = new Thickness(10, 4, 10, 4);
    }

    private async Task RefreshAsync()
    {
        if (_service is null || string.IsNullOrWhiteSpace(CurrentCompanyCode))
            return;
        try
        {
            if (_canManage)
            {
                _policy = await _service.GetPolicyAsync(CurrentCompanyCode);
                DisplayPolicy(_policy);
            }
            var evaluations = await _service.GetRecentEvaluationsAsync(CurrentCompanyCode, 100);
            _rows.Clear();
            foreach (var item in evaluations)
            {
                _rows.Add(new EvaluationRow(
                    item.EvaluationId,
                    item.InvoiceDocId,
                    item.EvaluationVersion,
                    item.PolicyVersion,
                    item.Decision,
                    string.Join(", ", item.ReasonCodes),
                    item.ResultSnapshotJson,
                    item.EvaluatedBy ?? string.Empty,
                    item.EvaluatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)));
            }
            _history.Items.Refresh();
            DisplayMatchDetails();
            UpdateReevaluateAvailability();
            _status.Text = T("PurchaseMatch.Loaded");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseMatchPolicyWin.RefreshAsync");
        }
    }

    private void UpdateReevaluateAvailability()
    {
        _reevaluate.IsEnabled = _canManage &&
            _history.SelectedItem is EvaluationRow row &&
            _policy is { IsEnabled: true } current &&
            row.PolicyVersion != current.PolicyVersion;
    }

    private async Task ReevaluateAsync()
    {
        if (_service is null || !_canManage || _history.SelectedItem is not EvaluationRow row)
            return;
        try
        {
            var result = await _service.ReevaluateInvoiceAsync(
                CurrentCompanyCode, row.InvoiceDocId, CurrentUserId);
            await RefreshAsync();
            _status.Text = T("PurchaseMatch.Reevaluated") + " " + result.Decision;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisplayMatchDetails()
    {
        if (_history.SelectedItem is not EvaluationRow row ||
            string.IsNullOrWhiteSpace(row.ResultSnapshotJson))
        {
            _matchDetails.Clear();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(row.ResultSnapshotJson);
            _matchDetails.Text = JsonSerializer.Serialize(
                document.RootElement,
                MatchDetailsJsonOptions);
        }
        catch (JsonException)
        {
            _matchDetails.Text = row.ResultSnapshotJson;
        }
    }

    private void DisplayPolicy(PurchaseMatchPolicy? policy)
    {
        var value = policy ?? new PurchaseMatchPolicy(false, true, 0m, 0m, 0m, 0m, 0m, 0m, 1);
        _enabled.IsChecked = value.IsEnabled;
        _requireReceipt.IsChecked = value.RequireReceiptMatch;
        _priceWarning.Text = value.PriceWarningPercent.ToString(CultureInfo.InvariantCulture);
        _priceBlock.Text = value.PriceBlockPercent.ToString(CultureInfo.InvariantCulture);
        _taxWarning.Text = value.TaxWarningPercent.ToString(CultureInfo.InvariantCulture);
        _taxBlock.Text = value.TaxBlockPercent.ToString(CultureInfo.InvariantCulture);
        _baseWarning.Text = value.BaseVarianceWarningAmount.ToString(CultureInfo.InvariantCulture);
        _baseBlock.Text = value.BaseVarianceBlockAmount.ToString(CultureInfo.InvariantCulture);
    }

    private async Task SaveAsync()
    {
        if (_service is null || !_canManage)
            return;
        try
        {
            var policy = new PurchaseMatchPolicy(
                _enabled.IsChecked == true,
                _requireReceipt.IsChecked == true,
                Parse(_priceWarning), Parse(_priceBlock),
                Parse(_taxWarning), Parse(_taxBlock),
                Parse(_baseWarning), Parse(_baseBlock),
                _policy?.PolicyVersion ?? 1);
            policy.Validate();
            await _service.SavePolicyAsync(
                CurrentCompanyCode, policy, _policy?.PolicyVersion ?? 0, CurrentUserId);
            await RefreshAsync();
            _status.Text = T("PurchaseMatch.Saved");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ReleaseAsync()
    {
        if (_service is null || !_canRelease || _history.SelectedItem is not EvaluationRow row)
            return;
        if (!string.Equals(row.Decision, "Blocked", StringComparison.Ordinal))
        {
            _status.Text = T("PurchaseMatch.OnlyBlockedCanRelease");
            return;
        }
        try
        {
            await _service.ReleaseBlockedEvaluationAsync(
                CurrentCompanyCode, row.InvoiceDocId, row.EvaluationId,
                CurrentUserId, _releaseReason.Text);
            _releaseReason.Clear();
            await RefreshAsync();
            _status.Text = T("PurchaseMatch.Released");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static decimal Parse(TextBox box)
    {
        if (!decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            throw new FormatException(T("PurchaseMatch.InvalidNumber"));
        return value;
    }

    private void SetPolicyControlsEnabled(bool enabled)
    {
        _enabled.IsEnabled = enabled;
        _requireReceipt.IsEnabled = enabled;
        foreach (var box in new[] { _priceWarning, _priceBlock, _taxWarning, _taxBlock, _baseWarning, _baseBlock })
            box.IsEnabled = enabled;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PurchaseMatch.Title");
        _enabled.Content = T("PurchaseMatch.Enabled");
        _requireReceipt.Content = T("PurchaseMatch.RequireReceipt");
        StyleButton(_save, "PurchaseMatch.Save");
        StyleButton(_refresh, "Common.Refresh");
        StyleButton(_release, "PurchaseMatch.Release");
        StyleButton(_reevaluate, "PurchaseMatch.Reevaluate");
        StyleButton(_close, "Common.Close");
        _releaseReason.ToolTip = T("PurchaseMatch.ReleaseReason");
        if (_history.Columns.Count == 6)
        {
            _history.Columns[0].Header = T("PurchaseMatch.ColInvoice");
            _history.Columns[1].Header = T("PurchaseMatch.ColDecision");
            _history.Columns[2].Header = T("PurchaseMatch.ColVersion");
            _history.Columns[3].Header = T("PurchaseMatch.ColReasons");
            _history.Columns[4].Header = T("PurchaseMatch.ColEvaluatedBy");
            _history.Columns[5].Header = T("PurchaseMatch.ColDate");
        }
    }

    private sealed record EvaluationRow(
        Guid EvaluationId,
        long InvoiceDocId,
        int EvaluationVersion,
        int PolicyVersion,
        string Decision,
        string Reasons,
        string ResultSnapshotJson,
        string EvaluatedBy,
        string EvaluatedAt);
}
