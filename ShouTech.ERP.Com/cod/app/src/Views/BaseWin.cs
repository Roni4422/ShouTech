using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;
using WpfApp = System.Windows.Application;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Mandatory abstract base class for every secondary window in ShouTech ERP.
///
/// Enforces the shell contract automatically for all derived windows:
///   1. Language        - re-localizes live when the shell language changes.
///   2. Flow direction  - RTL/LTR derived from the active language.
///   3. Central theme   - background/foreground resolve through DynamicResource.
///   4. User preferences- window geometry is persisted per user + company.
///   5. Context         - current company and current user are one property away.
///
/// Derived windows must override <see cref="ApplyLocalizedText"/> and route every
/// user-visible string through <see cref="T(string)"/>. No literal UI text is
/// allowed in any derived window.
/// </summary>
public abstract class BaseWin : Window
{
    private bool _geometryWired;
    private EventHandler? _langHandler;

    /// <summary>Stable key used to store per-user geometry. Defaults to the class name.</summary>
    protected virtual string PrefKey => GetType().Name;

    /// <summary>
    /// Name of this window's schedule tint from the central palette in
    /// Themes/VisuId.xaml (the "ScheduleTint{Name}Brush" tokens), or null to
    /// keep the default table tint. When set, every schedule (DataGrid) in
    /// the window alternates its white rows with this tint, applied centrally
    /// on load by overriding the TableAlternateRowBrush resource for this
    /// window only. Each schedule-bearing window claims its own tint so a
    /// window is recognizable by its schedule color alone.
    /// </summary>
    protected virtual string? ScheduleTintName => null;

    /// <summary>When false, geometry persistence is skipped (e.g. modal dialogs).</summary>
    protected virtual bool PersistGeometry => true;

    protected BaseWin()
    {
        // Windows 11 DWM chrome (rounded corners + Mica) for every
        // secondary window; best effort, no-op where unsupported.
        Win11Chrome.Apply(this);

        // Central theme: brushes resolve through the application resource dictionaries
        // so any theme or accent change propagates without code behind.
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "ApplicationFontFamily");
        SetResourceReference(FontSizeProperty, "ApplicationFontSize");

        // Set owner to main window if available and not self
        if (WpfApp.Current?.MainWindow != null && WpfApp.Current.MainWindow != this)
        {
            try
            {
                Owner = WpfApp.Current.MainWindow;
            }
            catch (Exception exL53)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL53, "BaseWin.BaseWin");
                // Window owner assignment should never block window initialization
            }
        }

        Loaded += OnBaseLoaded;
        Closed += OnBaseClosed;
    }

    /// <summary>Localization service owned by the application shell.</summary>
    protected static ILocSvc? LocSvc => (WpfApp.Current as App)?.Localization;

    /// <summary>Translates a resource key using the active language.</summary>
    protected static string T(string key) => LocSvc?.GetString(key) ?? key;

    /// <summary>Localized caption of the active company (empty before login).</summary>
    protected static string CurrentCompanyDisplay => AppState.CurrentCompanyDisplayName;

    /// <summary>Display name of the currently authenticated user.</summary>
    protected static string CurrentUserDisplay => AppState.CurrentUser?.FullName ?? string.Empty;

    /// <summary>Active company code for database connection context.</summary>
    protected static string CurrentCompanyCode => AppState.CurrentCompanyCode ?? string.Empty;

    /// <summary>Active database provider (sqlite or postgresql).</summary>
    protected static string CurrentDatabaseProvider =>
        DatabaseRuntime.IsPostgreSql() ? "postgresql" : "sqlite";

    /// <summary>Active user ID for audit and context purposes.</summary>
    protected static string CurrentUserId => AppState.CurrentUser?.Username ?? string.Empty;

    /// <summary>
    /// Applies every user-visible string of the window. Called once on load and
    /// again automatically whenever the shell language changes at runtime.
    /// </summary>
    protected abstract void ApplyLocalizedText();

    // ================================================================
    // FORM PROPORTIONS — the Phenix layout rule (docs/WINDOW_PROPORTIONS.md)
    //
    // Every form is ONE shared column grid: a fixed side-label column
    // beside each field column, field columns star-sized. Field edges
    // therefore align vertically across ALL rows of the form, a row
    // with fewer fields subdivides the same total width by column
    // spans (full / half / third / quarter), and every group in the
    // window repeats the same span scheme. Labels sit BESIDE their
    // field — never above it. Under RTL the grid mirrors as a whole,
    // so the label stays beside its field.
    //
    // Build a form with CreateFormGrid(pairs) where "pairs" is the
    // largest number of label+field pairs in any row, then place each
    // field with AddFormField. Fields carry NO Width/Height of their
    // own: height comes from the central control styles (32) and width
    // from the column they sit in.
    // ================================================================

    /// <summary>Margin of a field inside a form grid (gap to the next pair + row gap).</summary>
    protected static readonly Thickness FormFieldMargin = new(0, 0, 12, 8);

    /// <summary>Margin of a side label inside a form grid.</summary>
    protected static readonly Thickness FormLabelMargin = new(0, 0, 8, 8);

    /// <summary>
    /// Creates the one shared column grid of a form: <paramref name="pairs"/>
    /// repetitions of (label column = FormLabelWidth, field column = *).
    /// </summary>
    protected static Grid CreateFormGrid(int pairs)
    {
        var grid = new Grid();
        for (var i = 0; i < pairs; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(VisuId.FormLabelWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        return grid;
    }

    /// <summary>
    /// Places a side label (localized by <paramref name="key"/>, re-localized
    /// by the windows' Tag tree-walk) and its field into the form grid.
    /// <paramref name="spanPairs"/> &gt; 1 makes the field span that many
    /// pairs, so a wide field (name, address, memo) still ends exactly on
    /// the shared column edges of the rows above and below it.
    /// </summary>
    protected static void AddFormField(Grid grid, int row, int pair, string key, UIElement control, int spanPairs = 1)
    {
        EnsureFormRow(grid, row);
        var label = new TextBlock
        {
            Text = T(key),
            Tag = key,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = FormLabelMargin
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(label, row);
        Grid.SetColumn(label, pair * 2);
        grid.Children.Add(label);
        PlaceFormControl(grid, row, pair, control, spanPairs);
    }

    /// <summary>
    /// Places a control that carries its own caption (a CheckBox) or needs
    /// no label into the field cell of the form grid, leaving the label
    /// cell empty so the column edges stay aligned.
    /// </summary>
    protected static void AddFormElement(Grid grid, int row, int pair, UIElement control, int spanPairs = 1)
    {
        EnsureFormRow(grid, row);
        PlaceFormControl(grid, row, pair, control, spanPairs);
    }

    private static void EnsureFormRow(Grid grid, int row)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    private static void PlaceFormControl(Grid grid, int row, int pair, UIElement control, int spanPairs)
    {
        ApplyFieldStyle(control);
        if (control is FrameworkElement fe)
        {
            fe.Margin = FormFieldMargin;
            if (control is CheckBox or RadioButton)
                fe.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetRow(control, row);
        Grid.SetColumn(control, pair * 2 + 1);
        if (spanPairs > 1)
            Grid.SetColumnSpan(control, spanPairs * 2 - 1);
        grid.Children.Add(control);
    }

    /// <summary>
    /// Applies the central input style for the control type, so every field
    /// in every form carries the standard height (ControlHeight = 32) from
    /// the theme instead of a local size.
    /// </summary>
    protected static void ApplyFieldStyle(UIElement control)
    {
        switch (control)
        {
            case TextBox textBox:
                textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
                break;
            case ComboBox comboBox:
                comboBox.SetResourceReference(StyleProperty, "VisuComboBoxStyle");
                break;
            case DatePicker datePicker:
                datePicker.SetResourceReference(StyleProperty, "Win11DatePickerStyle");
                break;
            case PasswordBox passwordBox:
                passwordBox.SetResourceReference(StyleProperty, "VisuPasswordBoxStyle");
                break;
        }
    }

    /// <summary>Applies RTL/LTR according to the active language code.</summary>
    protected void ApplyFlowDirection()
    {
        // Single central mechanism: ShellContext applies the shell's live
        // context (direction via LangDirSv, which covers ar/he/fa/ur/...,
        // plus the context stamps), resolved at call time from the central
        // holders. Do not re-derive direction here — a local "ar"-only
        // check would silently override the service for any other RTL
        // language.
        ShellContext.ApplyToWindow(this);
    }

    private void OnBaseLoaded(object sender, RoutedEventArgs e)
    {
        ApplyFlowDirection();
        ApplyScheduleTint();
        ApplyLocalizedText();
        HookLanguageChanged();
        WireGeometryPersistence();
    }

    /// <summary>
    /// Applies the window's claimed schedule tint (see
    /// <see cref="ScheduleTintName"/>) by overriding the alternate-row brush
    /// in this window's own resources. Every DataGrid in the window resolves
    /// TableAlternateRowBrush through DynamicResource, so all of the window's
    /// schedules share the tint and no other window is affected.
    /// </summary>
    private void ApplyScheduleTint()
    {
        try
        {
            if (ScheduleTintName is { Length: > 0 } name &&
                TryFindResource($"ScheduleTint{name}Brush") is Brush tint)
            {
                Resources["TableAlternateRowBrush"] = tint;
            }
        }
        catch (Exception exTint)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exTint, "BaseWin.ApplyScheduleTint");
        }
    }

    private void HookLanguageChanged()
    {
        var loc = LocSvc;
        if (loc == null || _langHandler != null)
            return;

        _langHandler = (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyFlowDirection();
            ApplyLocalizedText();
            OnLanguageChanged();
        }));
        loc.LanguageChanged += _langHandler;
    }

    /// <summary>Optional hook for derived windows after a live language switch.</summary>
    protected virtual void OnLanguageChanged()
    {
    }

    private void WireGeometryPersistence()
    {
        if (_geometryWired || !PersistGeometry)
            return;

        _geometryWired = true;
        WinPref.RestoreGeometry(this, GeometryScope());
        Closing += OnGeometryClosing;
    }

    private void OnGeometryClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            WinPref.SaveGeometry(this, GeometryScope());
        }
        catch (Exception exL152)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL152, "BaseWin.OnGeometryClosing");
            // Geometry persistence must never block a window from closing.
        }
    }

    private string GeometryScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/{PrefKey}";
    }

    private void OnBaseClosed(object? sender, EventArgs e)
    {
        if (LocSvc != null && _langHandler != null)
            LocSvc.LanguageChanged -= _langHandler;
        _langHandler = null;
    }

    /// <summary>Shows a localized information message box owned by this window.</summary>
    protected void ShowInfo(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Info"),
            MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Shows a localized warning message box owned by this window.</summary>
    protected void ShowWarn(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Warning"),
            MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Shows a localized error message box owned by this window.</summary>
    protected void ShowError(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Error"),
            MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>Asks a localized yes/no question; returns true on Yes.</summary>
    protected bool AskConfirm(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Confirm"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>
    /// Formats an integer using Western digits (0-9) only.
    /// Use for all card codes, document numbers, and quantities shown in code fields.
    /// </summary>
    protected static string FormatWestern(long value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a position label as "current / total" with Western digits.
    /// </summary>
    protected static string FormatPosition(int index1Based, int total)
        => index1Based.ToString(System.Globalization.CultureInfo.InvariantCulture)
         + " / "
         + total.ToString(System.Globalization.CultureInfo.InvariantCulture);



}
