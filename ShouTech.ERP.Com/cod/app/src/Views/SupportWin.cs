using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>Which ticket register the Support window writes to.</summary>
public enum SupportWinMode
{
    Support,
    Suggestion
}

/// <summary>
/// Support / Suggestions window: subject, details and contact e-mail are
/// saved as a real support_tickets row (migration 037) through
/// OperationsService, and the window lists the company's tickets of the
/// same kind with their numbers and status. Honest scope: tickets are
/// persisted in the local ticket register on this installation — outbound
/// sending needs a configured mail server, which this window does not
/// pretend to have.
/// </summary>
public sealed class SupportWin : BaseWin
{
    private readonly OperationsService? _service;
    private readonly SupportWinMode _mode;
    private readonly List<SupportTicket> _tickets = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBox _subject = new();
    private readonly TextBox _body = new();
    private readonly TextBox _email = new();
    private readonly TextBlock _note = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    // CA1863: cache CompositeFormat instances for repeatedly used format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    public SupportWin(SupportWinMode mode = SupportWinMode.Support)
    {
        _mode = mode;
        _service = AppHost.Operations;
        Width = 920;
        Height = 640;
        MinWidth = 760;
        MinHeight = 500;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Formats a localized pattern using a cached CompositeFormat
    /// to avoid re-parsing the format string on every call (CA1863),
    /// with the current UI culture applied explicitly (CA1305).
    /// </summary>
    private static string FormatLocalized(string pattern, params object?[] args)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        var compositeFormat = CompositeFormats.GetOrAdd(
            pattern,
            static value => CompositeFormat.Parse(value));

        return string.Format(CultureInfo.CurrentCulture, compositeFormat, args);
    }

    private string Kind => _mode == SupportWinMode.Suggestion ? SupportTicketKinds.Suggestion : SupportTicketKinds.Support;
    private string Permission => _mode == SupportWinMode.Suggestion ? PermCat.Help.Suggestions : PermCat.Help.Support;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(Permission))
        {
            MessageBox.Show(this, T("Support.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_saveButton, "Support.Save");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _saveButton.Click += async (_, _) => await SaveAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_saveButton, _refreshButton, _closeButton));

        var content = new DockPanel();
        var form = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        // One-pair form grid: side labels, fields stretch to the window
        // width, edges aligned (Phenix rule).
        var formGrid = F2Ui.CreateFormGrid(1);
        F2Ui.AddField(_labels, formGrid, 0, 0, "Support.Subject", _subject);
        _body.Height = 110;
        _body.AcceptsReturn = true;
        _body.TextWrapping = TextWrapping.Wrap;
        F2Ui.AddField(_labels, formGrid, 1, 0, "Support.Body", _body);
        F2Ui.AddField(_labels, formGrid, 2, 0, "Support.ContactEmail", _email);
        form.Children.Add(formGrid);
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 2, 0, 4);
        form.Children.Add(_note);
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);

        F2Ui.ConfigureGrid(_grid);
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Support.TicketNo", nameof(SupportTicket.TicketNo), 80, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Support.Subject", nameof(SupportTicket.Subject), 240));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Support.Status", nameof(SupportTicket.Status), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Support.ContactEmail", nameof(SupportTicket.ContactEmail), 180));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "Support.Created", nameof(SupportTicket.CreatedAt), 150, center: true));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T(_mode == SupportWinMode.Suggestion ? "Support.SuggestionTitle" : "Support.Title");
        _note.Text = T("Support.LocalRegisterNote");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _saveButton, _refreshButton, _closeButton }, T);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Support.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetTicketsAsync(CurrentCompanyCode);
            _tickets.Clear();
            _tickets.AddRange(rows.Where(t => t.Kind == Kind));
            _grid.ItemsSource = null;
            _grid.ItemsSource = _tickets;
            _statusBar.Text = $"{_tickets.Count}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Support.ServiceUnavailable"));
            var ticket = new SupportTicket
            {
                Kind = Kind,
                Subject = _subject.Text.Trim(),
                Body = _body.Text.Trim(),
                ContactEmail = F2Ui.NullIfBlank(_email.Text)
            };
            var saved = await _service.CreateTicketAsync(CurrentCompanyCode, ticket, CurrentUserId);
            MessageBox.Show(this,
                FormatLocalized(T("Support.Saved"), saved.TicketNo),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            _subject.Clear();
            _body.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
