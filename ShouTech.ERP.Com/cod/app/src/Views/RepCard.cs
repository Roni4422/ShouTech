using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Sales representative card. Fully persisted through
/// <see cref="CardStore"/> (migration 050: sales_reps + sales_rep_targets):
/// the header fields and EVERY row of the targets schedule grid are saved
/// in one transaction and reloaded by rep code; delete is a soft delete.
/// </summary>
public sealed class RepCard : BaseWin
{
    private readonly TextBox _txtCode = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtName = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtPhone = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtComm = new() { Width = 90, Height = 26, Text = "2.5" };
    private readonly TextBox _txtTarget = new() { Width = 120, Height = 26, Text = "100000.00" };

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();

    private readonly TextBlock _lblTotSales = new();
    private readonly TextBlock _lblTotComm = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<RepTargetVm> _targets = new();

    private long? _loadedId;

    public RepCard()
    {
        Width = 760;
        Height = 490;
        MinWidth = 680;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");

        _btnNew.Click += (_, _) => ResetCard();
        _btnLoad.Click += (_, _) => LoadCard();
        _btnSave.Click += (_, _) => SaveCard();
        _btnDelete.Click += (_, _) => DeleteCard();

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnDelete })
            toolbar.Children.Add(b);

        // Explorer-style bottom command bar: flat, transparent, subtle 1px top divider.
        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);

        // 2. Metrics Bar
        var metricsBorder = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        metricsBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        metricsBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var metricsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        metricsPanel.Children.Add(_lblTotSales);
        metricsPanel.Children.Add(new TextBlock { Width = 25 });
        metricsPanel.Children.Add(_lblTotComm);
        metricsBorder.Child = metricsPanel;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);
        root.Children.Add(actionBar);

        // 3. Body
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(Labeled("SalesRepCard.Code", _txtCode));
        headerPanel.Children.Add(Labeled("SalesRepCard.Name", _txtName));
        headerPanel.Children.Add(Labeled("SalesRepCard.Phone", _txtPhone));
        headerPanel.Children.Add(Labeled("SalesRepCard.Commission", _txtComm));
        headerPanel.Children.Add(Labeled("SalesRepCard.Target", _txtTarget));

        Grid.SetRow(headerPanel, 0);
        body.Children.Add(headerPanel);

        // Setup DataGrid
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");

        _grid.ItemsSource = _targets;
        _grid.Columns.Add(TextCol(nameof(RepTargetVm.Period), 110));
        _grid.Columns.Add(TextCol(nameof(RepTargetVm.TargetText), 120));
        _grid.Columns.Add(TextCol(nameof(RepTargetVm.ActualSalesText), 130));
        _grid.Columns.Add(TextCol(nameof(RepTargetVm.CommissionEarnedText), 130));
        _grid.Columns.Add(TextCol(nameof(RepTargetVm.Status), 120));

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void ResetCard()
    {
        _loadedId = null;
        _txtCode.Text = "REP-01";
        _txtName.Text = string.Empty;
        _txtPhone.Text = string.Empty;
        _txtComm.Text = "2.5";
        _txtTarget.Text = "100000.00";
        _targets.Clear();
        _targets.Add(new RepTargetVm { Period = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture), Target = 100000, ActualSales = 0, CommissionEarned = 0, Status = T("Invoice.StatusDraft") });
        UpdateSummary();
    }

    private void LoadCard()
    {
        try
        {
            var rec = CardStore.LoadRep(_txtCode.Text);
            if (rec is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _loadedId = rec.Id;
            _txtCode.Text = rec.Code;
            _txtName.Text = rec.Name;
            _txtPhone.Text = rec.Phone;
            _txtComm.Text = rec.CommissionPercent.ToString("0.##", CultureInfo.InvariantCulture);
            _txtTarget.Text = rec.MonthlyTarget.ToString("0.00", CultureInfo.InvariantCulture);
            _targets.Clear();
            foreach (var t in rec.Targets)
            {
                _targets.Add(new RepTargetVm
                {
                    Period = t.Period,
                    Target = t.Target,
                    ActualSales = t.ActualSales,
                    CommissionEarned = t.CommissionEarned,
                    Status = t.Status
                });
            }
            UpdateSummary();
        }
        catch (Exception ex)
        {
            ReportFailure("LoadRep", ex);
        }
    }

    private void SaveCard()
    {
        try
        {
            var permission = _loadedId is null ? PermCat.Representatives.Create : PermCat.Representatives.Update;
            if (!AppHost.Permissions.Has(permission))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));

            // Commit any in-progress grid cell edit so the schedule values
            // below are the values the user actually sees.
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            var code = _txtCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException(T("Cards.CodeRequired"));
            if (string.IsNullOrWhiteSpace(_txtName.Text))
                throw new InvalidOperationException(T("Cards.NameRequired"));

            var rec = new CardStore.RepRec(
                _loadedId ?? 0, code, _txtName.Text.Trim(), _txtPhone.Text.Trim(),
                ParseDec(_txtComm.Text), ParseDec(_txtTarget.Text),
                _targets.Select(t => new CardStore.RepTargetRec(
                    t.Period.Trim(), t.Target, t.ActualSales, t.CommissionEarned, t.Status)).ToList());
            _loadedId = CardStore.SaveRep(rec, Actor());
            UpdateSummary();
            MessageBox.Show(this, T("SalesRepCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveRep", ex);
        }
    }

    private void DeleteCard()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Representatives.Delete))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));
            if (_loadedId is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(this, T("Cards.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (CardStore.SoftDeleteRep(_txtCode.Text, Actor()))
            {
                MessageBox.Show(this, T("SalesRepCard.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                ResetCard();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("DeleteRep", ex);
        }
    }

    private void UpdateSummary()
    {
        var sales = _targets.Sum(t => t.ActualSales);
        var comm = _targets.Sum(t => t.CommissionEarned);
        _lblTotSales.Text = $"{T("SalesRepCard.SalesTotal")}: {sales:N2} SAR";
        _lblTotComm.Text = $"{T("SalesRepCard.Commission")}: {comm:N2} SAR";
        _lblTotSales.FontWeight = FontWeights.Bold;
    }

    private static decimal ParseDec(string text)
        => decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static string Actor()
        => !string.IsNullOrWhiteSpace(UsrSession.Current?.Id)
            ? UsrSession.Current!.Id
            : AppHost.Session.CurrentUser ?? string.Empty;

    private void ReportFailure(string operation, Exception exception)
    {
        AppHost.Logging?.LogError("WindowPersistence",
            $"Window={GetType().Name} | Operation={operation} | {exception.Message}", exception);
        MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("SalesRepCard.Title");
        if (_grid.Columns.Count >= 5)
        {
            _grid.Columns[0].Header = T("Invoice.Date");
            _grid.Columns[1].Header = T("SalesRepCard.Target");
            _grid.Columns[2].Header = T("SalesRepCard.SalesTotal");
            _grid.Columns[3].Header = T("SalesRepCard.Commission");
            _grid.Columns[4].Header = T("Invoice.Status");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        UpdateSummary();
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(VisualTreeHelper.GetChild(parent, i));
    }

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is TextBox textBox)
            textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        sp.Children.Add(control);
        return sp;
    }

    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 5, 0);
        btn.Padding = new Thickness(9, 3, 9, 3);
        btn.MinWidth = 76;
        btn.Height = 28;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static DataGridTextColumn TextCol(string path, double width)
        => new()
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width
        };

    private sealed class RepTargetVm : INotifyPropertyChanged
    {
        private string _period = "", _status = "";
        private decimal _target, _actual, _comm;

        public string Period { get => _period; set { _period = value; OnChanged(); } }
        public decimal Target { get => _target; set { _target = value; OnChanged(nameof(TargetText)); } }
        public decimal ActualSales { get => _actual; set { _actual = value; OnChanged(nameof(ActualSalesText)); } }
        public decimal CommissionEarned { get => _comm; set { _comm = value; OnChanged(nameof(CommissionEarnedText)); } }
        public string Status { get => _status; set { _status = value; OnChanged(); } }

        public string TargetText { get => _target.ToString("N2", CultureInfo.InvariantCulture); set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Target = v; } }
        public string ActualSalesText { get => _actual.ToString("N2", CultureInfo.InvariantCulture); set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) ActualSales = v; } }
        public string CommissionEarnedText { get => _comm.ToString("N2", CultureInfo.InvariantCulture); set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) CommissionEarned = v; } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
