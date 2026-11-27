using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using ShouTech.App.Services;
using ShouTech.App.Localization;
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

        PreviewKeyDown += OnHelpPreviewKeyDown;
        PreviewKeyDown += OnInfoPreviewKeyDown;
        Loaded += OnBaseLoaded;
        Closed += OnBaseClosed;
    }

    /// <summary>
    /// The window's F1 help topic key (owner help decision
    /// 2026-11-24): by convention the window's class name; a window
    /// overrides this only when its topic deliberately differs.
    /// The audit's topic-key column is the enforcement mechanism —
    /// every window has a key, and the commercial-phase help index
    /// maps each key to its topic, index entry and keywords.
    /// </summary>
    public virtual string HelpTopicKey => GetType().Name;

    private void OnHelpPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1 || e.IsRepeat || this is HelpWin)
            return;

        e.Handled = true;
        try
        {
            var help = new HelpWin(HelpTopicKey) { Owner = this };
            help.ShowDialog();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BaseWin.F1Help");
        }
    }

    /// <summary>
    /// F9 — the global Information Window (INFO_WINDOW_STUDY): opens
    /// InfoWin on this window's current selection when the window
    /// implements IInfoContextProvider; otherwise reopens it on the
    /// last published selection, and with no selection anywhere it
    /// opens on its localized empty state. InfoWin and HelpWin are
    /// excluded (InfoWin is the surface itself; HelpWin is modal
    /// reading material, not a data window).
    /// </summary>
    private void OnInfoPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9 || e.IsRepeat || this is InfoWin || this is HelpWin)
            return;

        e.Handled = true;
        try
        {
            if (this is IInfoContextProvider provider && provider.CurrentInfoRef is { } reference)
                InfoWin.ShowFor(reference, provider.CurrentInfoContext, this);
            else
                InfoWin.ShowLast(this);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BaseWin.F9Info");
        }
    }

    /// <summary>
    /// Publishes this window's current selection to the information
    /// hub (a visible InfoWin in Follow mode tracks it live). Windows
    /// implementing IInfoContextProvider call this whenever their
    /// selection changes.
    /// </summary>
    protected void PublishInfoSelection()
    {
        if (this is IInfoContextProvider provider)
            InfoContextHub.Publish(provider.CurrentInfoRef, provider.CurrentInfoContext);
    }

    /// <summary>Localization service owned by the application shell.</summary>
    protected static ILocSvc? LocSvc => (WpfApp.Current as App)?.Localization;

    /// <summary>Translates a resource key using the active language.</summary>
    protected static string T(string key) => LocSvc?.GetString(key) ?? key;

    // CA1863: cache CompositeFormat instances for repeatedly used
    // localized format patterns (the BinWin/BcdGenWin pattern, held
    // centrally here so every window formats the same way).
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats = new();

    /// <summary>
    /// Translates a resource key and formats it with the supplied
    /// arguments in the current culture — the one sanctioned way to
    /// build a localized message with placeholders.
    /// </summary>
    protected static string Tf(string key, params object?[] args)
    {
        var format = CompositeFormats.GetOrAdd(T(key), static value => CompositeFormat.Parse(value));
        return string.Format(CultureInfo.CurrentCulture, format, args);
    }

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
    //
    // Side labels created here carry the central VisuTextBlockLabelStyle
    // (Themes/VisuId.xaml), which owns the label contract: text aligns
    // to the reading-start edge of the label column — the edge adjacent
    // to the field — resolved from the window's FlowDirection by the
    // style's triggers, in LTR and RTL alike. Do not re-derive label
    // alignment in a window; that is how MatCard's local copy inverted
    // RTL and pushed Arabic labels to the far edge of their columns.
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
        label.SetResourceReference(StyleProperty, "VisuTextBlockLabelStyle");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(label, row);
        Grid.SetColumn(label, pair * 2);
        grid.Children.Add(label);
        PlaceFormControl(grid, row, pair, control, spanPairs);
    }

    /// <summary>
    /// List-registered variant of
    /// <see cref="AddFormField(Grid, int, int, string, UIElement, int)"/> for
    /// windows that localize through a label registry (the ProjWin pattern):
    /// the side label is added to <paramref name="labels"/> so the window's
    /// ApplyLocalizedText can set its text from the key, exactly as the old
    /// per-window Labeled helpers did.
    /// </summary>
    protected static void AddFormField(
        List<(TextBlock Label, string Key)> labels,
        Grid grid, int row, int pair, string key, UIElement control, int spanPairs = 1)
    {
        EnsureFormRow(grid, row);
        var label = new TextBlock
        {
            Text = T(key),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = FormLabelMargin
        };
        label.SetResourceReference(StyleProperty, "VisuTextBlockLabelStyle");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        labels.Add((label, key));
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

    /// <summary>
    /// Builds the shared compact record-number strip for data-entry windows.
    /// Order is fixed physically: next 10, next 1, number, previous 1, previous 10.
    /// Navigation is delegated to the owning window; this class owns the UI contract.
    /// </summary>
    protected StackPanel CreateRecordNumberBar(TextBox numberBox, Action<int> navigate)
    {
        ArgumentNullException.ThrowIfNull(numberBox);
        ArgumentNullException.ThrowIfNull(navigate);

        numberBox.Width = 104;
        numberBox.MinWidth = 104;
        numberBox.Height = 26;
        numberBox.Margin = new Thickness(1, 0, 1, 0);
        numberBox.IsReadOnly = true;
        numberBox.Focusable = false;
        numberBox.TextAlignment = TextAlignment.Center;
        ApplyFieldStyle(numberBox);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            FlowDirection = this.FlowDirection,
            HorizontalAlignment = this.FlowDirection == FlowDirection.RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 2, 2, 6),
            Tag = "RecordNavigationBar"
        };

        var label = new TextBlock
        {
            Text = T("RecordNavigation.Number"),
            Tag = "RecordNavigation.Number",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        bar.Children.Add(label);

        static Button MakeButton(string glyph, string key, int delta, Action<int> onNavigate)
        {
            var button = new Button
            {
                Content = glyph,
                Width = 23,
                MinWidth = 23,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0),
                ToolTip = T(key),
                Focusable = true
            };
            button.SetResourceReference(StyleProperty, "VisuButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(button, T(key));
            button.Click += (_, _) => onNavigate(delta);
            return button;
        }

        // Each window owns its own numbering source and sequence.
        // Sentinels navigate to the first/last record; +/-1 and +/-10 are relative.
        bar.Children.Add(MakeButton(this.FlowDirection == FlowDirection.RightToLeft ? "⇥" : "⇤", "Common.First", int.MinValue, navigate));
        bar.Children.Add(MakeButton("≪", "RecordNavigation.PreviousTen", -10, navigate));
        bar.Children.Add(MakeButton("‹", "RecordNavigation.PreviousOne", -1, navigate));
        bar.Children.Add(numberBox);
        bar.Children.Add(MakeButton("›", "RecordNavigation.NextOne", 1, navigate));
        bar.Children.Add(MakeButton("≫", "RecordNavigation.NextTen", 10, navigate));
        bar.Children.Add(MakeButton(this.FlowDirection == FlowDirection.RightToLeft ? "⇤" : "⇥", "Common.Last", int.MaxValue, navigate));
        return bar;
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

        // Navigation strips belong to the form edge opposite the active
        // reading direction; do not leave them stranded at the physical left
        // when the window switches to an RTL language.
        var pending = new Stack<DependencyObject>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is StackPanel panel &&
                string.Equals(panel.Tag as string, "RecordNavigationBar", StringComparison.Ordinal))
            {
                panel.HorizontalAlignment = FlowDirection == FlowDirection.RightToLeft
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = count - 1; index >= 0; index--)
                pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }

    /// <summary>
    /// Applies the shared RTL-aware form-label style to XAML and code-built
    /// label/field pairs that have not declared a more specific style.
    /// A label is recognized only when it shares a Grid row with an input
    /// control in a later column; headings and report text are left untouched.
    /// </summary>
    private void ApplyFormLabelStyles()
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(this);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is Grid grid)
            {
                foreach (UIElement child in grid.Children)
                {
                    if (child is not TextBlock label ||
                        label.Style is not null ||
                        Grid.GetColumnSpan(label) != 1)
                        continue;

                    var row = Grid.GetRow(label);
                    var column = Grid.GetColumn(label);
                    var hasAdjacentInput = grid.Children.Cast<UIElement>().Any(candidate =>
                        !ReferenceEquals(candidate, label) &&
                        Grid.GetRow(candidate) == row &&
                        Grid.GetColumn(candidate) > column &&
                        (candidate is TextBox or ComboBox or DatePicker or PasswordBox or CheckBox or RadioButton));

                    if (hasAdjacentInput)
                        label.SetResourceReference(StyleProperty, "VisuTextBlockLabelStyle");
                }
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var i = childCount - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private void OnBaseLoaded(object sender, RoutedEventArgs e)
    {
        ApplyBackdropKind();
        ApplyFlowDirection();
        ApplyFormLabelStyles();
        ApplyScheduleTint();
        ApplyLocalizedText();
        ApplyPrimaryButtonConvention();
        HookLanguageChanged();
        // Owner rule (2026-11-24): a saved user geometry WINS — the
        // 50% work-area cap is the FIRST-OPEN default only, so the
        // clamp runs solely when no saved geometry was restored.
        if (!_geometryRestored)
            ClampToWorkArea();
    }

    /// <summary>True when WinPref restored a saved geometry for
    /// this window (see OnSourceInitialized).</summary>
    private bool _geometryRestored;

    /// <summary>
    /// Geometry restore happens here, not in the constructor or at
    /// Loaded: the platform guidance is to restore inside
    /// OnSourceInitialized — restoring earlier misplaces maximized
    /// windows on multi-monitor setups.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WireGeometryPersistence();
    }

    /// <summary>Hook for derived windows to restore their own
    /// persisted state (pane widths, selected tab, expanded
    /// sections) via PrefGet — called right after the geometry
    /// restore, whether or not a geometry record existed.</summary>
    protected virtual void OnRestoreWindowState()
    {
    }

    /// <summary>Hook for derived windows to persist their own
    /// state via PrefSet — called on Closing, before the geometry
    /// save.</summary>
    protected virtual void OnSaveWindowState()
    {
    }

    /// <summary>Reads one of this window's named state values from
    /// the per-user store (company defaults underneath).</summary>
    protected string? PrefGet(string key) => WinPref.LoadValue(GeometryScope(), key);

    /// <summary>Saves one of this window's named state values to
    /// the per-user store.</summary>
    protected void PrefSet(string key, string value) => WinPref.SaveValue(GeometryScope(), key, value);

    // Windows 11 backdrop layering: the shell window keeps the full
    // Mica backdrop (the central WindowBackdropKind token); secondary
    // windows — everything the shell owns — use Mica Alt, the flatter
    // transient-window material Windows 11 itself uses for dialogs,
    // so the shell reads as the surface behind its tools. Best effort:
    // Win11Chrome no-ops where the DWM backdrop is unsupported.
    private void ApplyBackdropKind()
    {
        Win11Chrome.SetBackdrop(
            this,
            Owner != null
                ? Win11Chrome.BackdropKind.MicaAlt
                : Win11Chrome.ResolveBackdropKind());
    }

    // ================================================================
    // ACCENTED-PRIMARY CONVENTION (docs/WINDOW_PROPORTIONS.md §2.2)
    //
    // The primary action of a window's bottom command bar carries the
    // central accent treatment (VisuAccentButtonStyle +
    // PrimaryButtonMinWidth); every other action stays on the plain
    // button style. Windows used to apply this by hand, so most bars
    // shipped with no primary distinction at all (acceptance sweep,
    // visual audits: "no primary-button distinction anywhere").
    //
    // The convention is now applied centrally, here, from the one
    // signal every button already carries: its localization key
    // (LocProps.Key in XAML, or the Tag the code-built StyleButton
    // helpers store the key in). Priority: a Post action outranks a
    // Save action (posting is the culminating act of a document
    // window); at most ONE button per window is accented. A window
    // that already accents a button itself (DatWin's Start, WhsWin's
    // Post, …) is respected untouched.
    // ================================================================

    private static readonly string[] PrimaryKeySegments = { "Post", "Save" };

    private void ApplyPrimaryButtonConvention()
    {
        try
        {
            var buttons = new List<Button>();
            CollectButtons(this, buttons);
            if (buttons.Count == 0)
                return;

            if (TryFindResource("VisuAccentButtonStyle") is not Style accentStyle)
                return;
            var primaryStyle = TryFindResource("PrimaryButtonStyle") as Style;

            // Respect a window's own accent choice.
            foreach (var button in buttons)
            {
                if (StyleChainContains(button.Style, accentStyle) ||
                    (primaryStyle != null && StyleChainContains(button.Style, primaryStyle)))
                    return;
            }

            foreach (var segment in PrimaryKeySegments)
            {
                var candidate = buttons.FirstOrDefault(b => PrimarySegment(b) == segment);
                if (candidate == null)
                    continue;
                candidate.SetResourceReference(StyleProperty, "VisuAccentButtonStyle");
                if (candidate.MinWidth < VisuId.PrimaryButtonMinWidth)
                    candidate.MinWidth = VisuId.PrimaryButtonMinWidth;
                return;
            }
        }
        catch (Exception exPrimary)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exPrimary, "BaseWin.ApplyPrimaryButtonConvention");
        }
    }

    private static string? PrimarySegment(Button button)
    {
        var key = LocProps.GetKey(button) ?? button.Tag as string;
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var dot = key.LastIndexOf('.');
        return dot >= 0 ? key[(dot + 1)..] : key;
    }

    private static bool StyleChainContains(Style? style, Style target)
    {
        for (var current = style; current != null; current = current.BasedOn)
        {
            if (ReferenceEquals(current, target))
                return true;
        }
        return false;
    }

    private static void CollectButtons(DependencyObject root, List<Button> into)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node)
                continue;
            if (node is Button button)
                into.Add(button);
            CollectButtons(node, into);
        }
    }

    /// <summary>
    /// Fraction of the work area a child window may occupy at load, per
    /// dimension (owner rule, 2026-11-17): a ribbon/child window opens at
    /// most 50% of the screen — equivalently 50% of the main window when
    /// the shell is full-screen, because a maximized shell fills the work
    /// area, so the work-area halves implement both formulations.
    /// </summary>
    private const double WorkAreaSizeCapFraction = 0.5;

    /// <summary>
    /// Window sizing policy (owner rule, 2026-11-17 — supersedes the
    /// earlier comfort-margin cap): only the main shell window is meant
    /// to fill the screen; a child window opens at most
    /// <see cref="WorkAreaSizeCapFraction"/> of the work area in each
    /// dimension. A window larger than the cap is trimmed to it at load,
    /// and a window positioned partly off-screen (small displays, high
    /// scaling, geometry restored from a larger monitor) is pulled back
    /// inside so its bottom command bar can never sit unreachable
    /// off-screen. The cap is a load-time trim only: no MaxWidth/MaxHeight
    /// is set, so the user can resize larger or maximize freely
    /// afterwards, geometry persistence keeps whatever size they leave
    /// the window at, and the same trim applies again on the next load
    /// only if that size still exceeds the cap. Never grows a window and
    /// never touches a maximized/minimized window. One exception: a
    /// window is never shrunk below its declared MinWidth/MinHeight, so
    /// a window whose minimum size exceeds the cap opens at its minimum
    /// instead (the acceptance sweep catalogues those windows; reducing
    /// their minimums is a separate layout pass).
    /// </summary>
    private void ClampToWorkArea()
    {
        try
        {
            if (WindowState != WindowState.Normal)
                return;

            var area = SystemParameters.WorkArea;
            var maxWidth = Math.Max(MinWidth, area.Width * WorkAreaSizeCapFraction);
            if (Width > maxWidth)
                Width = maxWidth;
            var maxHeight = Math.Max(MinHeight, area.Height * WorkAreaSizeCapFraction);
            if (Height > maxHeight)
                Height = maxHeight;
            if (Left < area.Left)
                Left = area.Left;
            if (Top < area.Top)
                Top = area.Top;
            if (Left + Width > area.Right)
                Left = Math.Max(area.Left, area.Right - Width);
            if (Top + Height > area.Bottom)
                Top = Math.Max(area.Top, area.Bottom - Height);
        }
        catch (Exception exClamp)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exClamp, "BaseWin.ClampToWorkArea");
        }
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
            ApplyFormLabelStyles();
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
        _geometryRestored = WinPref.RestoreGeometry(this, GeometryScope());
        Closing += OnGeometryClosing;
        try
        {
            OnRestoreWindowState();
        }
        catch (Exception exState)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exState, "BaseWin.OnRestoreWindowState");
        }
    }

    private void OnGeometryClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            OnSaveWindowState();
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
