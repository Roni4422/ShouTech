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

namespace ShouTech.App.Views;

/// <summary>
/// CRM pipeline board (Phase B 057, design section 4.2's
/// CrmPipelineWin; decision C2: a grouped-list board with move buttons,
/// in the app's grid language — no drag-and-drop kanban in v1). One
/// section per stage in sequence order, each with its opportunities and
/// a footer of raw + weighted totals (weighted = amount x probability,
/// the pipeline math). Advance / Regress move the selected opportunity
/// to the adjacent stage through CrmService.MoveStageAsync, so every
/// move lands in the stage history; moving INTO a Lost stage is done
/// from CrmOppWin, where the lost reason is captured. The side panel
/// configures stages and lost reasons (CRM.STAGE.EDIT): stages are
/// per-company configuration, not code (Odoo pattern).
/// </summary>
public sealed class CrmPipelineWin : BaseWin
{
    protected override string? ScheduleTintName => "Ochre";

    private readonly CrmService? _service;
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly List<(DataGrid Grid, CrmStage Stage)> _boardGrids = new();

    private readonly StackPanel _board = new();
    private readonly TextBlock _boardHeader = new();
    private readonly TextBlock _configHeader = new();
    private readonly TextBlock _reasonsHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _stageName = new();
    private readonly TextBox _stageSequence = new();
    private readonly TextBox _stageProbability = new();
    private readonly ComboBox _stageType = new();
    private readonly CheckBox _stageActive = new();
    private readonly DataGrid _stageGrid = new();
    private readonly TextBox _reasonName = new();
    private readonly DataGrid _reasonGrid = new();
    private readonly Button _advanceButton = new();
    private readonly Button _regressButton = new();
    private readonly Button _openButton = new();
    private readonly Button _saveStageButton = new();
    private readonly Button _deleteStageButton = new();
    private readonly Button _saveReasonButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private List<CrmStage> _stages = new();
    private Guid? _selectedStageId;
    private Guid? _selectedReasonId;

    public CrmPipelineWin()
    {
        _service = AppHost.Crm;
        Width = 1180;
        Height = 720;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Crm.OppView))
        {
            MessageBox.Show(this, T("Crm.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_regressButton, "Crm.Regress");
        StyleButton(_advanceButton, "Crm.Advance");
        StyleButton(_openButton, "Crm.OpenOpportunity");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _advanceButton.Click += async (_, _) => await MoveSelectedAsync(+1);
        _regressButton.Click += async (_, _) => await MoveSelectedAsync(-1);
        _openButton.Click += (_, _) => OpenSelected();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _regressButton, _advanceButton, _openButton, _refreshButton, _closeButton })
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
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });

        var boardPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _boardHeader.FontWeight = FontWeights.SemiBold;
        _boardHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_boardHeader, Dock.Top);
        boardPanel.Children.Add(_boardHeader);
        var boardScroll = new ScrollViewer { Content = _board, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        boardPanel.Children.Add(boardScroll);
        Grid.SetColumn(boardPanel, 0);
        content.Children.Add(boardPanel);

        var config = new StackPanel();
        _configHeader.FontWeight = FontWeights.SemiBold;
        _configHeader.Margin = new Thickness(0, 0, 0, 6);
        config.Children.Add(_configHeader);
        ConfigureGrid(_stageGrid);
        _stageGrid.MaxHeight = 170;
        _stageGrid.Columns.Add(TextCol("Crm.Col.Stage", nameof(CrmStage.NameEn), 110));
        _stageGrid.Columns.Add(TextCol("Crm.Col.Sequence", nameof(CrmStage.Sequence), 60));
        _stageGrid.Columns.Add(TextCol("Crm.Col.Probability", nameof(CrmStage.Probability), 70));
        _stageGrid.Columns.Add(TextCol("Crm.Col.StageType", nameof(CrmStage.StageType), 60));
        _stageGrid.SelectionChanged += (_, _) => OnStageSelected();
        config.Children.Add(_stageGrid);
        var stageForm = CreateFormGrid(1);
        AddFormField(_labels, stageForm, 0, 0, "Crm.Col.Stage", _stageName);
        AddFormField(_labels, stageForm, 1, 0, "Crm.Col.Sequence", _stageSequence);
        AddFormField(_labels, stageForm, 2, 0, "Crm.Col.Probability", _stageProbability);
        AddFormField(_labels, stageForm, 3, 0, "Crm.Col.StageType", _stageType);
        AddFormElement(stageForm, 4, 0, _stageActive);
        _stageType.ItemsSource = CrmStageTypes.All;
        _stageActive.IsChecked = true;
        config.Children.Add(stageForm);
        var stageBar = new StackPanel { Orientation = Orientation.Horizontal,  Margin = new Thickness(0, 0, 0, 10) };
        FlowAlign.End(stageBar);
        StyleButton(_saveStageButton, "Crm.SaveStage");
        StyleButton(_deleteStageButton, "Common.Delete");
        _saveStageButton.Click += async (_, _) => await SaveStageAsync();
        _deleteStageButton.Click += async (_, _) => await DeleteStageAsync();
        stageBar.Children.Add(_saveStageButton);
        stageBar.Children.Add(_deleteStageButton);
        config.Children.Add(stageBar);

        _reasonsHeader.FontWeight = FontWeights.SemiBold;
        _reasonsHeader.Margin = new Thickness(0, 0, 0, 6);
        config.Children.Add(_reasonsHeader);
        ConfigureGrid(_reasonGrid);
        _reasonGrid.MaxHeight = 140;
        _reasonGrid.Columns.Add(TextCol("Crm.LostReason", nameof(CrmLostReason.NameEn), 160));
        _reasonGrid.SelectionChanged += (_, _) =>
        {
            if (_reasonGrid.SelectedItem is CrmLostReason reason)
            {
                _selectedReasonId = reason.Id;
                _reasonName.Text = reason.NameEn;
            }
        };
        config.Children.Add(_reasonGrid);
        var reasonForm = CreateFormGrid(1);
        AddFormField(_labels, reasonForm, 0, 0, "Crm.LostReason", _reasonName);
        config.Children.Add(reasonForm);
        var reasonBar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(reasonBar);
        StyleButton(_saveReasonButton, "Crm.SaveReason");
        _saveReasonButton.Click += async (_, _) => await SaveReasonAsync();
        reasonBar.Children.Add(_saveReasonButton);
        config.Children.Add(reasonBar);

        var configScroll = new ScrollViewer { Content = config, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(configScroll, 1);
        content.Children.Add(configScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Crm.PipelineTitle");
        _boardHeader.Text = T("Crm.PipelineTitle");
        _configHeader.Text = T("Crm.StageConfig");
        _reasonsHeader.Text = T("Crm.LostReasons");
        _stageActive.Content = T("Crm.Active");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _advanceButton, _regressButton, _openButton, _refreshButton, _closeButton, _saveStageButton, _deleteStageButton, _saveReasonButton })
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
            var board = await _service.GetPipelineAsync(CurrentCompanyCode);
            _stages = board.Columns.Select(c => c.Stage).ToList();
            _board.Children.Clear();
            _boardGrids.Clear();
            foreach (var column in board.Columns)
            {
                var header = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 4),
                    Text = Tf("Crm.StageColumn", column.Stage.NameEn,
                        column.Opportunities.Count.ToString(CultureInfo.CurrentCulture),
                        column.TotalAmount.ToString("0.##", CultureInfo.CurrentCulture),
                        column.WeightedAmount.ToString("0.##", CultureInfo.CurrentCulture))
                };
                _board.Children.Add(header);
                var grid = new DataGrid { MaxHeight = 220 };
                ConfigureGrid(grid);
                grid.Columns.Add(TextCol("Crm.Col.Title", nameof(CrmOpportunity.Title), 200));
                grid.Columns.Add(TextCol("Crm.Col.Party", nameof(CrmOpportunity.PartyName), 140));
                grid.Columns.Add(TextCol("Crm.Col.Amount", nameof(CrmOpportunity.ExpectedAmount), 90));
                grid.Columns.Add(TextCol("Crm.Col.Probability", nameof(CrmOpportunity.Probability), 70));
                grid.Columns.Add(TextCol("Crm.Col.Weighted", nameof(CrmOpportunity.WeightedValue), 90));
                grid.Columns.Add(TextCol("Crm.Col.CloseDate", nameof(CrmOpportunity.ExpectedCloseDate), 90));
                grid.ItemsSource = column.Opportunities;
                _board.Children.Add(grid);
                _boardGrids.Add((grid, column.Stage));
            }
            _stageGrid.ItemsSource = null;
            _stageGrid.ItemsSource = _stages;
            var reasons = await _service.GetLostReasonsAsync(CurrentCompanyCode);
            _reasonGrid.ItemsSource = reasons;
            _statusBar.Text = Tf("Crm.PipelineStatus", _stages.Count,
                board.OpenWeightedTotal.ToString("0.##", CultureInfo.CurrentCulture));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmPipelineWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private (CrmOpportunity? Opp, CrmStage? Stage) SelectedBoardItem()
    {
        foreach (var (grid, stage) in _boardGrids)
        {
            if (grid.SelectedItem is CrmOpportunity opp)
                return (opp, stage);
        }
        return (null, null);
    }

    private async Task MoveSelectedAsync(int direction)
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.OppEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var (opp, stage) = SelectedBoardItem();
            if (opp is null || stage is null)
                throw new InvalidOperationException(T("Crm.SelectOpportunity"));
            var index = _stages.FindIndex(s => s.Id == stage.Id);
            var targetIndex = index + direction;
            if (targetIndex < 0 || targetIndex >= _stages.Count)
                throw new InvalidOperationException(T("Crm.NoAdjacentStage"));
            var target = _stages[targetIndex];
            if (target.StageType == CrmStageTypes.Lost)
                throw new InvalidOperationException(T("Crm.LostFromEditor"));
            await _service.MoveStageAsync(CurrentCompanyCode, opp.Id, target.Id, null, null, null, CurrentUserId);
            _statusBar.Text = T("Crm.StageMoved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmPipelineWin.MoveSelectedAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSelected()
    {
        try
        {
            var (opp, _) = SelectedBoardItem();
            if (opp is null)
                throw new InvalidOperationException(T("Crm.SelectOpportunity"));
            var win = new CrmOppWin(opp.Id) { Owner = this };
            win.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmPipelineWin.OpenSelected");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnStageSelected()
    {
        if (_stageGrid.SelectedItem is not CrmStage stage)
            return;
        _selectedStageId = stage.Id;
        _stageName.Text = stage.NameEn;
        _stageSequence.Text = stage.Sequence.ToString(CultureInfo.InvariantCulture);
        _stageProbability.Text = stage.Probability.ToString("0.##", CultureInfo.InvariantCulture);
        _stageType.SelectedItem = stage.StageType;
        _stageActive.IsChecked = stage.IsActive;
    }

    private async Task SaveStageAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.StageEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var stage = _selectedStageId is Guid id
                ? _stages.FirstOrDefault(s => s.Id == id) ?? new CrmStage { Id = id }
                : new CrmStage();
            stage.NameEn = _stageName.Text.Trim();
            stage.Sequence = int.TryParse(_stageSequence.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : 0;
            stage.Probability = decimal.TryParse(_stageProbability.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var prob) ? prob : 0m;
            stage.StageType = _stageType.SelectedItem as string ?? CrmStageTypes.Open;
            stage.IsActive = _stageActive.IsChecked == true;
            await _service.SaveStageAsync(CurrentCompanyCode, stage, CurrentUserId);
            _selectedStageId = stage.Id;
            _statusBar.Text = T("Crm.StageSaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmPipelineWin.SaveStageAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteStageAsync()
    {
        try
        {
            if (_service is null || _selectedStageId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectStage"));
            if (!AppHost.Permissions.Has(PermCat.Crm.StageEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            await _service.DeleteStageAsync(CurrentCompanyCode, id, CurrentUserId);
            _selectedStageId = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmPipelineWin.DeleteStageAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveReasonAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.StageEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var reason = _selectedReasonId is Guid id ? new CrmLostReason { Id = id } : new CrmLostReason();
            reason.NameEn = _reasonName.Text.Trim();
            reason.IsActive = true;
            await _service.SaveLostReasonAsync(CurrentCompanyCode, reason, CurrentUserId);
            _selectedReasonId = reason.Id;
            _statusBar.Text = T("Crm.ReasonSaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmPipelineWin.SaveReasonAsync");
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
}
