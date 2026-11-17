using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Configuration;
using ShouTech.App.Interaction;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// CRM opportunity window (Phase B 057, design section 4.2's CrmOppWin):
/// opportunity list + card editor, a stage stepper (stage combo +
/// probability + Move, every move recorded in the stage history shown
/// below the card), an activities strip for the selected opportunity,
/// and the documents tab: linked quotations/orders/invoices with live
/// status from sales_documents. "New Quotation" creates the quotation
/// through CrmService (a Quotation-kind draft, decision C3) and opens
/// it in the existing InvoiceWin; converting it onward
/// (quotation -&gt; order -&gt; invoice) follows the step-9 chain from
/// DocSearch / OrdWin, and the produced documents are linked back here
/// with "Link Document" by document id.
/// </summary>
public sealed class CrmOppWin : BaseWin, IWindowMode
{
    protected override string? ScheduleTintName => "Violet";

    private readonly CrmService? _service;
    private readonly Guid? _focusId;
    private readonly List<CrmOpportunity> _opportunities = new();
    private readonly List<CrmStage> _stages = new();
    private readonly List<CrmPartyOption> _parties = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly DataGrid _historyGrid = new();
    private readonly DataGrid _documentsGrid = new();
    private readonly TextBlock _oppsHeader = new();
    private readonly TextBlock _historyHeader = new();
    private readonly TextBlock _documentsHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _title = new();
    private readonly ComboBox _party = new();
    private readonly TextBox _amount = new();
    private readonly TextBox _currency = new();
    private readonly DatePicker _closeDate = new();
    private readonly ComboBox _oppType = new();
    private readonly ComboBox _source = new();
    private readonly TextBox _campaign = new();
    private readonly ComboBox _rep = new();
    private readonly ComboBox _stage = new();
    private readonly TextBox _probability = new();
    private readonly ComboBox _lostReason = new();
    private readonly TextBox _lostNotes = new();
    private readonly TextBox _linkDocId = new();
    private readonly ComboBox _linkType = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _moveButton = new();
    private readonly Button _quotationButton = new();
    private readonly Button _openDocButton = new();
    private readonly Button _linkButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;

    public CrmOppWin() : this(null) { }

    public CrmOppWin(Guid? focusOpportunityId)
    {
        _service = AppHost.Crm;
        _focusId = focusOpportunityId;
        Width = 1180;
        Height = 720;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>Step 11 router identity: one editor per focused
    /// opportunity (or the shared new/list window).</summary>
    public string ModeKey => $"CrmOpp:{_focusId?.ToString("D") ?? "list"}";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Crm.OppView))
        {
            MessageBox.Show(this, T("Crm.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
        if (_focusId is Guid focus && _opportunities.Any(o => o.Id == focus))
        {
            _grid.SelectedItem = _opportunities.First(o => o.Id == focus);
        }
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

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_moveButton, "Crm.MoveStage");
        StyleButton(_quotationButton, "Crm.NewQuotation");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _moveButton.Click += async (_, _) => await MoveStageAsync();
        _quotationButton.Click += async (_, _) => await NewQuotationAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _moveButton, _quotationButton, _deleteButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 320 });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 140 });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 8) };
        _oppsHeader.FontWeight = FontWeights.SemiBold;
        _oppsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_oppsHeader, Dock.Top);
        listPanel.Children.Add(_oppsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Crm.Col.Title", nameof(CrmOpportunity.Title), 170));
        _grid.Columns.Add(TextCol("Crm.Col.Party", nameof(CrmOpportunity.PartyName), 130));
        _grid.Columns.Add(TextCol("Crm.Col.Stage", nameof(CrmOpportunity.StageName), 100));
        _grid.Columns.Add(TextCol("Crm.Col.Amount", nameof(CrmOpportunity.ExpectedAmount), 90));
        _grid.Columns.Add(TextCol("Crm.Col.Probability", nameof(CrmOpportunity.Probability), 70));
        _grid.Columns.Add(TextCol("Crm.Col.Weighted", nameof(CrmOpportunity.WeightedValue), 90));
        _grid.Columns.Add(TextCol("Crm.Col.CloseDate", nameof(CrmOpportunity.ExpectedCloseDate), 90));
        _grid.SelectionChanged += async (_, _) => await OnOpportunitySelectedAsync();
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        Grid.SetRow(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Crm.Col.Title", _title);
        AddFormField(_labels, form, 1, 0, "Crm.Col.Party", _party);
        AddFormField(_labels, form, 2, 0, "Crm.Col.Amount", _amount);
        AddFormField(_labels, form, 3, 0, "Crm.Currency", _currency);
        AddFormField(_labels, form, 4, 0, "Crm.Col.CloseDate", _closeDate);
        AddFormField(_labels, form, 5, 0, "Crm.OppType", _oppType);
        AddFormField(_labels, form, 6, 0, "Crm.Col.Source", _source);
        AddFormField(_labels, form, 7, 0, "Crm.Campaign", _campaign);
        AddFormField(_labels, form, 8, 0, "Crm.Col.Rep", _rep);
        AddFormField(_labels, form, 9, 0, "Crm.Col.Stage", _stage);
        AddFormField(_labels, form, 10, 0, "Crm.Col.Probability", _probability);
        AddFormField(_labels, form, 11, 0, "Crm.LostReason", _lostReason);
        AddFormField(_labels, form, 12, 0, "Crm.LostNotes", _lostNotes);
        _party.DisplayMemberPath = nameof(CrmPartyOption.Name);
        _oppType.ItemsSource = CrmOpportunityTypes.All;
        _source.ItemsSource = CrmSources.All;
        _source.IsEditable = true;
        _rep.IsEditable = true;
        _stage.DisplayMemberPath = nameof(CrmStage.NameEn);
        _lostReason.DisplayMemberPath = nameof(CrmLostReason.NameEn);
        _currency.Text = UsrPref.Load().CurrencyCode;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        Grid.SetRow(formScroll, 0);
        content.Children.Add(formScroll);

        var historyPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _historyHeader.FontWeight = FontWeights.SemiBold;
        _historyHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_historyHeader, Dock.Top);
        historyPanel.Children.Add(_historyHeader);
        ConfigureGrid(_historyGrid);
        _historyGrid.Columns.Add(TextCol("Crm.Col.FromStage", nameof(CrmStageHistoryEntry.FromStageName), 100));
        _historyGrid.Columns.Add(TextCol("Crm.Col.ToStage", nameof(CrmStageHistoryEntry.ToStageName), 100));
        _historyGrid.Columns.Add(TextCol("Crm.Col.Probability", nameof(CrmStageHistoryEntry.ToProbability), 70));
        _historyGrid.Columns.Add(TextCol("Crm.Col.ChangedBy", nameof(CrmStageHistoryEntry.ChangedBy), 90));
        _historyGrid.Columns.Add(TextCol("Crm.Col.ChangedAt", nameof(CrmStageHistoryEntry.ChangedAt), 130));
        historyPanel.Children.Add(_historyGrid);
        Grid.SetColumn(historyPanel, 0);
        Grid.SetRow(historyPanel, 1);
        content.Children.Add(historyPanel);

        var docsPanel = new DockPanel();
        _documentsHeader.FontWeight = FontWeights.SemiBold;
        _documentsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_documentsHeader, Dock.Top);
        docsPanel.Children.Add(_documentsHeader);
        var linkBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _linkType.ItemsSource = CrmDocumentLinkTypes.All;
        _linkType.SelectedItem = CrmDocumentLinkTypes.Order;
        _linkType.Width = 110;
        _linkDocId.Width = 90;
        StyleButton(_linkButton, "Crm.LinkDocument");
        StyleButton(_openDocButton, "Crm.OpenDocument");
        _linkButton.Click += async (_, _) => await LinkDocumentAsync();
        _openDocButton.Click += (_, _) => OpenSelectedDocument();
        linkBar.Children.Add(_linkType);
        linkBar.Children.Add(_linkDocId);
        linkBar.Children.Add(_linkButton);
        linkBar.Children.Add(_openDocButton);
        DockPanel.SetDock(linkBar, Dock.Top);
        docsPanel.Children.Add(linkBar);
        ConfigureGrid(_documentsGrid);
        _documentsGrid.Columns.Add(TextCol("Crm.Col.DocKind", nameof(CrmOpportunityDocument.DocKind), 60));
        _documentsGrid.Columns.Add(TextCol("Crm.Col.DocNo", nameof(CrmOpportunityDocument.DocNo), 110));
        _documentsGrid.Columns.Add(TextCol("Crm.Col.DocStatus", nameof(CrmOpportunityDocument.DocStatus), 80));
        _documentsGrid.Columns.Add(TextCol("Crm.Col.DocTotal", nameof(CrmOpportunityDocument.DocTotal), 90));
        docsPanel.Children.Add(_documentsGrid);
        Grid.SetColumn(docsPanel, 1);
        Grid.SetRow(docsPanel, 1);
        content.Children.Add(docsPanel);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Crm.OpportunitiesTitle");
        _oppsHeader.Text = T("Crm.OpportunitiesTitle");
        _historyHeader.Text = T("Crm.StageHistory");
        _documentsHeader.Text = T("Crm.Documents");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _moveButton, _quotationButton, _deleteButton, _refreshButton, _closeButton, _linkButton, _openDocButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Crm.ServiceUnavailable");
                return;
            }
            var stages = await _service.GetStagesAsync(CurrentCompanyCode);
            _stages.Clear();
            _stages.AddRange(stages);
            _stage.ItemsSource = null;
            _stage.ItemsSource = _stages;
            var reasons = await _service.GetLostReasonsAsync(CurrentCompanyCode);
            _lostReason.ItemsSource = reasons;
            var parties = await _service.GetPartyOptionsAsync(CurrentCompanyCode);
            _parties.Clear();
            _parties.AddRange(parties);
            _party.ItemsSource = null;
            _party.ItemsSource = _parties;
            var reps = await _service.GetRepOptionsAsync(CurrentCompanyCode);
            _rep.ItemsSource = reps.Select(r => r.Code).ToList();

            var rows = await _service.GetOpportunitiesAsync(CurrentCompanyCode);
            _opportunities.Clear();
            _opportunities.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _opportunities;
            var board = await _service.GetPipelineAsync(CurrentCompanyCode);
            _statusBar.Text = Tf("Crm.OpportunitiesStatus", _opportunities.Count,
                board.OpenWeightedTotal.ToString("0.##", CultureInfo.CurrentCulture));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnOpportunitySelectedAsync()
    {
        if (_grid.SelectedItem is not CrmOpportunity opp)
            return;
        _selectedId = opp.Id;
        _title.Text = opp.Title;
        _party.SelectedItem = _parties.FirstOrDefault(p => p.Id == opp.PartyId);
        _amount.Text = opp.ExpectedAmount.ToString("0.##", CultureInfo.InvariantCulture);
        _currency.Text = opp.CurrencyCode;
        _closeDate.SelectedDate = DateTime.TryParse(opp.ExpectedCloseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var close) ? close : null;
        _oppType.SelectedItem = opp.OppType;
        _source.Text = opp.Source ?? string.Empty;
        _campaign.Text = opp.Campaign ?? string.Empty;
        _rep.Text = opp.AssignedRepCode ?? string.Empty;
        _stage.SelectedItem = _stages.FirstOrDefault(s => s.Id == opp.StageId);
        _probability.Text = opp.Probability.ToString("0.##", CultureInfo.InvariantCulture);
        _lostReason.SelectedItem = (_lostReason.ItemsSource as IReadOnlyList<CrmLostReason>)?.FirstOrDefault(r => r.Id == opp.LostReasonId);
        _lostNotes.Text = opp.LostNotes ?? string.Empty;
        await RefreshDetailAsync(opp.Id);
    }

    private async Task RefreshDetailAsync(Guid oppId)
    {
        if (_service is null) return;
        try
        {
            var history = await _service.GetStageHistoryAsync(CurrentCompanyCode, oppId);
            _historyGrid.ItemsSource = history;
            var documents = await _service.GetOpportunityDocumentsAsync(CurrentCompanyCode, oppId);
            _documentsGrid.ItemsSource = documents;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.RefreshDetailAsync");
        }
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _title.Text = string.Empty;
        _party.SelectedItem = null;
        _amount.Text = "0";
        _currency.Text = UsrPref.Load().CurrencyCode;
        _closeDate.SelectedDate = null;
        _oppType.SelectedItem = null;
        _source.Text = string.Empty;
        _campaign.Text = string.Empty;
        _rep.Text = string.Empty;
        _stage.SelectedItem = _stages.FirstOrDefault(s => s.StageType == CrmStageTypes.Open);
        _probability.Text = "0";
        _lostReason.SelectedItem = null;
        _lostNotes.Text = string.Empty;
        _historyGrid.ItemsSource = null;
        _documentsGrid.ItemsSource = null;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.OppEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var existing = _selectedId is Guid id ? _opportunities.FirstOrDefault(o => o.Id == id) : null;
            var opp = existing ?? new CrmOpportunity();
            opp.Title = _title.Text.Trim();
            opp.PartyId = (_party.SelectedItem as CrmPartyOption)?.Id;
            opp.ExpectedAmount = ParseDecimal(_amount.Text);
            opp.CurrencyCode = string.IsNullOrWhiteSpace(_currency.Text) ? UsrPref.Load().CurrencyCode : _currency.Text.Trim();
            opp.ExpectedCloseDate = _closeDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            opp.OppType = _oppType.SelectedItem as string;
            opp.Source = NullIfBlank(_source.Text);
            opp.Campaign = NullIfBlank(_campaign.Text);
            opp.AssignedRepCode = NullIfBlank(_rep.Text);
            if (existing is null)
            {
                opp.StageId = (_stage.SelectedItem as CrmStage)?.Id;
                opp.Probability = ParseDecimal(_probability.Text);
            }
            await _service.SaveOpportunityAsync(CurrentCompanyCode, opp, CurrentUserId);
            _selectedId = opp.Id;
            _statusBar.Text = T("Crm.OpportunitySaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task MoveStageAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectOpportunity"));
            if (!AppHost.Permissions.Has(PermCat.Crm.OppEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            if (_stage.SelectedItem is not CrmStage stage)
                throw new InvalidOperationException(T("Crm.SelectStage"));
            var probability = string.IsNullOrWhiteSpace(_probability.Text) ? (decimal?)null : ParseDecimal(_probability.Text);
            var reason = (_lostReason.SelectedItem as CrmLostReason)?.Id;
            await _service.MoveStageAsync(CurrentCompanyCode, id, stage.Id, probability,
                reason, NullIfBlank(_lostNotes.Text), CurrentUserId);
            _statusBar.Text = T("Crm.StageMoved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.MoveStageAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task NewQuotationAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectOpportunity"));
            if (!AppHost.Permissions.Has(PermCat.Crm.OppEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var result = await _service.CreateQuotationAsync(CurrentCompanyCode, id, CurrentUserId);
            _statusBar.Text = Tf("Crm.QuotationCreated", result.DocNo);
            await RefreshDetailAsync(id);
            var win = new InvoiceWin(result.DocId, InvDocKind.Quotation) { Owner = this };
            win.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.NewQuotationAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LinkDocumentAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectOpportunity"));
            if (!long.TryParse(_linkDocId.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var docId) || docId <= 0)
                throw new InvalidOperationException(T("Crm.DocIdRequired"));
            var linkType = _linkType.SelectedItem as string ?? CrmDocumentLinkTypes.Order;
            await _service.LinkDocumentAsync(CurrentCompanyCode, id, linkType, docId, CurrentUserId);
            _linkDocId.Text = string.Empty;
            await RefreshDetailAsync(id);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.LinkDocumentAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSelectedDocument()
    {
        try
        {
            if (_documentsGrid.SelectedItem is not CrmOpportunityDocument doc)
                throw new InvalidOperationException(T("Crm.SelectDocument"));
            if (doc.DocKind != "QUO")
                throw new InvalidOperationException(T("Crm.OpenQuotationOnly"));
            var win = new InvoiceWin(doc.DocId, InvDocKind.Quotation) { Owner = this };
            win.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.OpenSelectedDocument");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectOpportunity"));
            if (!AppHost.Permissions.Has(PermCat.Crm.OppEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            await _service.DeleteOpportunityAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmOppWin.DeleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Helpers (QcAlertWin pattern)
    // ------------------------------------------------------------------

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

    private static decimal ParseDecimal(string? text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            ? value
            : 0m;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
