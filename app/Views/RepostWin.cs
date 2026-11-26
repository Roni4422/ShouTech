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
/// Repost window: pick an entity type (sales invoice, purchase invoice or
/// manufacturing order) and a document number; the window loads the stored
/// header totals next to totals recomputed from the lines (documents:
/// subtotal = SUM(qty*price), discount = SUM(qty*price*discount_pct/100),
/// tax = SUM(line_tax), grand = subtotal - discount + tax; manufacturing:
/// total = SUM(line amounts) + overhead). Apply writes the recomputed
/// totals back in ONE transaction with an audit record; when the totals
/// already match, the window says so and Apply stays disabled.
/// </summary>
public sealed class RepostWin : BaseWin
{
    protected override string? ScheduleTintName => "Thicket";
    private readonly OperationsService? _service;
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly ComboBox _entity = new();
    private readonly TextBox _docNo = new();
    private readonly TextBlock _result = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _loadButton = new();
    private readonly Button _applyButton = new();
    private readonly Button _closeButton = new();

    private DocumentTotalsRow? _loaded;
    private bool _loadedIsMfg;

    public RepostWin()
    {
        _service = AppHost.Operations;
        Width = 720;
        Height = 520;
        MinWidth = 620;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.InvoiceRepost))
        {
            MessageBox.Show(this, T("Repost.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _entity.SelectedIndex = 0;
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_loadButton, "Repost.Load");
        F2Ui.StyleButton(_applyButton, "Repost.Apply");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += async (_, _) => await LoadAsync();
        _applyButton.Click += async (_, _) => await ApplyAsync();
        _closeButton.Click += (_, _) => Close();
        _applyButton.IsEnabled = false;
        root.Children.Add(F2Ui.BottomBar(_loadButton, _applyButton, _closeButton));

        var content = new DockPanel();
        var selector = F2Ui.CreateFormGrid(2);
        selector.Margin = new Thickness(0, 0, 0, 8);
        _entity.Items.Add(new ComboBoxItem { Tag = "sales" });
        _entity.Items.Add(new ComboBoxItem { Tag = "purchase" });
        _entity.Items.Add(new ComboBoxItem { Tag = "mfg" });
        F2Ui.AddField(_labels, selector, 0, 0, "Repost.Entity", _entity);
        F2Ui.AddField(_labels, selector, 0, 1, "Repost.DocNo", _docNo);
        DockPanel.SetDock(selector, Dock.Top);
        content.Children.Add(selector);

        _result.TextWrapping = TextWrapping.Wrap;
        _result.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        content.Children.Add(_result);
        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Repost.Title");
        if (_entity.Items[0] is ComboBoxItem sales) sales.Content = T("Repost.SalesInvoice");
        if (_entity.Items[1] is ComboBoxItem purchase) purchase.Content = T("Repost.PurchaseInvoice");
        if (_entity.Items[2] is ComboBoxItem mfg) mfg.Content = T("Repost.ManufacturingOrder");
        F2Ui.ApplyTexts(_labels, new List<(DataGridColumn, string)>(), new[] { _loadButton, _applyButton, _closeButton }, T);
    }

    private string EntityTag => (_entity.SelectedItem as ComboBoxItem)?.Tag as string ?? "sales";

    private async Task LoadAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Repost.ServiceUnavailable"));
            var no = _docNo.Text.Trim();
            if (string.IsNullOrWhiteSpace(no))
                throw new InvalidOperationException(T("Repost.DocNoRequired"));
            _loadedIsMfg = EntityTag == "mfg";
            _loaded = _loadedIsMfg
                ? await _service.GetMfgOrderTotalsAsync(CurrentCompanyCode, no)
                : await _service.GetDocumentTotalsAsync(CurrentCompanyCode, EntityTag, no);
            _applyButton.IsEnabled = false;
            if (_loaded is null)
            {
                _result.Text = T("Repost.NotFound");
                _statusBar.Text = no;
                return;
            }
            ShowLoaded();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowLoaded()
    {
        var r = _loaded!;
        var lines = new List<string>
        {
            $"{T("Repost.DocNo")}: {r.DocNo}   {T("Repost.Date")}: {r.DocDate}   {T("Repost.Status")}: {r.Status}   {T("Repost.Lines")}: {r.LineCount}",
            string.Empty,
            $"{T("Repost.Field")}        {T("Repost.Stored")}        {T("Repost.Recomputed")}",
        };
        if (_loadedIsMfg)
        {
            lines.Add($"{T("Repost.TotalCost")}        {F2Ui.Fmt(r.StoredGrand)}        {F2Ui.Fmt(r.ComputedGrand)}");
        }
        else
        {
            lines.Add($"{T("Repost.Subtotal")}        {F2Ui.Fmt(r.StoredSubtotal)}        {F2Ui.Fmt(r.ComputedSubtotal)}");
            lines.Add($"{T("Repost.Discount")}        {F2Ui.Fmt(r.StoredDiscount)}        {F2Ui.Fmt(r.ComputedDiscount)}");
            lines.Add($"{T("Repost.Tax")}        {F2Ui.Fmt(r.StoredTax)}        {F2Ui.Fmt(r.ComputedTax)}");
            lines.Add($"{T("Repost.GrandTotal")}        {F2Ui.Fmt(r.StoredGrand)}        {F2Ui.Fmt(r.ComputedGrand)}");
        }
        lines.Add(string.Empty);
        lines.Add(r.Differs ? T("Repost.Differs") : T("Repost.Matches"));
        _result.Text = string.Join(Environment.NewLine, lines);
        _applyButton.IsEnabled = r.Differs;
        _statusBar.Text = r.DocNo;
    }

    private async Task ApplyAsync()
    {
        try
        {
            if (_service is null || _loaded is null)
                throw new InvalidOperationException(T("Repost.ServiceUnavailable"));
            if (MessageBox.Show(this, T("Repost.ConfirmApply"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (_loadedIsMfg)
                await _service.ApplyMfgRepostAsync(CurrentCompanyCode, _loaded, CurrentUserId);
            else
                await _service.ApplyRepostAsync(CurrentCompanyCode, EntityTag, _loaded, CurrentUserId);
            MessageBox.Show(this, T("Repost.Applied"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
