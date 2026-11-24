using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.Win32;
using ShouTech.App.Services;
using ShouTech.App.Localization;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
// Implementation note: UI text is provided by language resources.
// Implementation note: UI text is provided by language resources.
/// </summary>
public partial class CoaWin : BaseWin
{
    protected override string? ScheduleTintName => "Lavender";

        private static bool IsGroupMode(string? postingMode)
        {
            if (string.IsNullOrWhiteSpace(postingMode)) return false;
            var m = postingMode.Trim();
            return m.Equals("Summary", StringComparison.OrdinalIgnoreCase)
                || m.Equals("Group", StringComparison.OrdinalIgnoreCase)
                || m.Equals("Summary", StringComparison.Ordinal);
        }


    private const int EncryptionIterations = 150_000;
    private static readonly string[] XlsxHeader = { "Code", "ParentCode", "Name", "NameEn", "Type" };
    private static readonly JsonSerializerOptions StaJsonOptions = new() { WriteIndented = false };
    private const string EncryptedMagic = "STA1";
    private const int EncryptionVersion = 1;

    private readonly ObservableCollection<AccountNode> _allRoots = new();
    private AccountNode? _selected;

    // Picker mode (owner grid spec item 5): the chart IS the account
    // search window for voucher grids — the same directory-as-search
    // pattern as MatDirWin for materials. Editing actions hide;
    // double-click / Enter on a postable (leaf) account accepts it.
    private bool _pickerMode;
    public AccountNode? PickedAccount { get; private set; }

    public static AccountNode? ShowPicker(Window owner, string? initialSearch = null)
    {
        var win = new CoaWin { Owner = owner, _pickerMode = true };
        win.ActionButtonsPanel.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(initialSearch))
            win.TxtSearch.Text = initialSearch;
        return win.ShowDialog() == true ? win.PickedAccount : null;
    }

    private void TryAcceptPick()
    {
        if (!_pickerMode || _selected is null)
            return;
        if (IsGroupMode(_selected.TypeLabel))
            return; // group/summary accounts are not postable
        PickedAccount = _selected;
        DialogResult = true;
        Close();
    }

    private void TvAccounts_MouseDoubleClick(object sender, MouseButtonEventArgs e) => TryAcceptPick();

    private void TvAccounts_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAcceptPick();
            if (_pickerMode)
                e.Handled = true;
        }
    }

    public CoaWin()
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);
        LoadChart();
        Loaded += (_, _) =>
        {
            SizeToLongestAccountName();
            PositionOnRightWithPadding();
        };
    }

    private void PositionOnRightWithPadding()
    {
        const double padding = 20;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + padding, workArea.Right - ActualWidth - padding);
        Top = workArea.Top + padding;
    }

    private void LoadChart()
    {
        try
        {
            AccRepo.EnsureSchema();
            var rows = AccRepo.LoadAll();
            if (rows.Count == 0)
            {
                SeedDefaultChart();
                rows = AccRepo.LoadAll();
            }
            BuildTreeFromRows(rows);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CoaWin.xaml.LoadChart");
            MessageBox.Show(this, Tf("CoaWin.LoadFailed", ex.Message), T("CoaWin.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            _allRoots.Clear();
            RebindTree(_allRoots);
        }
    }

    private void BuildTreeFromRows(IEnumerable<AccRepo.AccRec> rows)
    {
        _allRoots.Clear();
        var map = rows.OrderBy(x => x.Code, StringComparer.Ordinal).ToDictionary(x => x.Code, x => new AccountNode
        {
            Id = x.Id, Code = x.Code, ParentCode = x.ParentCode, Name = x.Name, NameEn = x.NameEn,
            TypeLabel = IsGroupMode(x.PostingMode) ? "Group" : "Account", IsExpanded = x.ParentCode == null
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var node in map.Values.OrderBy(x => x.Code, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(node.ParentCode) && map.TryGetValue(node.ParentCode, out var parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else _allRoots.Add(node);
        }
        RebindTree(_allRoots);
    }

    private static void SeedDefaultChart()
    {
        var data = new (string Code, string? Parent, string Ar, string En, string Mode)[]
        {
            ("1", null, "Assets", "Assets", "Summary"), ("11", "1", "Fixed Assets", "Fixed Assets", "Summary"),
            ("111", "11", "Land", "Land", "Postable"), ("112", "11", "Buildings", "Buildings", "Postable"),
            ("113", "11", "Machinery & Equipment", "Machinery & Equipment", "Postable"), ("114", "11", "Vehicles", "Vehicles", "Postable"),
            ("115", "11", "Furniture & Fixtures", "Furniture & Fixtures", "Postable"), ("116", "11", "Office Equipment & Computers", "Office Equipment & Computers", "Postable"),
            ("12", "1", "Current Assets", "Current Assets", "Summary"), ("121", "12", "Inventory", "Inventory", "Postable"),
            ("122", "12", "Prepaid Expenses", "Prepaid Expenses", "Postable"), ("123", "12", "Advances", "Advances", "Postable"),
            ("13", "1", "Cash & Cash Equivalents", "Cash & Cash Equivalents", "Summary"), ("131", "13", "Cash", "Cash", "Postable"),
            ("132", "13", "Banks", "Banks", "Postable"), ("133", "13", "Checks & Receivables", "Checks & Receivables", "Postable"),
            ("14", "1", "Customers", "Customers", "Summary"), ("1401", "14", "Customer 1", "Customer 1", "Postable"),
            ("15", "1", "Other Receivables", "Other Receivables", "Summary"), ("151", "15", "Other Receivables", "Other Receivables", "Postable"),
            ("2", null, "Liabilities", "Liabilities", "Summary"), ("21", "2", "Current Liabilities", "Current Liabilities", "Summary"),
            ("211", "21", "Suppliers", "Suppliers", "Postable"), ("212", "21", "Accrued Expenses", "Accrued Expenses", "Postable"),
            ("213", "21", "Taxes Payable", "Taxes Payable", "Postable"), ("22", "2", "Long-term Liabilities", "Long-term Liabilities", "Summary"),
            ("221", "22", "Long-term Loans", "Long-term Loans", "Postable"), ("3", null, "Equity", "Equity", "Summary"),
            ("31", "3", "Capital", "Capital", "Postable"), ("32", "3", "Retained Earnings", "Retained Earnings", "Postable"),
            ("33", "3", "Net Profit or Loss", "Net Profit or Loss", "Postable"), ("4", null, "Purchases", "Purchases", "Summary"),
            ("41", "4", "Purchases", "Purchases", "Postable"), ("42", "4", "Purchase Returns", "Purchase Returns", "Postable"),
            ("5", null, "Revenue", "Revenue", "Summary"), ("51", "5", "Sales", "Sales", "Postable"),
            ("52", "5", "Sales Returns", "Sales Returns", "Postable"), ("53", "5", "Other Revenue", "Other Revenue", "Postable"),
            ("6", null, "Expenses", "Expenses", "Summary"), ("61", "6", "Salaries & Wages", "Salaries & Wages", "Postable"),
            ("62", "6", "Rent", "Rent", "Postable"), ("63", "6", "Utilities", "Utilities", "Postable"),
            ("64", "6", "Telecommunications", "Telecommunications", "Postable"), ("65", "6", "Maintenance", "Maintenance", "Postable"),
            ("66", "6", "Office Supplies", "Office Supplies", "Postable")
        };
        foreach (var item in data)
        {
            var rec = new AccRepo.AccRec(string.Empty, item.Code, item.Parent, item.Ar, item.En, item.Mode, "Active", "Low", "Unspecified", false, false, false, false, false, false, 0, string.Empty, 0, 0, 0);
            AccRepo.Save(null, rec, false);
        }
    }

    private void BuildSampleChart() => LoadChart();


    private static AccountNode Node(string code, string ar, string en, string type, params AccountNode[] children)
    {
        var node = new AccountNode { Code = code, Name = ar, NameEn = en, TypeLabel = type };
        foreach (var child in children)
        {
            child.Parent = node;
            node.Children.Add(child);
        }
        return node;
    }

    private void RebindTree(IEnumerable<AccountNode> roots)
    {
        TvAccounts.ItemsSource = roots.ToList();
        TxtCount.Text = Tf("CoaWin.AccountsCount", CountNodes(_allRoots));
    }

    private static int CountNodes(IEnumerable<AccountNode> nodes) => nodes.Sum(n => 1 + CountNodes(n.Children));

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = TxtSearch.Text.Trim();
        if (q.Length == 0)
        {
            RebindTree(_allRoots);
            SizeToLongestAccountName();
            return;
        }

        var result = new List<AccountNode>();
        foreach (var root in _allRoots)
            CollectMatches(root, q, result);

        RebindTree(result.Select(ClonePath).Where(x => x != null).Cast<AccountNode>());
    }

    private static void CollectMatches(AccountNode node, string q, List<AccountNode> result)
    {
        if (node.Code.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            node.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            node.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase))
            result.Add(node);

        foreach (var child in node.Children)
            CollectMatches(child, q, result);
    }

    private static AccountNode? ClonePath(AccountNode node)
    {
        var chain = new Stack<AccountNode>();
        for (var n = node; n != null; n = n.Parent)
            chain.Push(n);

        AccountNode? parent = null;
        AccountNode? root = null;
        while (chain.Count > 0)
        {
            var source = chain.Pop();
            var clone = Node(source.Code, source.Name, source.NameEn, source.TypeLabel);
            clone.IsExpanded = true;
            if (parent == null)
                root = clone;
            else
            {
                clone.Parent = parent;
                parent.Children.Add(clone);
            }
            parent = clone;
        }
        return root;
    }

    private void SizeToLongestAccountName()
    {
        var names = _allRoots.SelectMany(Flatten).Select(GetDisplayName).ToList();
        if (names.Count == 0)
            return;

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var maxWidth = names.Max(name =>
        {
            var ft = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize, Foreground, 1.0);
            return ft.WidthIncludingTrailingWhitespace;
        });

        // Step 12: never re-widen past the work area — this Loaded-time
        // resize runs AFTER the central clamp/cap, so the old uncapped
        // 1600 upper bound silently defeated the screen-fit policy
        // (sweep section 13, CoaWin post-clamp re-widening).
        var maxFit = Math.Max(MinWidth, SystemParameters.WorkArea.Width);
        Width = Math.Clamp(Math.Ceiling((maxWidth + 90) * 1.20), MinWidth, Math.Min(1600, maxFit));
    }

    private static string GetDisplayName(AccountNode node) =>
        string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
            ? node.Name
            : node.NameEn;

    private static IEnumerable<AccountNode> Flatten(AccountNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var nested in Flatten(child))
                yield return nested;
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void TvAccounts_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as AccountNode;
        UpdateSelectedText();
    }

    private void OpenAccount_Click(object sender, RoutedEventArgs e)
    {
        var node = GetContextAccount(sender) ?? _selected;
        OpenSelectedAccount(node);
    }

    private void AddChild_Click(object sender, RoutedEventArgs e)
    {
        var node = GetContextAccount(sender) ?? _selected;
        OpenNewChildAccount(node);
    }

    private static AccountNode? GetContextAccount(object sender)
    {
        if (sender is not MenuItem menuItem)
            return null;

        var contextMenu = menuItem.Parent as ContextMenu;
        if (contextMenu?.PlacementTarget is not TreeViewItem item)
            return null;

        return item.DataContext as AccountNode;
    }

    private void OpenSelectedAccount(AccountNode? node)
    {
        if (node == null)
            return;

        _selected = node;
        var record = AccRepo.FindById(node.Id);
        if (record == null)
        {
            RefreshChart();
            return;
        }

        var card = new AccCard(record)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        card.Saved += (_, _) => RefreshChart(record.Code);
        card.ShowDialog();
        RefreshChart(record.Code);
    }

    private void OpenNewChildAccount(AccountNode? node)
    {
        if (node == null)
            return;

        _selected = node;
        var parentCode = node.Code;
        var card = new AccCard(parentCode)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        card.Saved += (_, _) => RefreshChart();
        card.ShowDialog();
        RefreshChart();
    }

    // Opens the real account statement report (was a placeholder
    // "Action: ..." message box via ShowAction).
    private void Statement_Click(object sender, RoutedEventArgs e) => RptNav.OpenAccountStatement(this);
    private void Ledger_Click(object sender, RoutedEventArgs e) => RptNav.OpenAccountStatement(this);
    private void Print_Click(object sender, RoutedEventArgs e) => ExportPdf();

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) Clipboard.SetText(_selected.Code);
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) Clipboard.SetText(GetDisplayName(_selected));
    }

    private void OpenParent_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Parent == null)
            return;

        _selected = _selected.Parent;
        UpdateSelectedText();
        SelectTreeNode(_selected);
    }

    private void ExpandBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        SetExpanded(_selected, true);
    }

    private void CollapseBranch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        SetExpanded(_selected, false);
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetExpandedAll(true);
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetExpandedAll(false);

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshChart(_selected?.Code);

    private void RefreshChart(string? preferredCode = null)
    {
        var search = TxtSearch.Text;
        LoadChart();
        SizeToLongestAccountName();

        if (!string.IsNullOrWhiteSpace(search))
        {
            TxtSearch.Text = search;
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredCode))
        {
            var node = FindNodeByCode(preferredCode);
            if (node != null)
            {
                _selected = node;
                UpdateSelectedText();
                SelectTreeNode(node);
            }
        }
    }

    private AccountNode? FindNodeByCode(string code) =>
        _allRoots.SelectMany(Flatten).FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));

    private void SelectTreeNode(AccountNode node)
    {
        foreach (var root in _allRoots)
            if (SelectTreeNode(root, node))
                return;
    }

    private static bool SelectTreeNode(AccountNode node, AccountNode target)
    {
        if (ReferenceEquals(node, target))
            return true;
        foreach (var child in node.Children)
            if (SelectTreeNode(child, target))
            {
                node.IsExpanded = true;
                return true;
            }
        return false;
    }

    private static void SetExpanded(AccountNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        if (!expanded) return;
        foreach (var child in node.Children)
            child.IsExpanded = false;
    }

    private void SetExpandedAll(bool expanded)
    {
        foreach (var root in _allRoots)
            SetExpandedRecursive(root, expanded);
    }

    private static void SetExpandedRecursive(AccountNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            SetExpandedRecursive(child, expanded);
    }

    private void UpdateSelectedText()
    {
        TxtSelected.Text = _selected == null
            ? T("CoaWin.NoSelection")
            : Tf("CoaWin.SelectedAccount", _selected.Code, _selected.Name);
    }

    // The XAML-declared TreeView context menu lives in a popup and does not
    // inherit the window FlowDirection; sync it from the window every time it
    // opens (same pattern as the code-created menus below).
    private void TreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            menu.FlowDirection = FlowDirection;
    }

    private void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { FlowDirection = FlowDirection };
        menu.Items.Add(CreateMenuItem("Excel", T("CoaWin.ExpExcelTip"), (_, _) => ExportExcel()));
        menu.Items.Add(CreateMenuItem("CSV", T("CoaWin.ExpCsvTip"), (_, _) => ExportCsv()));
        menu.Items.Add(CreateMenuItem("PDF", T("CoaWin.ExpPdfTip"), (_, _) => ExportPdf()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("CoaWin.ExpSta"), T("CoaWin.ExpStaTip"), (_, _) => ExportEncrypted()));
        menu.IsOpen = true;
    }

    private void ImportMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { FlowDirection = FlowDirection };
        menu.Items.Add(CreateMenuItem("Excel", T("CoaWin.ImpExcelTip"), (_, _) => ImportFile(T("CoaWin.ExcelFilter"))));
        menu.Items.Add(CreateMenuItem("CSV", T("CoaWin.ImpCsvTip"), (_, _) => ImportFile(T("CoaWin.CsvFilter"))));
        menu.Items.Add(CreateMenuItem(T("CoaWin.ExpSta"), T("CoaWin.ImpStaTip"), (_, _) => ImportEncrypted()));
        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, string toolTip, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header, ToolTip = toolTip, Padding = new Thickness(12, 7, 12, 7) };
        item.Click += handler;
        return item;
    }

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            Filter = T("CoaWin.CsvFilter"),
            DefaultExt = ".csv",
            FileName = "ChartOfAccounts.csv",
            Title = T("CoaWin.ExportCsvTitle")
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dlg.FileName, BuildDelimitedContent(','), new UTF8Encoding(true));
            ShowInfoMsg(T("CoaWin.ExportCsvOk"));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CoaWin.xaml.ExportCsv");
            ShowErrorMsg(Tf("CoaWin.ExportCsvFailed", ex.Message));
        }
    }

    private void ExportExcel()
    {
        var dlg = new SaveFileDialog
        {
            Filter = T("CoaWin.ExcelFilter"),
            DefaultExt = ".xlsx",
            FileName = "ChartOfAccounts.xlsx",
            Title = T("CoaWin.ExportExcelTitle")
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            using var stream = File.Create(dlg.FileName);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
            WriteZipText(archive, "[Content_Types].xml", BuildXlsxContentTypes());
            WriteZipText(archive, "_rels/.rels", BuildXlsxRootRels());
            WriteZipText(archive, "xl/workbook.xml", BuildXlsxWorkbook());
            WriteZipText(archive, "xl/_rels/workbook.xml.rels", BuildXlsxWorkbookRels());
            WriteZipText(archive, "xl/worksheets/sheet1.xml", BuildXlsxSheet());
            ShowInfoMsg(T("CoaWin.ExportExcelOk"));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CoaWin.xaml.ExportExcel");
            ShowErrorMsg(Tf("CoaWin.ExportExcelFailed", ex.Message));
        }
    }

    private static void WriteZipText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildXlsxContentTypes() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "</Types>";

    private static string BuildXlsxRootRels() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string BuildXlsxWorkbook() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"ChartOfAccounts\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private static string BuildXlsxWorkbookRels() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";

    private string BuildXlsxSheet()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<string[]> { XlsxHeader };
        rows.AddRange(_allRoots.SelectMany(Flatten).Select(n => new[]
        {
            n.Code, n.Parent?.Code ?? string.Empty, n.Name, n.NameEn, n.TypeLabel
        }));

        var sheetData = new XElement(ns + "sheetData");
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new XElement(ns + "row", new XAttribute("r", r + 1));
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cellRef = $"{ExcelColumn(c + 1)}{r + 1}";
                row.Add(new XElement(ns + "c", new XAttribute("r", cellRef), new XAttribute("t", "inlineStr"),
                    new XElement(ns + "is", new XElement(ns + "t", rows[r][c]))));
            }
            sheetData.Add(row);
        }

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "worksheet",
                new XElement(ns + "dimension", new XAttribute("ref", $"A1:E{rows.Count}")),
                sheetData));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string ExcelColumn(int number)
    {
        var result = string.Empty;
        while (number > 0)
        {
            number--;
            result = (char)('A' + number % 26) + result;
            number /= 26;
        }
        return result;
    }

    private void ExportEncrypted()
    {
        var dlg = new SaveFileDialog
        {
            Filter = T("CoaWin.StaFilter"),
            DefaultExt = ".sta",
            FileName = "ShouTechCrtOfAcc.sta",
            Title = T("CoaWin.ExportStaTitle")
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var password = AskPassword(T("CoaWin.PasswordTitle"));
        if (password == null)
            return;

        try
        {
            var payload = new StaPayload
            {
                Format = "STCOA",
                SchemaVersion = 1,
                ExportedAtUtc = DateTime.UtcNow,
                Language = App.CurrentLanguageCode ?? "ar",
                Accounts = _allRoots.SelectMany(Flatten).Select(ToStaAccount).ToList()
            };
            var json = JsonSerializer.Serialize(payload, StaJsonOptions);
            var plain = Encoding.UTF8.GetBytes(json);

            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, EncryptionIterations, HashAlgorithmName.SHA256, 32);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain, cipher, tag);

            using var fs = File.Create(dlg.FileName);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
            bw.Write(EncryptedMagic);
            bw.Write(EncryptionVersion);
            bw.Write(EncryptionIterations);
            WriteBytes(bw, salt);
            WriteBytes(bw, nonce);
            WriteBytes(bw, tag);
            WriteBytes(bw, cipher);

            ShowInfoMsg(T("CoaWin.ExportStaOk"));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CoaWin.xaml.ExportEncrypted");
            ShowErrorMsg(Tf("CoaWin.ExportStaFailed", ex.Message));
        }
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static StaAccount ToStaAccount(AccountNode node) => new()
    {
        Code = node.Code,
        ParentCode = node.Parent?.Code,
        Name = node.Name,
        NameEn = node.NameEn,
        Type = node.TypeLabel
    };

    private void ImportFile(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = T("CoaWin.ImportTitle") };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            if (Path.GetExtension(dlg.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                ImportXlsx(dlg.FileName);
            else
                ImportDelimitedContent(File.ReadAllText(dlg.FileName, Encoding.UTF8).TrimStart('\uFEFF'), ',');

            ShowInfoMsg(T("CoaWin.ImportOk"));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CoaWin.xaml.ImportFile");
            ShowErrorMsg(Tf("CoaWin.ImportFailed", ex.Message));
        }
    }

    private void ImportXlsx(string fileName)
    {
        using var archive = ZipFile.OpenRead(fileName);
        var entry = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException(T("CoaWin.ValExcelSheetMissing"));
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var lines = new List<string>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = row.Elements(ns + "c").Select(cell =>
            {
                var type = (string?)cell.Attribute("t");
                if (type == "inlineStr")
                    return cell.Element(ns + "is")?.Element(ns + "t")?.Value ?? string.Empty;
                return cell.Element(ns + "v")?.Value ?? string.Empty;
            }).ToArray();
            lines.Add(string.Join(',', values.Select(EscapeFieldForCommaImport)));
        }
        ImportDelimitedContent(string.Join(Environment.NewLine, lines), ',');
    }

    private static string EscapeFieldForCommaImport(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private void ImportEncrypted()
    {
        var dlg = new OpenFileDialog
        {
            Filter = T("CoaWin.StaFilter"),
            Title = T("CoaWin.ImportStaTitle")
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var password = AskPassword(T("CoaWin.PasswordTitle"));
        if (password == null)
            return;

        try
        {
            using var fs = File.OpenRead(dlg.FileName);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            if (br.ReadString() != EncryptedMagic)
                throw new InvalidDataException(T("CoaWin.ValStaInvalid"));

            var version = br.ReadInt32();
            if (version != EncryptionVersion)
                throw new InvalidDataException(Tf("CoaWin.ValStaVersion", version));

            var iterations = br.ReadInt32();
            var salt = ReadBytes(br, 64);
            var nonce = ReadBytes(br, 32);
            var tag = ReadBytes(br, 32);
            var cipher = ReadBytes(br, 100_000_000);

            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, cipher, tag, plain);

            var payload = JsonSerializer.Deserialize<StaPayload>(Encoding.UTF8.GetString(plain))
                ?? throw new InvalidDataException(T("CoaWin.ValStaEmpty"));
            if (!string.Equals(payload.Format, "STCOA", StringComparison.Ordinal) || payload.SchemaVersion != 1)
                throw new InvalidDataException(T("CoaWin.ValStaStructure"));

            ImportAccounts(payload.Accounts);
            ShowInfoMsg(T("CoaWin.ImportStaOk"));
        }
        catch (CryptographicException exL754)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL754, "CoaWin.xaml.ImportEncrypted");
            ShowErrorMsg(T("CoaWin.DecryptFailed"));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CoaWin.xaml.ImportEncrypted");
            ShowErrorMsg(Tf("CoaWin.ImportStaFailed", ex.Message));
        }
    }

    private static byte[] ReadBytes(BinaryReader reader, int maxLength)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > maxLength)
            throw new InvalidDataException(T("CoaWin.ValStaChunk"));
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidDataException(T("CoaWin.ValStaIncomplete"));
        return bytes;
    }

    private string BuildDelimitedContent(char delimiter)
    {
        var sb = new StringBuilder();
        sb.Append("Code").Append(delimiter)
            .Append("ParentCode").Append(delimiter)
            .Append("Name").Append(delimiter)
            .Append("NameEn").Append(delimiter)
            .Append("Type").AppendLine();

        foreach (var node in _allRoots.SelectMany(Flatten))
        {
            sb.Append(EscapeField(node.Code, delimiter)).Append(delimiter)
                .Append(EscapeField(node.Parent?.Code ?? string.Empty, delimiter)).Append(delimiter)
                .Append(EscapeField(node.Name, delimiter)).Append(delimiter)
                .Append(EscapeField(node.NameEn, delimiter)).Append(delimiter)
                .Append(EscapeField(node.TypeLabel, delimiter)).AppendLine();
        }
        return sb.ToString();
    }

    private static string EscapeField(string value, char delimiter)
    {
        if (value.Contains(delimiter) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private void ImportDelimitedContent(string text, char delimiter)
    {
        var rows = ParseDelimited(text, delimiter).ToList();
        if (rows.Count <= 1)
            throw new InvalidDataException(T("CoaWin.ValNoAccounts"));

        var data = rows.Skip(1)
            .Where(r => r.Count >= 5 && !string.IsNullOrWhiteSpace(r[0]))
            .Select(r => new ImportedAccount(r[0], r[1], r[2], r[3], r[4]))
            .ToList();

        ImportAccounts(data.Select(x => new StaAccount
        {
            Code = x.Code,
            ParentCode = x.ParentCode,
            Name = x.Name,
            NameEn = x.NameEn,
            Type = x.TypeLabel
        }).ToList());
    }

    private void ImportAccounts(IReadOnlyCollection<StaAccount> accounts)
    {
        if (accounts.Count == 0)
            throw new InvalidDataException(T("CoaWin.ValNoValid"));

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in accounts)
        {
            if (string.IsNullOrWhiteSpace(row.Code) || !unique.Add(row.Code))
                throw new InvalidDataException(T("CoaWin.ValMissingOrDupCode"));
            if (string.IsNullOrWhiteSpace(row.Name) && string.IsNullOrWhiteSpace(row.NameEn))
                throw new InvalidDataException(Tf("CoaWin.ValAccountNoName", row.Code));
        }

        var byCode = accounts.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        if (accounts.Any(x => !string.IsNullOrWhiteSpace(x.ParentCode) && !byCode.ContainsKey(x.ParentCode)))
            throw new InvalidDataException(T("CoaWin.ValParentMissing"));

        var nodes = accounts.ToDictionary(
            x => x.Code,
            x => Node(x.Code, x.Name ?? string.Empty, x.NameEn ?? string.Empty, x.Type ?? "Account"),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in accounts)
        {
            var node = nodes[row.Code];
            if (string.IsNullOrWhiteSpace(row.ParentCode))
                continue;

            var parent = nodes[row.ParentCode];
            if (string.Equals(parent.Code, node.Code, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Tf("CoaWin.ValOwnParent", node.Code));
            node.Parent = parent;
            parent.Children.Add(node);
        }

        foreach (var node in nodes.Values)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var current = node; current != null; current = current.Parent)
            {
                if (!visited.Add(current.Code))
                    throw new InvalidDataException(T("CoaWin.ValCycle"));
            }
        }

        // Persist the import. Previously this method only rebuilt the
        // in-memory tree and the callers showed "imported successfully"
        // while nothing reached the database — the chart silently reverted
        // on Refresh/close. Now every imported account is upserted by code
        // through AccRepo (parents first: a parent code is a prefix of its
        // children, so ordering by code length is a valid topological order).
        // Existing accounts keep their balances, flags and tax settings —
        // the file formats carry only code/parent/names/type.
        var existing = AccRepo.LoadAll()
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var row in accounts.OrderBy(x => x.Code.Length))
        {
            var postingMode = string.Equals(row.Type, "Group", StringComparison.OrdinalIgnoreCase)
                ? "Summary"
                : "Postable";
            var parentCode = string.IsNullOrWhiteSpace(row.ParentCode) ? null : row.ParentCode;
            if (existing.TryGetValue(row.Code, out var current))
            {
                var updated = current with
                {
                    ParentCode = parentCode,
                    Name = string.IsNullOrWhiteSpace(row.Name) ? current.Name : row.Name,
                    NameEn = string.IsNullOrWhiteSpace(row.NameEn) ? current.NameEn : row.NameEn,
                    PostingMode = postingMode
                };
                AccRepo.Save(current, updated, renumberDescendants: false);
            }
            else
            {
                var created = new AccRepo.AccRec(
                    string.Empty, row.Code, parentCode,
                    row.Name ?? string.Empty, row.NameEn ?? string.Empty,
                    postingMode, "Active", "Low", "Unspecified",
                    false, false, false, false, false, false,
                    0m, string.Empty, 0m, 0m, 0m);
                AccRepo.Save(null, created, renumberDescendants: false);
            }
        }

        // Reload from the database so the tree shows the persisted truth.
        LoadChart();
    }

    private static IEnumerable<List<string>> ParseDelimited(string text, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                    quoted = !quoted;
            }
            else if (ch == delimiter && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((ch == '\n' || ch == '\r') && !quoted)
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 0)
                {
                    yield return row;
                    row = new List<string>();
                }
            }
            else
                field.Append(ch);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    private string? AskPassword(string title)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Width = 390,
            Height = 175,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            FlowDirection = FlowDirection,
            Background = TryFindResource("BackgroundBrush") as Brush ?? Brushes.White,
            Foreground = TryFindResource("TextBrush") as Brush ?? Brushes.Black
        };

        // Label BESIDE its field on the central form grid (the
        // label-above layout this dialog used is banned by
        // docs/WINDOW_PROPORTIONS.md \u00a71.4); sizes come from the
        // central tokens, not literals.
        var panel = new Grid { Margin = new Thickness(18) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(VisuId.FormLabelWidth) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = T("CoaWin.PasswordPrompt"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = FormLabelMargin
        };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(prompt, 0);
        Grid.SetColumn(prompt, 0);
        panel.Children.Add(prompt);

        var box = new PasswordBox { Height = VisuId.ControlHeight };
        box.SetResourceReference(StyleProperty, "VisuPasswordBoxStyle");
        Grid.SetRow(box, 0);
        Grid.SetColumn(box, 1);
        panel.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal,  Margin = new Thickness(0, 14, 0, 0) };
        FlowAlign.End(buttons);
        var ok = new Button { Content = T("Common.OK"), MinWidth = VisuId.ButtonMinWidth, IsDefault = true, Margin = new Thickness(0, 0, 6, 0), ToolTip = T("CoaWin.PasswordOkTip") };
        var cancel = new Button { Content = T("Common.Cancel"), MinWidth = VisuId.ButtonMinWidth, IsCancel = true, ToolTip = T("CoaWin.PasswordCancelTip") };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(box.Password)) dialog.DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 1);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Password : null;
    }

    private void ExportPdf()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        var document = new FlowDocument
        {
            PagePadding = new Thickness(40),
            FlowDirection = FlowDirection,
            FontFamily = ShouTech.App.Services.VisuId.ResolveFontFamily("ApplicationFontFamily", "Segoe UI"),
            FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11)
        };
        document.Blocks.Add(new Paragraph(new Run(T("CoaWin.Title"))) { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(20), FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
        document.Blocks.Add(new Paragraph(new Run(Tf("CoaWin.ExportDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))) { TextAlignment = TextAlignment.Right });

        foreach (var node in _allRoots.SelectMany(Flatten))
        {
            var indent = Math.Max(0, GetDepth(node) * 18);
            var text = $"{node.Code}    {GetDisplayName(node)}    {node.TypeLabel}";
            document.Blocks.Add(new Paragraph(new Run(text))
            {
                Margin = new Thickness(indent, 1, 0, 1),
                TextAlignment = FlowDirection == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left
            });
        }

        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, T("CoaWin.PrintJobName"));
    }

    private static int GetDepth(AccountNode node)
    {
        var depth = 0;
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            depth++;
        return depth;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowInfoMsg(string message) => MessageBox.Show(this, message, "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowErrorMsg(string message) => MessageBox.Show(this, message, "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Error);

    private sealed class ImportedAccount
    {
        public ImportedAccount(string code, string parentCode, string nameAr, string nameEn, string typeLabel)
        {
            Code = code;
            ParentCode = parentCode;
            Name = nameAr;
            NameEn = nameEn;
            TypeLabel = typeLabel;
        }
        public string Code { get; }
        public string ParentCode { get; }
        public string Name { get; }
        public string NameEn { get; }
        public string TypeLabel { get; }
    }

    private sealed class StaPayload
    {
        public string Format { get; set; } = "STCOA";
        public int SchemaVersion { get; set; } = 1;
        public DateTime ExportedAtUtc { get; set; }
        public string Language { get; set; } = "ar";
        public List<StaAccount> Accounts { get; set; } = new();
    }

    private sealed class StaAccount
    {
        public string Code { get; set; } = string.Empty;
        public string? ParentCode { get; set; }
        public string? Name { get; set; }
        public string? NameEn { get; set; }
        public string? Type { get; set; }
    }
    protected override void ApplyLocalizedText()
    {
        // Static captions resolve through the LocProps keys in the XAML
        // via the central UiLoczr walk; the dynamic surfaces (status
        // readouts, tree node display/tooltips, the popup context menu
        // the walk cannot reach) are refreshed here so the window
        // relocalizes live on a language switch.
        try { UiLoczr.ApplyToWindow(this); }
        catch (Exception exLoc)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exLoc, "CoaWin.ApplyLocalizedText");
        }
        TxtCount.Text = Tf("CoaWin.AccountsCount", CountNodes(_allRoots));
        UpdateSelectedText();
        LocalizeContextMenu();
        // Node display text and tooltips are binding-evaluated; rebind
        // so they re-resolve in the new language.
        var items = TvAccounts.ItemsSource;
        TvAccounts.ItemsSource = null;
        TvAccounts.ItemsSource = items;
    }

    private void LocalizeContextMenu()
    {
        if (TvAccounts.ContextMenu is not ContextMenu menu)
            return;
        menu.FlowDirection = FlowDirection;
        foreach (var obj in menu.Items)
        {
            if (obj is MenuItem item)
            {
                var key = LocProps.GetKey(item);
                if (!string.IsNullOrWhiteSpace(key))
                    item.Header = T(key);
                var tipKey = LocProps.GetToolTipKey(item);
                if (!string.IsNullOrWhiteSpace(tipKey))
                    item.ToolTip = T(tipKey);
            }
        }
    }

}

public sealed class AccountNode
{
    public string Id { get; init; } = string.Empty;
    public string? ParentCode { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string TypeLabel { get; init; } = string.Empty;
    // The tree template binds DisplayCode — the property did not exist, so
    // the code column rendered blank for every account.
    public string DisplayCode => Code;
    // Language-aware like CoaWin.GetDisplayName: Arabic UI shows Name,
    // other languages prefer NameEn. (Was NameEn-first in all languages.)
    public string DisplayName => string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
        ? (!string.IsNullOrWhiteSpace(Name) ? Name : NameEn)
        : (!string.IsNullOrWhiteSpace(NameEn) ? NameEn : Name);
    // Localized rendering of the canonical TypeLabel ("Group"/"Account");
    // TypeLabel itself stays canonical because it is the export file's
    // Type column value.
    public string TypeDisplay => string.Equals(TypeLabel, "Group", StringComparison.OrdinalIgnoreCase)
        ? AppHost.Localization.GetString("CoaWin.TypeGroup", "Group")
        : AppHost.Localization.GetString("CoaWin.TypeAccount", "Account");
    // CA1863: cache CompositeFormat instances for the tooltip patterns.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.CompositeFormat> NodeFormats = new();

    private static string NodeFormat(string key, string fallback, object? arg)
    {
        var format = NodeFormats.GetOrAdd(
            AppHost.Localization.GetString(key, fallback),
            static value => System.Text.CompositeFormat.Parse(value));
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, arg);
    }

    public string ToolTipText => $"{Code} - {Name}\n{NameEn}\n{NodeFormat("CoaWin.NodeTypeLine", "Type: {0}", TypeDisplay)}\n{(Children.Count > 0 ? NodeFormat("CoaWin.NodeChildren", "Child accounts: {0}", Children.Count) : AppHost.Localization.GetString("CoaWin.NodePostable", "Postable account"))}";
    public bool IsExpanded { get; set; }
    public AccountNode? Parent { get; set; }
    public ObservableCollection<AccountNode> Children { get; } = new();
}
