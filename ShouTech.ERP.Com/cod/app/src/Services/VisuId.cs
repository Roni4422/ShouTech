
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Ribbon;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services
{
    /// <summary>
    /// Central ShouTech ERP visual identity and window manager.
    ///
    /// Normal ShouTech windows:
    ///
    ///     InitializeComponent();
    ///     VisuId.ApplyWindow(this);
    ///
    /// Visual definitions are stored centrally in:
    ///
    ///     Themes/VisuId.xaml
    ///
    /// This class contains centralized:
    /// - visual identity loading
    /// - window styling
    /// - rendering behavior
    /// - WindowChrome behavior
    /// - accent management
    /// - common window commands
    ///
    /// IMPORTANT:
    /// RibbonWindow instances such as MWin are treated as
    /// application shells. Their own visual tree is preserved.
    /// VisuWindowStyle must never replace the MWin visual tree.
    /// </summary>
    public sealed class VisuId
    {
        private const string VisuIdUri =
            "pack://application:,,,/Themes/VisuId.xaml";

        public static double ResolveFontSize(double fallback)
        {
            var size = fallback.ToString(CultureInfo.InvariantCulture)
                .Replace('.', '_');
            return System.Windows.Application.Current?.TryFindResource($"FontSize{size}") is double configured
                ? configured
                : fallback;
        }

        public static FontFamily ResolveFontFamily(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as FontFamily
            ?? new FontFamily(fallback);

        public static Brush ResolveBrushResource(string key, string fallbackHex) =>
            System.Windows.Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));

        private string _accentKey = "gray";

        // ============================================================
        // ACCENT PALETTES
        // ============================================================

        private static readonly Dictionary<string, AccentPalette> Palettes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["gray"] =
                    new(
                        "#607D8B",
                        "#455A64",
                        "#ECEFF1",
                        "#CFD8DC",
                        "Gray"),

                ["blue"] =
                    new(
                        "#3498DB",
                        "#21618C",
                        "#D6EAF8",
                        "#AED6F1",
                        "Blue"),

                ["green"] =
                    new(
                        "#27AE60",
                        "#1E8449",
                        "#D5F5E3",
                        "#A9DFBF",
                        "Green"),

                ["red"] =
                    new(
                        "#E74C3C",
                        "#B03A2E",
                        "#FADBD8",
                        "#F5B7B1",
                        "Red"),

                ["purple"] =
                    new(
                        "#8E44AD",
                        "#6C3483",
                        "#E8DAEF",
                        "#D2B4DE",
                        "Purple"),

                ["teal"] =
                    new(
                        "#16A085",
                        "#117864",
                        "#D1F2EB",
                        "#A3E4D7",
                        "Teal"),

                ["amber"] =
                    new(
                        "#F39C12",
                        "#B9770E",
                        "#FCF3CF",
                        "#F9E79F",
                        "Amber"),

                ["orange"] =
                    new(
                        "#E67E22",
                        "#D35400",
                        "#F5E6D3",
                        "#F5D5B8",
                        "Orange"),

                ["pink"] =
                    new(
                        "#E91E63",
                        "#AD1457",
                        "#FCE4EC",
                        "#F8BBD0",
                        "Pink")
            };

        // ============================================================
        // EVENTS
        // ============================================================

        public event EventHandler<string>? AccentChanged;

        // ============================================================
        // PUBLIC ACCENT INFORMATION
        // ============================================================

        public static IReadOnlyList<AccentInfo> AvailableAccents =>
            Palettes
                .Select(x => new AccentInfo
                {
                    Key = x.Key,
                    Display = x.Value.Display,
                    Primary = x.Value.Primary
                })
                .ToList();

        public string GetCurrentAccent()
        {
            return _accentKey;
        }

        // ============================================================
        // INITIALIZATION
        // ============================================================

        /// <summary>
        /// Loads the central visual identity and restores
        /// the saved accent.
        /// </summary>
        public void Initialize()
        {
            LoadVisuId();
            ApplySavedAccent();
        }

        /// <summary>
        /// Loads Themes/VisuId.xaml as the central application
        /// visual identity dictionary.
        ///
        /// Existing visual identity dictionaries are removed first.
        /// </summary>
        public static void LoadVisuId()
        {
            var app = System.Windows.Application.Current;

            if (app == null)
                return;

            var merged = app.Resources.MergedDictionaries;

            ResourceDictionary? current = null;

            foreach (var dictionary in merged)
            {
                if (dictionary.Contains("ThemeName"))
                {
                    current = dictionary;
                    break;
                }
            }

            if (current != null)
                merged.Remove(current);

            merged.Insert(
                0,
                CreateVisuIdDictionary());
        }

        /// <summary>
        /// Creates the central visual identity resource dictionary.
        /// </summary>
        private static ResourceDictionary CreateVisuIdDictionary()
        {
            return new ResourceDictionary
            {
                Source = new Uri(
                    VisuIdUri,
                    UriKind.Absolute)
            };
        }

        // ============================================================
        // CENTRAL WINDOW ENTRY POINT
        // ============================================================

        /// <summary>
        /// Applies the centralized ShouTech visual identity
        /// and common window behavior.
        ///
        /// Normal window example:
        ///
        ///     public DatWin()
        ///     {
        ///         InitializeComponent();
        ///         VisuId.ApplyWindow(this);
        ///     }
        ///
        /// RibbonWindow instances such as MWin are handled
        /// differently so their application shell is preserved.
        /// </summary>
        public static void ApplyWindow(Window window)
		{
			ArgumentNullException.ThrowIfNull(window);

			EnsureVisualIdentityLoaded();

			// Every child window inherits the main application language direction.
			LangDirSv.ApplyToWindow(window);

			ApplyWindowStyle(window);
			ApplyWindowRendering(window);
			ApplyWindowChrome(window);
			AttachWindowBehavior(window);

			// Re-apply direction AFTER the style/template is applied so the
			// inherited FlowDirection reaches the chrome's visual tree.
			// (The chrome template must NOT pin FlowDirection locally — see
			//  VisuId.xaml — otherwise this call cannot fix the title bar.)
			LangDirSv.ApplyToWindow(window);

			// Confirm again once the visual tree is built, to cover
			// the case where the user changes language after opening.
			window.Dispatcher.BeginInvoke(
				System.Windows.Threading.DispatcherPriority.Loaded,
				new Action(() =>
				{
					try { LangDirSv.ApplyToWindow(window); } catch { }
				}));
		}

        // ============================================================
        // VISUAL IDENTITY LOADING
        // ============================================================

        private static void EnsureVisualIdentityLoaded()
        {
            var app = System.Windows.Application.Current;

            if (app == null)
                return;

            bool exists =
                app.Resources.MergedDictionaries
                    .Any(x => x.Contains("ThemeName"));

            if (exists)
                return;

            app.Resources.MergedDictionaries.Insert(
                0,
                CreateVisuIdDictionary());
        }

        // ============================================================
        // CENTRAL WINDOW STYLE
        // ============================================================

        private static void ApplyWindowStyle(Window window)
        {
            /*
             * IMPORTANT:
             *
             * MWin is a RibbonWindow and owns the complete
             * application shell:
             *
             * - title bar
             * - menu
             * - toolbar
             * - ribbon
             * - content
             * - status bar
             *
             * Applying VisuWindowStyle to it would replace its
             * ControlTemplate and therefore replace its entire
             * visual tree.
             *
             * Therefore RibbonWindow instances receive only the
             * central visual properties and keep their own template.
             */

            if (window is RibbonWindow)
            {
                ApplyShellVisualProperties(window);
                return;
            }

            /*
             * Normal ShouTech windows receive the complete
             * centralized VisuWindowStyle.
             */

            if (window.Style == null &&
                window.TryFindResource("VisuWindowStyle") is Style style)
            {
                window.Style = style;
            }

            ApplyShellVisualProperties(window);
        }

        // ============================================================
        // CENTRAL BASIC VISUAL PROPERTIES
        // ============================================================

        private static void ApplyShellVisualProperties(Window window)
        {
            /*
             * Central typography.
             */

            if (window.FontFamily == null)
            {
                window.FontFamily =
                    FindResource(
                        window,
                        "ApplicationFontFamily",
                        new FontFamily("Segoe UI"));
            }

            if (window.FontSize <= 0)
            {
                window.FontSize =
                    FindResource(
                        window,
                        "ApplicationFontSize",
                        13d);
            }

            /*
             * Do not overwrite explicitly assigned
             * window background/foreground values.
             */

            if (window.Background == null)
            {
                window.Background =
                    FindResource(
                        window,
                        "BackgroundBrush",
                        Brushes.White);
            }

            if (window.Foreground == null)
            {
                window.Foreground =
                    FindResource(
                        window,
                        "TextPrimary",
                        Brushes.Black);
            }
        }

        // ============================================================
        // CENTRAL WINDOW RENDERING
        // ============================================================

        private static void ApplyWindowRendering(Window window)
        {
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;

            RenderOptions.SetBitmapScalingMode(
                window,
                BitmapScalingMode.HighQuality);
        }

        // ============================================================
        // CENTRAL WINDOW CHROME
        // ============================================================

        private static void ApplyWindowChrome(Window window)
        {
            /*
             * MWin owns its own shell/chrome.
             *
             * Do not attach another WindowChrome to RibbonWindow.
             *
             * This prevents the central WindowChrome from fighting
             * with MWin's existing title bar and shell layout.
             */

            if (window is RibbonWindow)
                return;

            /*
             * Normal windows receive WindowChrome from
             * VisuWindowStyle in VisuId.xaml.
             */

            var existingChrome =
                WindowChrome.GetWindowChrome(window);

            if (existingChrome != null)
            {
                existingChrome.UseAeroCaptionButtons = false;
                return;
            }

            /*
             * Fallback for a normal window which does not yet
             * have WindowChrome from its central style.
             */

            var chrome = new WindowChrome
            {
                CaptionHeight =
                    FindResource(
                        window,
                        "ChromeHeight",
                        36d),

                ResizeBorderThickness =
                    FindResource(
                        window,
                        "ChromeResizeBorderThickness",
                        new Thickness(6)),

                CornerRadius =
                    FindResource(
                        window,
                        "ChromeCornerRadius",
                        new CornerRadius(0)),

                GlassFrameThickness =
                    new Thickness(0),

                UseAeroCaptionButtons = false
            };

            WindowChrome.SetWindowChrome(
                window,
                chrome);
        }

        // ============================================================
        // CENTRAL WINDOW BEHAVIOR
        // ============================================================

        private static readonly HashSet<Window> AttachedWindows = new();

        private static readonly object WindowBehaviorLock = new();

        private static void AttachWindowBehavior(Window window)
        {
            lock (WindowBehaviorLock)
            {
                if (AttachedWindows.Contains(window))
                    return;

                AttachedWindows.Add(window);
            }

            window.Closed += Window_Closed;
            window.StateChanged += Window_StateChanged;
            window.PreviewKeyDown += Window_PreviewKeyDown;

            AttachChromeCommands(window);
        }

        /// <summary>
        /// The custom title bar in VisuWindowStyle uses the WPF SystemCommands
        /// (close / minimize / maximize / restore). Those commands do nothing
        /// unless the window handles them, so bind them here once for every
        /// window that goes through VisuId.ApplyWindow.
        /// </summary>
        private static void AttachChromeCommands(Window window)
        {
            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.CloseWindowCommand,
                (_, _) => SystemCommands.CloseWindow(window)));

            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.MinimizeWindowCommand,
                (_, _) => SystemCommands.MinimizeWindow(window)));

            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.MaximizeWindowCommand,
                (_, _) => SystemCommands.MaximizeWindow(window)));

            window.CommandBindings.Add(new CommandBinding(
                SystemCommands.RestoreWindowCommand,
                (_, _) => SystemCommands.RestoreWindow(window)));
        }

        private static void Window_Closed(
            object? sender,
            EventArgs e)
        {
            if (sender is not Window window)
                return;

            window.StateChanged -= Window_StateChanged;
            window.PreviewKeyDown -= Window_PreviewKeyDown;
            window.Closed -= Window_Closed;

            lock (WindowBehaviorLock)
            {
                AttachedWindows.Remove(window);
            }
        }

        private static void Window_StateChanged(
            object? sender,
            EventArgs e)
        {
            if (sender is not Window window)
                return;

            /*
             * Central location for future responsive
             * window-state behavior.
             */
        }

        private static void Window_PreviewKeyDown(
            object? sender,
            KeyEventArgs e)
        {
            if (sender is not Window window)
                return;

            /*
             * F11:
             * Maximize / Restore.
             */

            if (e.Key == Key.F11)
            {
                ToggleMaximize(window);
                e.Handled = true;
            }
        }

        // ============================================================
        // PUBLIC WINDOW COMMANDS
        // ============================================================

        public static void Minimize(Window window)
        {
            if (window == null)
                return;

            window.WindowState =
                WindowState.Minimized;
        }

        public static void Maximize(Window window)
        {
            if (window == null)
                return;

            window.WindowState =
                WindowState.Maximized;
        }

        public static void Restore(Window window)
        {
            if (window == null)
                return;

            window.WindowState =
                WindowState.Normal;
        }

        public static void ToggleMaximize(Window window)
        {
            if (window == null)
                return;

            window.WindowState =
                window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
        }

        public static void Close(Window window)
        {
            if (window == null)
                return;

            window.Close();
        }

        // ============================================================
        // OPTIONAL CENTRAL TITLE-BAR DRAG SUPPORT
        // ============================================================

        /// <summary>
        /// Enables optional centralized drag behavior on a UI element.
        ///
        /// Normal windows do not need this because WindowChrome
        /// handles the title-bar caption area automatically.
        ///
        /// This property remains available for special shell
        /// elements that explicitly require centralized dragging.
        /// </summary>
        public static readonly DependencyProperty
            EnableChromeDragProperty =
                DependencyProperty.RegisterAttached(
                    "EnableChromeDrag",
                    typeof(bool),
                    typeof(VisuId),
                    new PropertyMetadata(
                        false,
                        OnEnableChromeDragChanged));

        public static void SetEnableChromeDrag(
            DependencyObject element,
            bool value)
        {
            if (element == null)
                return;

            element.SetValue(
                EnableChromeDragProperty,
                value);
        }

        public static bool GetEnableChromeDrag(
            DependencyObject element)
        {
            if (element == null)
                return false;

            return (bool)element.GetValue(
                EnableChromeDragProperty);
        }

        private static void OnEnableChromeDragChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement element)
                return;

            if ((bool)e.NewValue)
            {
                element.MouseLeftButtonDown +=
                    Chrome_MouseLeftButtonDown;
            }
            else
            {
                element.MouseLeftButtonDown -=
                    Chrome_MouseLeftButtonDown;
            }
        }

        private static void Chrome_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (sender is not DependencyObject element)
                return;

            var window =
                Window.GetWindow(element);

            if (window == null)
                return;

            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize(window);
                e.Handled = true;
                return;
            }

            try
            {
                window.DragMove();
                e.Handled = true;
            }
            catch
            {
                /*
                 * DragMove may fail for an invalid mouse state.
                 * Nothing needs to be done.
                 */
            }
        }

        // ============================================================
        // ACCENT MANAGEMENT
        // ============================================================

        public void SetAccent(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            key =
                key.Trim()
                    .ToLowerInvariant();

            if (!Palettes.ContainsKey(key))
                key = "gray";

            _accentKey = key;

            ApplyAccentInternal(key);

            try
            {
                var pref = UsrPref.Load();

                pref.AccentColor = key;

                pref.Save();
            }
            catch
            {
                /*
                 * Accent persistence is non-critical.
                 */
            }

            AccentChanged?.Invoke(
                this,
                key);
        }

        public void ApplySavedAccent()
        {
            try
            {
                var pref = UsrPref.Load();

                var key =
                    string.IsNullOrWhiteSpace(
                        pref.AccentColor)
                            ? "gray"
                            : pref.AccentColor
                                .Trim()
                                .ToLowerInvariant();

                if (!Palettes.ContainsKey(key))
                    key = "gray";

                _accentKey = key;

                ApplyAccentInternal(key);
            }
            catch
            {
                _accentKey = "gray";

                ApplyAccentInternal("gray");
            }
        }

        private static void ApplyAccentInternal(
            string key)
        {
            var app =
                System.Windows.Application.Current;

            if (app == null)
                return;

            if (!Palettes.TryGetValue(
                    key,
                    out var palette))
            {
                palette =
                    Palettes["gray"];
            }

            /*
             * Primary accent resources.
             */

            app.Resources["AccentPrimary"] =
                MakeBrush(palette.Primary);

            app.Resources["AccentDark"] =
                MakeBrush(palette.Dark);

            app.Resources["AccentLight"] =
                MakeBrush(palette.Light);

            app.Resources["AccentHover"] =
                MakeBrush(palette.Hover);

            /*
             * Compatibility aliases.
             */

            app.Resources["PrimaryBrush"] =
                MakeBrush(palette.Primary);

            app.Resources["PrimaryDarkBrush"] =
                MakeBrush(palette.Dark);

            app.Resources["PrimaryLightBrush"] =
                MakeBrush(palette.Light);

            app.Resources["PrimaryTransparentBrush"] =
                MakeBrush(
                    palette.Primary,
                    0x1A);

            app.Resources["AccentBrush"] =
                MakeBrush(palette.Primary);

            app.Resources["AccentDarkBrush"] =
                MakeBrush(palette.Dark);

            app.Resources["AccentLightBrush"] =
                MakeBrush(palette.Light);

            /*
             * Existing ShouTech shell compatibility.
             */

            app.Resources["ShellStatusBarBackgroundBrush"] =
                MakeBrush(palette.Primary);
        }

        // ============================================================
        // RESOURCE HELPERS
        // ============================================================

        private static T FindResource<T>(
            FrameworkElement element,
            string key,
            T fallback)
        {
            try
            {
                var value =
                    element.TryFindResource(key);

                if (value is T typed)
                    return typed;
            }
            catch
            {
                /*
                 * Resource lookup failure must never prevent
                 * a window from opening.
                 */
            }

            return fallback;
        }

        // ============================================================
        // BRUSH CREATION
        // ============================================================

        private static SolidColorBrush MakeBrush(
            string hex,
            byte alpha = 0xFF)
        {
            var color =
                (Color)ColorConverter
                    .ConvertFromString(hex)!;

            if (alpha != 0xFF)
            {
                color =
                    Color.FromArgb(
                        alpha,
                        color.R,
                        color.G,
                        color.B);
            }

            var brush =
                new SolidColorBrush(color);

            brush.Freeze();

            return brush;
        }

        // ============================================================
        // PALETTE MODEL
        // ============================================================

        private readonly record struct AccentPalette(
            string Primary,
            string Dark,
            string Light,
            string Hover,
            string Display);
    }
}
