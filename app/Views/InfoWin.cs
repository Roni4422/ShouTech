using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// The global Information Window (INFO_WINDOW_STUDY): one modeless
/// singleton that shows the property sheet of whatever entity the user
/// has selected anywhere in the program. Opened with F9 from any
/// BaseWin window (the host publishes its selection through
/// IInfoContextProvider), with the shell status-bar button, or with
/// the POS screen's labelled button. In Follow mode it tracks the
/// latest published selection live; Pin freezes the current entity.
/// The visible properties are configured PER ENTITY TYPE from the
/// checkbox menu that opens on right-click over the window's header
/// (the Phenix pattern) or from the Properties button, and persist
/// per user through WinPref (InfoSvc). Every value comes from
/// InfoSvc — the system's own stores, no parallel computation.
/// </summary>
public sealed class InfoWin : BaseWin
{
    private static InfoWin? _instance;

    private readonly Border _header = new();
    private readonly TextBlock _kindLabel = new();
    private readonly TextBlock _titleLabel = new();
    private readonly TextBlock _subtitleLabel = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _message = new();
    private readonly CheckBox _chkFollow = new();
    private readonly CheckBox _chkPin = new();
    private readonly Button _btnProperties = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnClose = new();

    private InfoRef? _ref;
    private InfoContext? _context;

    private InfoWin()
    {
        Width = 460;
        Height = 620;
        MinWidth = 340;
        MinHeight = 320;
        BuildUi();
        ApplyLocalizedText();
        InfoContextHub.SelectionPublished += OnSelectionPublished;
        Closed += (_, _) =>
        {
            InfoContextHub.SelectionPublished -= OnSelectionPublished;
            if (ReferenceEquals(_instance, this))
                _instance = null;
        };
        ShowEmpty();
    }

    /// <summary>Opens (or reuses) the window on the given entity.</summary>
    public static void ShowFor(InfoRef reference, InfoContext? context, Window? owner)
    {
        var window = GetOrCreate(owner);
        window.SetRef(reference, context);
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }

    /// <summary>Opens the window on the last published selection, if any.</summary>
    public static void ShowLast(Window? owner)
    {
        var window = GetOrCreate(owner);
        if (window._ref is null && InfoContextHub.LastRef is { } last)
            window.SetRef(last, InfoContextHub.LastContext);
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }

    private static InfoWin GetOrCreate(Window? owner)
    {
        if (_instance is { IsLoaded: true })
            return _instance;
        _instance = new InfoWin();
        if (owner is not null && !ReferenceEquals(owner, _instance) && owner.IsLoaded)
        {
            try { _instance.Owner = owner; }
            catch (InvalidOperationException) { /* base class already owned it */ }
        }
        return _instance;
    }

    private void OnSelectionPublished(InfoRef? reference, InfoContext? context)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSelectionPublished(reference, context)));
            return;
        }
        if (reference is null)
            return;
        if (_chkFollow.IsChecked == true && _chkPin.IsChecked != true)
            SetRef(reference, context);
    }

    private void SetRef(InfoRef reference, InfoContext? context)
    {
        _ref = reference;
        _context = context;
        RefreshSheet();
    }

    private void ShowEmpty()
    {
        _kindLabel.Text = string.Empty;
        _titleLabel.Text = L("InfoWin.NoSelection", "Select an item in any window, then press F9.");
        _subtitleLabel.Text = string.Empty;
        _rows.Children.Clear();
        _message.Text = string.Empty;
    }

    private void RefreshSheet()
    {
        if (_ref is null)
        {
            ShowEmpty();
            return;
        }
        _rows.Children.Clear();
        _message.Text = string.Empty;
        try
        {
            var sheet = InfoSvc.Resolve(_ref, _context);
            _kindLabel.Text = L($"InfoWin.Kind.{sheet.Kind}", sheet.Kind.ToString());
            _titleLabel.Text = sheet.Title;
            _subtitleLabel.Text = sheet.Subtitle ?? string.Empty;
            var visible = InfoSvc.LoadVisibleKeys(sheet.Kind);
            foreach (var property in sheet.Properties)
            {
                if (!visible.Contains(property.Key))
                    continue;
                AddRow(property, 0);
            }
        }
        catch (AppError ex) when (ex.Code == ErrorCodes.InfoEntityNotFound)
        {
            _kindLabel.Text = string.Empty;
            _titleLabel.Text = L("InfoWin.NotFound", "The selected item no longer exists.");
            _subtitleLabel.Text = string.Empty;
        }
        catch (AppError ex) when (ex.Code == ErrorCodes.InfoLoadFailed)
        {
            // InfoSvc already reported the failure with its catalog code.
            _message.Text = L("InfoWin.LoadFailed", "The information could not be loaded.");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InfoWin.RefreshSheet");
            _message.Text = L("InfoWin.LoadFailed", "The information could not be loaded.");
        }
    }

    private void AddRow(InfoProperty property, int indent)
    {
        var grid = new Grid { Margin = new Thickness(indent * 18, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = L($"InfoWin.Prop.{_ref!.Kind}.{property.Key}", property.Key),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var value = new TextBlock
        {
            Text = FormatValue(property),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        _rows.Children.Add(grid);
        if (property.Children is { Count: > 0 } children)
            foreach (var child in children)
                AddRow(child, indent + 1);
    }

    private static string FormatValue(InfoProperty property)
    {
        if (property.Text is not null)
            return property.Text;
        var number = property.Number ?? 0m;
        return property.Format switch
        {
            InfoFormat.Qty => number.ToString("N3", CultureInfo.InvariantCulture),
            InfoFormat.Money => number.ToString("N2", CultureInfo.InvariantCulture),
            InfoFormat.Count => number.ToString("N0", CultureInfo.InvariantCulture),
            InfoFormat.Percent => number.ToString("N1", CultureInfo.InvariantCulture) + "%",
            InfoFormat.YesNo => number != 0m ? L("Buttons.Yes", "Yes") : L("Buttons.No", "No"),
            InfoFormat.MoneySide => FormatMoneySide(number),
            _ => number.ToString("N2", CultureInfo.InvariantCulture)
        };
    }

    private static string FormatMoneySide(decimal number)
    {
        var magnitude = Math.Abs(number).ToString("N2", CultureInfo.InvariantCulture);
        if (number > 0m)
            return $"{magnitude} {L("InfoWin.Side.Debit", "Debit")}";
        if (number < 0m)
            return $"{magnitude} {L("InfoWin.Side.Credit", "Credit")}";
        return $"{magnitude} {L("InfoWin.Side.Balanced", "Balanced")}";
    }

    /// <summary>
    /// The Phenix properties pattern: a checkable list of every
    /// property the current entity type can serve; toggling persists
    /// the visible set for this entity type (WinPref) and re-renders.
    /// </summary>
    private void ShowPropertiesMenu(FrameworkElement target)
    {
        if (_ref is null)
            return;
        var kind = _ref.Kind;
        var menu = new ContextMenu { PlacementTarget = target };
        foreach (var key in InfoSvc.PropertyKeys(kind))
        {
            var item = new MenuItem
            {
                Header = L($"InfoWin.Prop.{kind}.{key}", key),
                IsCheckable = true,
                Tag = key
            };
            menu.Items.Add(item);
        }
        var visible = InfoSvc.LoadVisibleKeys(kind);
        foreach (MenuItem item in menu.Items)
            item.IsChecked = visible.Contains((string)item.Tag);
        menu.Closed += (_, _) =>
        {
            var chosen = menu.Items.Cast<MenuItem>()
                .Where(i => i.IsChecked)
                .Select(i => (string)i.Tag)
                .ToList();
            InfoSvc.SaveVisibleKeys(kind, chosen);
            RefreshSheet();
        };
        menu.IsOpen = true;
    }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header — right-click opens the property checklist (Phenix).
        var headerPanel = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        _kindLabel.FontSize = 11;
        _kindLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _titleLabel.FontSize = 16;
        _titleLabel.FontWeight = FontWeights.SemiBold;
        _titleLabel.TextWrapping = TextWrapping.Wrap;
        _subtitleLabel.FontSize = 12;
        _subtitleLabel.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        headerPanel.Children.Add(_kindLabel);
        headerPanel.Children.Add(_titleLabel);
        headerPanel.Children.Add(_subtitleLabel);
        _header.Child = headerPanel;
        _header.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        _header.MouseRightButtonUp += (_, e) =>
        {
            ShowPropertiesMenu(_header);
            e.Handled = true;
        };
        Grid.SetRow(_header, 0);
        root.Children.Add(_header);

        var body = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        body.Children.Add(_rows);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        _message.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_message);
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Bottom action bar (owner rule: actions live at the bottom).
        var bar = new Grid { Margin = new Thickness(10, 8, 10, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _chkFollow.IsChecked = true;
        _chkFollow.Margin = new Thickness(0, 0, 10, 0);
        _chkFollow.VerticalAlignment = VerticalAlignment.Center;
        _chkPin.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_chkFollow, 0);
        Grid.SetColumn(_chkPin, 1);
        bar.Children.Add(_chkFollow);
        bar.Children.Add(_chkPin);
        var column = 3;
        foreach (var button in new[] { _btnProperties, _btnRefresh, _btnClose })
        {
            button.Margin = new Thickness(6, 0, 0, 0);
            button.Padding = new Thickness(12, 4, 12, 4);
            Grid.SetColumn(button, column++);
            bar.Children.Add(button);
        }
        _btnProperties.Click += (_, _) => ShowPropertiesMenu(_btnProperties);
        _btnRefresh.Click += (_, _) => RefreshSheet();
        _btnClose.Click += (_, _) => Close();
        var barBorder = new Border
        {
            Child = bar,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(barBorder, 2);
        root.Children.Add(barBorder);

        Content = root;
    }

    protected override void ApplyLocalizedText()
    {
        Title = L("InfoWin.Title", "Information");
        _header.ToolTip = L("InfoWin.PropertiesTip", "Right-click here to choose the properties shown for this kind of item.");
        _chkFollow.Content = L("InfoWin.Follow", "Follow selection");
        _chkPin.Content = L("InfoWin.Pin", "Pin");
        _btnProperties.Content = L("InfoWin.Properties", "Properties");
        _btnRefresh.Content = L("Common.Refresh", "Refresh");
        _btnClose.Content = L("Common.Close", "Close");
        if (_ref is null)
            ShowEmpty();
        else
            RefreshSheet();
    }

    private static string L(string key, string fallback)
    {
        try
        {
            return Loc.Get(key, fallback);
        }
        catch
        {
            return fallback;
        }
    }
}
