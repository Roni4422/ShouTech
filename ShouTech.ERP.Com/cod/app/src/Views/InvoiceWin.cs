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
using ShouTech.App.Reporting;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Shared editor for sales, purchase, return, and related commercial documents.
/// Header / footer layout follows Phoenix-style invoice windows:
/// right payment & document fields, middle party/rep/warehouse, left cost centre & accounts,
/// footer totals (lines total, invoice value, paid / discount, payment, remaining / extra, net).
/// </summary>
public sealed class InvoiceWin : BaseWin
{
    private readonly InvDoc _doc;
    private readonly ObservableCollection<LineVm> _lines = new();

    // ---- Right header (document + payment) ----
    private readonly ComboBox _cmbPayment = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtDocNo = new() { IsReadOnly = true, Width = 120, Height = 26 };
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today, Width = 130, Height = 26 };
    private readonly TextBox _txtCurrency = new() { Width = 80, Height = 26, Text = "SAR" };
    private readonly TextBox _txtTaxRate = new() { Width = 70, Height = 26 };
    private readonly TextBox _txtReceiptNo = new() { Width = 120, Height = 26 };
    private readonly TextBox _txtReference = new() { Width = 120, Height = 26 };
    private readonly TextBox _txtBranch = new() { Width = 120, Height = 26 };

    // ---- Middle header ----
    private readonly TextBox _txtPartyCode = new() { Width = 100, Height = 26 };
    private readonly TextBox _txtPartyName = new() { Width = 180, Height = 26 };
    private readonly TextBox _txtSalesRep = new() { Width = 140, Height = 26 };
    private readonly TextBox _txtWarehouse = new() { Width = 140, Height = 26 };

    // ---- Left header (accounts) ----
    private readonly TextBox _txtCostCenter = new() { Width = 140, Height = 26 };
    private readonly TextBox _txtPartyAccount = new() { Width = 160, Height = 26 };
    private readonly TextBox _txtGoodsAccount = new() { Width = 160, Height = 26 };

    // ---- Notes / grid / actions ----
    private readonly TextBox _txtNotes = new() { Height = 40, AcceptsReturn = true };
    private readonly DataGrid _grid = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnReverse = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnPdf = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();
    private readonly Button _btnFindMaterial = new();
    /// <summary>Phenix «السند» — journal voucher after post.</summary>
    private readonly Button _btnVoucher = new();
    /// <summary>Phenix «الأصل» — original invoice when opened from a linked document.</summary>
    private readonly Button _btnOriginal = new();

    // ---- Footer totals (Phenix-style three columns) ----
    private readonly TextBox _txtLinesTotal = new() { IsReadOnly = true, Width = 110, Height = 26 };
    private readonly TextBox _txtInvoiceValue = new() { IsReadOnly = true, Width = 110, Height = 26 };
    private readonly TextBox _txtPaid = new() { Width = 110, Height = 26, Text = "0" };
    private readonly TextBox _txtLinesDiscount = new() { Width = 110, Height = 26, Text = "0" };
    private readonly TextBox _txtPaymentValue = new() { Width = 110, Height = 26, Text = "0" };
    private readonly TextBox _txtRemaining = new() { IsReadOnly = true, Width = 110, Height = 26 };
    private readonly TextBox _txtLinesExtra = new() { Width = 110, Height = 26, Text = "0" };
    private readonly TextBox _txtNet = new() { IsReadOnly = true, Width = 110, Height = 26 };
    private readonly TextBlock _lblStatus = new() { Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    private bool _syncingPayment;
    private string _selectedTenderType = "Cash";

    public InvoiceWin() : this(InvDocKind.SalesInvoice) { }

    public InvoiceWin(InvDocKind kind)
    {
        if (kind == InvDocKind.PosSale)
            throw new ArgumentException("Point-of-sale documents must use the POS checkout workflow.", nameof(kind));

        _doc = new InvDoc
        {
            Kind = kind,
            DefaultTaxRate = TaxSvc.GetDefaultRate(),
            CurrencyCode = "SAR",
            WarehouseName = InvDoc.DefaultWarehouseName(),
            GoodsAccountName = InvDoc.DefaultGoodsAccountName(kind),
            PaymentMethod = InvPaymentMethod.Cash,
            PartyAccountName = InvDoc.DefaultCashAccountName()
        };

        Width = 1100;
        Height = 700;
        MinWidth = 960;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = WindowTitleFor(kind);

        BuildUi();
        ApplyLanguageDirection();

        _doc.DocNo = InvRepo.NextDocNo(kind);
        _txtDocNo.Text = _doc.DocNo;
        _txtTaxRate.Text = _doc.DefaultTaxRate.ToString("0.##", CultureInfo.InvariantCulture);
        _txtCurrency.Text = _doc.CurrencyCode;
        _txtWarehouse.Text = _doc.WarehouseName;
        _txtGoodsAccount.Text = _doc.GoodsAccountName;
        _txtPartyAccount.Text = _doc.PartyAccountName;

        _lines.Add(new LineVm { TaxRate = _doc.DefaultTaxRate, WarehouseName = _doc.WarehouseName });
        WireEvents();
        RefreshTotals();
    }

    public InvoiceWin(long docId, InvDocKind kind = InvDocKind.SalesInvoice) : this(kind)
    {
        var loaded = InvRepo.Load(docId, kind);
        if (loaded is not null) ApplyDoc(loaded);
    }

    private void ApplyLanguageDirection()
    {
        try
        {
            var code = App.CurrentLanguageCode;
            LangDirSv.ApplyToWindow(this, code);
        }
        catch
        {
            // Direction is best-effort when localization host is unavailable.
        }
    }

    private static string WindowTitleFor(InvDocKind kind) => kind switch
    {
        InvDocKind.PurchaseInvoice => "فاتورة مشتريات",
        InvDocKind.PurchaseReturn => "مرتجع مشتريات",
        InvDocKind.SalesInvoice => "فاتورة مبيعات",
        InvDocKind.SalesReturn => "مرتجع مبيعات",
        InvDocKind.Quotation => "عرض سعر",
        _ => "فاتورة"
    };

    private void WireEvents()
    {
        _txtPartyCode.LostFocus += (_, _) => ResolveParty();
        _txtPartyName.LostFocus += (_, _) =>
        {
            if (_doc.PaymentMethod == InvPaymentMethod.Credit &&
                !string.IsNullOrWhiteSpace(_txtPartyName.Text))
            {
                _txtPartyAccount.Text = _txtPartyName.Text.Trim();
            }
        };

        _cmbPayment.SelectionChanged += (_, _) => OnPaymentMethodChanged();

        void OnFooterEdit(object s, TextChangedEventArgs e) => RefreshTotalsFromFooterInputs();
        _txtLinesDiscount.TextChanged += OnFooterEdit;
        _txtLinesExtra.TextChanged += OnFooterEdit;
        _txtPaid.TextChanged += OnFooterEdit;
        _txtPaymentValue.TextChanged += (_, _) =>
        {
            // قيمة الدفعة mirrors المدفوع when user edits either.
            if (_txtPaymentValue.IsFocused)
                _txtPaid.Text = _txtPaymentValue.Text;
            RefreshTotalsFromFooterInputs();
        };
    }

    private void ShowTenderMethodDialog()
    {
        var dialog = new Window
        {
            Title = Tf("Invoice.PaymentMethods", "Payment methods"),
            Owner = this,
            Width = 250,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            FlowDirection = FlowDirection
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var choices = new[]
        {
            (Key: "Cash", Label: Tf("Invoice.PaymentCash", "Cash")),
            (Key: "Credit", Label: Tf("Invoice.PaymentCredit", "Credit")),
            (Key: "KNet", Label: Tf("Invoice.PaymentKNet", "K-Net")),
            (Key: "VisaMaster", Label: Tf("Invoice.PaymentVisaMaster", "Visa / MasterCard"))
        };
        var radios = new System.Collections.Generic.List<RadioButton>();
        foreach (var choice in choices)
        {
            var radio = new RadioButton
            {
                Content = choice.Label,
                Tag = choice.Key,
                Margin = new Thickness(4, 5, 4, 5),
                IsChecked = _selectedTenderType == choice.Key
            };
            radios.Add(radio);
            panel.Children.Add(radio);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = Tf("Common.OK", "OK"), MinWidth = 76, Margin = new Thickness(4), IsDefault = true };
        var cancel = new Button { Content = Tf("Common.Cancel", "Cancel"), MinWidth = 76, Margin = new Thickness(4), IsCancel = true };
        ok.Click += (_, _) =>
        {
            var selected = radios.FirstOrDefault(x => x.IsChecked == true)?.Tag?.ToString();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                _selectedTenderType = selected;
                if (selected == "Cash") _doc.PaymentMethod = InvPaymentMethod.Cash;
                else if (selected == "Credit") _doc.PaymentMethod = InvPaymentMethod.Credit;
                else
                    _lblStatus.Text = Tf("Invoice.CardTenderPending", "Card tender selected; payment-account mapping is required before posting.");
            }
            dialog.DialogResult = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private void OnPaymentMethodChanged()
    {
        if (_syncingPayment) return;
        if (_cmbPayment.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag?.ToString() ?? "Cash";
        _doc.PaymentMethod = tag switch
        {
            "Credit" => InvPaymentMethod.Credit,
            "Checks" => InvPaymentMethod.Checks,
            _ => InvPaymentMethod.Cash
        };

        switch (_doc.PaymentMethod)
        {
            case InvPaymentMethod.Cash:
                _txtPartyAccount.Text = InvDoc.DefaultCashAccountName();
                break;
            case InvPaymentMethod.Credit:
                // تفريغ حساب العميل؛ عند اختيار العميل يُملأ باسمه
                _txtPartyAccount.Text = string.IsNullOrWhiteSpace(_txtPartyName.Text)
                    ? string.Empty
                    : _txtPartyName.Text.Trim();
                break;
            case InvPaymentMethod.Checks:
                _txtPartyAccount.Text = InvDoc.DefaultBankAccountName();
                break;
        }

        // Always editable
        _txtPartyAccount.IsReadOnly = false;
    }

    private void BuildUi()
    {
        // Main layout: compact document-type navigation on the left and invoice workspace on the right.
        var root = new Grid { Margin = new Thickness(6) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(156) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navigation = new Border
        {
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.White
        };
        try { navigation.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch { navigation.BorderBrush = Brushes.LightGray; }

        var navStack = new StackPanel { VerticalAlignment = VerticalAlignment.Stretch };
        navStack.Children.Add(new TextBlock
        {
            Text = "ShouTech",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x5F, 0xA8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 22)
        });
        AddDocumentTypeButton(navStack, InvDocKind.SalesInvoice, Tf("Invoice.SalesTitle", "Sales"));
        AddDocumentTypeButton(navStack, InvDocKind.SalesReturn, Tf("Invoice.SalesReturnTitle", "Sales return"));
        AddDocumentTypeButton(navStack, InvDocKind.PurchaseInvoice, Tf("Invoice.PurchaseTitle", "Purchase"));
        AddDocumentTypeButton(navStack, InvDocKind.PurchaseReturn, Tf("Invoice.PurchaseReturnTitle", "Purchase return"));
        AddDocumentTypeButton(navStack, InvDocKind.Quotation, Tf("Invoice.QuotationTitle", "Quotation"));
        navigation.Child = navStack;
        Grid.SetColumn(navigation, 0);
        root.Children.Add(navigation);

        var workspace = new Grid();
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerHost = new Border
        {
            Child = BuildHeaderPanel(),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.White
        };
        try { headerHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch { headerHost.BorderBrush = Brushes.LightGray; }
        Grid.SetRow(headerHost, 0);
        workspace.Children.Add(headerHost);

        ConfigureGrid();
        var gridHost = new Border
        {
            Child = _grid,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.White
        };
        try { gridHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch { gridHost.BorderBrush = Brushes.Gray; }
        Grid.SetRow(gridHost, 1);
        workspace.Children.Add(gridHost);

        var footerHost = new Border
        {
            Child = BuildFooterPanel(),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.White
        };
        try { footerHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch { footerHost.BorderBrush = Brushes.LightGray; }
        Grid.SetRow(footerHost, 2);
        workspace.Children.Add(footerHost);

        var actionBar = BuildBottomActionBar();
        Grid.SetRow(actionBar, 3);
        workspace.Children.Add(actionBar);

        Grid.SetColumn(workspace, 1);
        root.Children.Add(workspace);
        Content = root;
    }

    private void AddDocumentTypeButton(Panel host, InvDocKind kind, string caption)
    {
        var button = new Button
        {
            Content = caption,
            Tag = kind,
            Height = 42,
            Margin = new Thickness(0, 3, 0, 3),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontWeight = kind == _doc.Kind ? FontWeights.Bold : FontWeights.SemiBold,
            Background = kind == _doc.Kind
                ? new SolidColorBrush(Color.FromRgb(0xD9, 0xEA, 0xFC))
                : Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xB7, 0xC9, 0xDE)),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) =>
        {
            if (kind == _doc.Kind) return;
            var next = new InvoiceWin(kind) { Owner = Owner };
            next.Show();
            Close();
        };
        host.Children.Add(button);
    }

    private Grid BuildBottomActionBar()
    {
        var bar = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var documentLinks = new StackPanel { Orientation = Orientation.Horizontal };
        StyleButton(_btnVoucher, "Invoice.Voucher");
        StyleButton(_btnOriginal, "Invoice.Original");
        _btnVoucher.IsEnabled = false;
        _btnOriginal.IsEnabled = false;
        documentLinks.Children.Add(_btnVoucher);
        documentLinks.Children.Add(_btnOriginal);
        _btnVoucher.Click += (_, _) => OpenVoucher();
        _btnOriginal.Click += (_, _) => OpenOriginal();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var btnNew = CreateActionButton("Invoice.New", "New", StartNewDocument);
        var btnEdit = CreateActionButton("Invoice.Edit", "Edit", SaveDraft);
        var btnDelete = CreateActionButton("Invoice.Delete", "Delete", DeleteSelectedLine);
        var btnCancel = CreateActionButton("Invoice.Cancel", "Cancel", Close);
        var btnPreview = CreateActionButton("Invoice.Preview", "Preview", PreviewDocument);
        var btnExit = CreateActionButton("Common.Close", "Close", Close);
        foreach (var button in new[] { btnNew, btnEdit, btnDelete, btnCancel, btnPreview, btnExit })
            actions.Children.Add(button);

        Grid.SetColumn(documentLinks, 0);
        Grid.SetColumn(actions, 2);
        bar.Children.Add(documentLinks);
        bar.Children.Add(actions);
        return bar;
    }

    private static Button CreateActionButton(string key, string fallback, Action action)
    {
        var button = new Button
        {
            Content = Tf(key, fallback),
            MinWidth = 78,
            Height = 36,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontWeight = FontWeights.SemiBold
        };
        try { button.SetResourceReference(StyleProperty, "VisuButtonStyle"); } catch { }
        button.Click += (_, _) => action();
        return button;
    }

    private void StartNewDocument()
    {
        var fresh = new InvoiceWin(_doc.Kind) { Owner = Owner };
        fresh.Show();
        Close();
    }

    private void DeleteSelectedLine()
    {
        if (_grid.SelectedItem is not LineVm line) return;
        if (!AskConfirm("Invoice.ConfirmRemoveLine")) return;
        _lines.Remove(line);
        RefreshTotals();
    }

    private void PreviewDocument()
    {
        try { InvPrn.Print(CaptureDoc(), CurrentCompanyDisplay); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>
    /// Phenix-like compact header: 3 columns × tight rows, uniform control size, no large gaps.
    /// </summary>
    private Grid BuildHeaderPanel()
    {
        const double h = 24;
        const double gap = 2;

        void Size(Control c, double w)
        {
            c.Height = h;
            c.MinHeight = h;
            c.MaxHeight = h;
            c.Width = double.NaN; // stretch within cell
            c.MinWidth = 60;
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            c.VerticalContentAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(0);
            c.Padding = new Thickness(4, 0, 4, 0);
        }

        Size(_cmbPayment, 100);
        Size(_txtDocNo, 100);
        Size(_dtDate, 100);
        Size(_txtCurrency, 60);
        Size(_txtTaxRate, 50);
        Size(_txtReceiptNo, 100);
        Size(_txtReference, 100);
        Size(_txtBranch, 100);
        Size(_txtPartyCode, 80);
        Size(_txtPartyName, 120);
        Size(_txtSalesRep, 100);
        Size(_txtWarehouse, 100);
        Size(_txtCostCenter, 100);
        Size(_txtPartyAccount, 120);
        Size(_txtGoodsAccount, 120);

        BuildPaymentCombo();

        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Payment / document column
        var payment = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 8; i++)
            payment.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(payment, 0, Tf("Invoice.PaymentMethod", "طريقة الدفع"), _cmbPayment);
        AddLabeledCell(payment, 1, Tf("Invoice.DocNo", "رقم الفاتورة"), _txtDocNo);
        AddLabeledCell(payment, 2, Tf("Invoice.Date", "التاريخ"), _dtDate);
        AddLabeledCell(payment, 3, Tf("Invoice.Currency", "العملة"), _txtCurrency);
        AddLabeledCell(payment, 4, Tf("Invoice.TaxRate", "الضريبة %"), _txtTaxRate);
        AddLabeledCell(payment, 5, Tf("Invoice.ReceiptNo", "رقم الإيصال"), _txtReceiptNo);
        AddLabeledCell(payment, 6, Tf("Invoice.Reference", "المرجع"), _txtReference);
        AddLabeledCell(payment, 7, Tf("Invoice.Branch", "الفرع"), _txtBranch);

        // Party / warehouse column
        var party = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 4; i++)
            party.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(party, 0, Tf(PartyCodeKey(), "رمز المورد"), _txtPartyCode);
        AddLabeledCell(party, 1, Tf(PartyNameKey(), "المورد"), _txtPartyName);
        AddLabeledCell(party, 2, Tf("Invoice.SalesRep", "اسم المندوب"), _txtSalesRep);
        AddLabeledCell(party, 3, Tf("Invoice.Warehouse", "المستودع"), _txtWarehouse);

        // Accounts column
        var accounts = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 3; i++)
            accounts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(accounts, 0, Tf("Invoice.CostCenter", "مركز الكلفة"), _txtCostCenter);
        AddLabeledCell(accounts, 1, Tf(PartyAccountLabelKey(), "حساب المورد"), _txtPartyAccount);
        AddLabeledCell(accounts, 2, Tf("Invoice.GoodsAccount", "حساب البضاعة"), _txtGoodsAccount);

        Grid.SetColumn(accounts, 0);
        Grid.SetColumn(party, 1);
        Grid.SetColumn(payment, 2);
        grid.Children.Add(accounts);
        grid.Children.Add(party);
        grid.Children.Add(payment);
        return grid;
    }

    /// <summary>Label (11px) + control in one auto row — minimal vertical gap.</summary>
    private static void AddLabeledCell(Grid host, int row, string label, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11),
            Margin = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0),
            LineHeight = 14
        });
        panel.Children.Add(control);
        Grid.SetRow(panel, row);
        host.Children.Add(panel);
    }

    private void BuildPaymentCombo()
    {
        var idx = _cmbPayment.SelectedIndex;
        _cmbPayment.Items.Clear();
        _cmbPayment.Items.Add(new ComboBoxItem { Content = Tf("Invoice.PaymentCash", "نقداً"), Tag = "Cash" });
        _cmbPayment.Items.Add(new ComboBoxItem { Content = Tf("Invoice.PaymentCredit", "آجل"), Tag = "Credit" });
        _cmbPayment.Items.Add(new ComboBoxItem { Content = Tf("Invoice.PaymentChecks", "شيكات"), Tag = "Checks" });
        _cmbPayment.SelectedIndex = idx >= 0 && idx < 3 ? idx : 0;
    }

    /// <summary>Footer totals: three equal columns, uniform fields, tight spacing.</summary>
    private Grid BuildFooterPanel()
    {
        const double h = 24;
        foreach (var tb in new[] { _txtLinesTotal, _txtInvoiceValue, _txtPaid, _txtLinesDiscount, _txtPaymentValue, _txtRemaining, _txtLinesExtra, _txtNet })
        {
            tb.Height = h;
            tb.MinHeight = h;
            tb.MaxHeight = h;
            tb.Width = double.NaN;
            tb.MinWidth = 70;
            tb.HorizontalAlignment = HorizontalAlignment.Stretch;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(0);
            tb.Padding = new Thickness(4, 0, 4, 0);
        }

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var colExtra = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colExtra, 0, Tf("Invoice.LinesExtra", "إضافي الأقلام"), _txtLinesExtra);
        AddLabeledCell(colExtra, 1, Tf("Invoice.Net", "الصافي"), _txtNet);

        var colDisc = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        for (var i = 0; i < 3; i++)
            colDisc.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colDisc, 0, Tf("Invoice.LinesDiscount", "حسم الأقلام"), _txtLinesDiscount);
        AddLabeledCell(colDisc, 1, Tf("Invoice.PaymentValue", "قيمة الدفعة"), _txtPaymentValue);
        AddLabeledCell(colDisc, 2, Tf("Invoice.Remaining", "الباقي"), _txtRemaining);

        var colTotals = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        for (var i = 0; i < 3; i++)
            colTotals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colTotals, 0, Tf("Invoice.LinesTotal", "مجموع الأقلام"), _txtLinesTotal);
        AddLabeledCell(colTotals, 1, Tf("Invoice.InvoiceValue", "قيمة الفاتورة"), _txtInvoiceValue);
        var paidPanel = new DockPanel { LastChildFill = true };
        var totalButton = new Button
        {
            Content = Tf("Invoice.Total", "المجموع"),
            MinWidth = 72,
            Height = 24,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        totalButton.Click += (_, _) => ShowTenderMethodDialog();
        DockPanel.SetDock(totalButton, Dock.Left);
        paidPanel.Children.Add(totalButton);
        paidPanel.Children.Add(_txtPaid);
        AddLabeledCell(colTotals, 2, Tf("Invoice.Paid", "المدفوع"), paidPanel);

        Grid.SetColumn(colExtra, 0);
        Grid.SetColumn(colDisc, 1);
        Grid.SetColumn(colTotals, 2);
        grid.Children.Add(colExtra);
        grid.Children.Add(colDisc);
        grid.Children.Add(colTotals);
        return grid;
    }

    private void ConfigureGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.CanUserResizeRows = false;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.AlternationCount = 2;
        _grid.RowHeight = 25;
        _grid.ColumnHeaderHeight = 30;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        _grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xD7, 0xE0, 0xEA));
        _grid.VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xD7, 0xE0, 0xEA));
        _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _grid.Margin = new Thickness(0);
        try
        {
            _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
            _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
            _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
            _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
            _grid.RowBackground = Brushes.White;
            _grid.AlternatingRowBackground = new SolidColorBrush(GetInvoiceGridPalette(_doc.Kind).Alternate);
        }
        catch { }

        var palette = GetInvoiceGridPalette(_doc.Kind);
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Header)));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        headerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        _grid.ColumnHeaderStyle = headerStyle;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        var alternate = new Trigger { Property = DataGridRow.AlternationIndexProperty, Value = 1 };
        alternate.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Alternate)));
        rowStyle.Triggers.Add(alternate);
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xB8, 0xD8, 0xF7))));
        rowStyle.Triggers.Add(selected);
        _grid.RowStyle = rowStyle;

        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 0, 3, 0)));
        cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        cellStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        _grid.CellStyle = cellStyle;

        _grid.ItemsSource = _lines;
        _grid.Columns.Clear();
        _grid.Columns.Add(TextCol(nameof(LineVm.MaterialCode), 80, Tf("Invoice.ColCode", "الرمز")));
        _grid.Columns.Add(TextCol(nameof(LineVm.Description), 180, Tf("Invoice.ColMaterial", "المادة")));
        _grid.Columns.Add(TextCol(nameof(LineVm.QtyText), 70, Tf("Invoice.ColQty", "الكمية")));
        _grid.Columns.Add(TextCol(nameof(LineVm.Unit), 60, Tf("Invoice.ColUnit", "الوحدة")));
        _grid.Columns.Add(TextCol(nameof(LineVm.UnitPriceText), 80, Tf("Invoice.ColUnitPrice", "الإفرادي")));
        _grid.Columns.Add(TextCol(nameof(LineVm.DiscountPctText), 60, Tf("Invoice.ColDiscount", "حسم%")));
        _grid.Columns.Add(TextCol(nameof(LineVm.TaxRateText), 60, Tf("Invoice.ColTax", "ضريبة%")));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Tf("Invoice.ColLineTotal", "الإجمالي"),
            Binding = new Binding(nameof(LineVm.LineTotalText)) { Mode = BindingMode.OneWay },
            Width = 90,
            IsReadOnly = true
        });
        _grid.Columns.Add(TextCol(nameof(LineVm.WarehouseName), 110, Tf("Invoice.ColWarehouse", "المستودع")));

        _grid.CellEditEnding += OnCellEditEnding;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is LineVm line)
            {
                line.NotifyTotals();
                RefreshTotals();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ResolveParty()
    {
        var found = TaxSvc.ResolveParty(_txtPartyCode.Text, customer: _doc.IsSalesSide);
        if (found is null) return;
        _doc.PartyId = found.Value.Id;
        _txtPartyName.Text = found.Value.Name;
        _doc.PartyName = found.Value.Name;
        _doc.PartyCode = _txtPartyCode.Text.Trim();

        if (_doc.PaymentMethod == InvPaymentMethod.Credit)
            _txtPartyAccount.Text = found.Value.Name;
    }

    private void RefreshTotalsFromFooterInputs()
    {
        _doc.LinesDiscountAmount = ParseDec(_txtLinesDiscount.Text, 0);
        _doc.LinesExtraAmount = ParseDec(_txtLinesExtra.Text, 0);
        _doc.PaidAmount = ParseDec(_txtPaid.Text, 0);
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        // Keep doc lines in sync for computed properties
        _doc.Lines = _lines.Select(l => l.ToLine()).ToList();
        _doc.LinesDiscountAmount = ParseDec(_txtLinesDiscount.Text, 0);
        _doc.LinesExtraAmount = ParseDec(_txtLinesExtra.Text, 0);
        _doc.PaidAmount = ParseDec(_txtPaid.Text, 0);

        _txtLinesTotal.Text = FormatAmt(_doc.LinesTotal);
        _txtInvoiceValue.Text = FormatAmt(_doc.InvoiceValue);
        _txtRemaining.Text = FormatAmt(_doc.RemainingAmount);
        _txtNet.Text = FormatAmt(_doc.NetAmount);
        if (!_txtPaymentValue.IsFocused)
            _txtPaymentValue.Text = FormatAmt(_doc.PaidAmount);

        _lblStatus.Text = _doc.Status switch
        {
            InvDocStatus.Posted => Tf("Invoice.StatusPosted", "مرحّلة"),
            InvDocStatus.Cancelled => Tf("Invoice.StatusCancelled", "ملغاة"),
            _ => Tf("Invoice.StatusDraft", "مسودة")
        };
    }

    private static string FormatAmt(decimal v)
        => v.ToString("N3", CultureInfo.CurrentCulture);

    private InvDoc CaptureDoc()
    {
        _doc.DocNo = _txtDocNo.Text.Trim();
        _doc.DocDate = _dtDate.SelectedDate ?? DateTime.Today;
        _doc.PartyCode = _txtPartyCode.Text.Trim();
        _doc.PartyName = _txtPartyName.Text.Trim();
        _doc.CurrencyCode = string.IsNullOrWhiteSpace(_txtCurrency.Text)
            ? "SAR"
            : _txtCurrency.Text.Trim().ToUpperInvariant();
        _doc.DefaultTaxRate = ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate());
        _doc.Notes = _txtNotes.Text;
        _doc.ReceiptNo = _txtReceiptNo.Text.Trim();
        _doc.Reference = _txtReference.Text.Trim();
        _doc.BranchName = _txtBranch.Text.Trim();
        _doc.SalesRepName = _txtSalesRep.Text.Trim();
        _doc.WarehouseName = string.IsNullOrWhiteSpace(_txtWarehouse.Text)
            ? InvDoc.DefaultWarehouseName()
            : _txtWarehouse.Text.Trim();
        _doc.CostCenter = _txtCostCenter.Text.Trim();
        _doc.PartyAccountName = _txtPartyAccount.Text.Trim();
        _doc.GoodsAccountName = string.IsNullOrWhiteSpace(_txtGoodsAccount.Text)
            ? InvDoc.DefaultGoodsAccountName(_doc.Kind)
            : _txtGoodsAccount.Text.Trim();
        _doc.LinesDiscountAmount = ParseDec(_txtLinesDiscount.Text, 0);
        _doc.LinesExtraAmount = ParseDec(_txtLinesExtra.Text, 0);
        _doc.PaidAmount = ParseDec(_txtPaid.Text, 0);
        _doc.Lines = _lines.Select(l => l.ToLine())
            .Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.Qty != 0)
            .ToList();
        _doc.RenumberLines();
        return _doc;
    }

    private void ApplyDoc(InvDoc doc)
    {
        _doc.DocId = doc.DocId;
        _doc.Kind = doc.Kind;
        _doc.DocNo = doc.DocNo;
        _doc.DocDate = doc.DocDate;
        _doc.PartyId = doc.PartyId;
        _doc.PartyCode = doc.PartyCode;
        _doc.PartyName = doc.PartyName;
        _doc.WarehouseId = doc.WarehouseId;
        _doc.WarehouseName = string.IsNullOrWhiteSpace(doc.WarehouseName)
            ? InvDoc.DefaultWarehouseName()
            : doc.WarehouseName;
        _doc.CurrencyCode = doc.CurrencyCode;
        _doc.DefaultTaxRate = doc.DefaultTaxRate;
        _doc.Notes = doc.Notes;
        _doc.Status = doc.Status;
        _doc.PaymentMethod = doc.PaymentMethod;
        _doc.ReceiptNo = doc.ReceiptNo;
        _doc.Reference = doc.Reference;
        _doc.BranchName = doc.BranchName;
        _doc.SalesRepName = doc.SalesRepName;
        _doc.CostCenter = doc.CostCenter;
        _doc.PartyAccountName = doc.PartyAccountName;
        _doc.GoodsAccountName = string.IsNullOrWhiteSpace(doc.GoodsAccountName)
            ? InvDoc.DefaultGoodsAccountName(doc.Kind)
            : doc.GoodsAccountName;
        _doc.LinesDiscountAmount = doc.LinesDiscountAmount;
        _doc.LinesExtraAmount = doc.LinesExtraAmount;
        _doc.PaidAmount = doc.PaidAmount;
        _doc.OriginalDocId = doc.OriginalDocId;
        _doc.OriginalDocKind = doc.OriginalDocKind;
        _doc.OriginalDocNo = doc.OriginalDocNo;
        _doc.JournalDocNo = doc.JournalDocNo;

        _txtDocNo.Text = doc.DocNo;
        _dtDate.SelectedDate = doc.DocDate;
        _txtPartyCode.Text = doc.PartyCode;
        _txtPartyName.Text = doc.PartyName;
        _txtCurrency.Text = doc.CurrencyCode;
        _txtTaxRate.Text = doc.DefaultTaxRate.ToString("0.##", CultureInfo.InvariantCulture);
        _txtNotes.Text = doc.Notes ?? string.Empty;
        _txtReceiptNo.Text = doc.ReceiptNo;
        _txtReference.Text = doc.Reference;
        _txtBranch.Text = doc.BranchName;
        _txtSalesRep.Text = doc.SalesRepName;
        _txtWarehouse.Text = _doc.WarehouseName;
        _txtCostCenter.Text = doc.CostCenter;
        _txtPartyAccount.Text = doc.PartyAccountName;
        _txtGoodsAccount.Text = _doc.GoodsAccountName;
        _txtLinesDiscount.Text = FormatAmt(doc.LinesDiscountAmount);
        _txtLinesExtra.Text = FormatAmt(doc.LinesExtraAmount);
        _txtPaid.Text = FormatAmt(doc.PaidAmount);

        _syncingPayment = true;
        try
        {
            _cmbPayment.SelectedIndex = doc.PaymentMethod switch
            {
                InvPaymentMethod.Credit => 1,
                InvPaymentMethod.Checks => 2,
                _ => 0
            };
        }
        finally
        {
            _syncingPayment = false;
        }

        _lines.Clear();
        foreach (var line in doc.Lines)
            _lines.Add(LineVm.FromLine(line));
        if (_lines.Count == 0)
            _lines.Add(new LineVm { TaxRate = doc.DefaultTaxRate, WarehouseName = _doc.WarehouseName });

        var locked = doc.Status == InvDocStatus.Posted || doc.Status == InvDocStatus.Cancelled;
        _btnSave.IsEnabled = !locked;
        _btnReverse.IsEnabled = locked && doc.Kind is InvDocKind.SalesInvoice or InvDocKind.PurchaseInvoice or InvDocKind.PosSale;
        if (_btnReverse.IsEnabled)
        {
            try { _btnReverse.IsEnabled = !InvRepo.HasCompletedReversal(doc.DocId, doc.Kind); }
            catch { _btnReverse.IsEnabled = false; }
        }
        _btnPost.IsEnabled = !locked;
        _grid.IsReadOnly = locked;
        UpdateVoucherOriginalButtons();
        RefreshTotals();
    }

    /// <summary>
    /// السند: enabled only after the invoice is posted (journal voucher exists).
    /// الأصل: enabled only when this window was opened from a linked document
    /// (return / drill-down) that points to an original invoice; disabled for a primary open.
    /// </summary>
    private void UpdateVoucherOriginalButtons()
    {
        var posted = _doc.Status == InvDocStatus.Posted;
        _btnVoucher.IsEnabled = posted && _doc.DocId > 0;
        _btnOriginal.IsEnabled = _doc.OriginalDocId is > 0;
    }

    private void OpenVoucher()
    {
        if (_doc.Status != InvDocStatus.Posted)
        {
            ShowWarn("Invoice.VoucherRequiresPost");
            return;
        }

        try
        {
            // Prefer stored journal number; fall back to invoice doc no as reference.
            var journalNo = string.IsNullOrWhiteSpace(_doc.JournalDocNo)
                ? _doc.DocNo
                : _doc.JournalDocNo;

            var win = new JrnWin();
            win.Owner = this;
            // Best-effort: set document number field if the public surface allows it.
            try
            {
                var field = win.GetType().GetField("_txtDocNo",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field?.GetValue(win) is TextBox txt)
                    txt.Text = journalNo;
            }
            catch
            {
                // JrnWin may load interactively if reflection surface changes.
            }

            win.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Tf("Invoice.VoucherOpenFailed", "تعذر فتح سند القيد:") + " " + ex.Message,
                Tf("Common.Error", "خطأ"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenOriginal()
    {
        if (_doc.OriginalDocId is not long originalId || originalId <= 0)
        {
            ShowWarn("Invoice.NoOriginal");
            return;
        }

        try
        {
            var kind = _doc.OriginalDocKind ?? (_doc.IsSalesSide ? InvDocKind.SalesInvoice : InvDocKind.PurchaseInvoice);
            var win = new InvoiceWin(originalId, kind)
            {
                Owner = this
            };
            win.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Tf("Invoice.OriginalOpenFailed", "تعذر فتح الفاتورة الأصلية:") + " " + ex.Message,
                Tf("Common.Error", "خطأ"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SaveDraft()
    {
        try
        {
            ResolveParty();
            var doc = CaptureDoc();
            if (doc.Lines.Count == 0) { ShowWarn("Invoice.NeedLines"); return; }
            InvRepo.SaveDraft(doc);
            _txtDocNo.Text = doc.DocNo;
            ShowInfo("Invoice.Saved");
            RefreshTotals();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PostDoc()
    {
        if (!AskConfirm("Invoice.ConfirmPost")) return;
        try
        {
            ResolveParty();
            var doc = CaptureDoc();
            if (doc.DocId <= 0) InvRepo.SaveDraft(doc);
            InvRepo.Post(doc.DocId, doc.Kind, AppState.CurrentUser?.Username ?? Environment.UserName);
            doc.Status = InvDocStatus.Posted;
            // Reload so JournalDocNo assigned during post is visible to «السند».
            var reloaded = InvRepo.Load(doc.DocId, doc.Kind);
            if (reloaded is not null)
            {
                _doc.JournalDocNo = reloaded.JournalDocNo;
                _doc.Status = reloaded.Status;
            }
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            _grid.IsReadOnly = true;
            UpdateVoucherOriginalButtons();
            ShowInfo("Invoice.Posted");
            RefreshTotals();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReverseCurrentDocument()
    {
        if (_doc.DocId <= 0) return;
        if (!AskConfirm("Invoice.ConfirmReverse")) return;
        try
        {
            var reason = Tf("Invoice.ReverseReason", "User requested reversal from invoice window");
            InvRepo.ReversePostedInvoice(
                _doc.DocId,
                _doc.Kind,
                reason,
                AppState.CurrentUser?.Username ?? Environment.UserName);
            ShowInfo("Invoice.Reversed");
            _btnReverse.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PickMaterial()
    {
        // Ensure a line is selected; focus description cell for manual entry.
        // Dedicated material search dialog can replace this when MatCard lookup is wired.
        if (_grid.SelectedItem is not LineVm line)
        {
            if (_lines.Count == 0)
                _lines.Add(new LineVm { TaxRate = ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate()) });
            line = _lines[^1];
            _grid.SelectedItem = line;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(line.WarehouseName))
                line.WarehouseName = _txtWarehouse.Text.Trim();
            if (_grid.Columns.Count > 1)
                _grid.CurrentCell = new DataGridCellInfo(line, _grid.Columns[1]);
            _grid.BeginEdit();
            _grid.Focus();
        }
        catch
        {
            // Grid may not be ready during early load.
        }
    }

    private string PartyCodeKey() => _doc.IsSalesSide ? "Invoice.CustomerCode" : "Invoice.SupplierCode";
    private string PartyNameKey() => _doc.IsSalesSide ? "Invoice.CustomerName" : "Invoice.SupplierName";
    private string PartyAccountLabelKey() => _doc.IsSalesSide ? "Invoice.CustomerAccount" : "Invoice.SupplierAccount";

    private static string GetHeaderBrushKey(InvDocKind kind) => kind switch
    {
        InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn => "AccentPrimary",
        InvDocKind.SalesInvoice or InvDocKind.SalesReturn => "AccentPrimary",
        _ => "AccentPrimary"
    };

    private static (Color Header, Color Alternate) GetInvoiceGridPalette(InvDocKind kind) => kind switch
    {
        InvDocKind.SalesInvoice => (Color.FromRgb(0x16, 0x70, 0xB8), Color.FromRgb(0xE8, 0xF2, 0xFC)),
        InvDocKind.SalesReturn => (Color.FromRgb(0xB9, 0x3C, 0x4A), Color.FromRgb(0xFCEB, 0xED)),
        InvDocKind.PurchaseInvoice => (Color.FromRgb(0x16, 0x80, 0x78), Color.FromRgb(0xE7, 0xF5, 0xF2)),
        InvDocKind.PurchaseReturn => (Color.FromRgb(0xB6, 0x69, 0x25), Color.FromRgb(0xFC, 0xF1, 0xE5)),
        InvDocKind.Quotation => (Color.FromRgb(0x3D, 0x8B, 0x52), Color.FromRgb(0xEA, 0xF5, 0xEC)),
        _ => (Color.FromRgb(0x4B, 0x65, 0x82), Color.FromRgb(0xF0, 0xF3, 0xF7))
    };

    private static DataGridTextColumn TextCol(string path, double width, string? headerKey = null)
    {
        var elementStyle = new Style(typeof(TextBlock));
        elementStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        elementStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        var editingStyle = new Style(typeof(TextBox));
        editingStyle.Setters.Add(new Setter(Control.TextAlignmentProperty, TextAlignment.Center));
        editingStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var col = new DataGridTextColumn
        {
            Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width,
            ElementStyle = elementStyle,
            EditingElementStyle = editingStyle
        };
        if (!string.IsNullOrEmpty(headerKey))
            col.Header = headerKey; // BaseWin localization may rewrite headers when present
        return col;
    }

    private static StackPanel Labeled(string key, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        var label = new TextBlock
        {
            Text = Tf(key, key),
            Margin = new Thickness(0, 0, 0, 2),
            FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12)
        };
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    private static TextBlock SectionTitle(string key)
        => new()
        {
            Text = key,
            FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12),
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 0, 0, 4),
            Opacity = 0.85
        };

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.Content = Tf(key, key);
        button.MinWidth = 72;
        button.Height = 28;
        button.Margin = new Thickness(0, 0, 3, 0);
        button.Padding = new Thickness(8, 0, 8, 0);
        try { button.SetResourceReference(StyleProperty, "VisuButtonStyle"); } catch { /* theme optional */ }
    }

    private static decimal ParseDec(string? text, decimal fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v)) return v;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
        return fallback;
    }

    /// <summary>
    /// Translate with optional Arabic/English fallback when the key is still missing from Lang JSON.
    /// Does not hide BaseWin.T(string).
    /// </summary>
    private static string Tf(string key, string? fallback = null)
    {
        var translated = T(key);
        if (!string.IsNullOrEmpty(translated) && !string.Equals(translated, key, StringComparison.Ordinal))
            return translated;
        return fallback ?? key;
    }

    /// <inheritdoc />
    protected override void ApplyLocalizedText()
    {
        Title = Tf(_doc.Kind switch
        {
            InvDocKind.SalesReturn => "Invoice.SalesReturnTitle",
            InvDocKind.PurchaseInvoice => "Invoice.PurchaseTitle",
            InvDocKind.PurchaseReturn => "Invoice.PurchaseReturnTitle",
            InvDocKind.StockIn => "Invoice.StockInTitle",
            InvDocKind.StockOut => "Invoice.StockOutTitle",
            InvDocKind.StockTransfer => "Invoice.StockTransferTitle",
            InvDocKind.OpeningStock => "Invoice.OpeningStockTitle",
            InvDocKind.InventoryCount => "Invoice.InventoryCountTitle",
            InvDocKind.Quotation => "Invoice.QuotationTitle",
            _ => "Invoice.SalesTitle"
        }, WindowTitleFor(_doc.Kind));

        foreach (var btn in new[] { _btnSave, _btnPost, _btnReverse, _btnPrint, _btnPdf, _btnAddLine, _btnRemoveLine, _btnFindMaterial, _btnVoucher, _btnOriginal })
        {
            if (btn.Tag is string key)
                btn.Content = Tf(key, btn.Content?.ToString());
        }

        _btnVoucher.Content = Tf("Invoice.Voucher", "السند");
        _btnOriginal.Content = Tf("Invoice.Original", "الأصل");

        // Rebuild payment combo captions without losing selection
        var payIdx = _cmbPayment.SelectedIndex;
        BuildPaymentCombo();
        if (payIdx >= 0 && payIdx < _cmbPayment.Items.Count)
            _cmbPayment.SelectedIndex = payIdx;

        if (_grid.Columns.Count >= 9)
        {
            _grid.Columns[0].Header = Tf("Invoice.ColCode", "الرمز");
            _grid.Columns[1].Header = Tf("Invoice.ColMaterial", "المادة");
            _grid.Columns[2].Header = Tf("Invoice.ColQty", "الكمية");
            _grid.Columns[3].Header = Tf("Invoice.ColUnit", "الوحدة");
            _grid.Columns[4].Header = Tf("Invoice.ColUnitPrice", "الإفرادي");
            _grid.Columns[5].Header = Tf("Invoice.ColDiscount", "الحسم %");
            _grid.Columns[6].Header = Tf("Invoice.ColTax", "الضريبة %");
            _grid.Columns[7].Header = Tf("Invoice.ColLineTotal", "الإجمالي");
            _grid.Columns[8].Header = Tf("Invoice.ColWarehouse", "المستودع");
        }

        RefreshTotals();
    }

    // ---- Line view-model ----
    private sealed class LineVm : INotifyPropertyChanged
    {
        private string _materialCode = string.Empty;
        private string _description = string.Empty;
        private string _unit = string.Empty;
        private string _qtyText = "1";
        private string _unitPriceText = "0";
        private string _discountPctText = "0";
        private string _taxRateText = "0";
        private string _warehouseName = string.Empty;

        public string MaterialCode { get => _materialCode; set { _materialCode = value; OnPropertyChanged(); } }
        public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnPropertyChanged(); } }
        public string QtyText { get => _qtyText; set { _qtyText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string UnitPriceText { get => _unitPriceText; set { _unitPriceText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string DiscountPctText { get => _discountPctText; set { _discountPctText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string TaxRateText { get => _taxRateText; set { _taxRateText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string WarehouseName { get => _warehouseName; set { _warehouseName = value; OnPropertyChanged(); } }

        public decimal TaxRate
        {
            get => ParseDec(_taxRateText, 0);
            set => TaxRateText = value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public string LineTotalText
        {
            get
            {
                var line = ToLine();
                return line.LineTotal.ToString("N3", CultureInfo.CurrentCulture);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyTotals()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotalText)));

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public InvLine ToLine() => new()
        {
            MaterialCode = MaterialCode,
            Description = Description,
            Unit = Unit,
            Qty = ParseDec(QtyText, 0),
            UnitPrice = ParseDec(UnitPriceText, 0),
            DiscountPct = ParseDec(DiscountPctText, 0),
            TaxRate = ParseDec(TaxRateText, 0),
            WarehouseName = WarehouseName
        };

        public static LineVm FromLine(InvLine line) => new()
        {
            MaterialCode = line.MaterialCode,
            Description = line.Description,
            Unit = line.Unit,
            QtyText = line.Qty.ToString("0.###", CultureInfo.InvariantCulture),
            UnitPriceText = line.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture),
            DiscountPctText = line.DiscountPct.ToString("0.##", CultureInfo.InvariantCulture),
            TaxRateText = line.TaxRate.ToString("0.##", CultureInfo.InvariantCulture),
            WarehouseName = line.WarehouseName
        };

        private static decimal ParseDec(string? text, decimal fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v)) return v;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }
    }
}
