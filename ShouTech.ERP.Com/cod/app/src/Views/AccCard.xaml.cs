using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Globalization;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>Persistent account card. Account numbering and storage are database-driven.</summary>
public partial class AccCard : BaseWin
{
    private bool _isNew;
    private bool _isEditing;
    private AccRepo.AccRec? _current;
    private readonly string? _initialParentCode;

    public event EventHandler? Saved;

    public AccCard() : this(LoadDefaultRecord(), null) { }

    private static AccRepo.AccRec? LoadDefaultRecord()
    {
        try { return AccRepo.LoadAll().OrderByDescending(x => x.Id).FirstOrDefault(); }
        catch (Exception exL27) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL27, "AccCard.xaml.LoadDefaultRecord"); return null; }
    }

    public AccCard(string parentCode) : this(null, parentCode) { }

    internal AccCard(AccRepo.AccRec record) : this(record, record.ParentCode) { }

    private AccCard(AccRepo.AccRec? record, string? parentCode)
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);
        _current = record;
        _isNew = record is null;
        _isEditing = record is null;
        _initialParentCode = parentCode;
        InitializeLists();
        LoadRecord();
        UpdateRiskIndicator();
        UpdateNavigationState();
    }

    private void InitializeLists()
    {
        var rows = AccRepo.LoadAll();
        CmbParent.ItemsSource = rows
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => x.Code + " - " + x.Name)
            .ToList();
        CmbStatus.ItemsSource = new[] { "Active", "Disabled" };
        CmbRisk.ItemsSource = new[] { "Low", "Medium", "High" };
        CmbPostingMode.ItemsSource = new[] { "Postable", "Summary" };
        CmbCostCenter.ItemsSource = new[] { "Unspecified", "Required", "Optional" };
    }

    private void LoadRecord()
    {
        if (_current is null)
        {
            TxtSerial.Text = "000000";
            TxtCode.Clear();
            TxtCode.IsReadOnly = false;
            TxtNameAr.Clear();
            TxtNameEn.Clear();
            TxtDescription.Clear();
            TxtTaxRate.Text = "0";
            CmbStatus.SelectedItem = "Active";
            CmbRisk.SelectedItem = "Low";
            CmbPostingMode.SelectedItem = "Postable";
            CmbCostCenter.SelectedItem = "Unspecified";
            ChkReconcile.IsChecked = false;
            ChkTaxable.IsChecked = false;
            ChkForeign.IsChecked = false;
            ChkCostRequired.IsChecked = false;
            ChkBudget.IsChecked = false;
            ChkDefault.IsChecked = false;
            SelectParent(_initialParentCode);
            if (!string.IsNullOrWhiteSpace(_initialParentCode))
                GenerateNextCode(_initialParentCode);
            return;
        }

        TxtSerial.Text = _current.Id.ToString("D6", CultureInfo.InvariantCulture);
        TxtCode.Text = _current.Code;
        TxtCode.IsReadOnly = true;
        TxtNameAr.Text = _current.Name;
        TxtNameEn.Text = _current.NameEn;
        SelectParent(_current.ParentCode);
        CmbStatus.SelectedItem = _current.Status;
        CmbRisk.SelectedItem = _current.Risk;
        CmbPostingMode.SelectedItem = _current.PostingMode;
        CmbCostCenter.SelectedItem = _current.CostCenter;
        ChkReconcile.IsChecked = _current.Reconcile;
        ChkTaxable.IsChecked = _current.Taxable;
        ChkForeign.IsChecked = _current.ForeignCurrency;
        ChkCostRequired.IsChecked = _current.CostRequired;
        ChkBudget.IsChecked = _current.Budget;
        ChkDefault.IsChecked = _current.IsDefault;
        TxtTaxRate.Text = _current.TaxRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TxtDescription.Text = _current.Description;
        TxtBalance.Text = _current.Balance.ToString("N2", CultureInfo.InvariantCulture);
        TxtDebit.Text = _current.Debit.ToString("N2", CultureInfo.InvariantCulture);
        TxtCredit.Text = _current.Credit.ToString("N2", CultureInfo.InvariantCulture);
    }

    private void SelectParent(string? parentCode)
    {
        if (string.IsNullOrWhiteSpace(parentCode)) { CmbParent.SelectedIndex = -1; return; }
        var match = CmbParent.Items.Cast<object>().FirstOrDefault(x => x.ToString()?.StartsWith(parentCode + " -", StringComparison.OrdinalIgnoreCase) == true);
        CmbParent.SelectedItem = match;
    }

    private string? SelectedParentCode()
    {
        var text = CmbParent.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dash = text.IndexOf(" -", StringComparison.Ordinal);
        return dash > 0 ? text[..dash].Trim() : text.Trim();
    }

    private void CmbParent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !_isNew) return;
        var parent = SelectedParentCode();
        if (!string.IsNullOrWhiteSpace(parent)) GenerateNextCode(parent);
        else TxtCode.Clear();
    }

    private void GenerateNextCode(string parentCode)
    {
        try { TxtCode.Text = AccRepo.NextChildCode(parentCode); }
        catch (Exception ex) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AccCard.xaml.GenerateNextCode"); MessageBox.Show(this, ex.Message, "Account Numbering", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CmbRisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateRiskIndicator();
    }

    private void UpdateRiskIndicator()
    {
        if (RiskIndicator == null || CmbRisk.SelectedItem == null) return;
        var risk = CmbRisk.SelectedItem.ToString() ?? "Low";
        RiskIndicator.Fill = risk switch
        {
            "Medium" => ShouTech.App.Services.VisuId.ResolveBrushResource("InfoBrush", "#2878B5"),
            "High" => ShouTech.App.Services.VisuId.ResolveBrushResource("ErrorBrush", "#C0392B"),
            _ => ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessBrush", "#198754")
        };
        var bg = TryGetBrushColor("BackgroundBrush");
        var dark = bg.HasValue && bg.Value.R * 299 + bg.Value.G * 587 + bg.Value.B * 114 < 128000;
        RiskIndicator.Effect = dark ? new DropShadowEffect { Color = Colors.White, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.7 } : null;
    }

    private Color? TryGetBrushColor(string key) => TryFindResource(key) is SolidColorBrush b ? b.Color : null;

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _current = null;
        _isNew = true;
        _isEditing = true;
        LoadRecord();
        UpdateRiskIndicator();
        UpdateNavigationState();
        TxtNameAr.Focus();
    }

    private void First_Click(object sender, RoutedEventArgs e) => NavigateTo(0);
    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateTo(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateTo(1);
    private void Last_Click(object sender, RoutedEventArgs e) => NavigateTo(int.MaxValue);

    private void NavigateTo(int offset)
    {
        var rows = AccRepo.LoadAll();
        if (rows.Count == 0) return;
        var index = _current is null ? 0 : rows.FindIndex(x => x.Id == _current.Id);
        if (index < 0) index = 0;
        index = offset switch { 0 => 0, int.MaxValue => rows.Count - 1, -1 => Math.Max(0, index - 1), 1 => Math.Min(rows.Count - 1, index + 1), _ => index };
        _current = rows[index];
        _isNew = false;
        _isEditing = false;
        InitializeLists();
        LoadRecord();
        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        var rows = AccRepo.LoadAll();
        if (rows.Count == 0)
        {
            BtnFirst.IsEnabled = false;
            BtnPrevious.IsEnabled = false;
            BtnNext.IsEnabled = false;
            BtnLast.IsEnabled = false;
            return;
        }

        var index = _current is null ? 0 : rows.FindIndex(x => x.Id == _current.Id);
        if (index < 0) index = 0;

        BtnFirst.IsEnabled = index > 0;
        BtnPrevious.IsEnabled = index > 0;
        BtnNext.IsEnabled = index < rows.Count - 1;
        BtnLast.IsEnabled = index < rows.Count - 1;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_isNew && !_isEditing)
        {
            MessageBox.Show(this, "Click Edit before changing the account data.", "Account Card", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var code = TxtCode.Text.Trim();
        var nameAr = TxtNameAr.Text.Trim();
        if (code.Length == 0 || nameAr.Length == 0)
        {
            MessageBox.Show(this, "You must enter the account name and choose the parent account.", "Missing Data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (code.Any(c => !char.IsDigit(c)))
        {
            MessageBox.Show(this, "The account number must consist of digits only.", "Account Number", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parent = SelectedParentCode();
        if (parent != null && code == parent)
        {
            MessageBox.Show(this, "An account cannot be a parent of itself.", "Account Number", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (parent != null && !code.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"The account number must start with the parent account number {parent}.", "Account Number", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(TxtTaxRate.Text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var taxRate) || taxRate < 0 || taxRate > 100)
        {
            MessageBox.Show(this, "The tax rate must be between 0 and 100.", "Tax Rate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var value = new AccRepo.AccRec(
            _current?.Id ?? 0, code, parent, nameAr, TxtNameEn.Text.Trim(),
            CmbPostingMode.SelectedItem?.ToString() ?? "Postable",
            CmbStatus.SelectedItem?.ToString() ?? "Active", CmbRisk.SelectedItem?.ToString() ?? "Low",
            CmbCostCenter.SelectedItem?.ToString() ?? "Unspecified", ChkReconcile.IsChecked == true,
            ChkTaxable.IsChecked == true, ChkForeign.IsChecked == true, ChkCostRequired.IsChecked == true,
            ChkBudget.IsChecked == true, ChkDefault.IsChecked == true, taxRate, TxtDescription.Text.Trim(),
            ParseAmount(TxtBalance.Text), ParseAmount(TxtDebit.Text), ParseAmount(TxtCredit.Text));

        var codeChanged = _current != null && !string.Equals(_current.Code, code, StringComparison.OrdinalIgnoreCase);
        var renumber = false;
        if (codeChanged && AccRepo.HasChildren(_current!.Code))
        {
            var answer = MessageBox.Show(this,
                $"The parent account number changed from {_current.Code} to {code}.\n\nDo you want to renumber all its child accounts according to the new number?",
                "Renumber Child Accounts", MessageBoxButton.YesNo, MessageBoxImage.Question);
            renumber = answer == MessageBoxResult.Yes;
        }

        try
        {
            var id = AccRepo.Save(_current, value, renumber);
            _current = AccRepo.FindById(id);
            _isNew = false;
            _isEditing = false;
            TxtCode.IsReadOnly = true;
            TxtSerial.Text = id.ToString("D6", CultureInfo.InvariantCulture);
            Saved?.Invoke(this, EventArgs.Empty);
            MessageBox.Show(this, "The account was saved successfully.", "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppHost.Logging?.LogError("AccountCard", $"Failed to save account | AccountCode={code}", ex);
            MessageBox.Show(this, $"Could not save the account.\n{ex.Message}", "Save Account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static decimal ParseAmount(string text) => decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { _isEditing = true; return; }
        _isEditing = true;
        TxtCode.IsReadOnly = false;
        TxtNameAr.Focus();
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        try
        {
            var guard = AccRepo.CheckLifecycle(_current);
            if (!guard.CanDisable)
            {
                MessageBox.Show(this,
                    "The account cannot be disabled for the following reasons:\n\n" + string.Join(Environment.NewLine, guard.Reasons.Select(x => "• " + x)),
                    "Disable Account", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(this, "Do you want to disable this account? Its accounting record will not be deleted.", "Disable Account", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            CmbStatus.SelectedItem = "Disabled";
            _isEditing = true;
            Save_Click(sender, e);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AccCard.xaml.Disable_Click");
            MessageBox.Show(this, $"Could not verify the account status.\n{ex.Message}", "Disable Account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        try
        {
            var guard = AccRepo.CheckLifecycle(_current);
            if (!guard.CanDelete)
            {
                MessageBox.Show(this,
                    "The account cannot be deleted because it is not completely empty:\n\n" + string.Join(Environment.NewLine, guard.Reasons.Select(x => "• " + x)),
                    "Delete Account", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmation = MessageBox.Show(this,
                $"The account will be permanently deleted:\n\n{_current.Code} - {_current.Name}\n\nThis operation cannot be undone. Do you want to continue?",
                "Confirm Account Deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            AccRepo.Delete(_current);
            Saved?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AccCard.xaml.Delete_Click");
            MessageBox.Show(this, $"Could not delete the account.\n{ex.Message}", "Delete Account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Statement_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "The account statement will open here once general ledger activity is linked.", "Account Statement", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Ledger_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "The ledger will open here once general ledger activity is linked.", "Ledger", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void ApplyLocalizedText()
    {
        // Captions applied via existing control text and future Loc keys.
    }

}
