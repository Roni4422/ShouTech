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

namespace ShouTech.App.Views;

/// <summary>
/// Centralized styling, live balancing badge, and two-tone alternating row DataGrid (#FFFFFF / #E4F4F8).
/// </summary>
public sealed class JrnWin : BaseWin
{
    /// <summary>Logs unavailable operations instead of reporting a false success.</summary>
    private void ShowOperationUnavailable(string operation)
    {
        const string errorCode = "APP-2011";
        var context = $"Operation={operation} is not implemented in window {GetType().Name}.";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var exception = new NotSupportedException(context);

        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowOperation",
            $"[{definition.Code}] {definition.Name} | Window={GetType().Name} | {context}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowOperation",
            "OperationUnavailable",
            $"Code={definition.Code} | Window={GetType().Name} | {context}");

        MessageBox.Show(
            this,
            $"[{definition.Code}] {definition.Message}\n{context}",
            "العملية غير متاحة",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtRefNo = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = "SAR" };
    private readonly TextBox _txtRate = new() { Width = 70, Height = 26, Text = "1.00" };
    private readonly TextBox _txtCc = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtMemo = new() { Width = 400, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();
    private readonly Button _btnAutoBal = new();

    private readonly TextBlock _lblTotDr = new();
    private readonly TextBlock _lblTotCr = new();
    private readonly Border _badgeDiff = new();
    private readonly TextBlock _lblDiff = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<JrnLineVm> _lines = new();
    private Guid? _currentJournalId;
    private bool _journalPosted;

    public JrnWin()
    {
        Width = 920;
        Height = 580;
        MinWidth = 800;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetVoucher();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");
        StyleButton(_btnAutoBal, "Vouchers.AutoBalance");

        _btnNew.Click += (_, _) => ResetVoucher();
        _btnLoad.Click += async (_, _) => await LoadVoucherAsync();
        _btnSave.Click += async (_, _) => await SaveVoucherAsync();
        _btnPost.Click += async (_, _) => await PostVoucherAsync();
        _btnPrint.Click += (_, _) => ShowOperationUnavailable("Print");
        _btnAddLine.Click += (_, _) =>
        {
            var line = new JrnLineVm { LineNo = _lines.Count + 1 };
            _lines.Add(line);
            _grid.SelectedItem = line;
            _grid.ScrollIntoView(line);
        };
        _btnRemoveLine.Click += (_, _) =>
        {
            if (_grid.SelectedItem is JrnLineVm selected && _lines.Count > 1)
            {
                _lines.Remove(selected);
                RenumberLines();
                RefreshBalances();
            }
        };
        _btnAutoBal.Click += (_, _) => AutoBalance();

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnPrint, _btnAddLine, _btnRemoveLine, _btnAutoBal })
            toolbar.Children.Add(b);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // 2. Footer summary with Live Difference Badge
        var footer = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var footerContent = new DockPanel();
        var leftSummary = new StackPanel { Orientation = Orientation.Horizontal };
        leftSummary.Children.Add(_lblTotDr);
        leftSummary.Children.Add(new TextBlock { Width = 20 });
        leftSummary.Children.Add(_lblTotCr);
        DockPanel.SetDock(leftSummary, Dock.Left);
        footerContent.Children.Add(leftSummary);

        _badgeDiff.CornerRadius = new CornerRadius(4);
        _badgeDiff.Padding = new Thickness(10, 3, 10, 3);
        _badgeDiff.Child = _lblDiff;
        _lblDiff.FontWeight = FontWeights.SemiBold;
        _lblDiff.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF");
        DockPanel.SetDock(_badgeDiff, Dock.Right);
        footerContent.Children.Add(_badgeDiff);

        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // 3. Center Body
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(Labeled("Vouchers.VoucherNo", _txtDocNo));
        headerPanel.Children.Add(Labeled("Vouchers.Date", _dtDate));
        headerPanel.Children.Add(Labeled("Vouchers.RefNo", _txtRefNo));
        headerPanel.Children.Add(Labeled("Vouchers.Currency", _txtCur));
        headerPanel.Children.Add(Labeled("Vouchers.ExchangeRate", _txtRate));
        headerPanel.Children.Add(Labeled("Vouchers.CostCenter", _txtCc));
        headerPanel.Children.Add(Labeled("Vouchers.Memo", _txtMemo));

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

        _grid.ItemsSource = _lines;
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.LineNo), 45, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.AccountCode), 110));
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.AccountName), 220, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.DebitText), 100));
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.CreditText), 100));
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.CostCenter), 110));
        _grid.Columns.Add(TextCol(nameof(JrnLineVm.Memo), 200));

        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshBalances);

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void RenumberLines()
    {
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].LineNo = i + 1;
    }

    private void ResetVoucher()
    {
        _txtDocNo.Text = "JV-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _currentJournalId = null;
        _journalPosted = false;
        _dtDate.SelectedDate = DateTime.Today;
        _txtRefNo.Text = string.Empty;
        _txtCc.Text = string.Empty;
        _txtMemo.Text = string.Empty;
        _lines.Clear();
        _lines.Add(new JrnLineVm { LineNo = 1 });
        _lines.Add(new JrnLineVm { LineNo = 2 });
        _btnLoad.IsEnabled = true;
        _btnSave.IsEnabled = true;
        _btnPost.IsEnabled = true;
        SetVoucherReadOnly(false);
        RefreshBalances();
    }

    private void RefreshBalances()
    {
        var totalDebit = _lines.Sum(l => l.Debit);
        var totalCredit = _lines.Sum(l => l.Credit);
        var diff = totalDebit - totalCredit;

        _lblTotDr.Text = $"{T("Vouchers.TotalDebit")}: {totalDebit:N2} {_txtCur.Text}";
        _lblTotCr.Text = $"{T("Vouchers.TotalCredit")}: {totalCredit:N2} {_txtCur.Text}";

        if (Math.Abs(diff) < 0.001m && (totalDebit > 0 || totalCredit > 0))
        {
            _badgeDiff.Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessBrush", "#198754");
            _lblDiff.Text = $"{T("Vouchers.Balanced")} (0.00)";
        }
        else
        {
            _badgeDiff.Background = ShouTech.App.Services.VisuId.ResolveBrushResource("ErrorBrush", "#C0392B");
            _lblDiff.Text = $"{T("Vouchers.Difference")}: {Math.Abs(diff):N2} {_txtCur.Text}";
        }
    }

    private void AutoBalance()
    {
        var totalDebit = _lines.Sum(l => l.Debit);
        var totalCredit = _lines.Sum(l => l.Credit);
        var diff = totalDebit - totalCredit;

        if (Math.Abs(diff) < 0.001m) return;

        var lastLine = _lines.LastOrDefault(l => l.Debit == 0 && l.Credit == 0);
        if (lastLine == null)
        {
            lastLine = new JrnLineVm { LineNo = _lines.Count + 1 };
            _lines.Add(lastLine);
        }

        if (diff > 0)
        {
            lastLine.Credit = diff;
            lastLine.Debit = 0;
        }
        else
        {
            lastLine.Debit = Math.Abs(diff);
            lastLine.Credit = 0;
        }

        RefreshBalances();
    }

    private async Task LoadVoucherAsync()
    {
        var documentNumber = _txtDocNo.Text.Trim();
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            MessageBox.Show(this, "أدخل رقم القيد المراد فتحه.", "رقم القيد مطلوب",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var user = ShouTech.App.Services.UsrSession.Current;
            var companyCode = user?.CompanyId;
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new InvalidOperationException("لا توجد شركة نشطة في جلسة المستخدم.");
            if (ShouTech.App.Services.AppHost.JournalStore is not { } store ||
                ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("خدمة القيود غير مهيأة؛ تحقق من إعداد اتصال قاعدة البيانات.");

            var companyId = await store.ResolveCompanyIdAsync(companyCode);
            var snapshot = await journal.FindByDocumentNumberAsync(companyId, documentNumber);
            if (snapshot is null)
                throw new InvalidOperationException($"لم يُعثر على القيد '{documentNumber}' في الشركة النشطة.");

            _currentJournalId = snapshot.Id;
            _journalPosted = snapshot.Status != ShouTech.Domain.Entities.JournalEntryStatus.Draft;
            _txtDocNo.Text = snapshot.DocumentNumber ?? string.Empty;
            _dtDate.SelectedDate = snapshot.EntryDate.ToDateTime(TimeOnly.MinValue);
            _txtRefNo.Text = snapshot.ReferenceNumber ?? string.Empty;
            _txtCur.Text = snapshot.CurrencyCode;
            _txtRate.Text = snapshot.ExchangeRate.ToString("0.########", CultureInfo.CurrentCulture);
            _txtMemo.Text = snapshot.Description;

            _lines.Clear();
            foreach (var line in snapshot.Lines)
            {
                _lines.Add(new JrnLineVm
                {
                    LineNo = line.LineNumber,
                    AccountCode = line.AccountCode,
                    AccountName = line.AccountName,
                    Debit = line.Debit,
                    Credit = line.Credit,
                    CostCenter = line.CostCenter ?? string.Empty,
                    Memo = line.Note ?? string.Empty
                });
            }

            var costCenters = snapshot.Lines
                .Select(line => line.CostCenter?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _txtCc.Text = costCenters.Length == 1 &&
                snapshot.Lines.All(line => string.IsNullOrWhiteSpace(line.CostCenter) ||
                    string.Equals(line.CostCenter.Trim(), costCenters[0], StringComparison.OrdinalIgnoreCase))
                ? costCenters[0]!
                : string.Empty;

            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = snapshot.Status == ShouTech.Domain.Entities.JournalEntryStatus.Draft;
            SetVoucherReadOnly(true);
            RefreshBalances();

            MessageBox.Show(
                this,
                $"تم تحميل القيد من قاعدة البيانات.\nالحالة: {snapshot.Status}\nالمعرف: {snapshot.Id:D}",
                "تم التحميل",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportPersistenceFailure("LoadByDocumentNumber", ex);
        }
    }

    private void SetVoucherReadOnly(bool readOnly)
    {
        _grid.IsReadOnly = readOnly;
        _dtDate.IsEnabled = !readOnly;
        _txtDocNo.IsReadOnly = readOnly;
        _txtRefNo.IsReadOnly = readOnly;
        _txtCur.IsReadOnly = readOnly;
        _txtRate.IsReadOnly = readOnly;
        _txtCc.IsReadOnly = readOnly;
        _txtMemo.IsReadOnly = readOnly;
        _btnAddLine.IsEnabled = !readOnly;
        _btnRemoveLine.IsEnabled = !readOnly;
        _btnAutoBal.IsEnabled = !readOnly;
    }

    private async Task SaveVoucherAsync()
    {
        if (_currentJournalId.HasValue)
        {
            MessageBox.Show(this, "هذا القيد محفوظ بالفعل. لا تُنشأ نسخة مكررة؛ افتح قيدًا جديدًا لإدخال قيد آخر.", "القيد محفوظ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            var user = ShouTech.App.Services.UsrSession.Current;
            var companyCode = user?.CompanyId;
            var actor = !string.IsNullOrWhiteSpace(user?.Id) ? user.Id : ShouTech.App.Services.AppHost.Session.CurrentUser;
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new InvalidOperationException("لا توجد شركة نشطة في جلسة المستخدم.");
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidOperationException("تعذر تحديد المستخدم الحالي لحفظ القيد.");
            if (ShouTech.App.Services.AppHost.JournalStore is not { } store ||
                ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("خدمة حفظ القيود غير مهيأة؛ تحقق من اتصال قاعدة البيانات وإعداداتها.");

            if (_dtDate.SelectedDate is not DateTime selectedDate)
                throw new InvalidOperationException("تاريخ القيد مطلوب.");
            if (string.IsNullOrWhiteSpace(_txtDocNo.Text))
                throw new InvalidOperationException("رقم القيد مطلوب.");
            if (string.IsNullOrWhiteSpace(_txtMemo.Text))
                throw new InvalidOperationException("بيان القيد مطلوب.");

            var currency = _txtCur.Text.Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                throw new InvalidOperationException("رمز العملة يجب أن يتكون من ثلاثة أحرف لاتينية.");
            if (!decimal.TryParse(_txtRate.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var rate) &&
                !decimal.TryParse(_txtRate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out rate))
                throw new InvalidOperationException("سعر الصرف غير صالح.");
            if (rate <= 0)
                throw new InvalidOperationException("سعر الصرف يجب أن يكون أكبر من صفر.");
            if (_lines.Count < 2)
                throw new InvalidOperationException("القيد يتطلب سطرين على الأقل.");

            var companyId = await store.ResolveCompanyIdAsync(companyCode);
            var requests = new List<ShouTech.Application.Services.JournalLineRequest>();
            foreach (var line in _lines)
            {
                if (string.IsNullOrWhiteSpace(line.AccountCode))
                    throw new InvalidOperationException($"رمز الحساب مطلوب في السطر {line.LineNo}.");
                if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0 && line.Credit > 0) ||
                    (line.Debit == 0 && line.Credit == 0))
                    throw new InvalidOperationException($"يجب إدخال مدين أو دائن واحد فقط بمبلغ موجب في السطر {line.LineNo}.");

                var accountId = await store.ResolveAccountIdAsync(companyId, line.AccountCode);
                line.AccountName = await store.ResolveAccountNameAsync(companyId, line.AccountCode);
                var costCenter = string.IsNullOrWhiteSpace(line.CostCenter) ? _txtCc.Text : line.CostCenter;
                requests.Add(new ShouTech.Application.Services.JournalLineRequest(
                    accountId, line.Debit, line.Credit, line.Memo, costCenter));
            }

            var id = await journal.CreateDraftAsync(
                companyId,
                DateOnly.FromDateTime(selectedDate),
                _txtMemo.Text.Trim(),
                actor,
                requests,
                referenceNumber: _txtRefNo.Text,
                currencyCode: currency,
                exchangeRate: rate,
                documentNumber: _txtDocNo.Text);
            _currentJournalId = id;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = true;
            SetVoucherReadOnly(true);
            MessageBox.Show(this, $"تم حفظ مسودة القيد في قاعدة البيانات.\nالمعرف: {id:D}", "تم الحفظ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportPersistenceFailure("SaveDraft", ex);
        }
    }

    private async Task PostVoucherAsync()
    {
        if (_journalPosted)
            return;

        try
        {
            if (!_currentJournalId.HasValue)
            {
                await SaveVoucherAsync();
                if (!_currentJournalId.HasValue)
                    return;
            }

            var user = ShouTech.App.Services.UsrSession.Current;
            var actor = !string.IsNullOrWhiteSpace(user?.Id) ? user.Id : ShouTech.App.Services.AppHost.Session.CurrentUser;
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidOperationException("تعذر تحديد المستخدم الحالي لترحيل القيد.");
            if (ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("خدمة ترحيل القيود غير مهيأة.");

            await journal.PostAsync(_currentJournalId.Value, actor);
            _journalPosted = true;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            SetVoucherReadOnly(true);
            MessageBox.Show(this, "تم ترحيل القيد فعليًا في قاعدة البيانات.", "تم الترحيل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportPersistenceFailure("Post", ex);
        }
    }

    private void ReportPersistenceFailure(string operation, Exception exception)
    {
        const string errorCode = "APP-2010";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var context = $"Operation={operation}; Window={GetType().Name}; Company={ShouTech.App.Services.UsrSession.Current?.CompanyId ?? "(none)"}";
        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowPersistence",
            $"[{definition.Code}] {definition.Name} | {context} | {exception.Message}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowPersistence",
            "DatabaseOperationFailed",
            $"Code={definition.Code}; {context}; Error={exception.Message}");
        MessageBox.Show(
            this,
            $"لم تنجح العملية ولم تُعرض كنجاح.\n[{definition.Code}] {exception.Message}",
            "فشل حفظ القيد",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Vouchers.JournalTitle");
        if (_grid.Columns.Count >= 7)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Vouchers.AccountCode");
            _grid.Columns[2].Header = T("Vouchers.AccountName");
            _grid.Columns[3].Header = T("Vouchers.Debit");
            _grid.Columns[4].Header = T("Vouchers.Credit");
            _grid.Columns[5].Header = T("Vouchers.CostCenter");
            _grid.Columns[6].Header = T("Vouchers.Memo");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        RefreshBalances();
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
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 3) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 1), Opacity = 0.85 });
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

    private static DataGridTextColumn TextCol(string path, double width, bool readOnly = false)
        => new()
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width,
            IsReadOnly = readOnly
        };

    private sealed class JrnLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _accCode = "", _accName = "", _costCenter = "", _memo = "";
        private decimal _debit, _credit;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string AccountCode { get => _accCode; set { _accCode = value; OnChanged(); } }
        public string AccountName { get => _accName; set { _accName = value; OnChanged(); } }
        public string CostCenter { get => _costCenter; set { _costCenter = value; OnChanged(); } }
        public string Memo { get => _memo; set { _memo = value; OnChanged(); } }

        public decimal Debit { get => _debit; set { _debit = value; OnChanged(nameof(DebitText)); } }
        public decimal Credit { get => _credit; set { _credit = value; OnChanged(nameof(CreditText)); } }

        public string DebitText
        {
            get => _debit == 0 ? "" : _debit.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Debit = v; else Debit = 0; }
        }

        public string CreditText
        {
            get => _credit == 0 ? "" : _credit.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Credit = v; else Credit = 0; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
