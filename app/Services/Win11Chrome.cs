using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using ShouTech.Domain;
namespace ShouTech.App.Services;

/// <summary>
/// Windows 11 window chrome effects (rounded corners + Mica backdrop)
/// applied through the Desktop Window Manager.
///
/// This is a best-effort visual layer only:
///   * every call is wrapped so a failure can never block a window,
///   * on Windows 10 / older DWM versions the attributes are unknown
///     and the calls simply no-op,
///   * transparent windows (AllowsTransparency, e.g. the splash) are
///     skipped because Mica requires an opaque window.
///
/// The backdrop kind is a central design token: the string resource
/// "WindowBackdropKind" in Themes/VisuId.xaml ("Mica", "MicaAlt" or
/// "None"). Mica Alt (DWMSBT_TRANSIENTWINDOW) is the flatter,
/// stronger-tinted variant Windows 11 uses for utility windows;
/// it suits dense ERP dialogs. Change the token - or call
/// <see cref="SetBackdrop"/> for a live window - and every window
/// follows.
///
/// The app ships a light theme only (see Themes/VisuId.xaml,
/// ThemeName = Light) and the theme service exposes no dark-mode
/// state, so DWMWA_USE_IMMERSIVE_DARK_MODE is deliberately not set;
/// the caption would otherwise disagree with the light client area.
/// </summary>
public static class Win11Chrome
{
    /// <summary>Backdrop material requested for a window.</summary>
    public enum BackdropKind
    {
        None = 0,
        Mica = 1,
        MicaAlt = 2
    }

    // DWM window attributes (dwmapi.h).
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    // DWM_WINDOW_CORNER_PREFERENCE: DWMWCP_ROUND.
    private const int DwmwcpRound = 2;

    // DWM_SYSTEMBACKDROP_TYPE values.
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;   // Mica.
    private const int DwmsbtTransientWindow = 3; // Mica Alt.

    private static readonly HashSet<Window> HookedWindows = new();
    private static readonly object HookLock = new();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Hooks the window so the Win11 DWM effects are applied as soon
    /// as its HWND exists. Safe to call more than once per window.
    /// </summary>
    public static void Apply(Window? window)
    {
        if (window == null)
            return;

        try
        {
            if (window.AllowsTransparency)
                return;

            lock (HookLock)
            {
                if (!HookedWindows.Add(window))
                    return;
            }

            window.SourceInitialized += OnSourceInitialized;
            window.Closed += OnClosed;

            // The handle may already exist (late call): apply now.
            if (PresentationSource.FromVisual(window) is HwndSource)
                ApplyToHandle(window);
        }
        catch (Exception exL89)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL89, "Win11Chrome.Apply");
            // Chrome effects must never prevent a window from opening.
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            ApplyToHandle(window);
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        window.SourceInitialized -= OnSourceInitialized;
        window.Closed -= OnClosed;

        lock (HookLock)
        {
            HookedWindows.Remove(window);
        }
    }

    /// <summary>
    /// Resolves the backdrop kind from the central
    /// "WindowBackdropKind" design token in Themes/VisuId.xaml.
    /// Unknown or missing values fall back to Mica, the long-standing
    /// default, so a token typo can never break window creation.
    /// </summary>
    public static BackdropKind ResolveBackdropKind()
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource("WindowBackdropKind") is string raw)
            {
                switch (raw.Trim().Replace(" ", string.Empty).ToLowerInvariant())
                {
                    case "none":
                    case "off":
                        return BackdropKind.None;
                    case "micaalt":
                    case "alt":
                        return BackdropKind.MicaAlt;
                }
            }
        }
        catch (Exception exL138)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL138, "Win11Chrome.ResolveBackdropKind");
            // Token lookup must never break chrome application.
        }

        return BackdropKind.Mica;
    }

    /// <summary>
    /// Changes the backdrop of a window whose handle already exists
    /// (e.g. from a future settings surface). Safe on any window and
    /// on Windows 10, where the DWM call simply no-ops.
    /// </summary>
    public static void SetBackdrop(Window? window, BackdropKind kind)
    {
        if (window == null)
            return;

        try
        {
            if (window.AllowsTransparency)
                return;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
                ApplyBackdrop(hwnd, kind);
        }
        catch (Exception exL165)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL165, "Win11Chrome.SetBackdrop");
            // Best effort only.
        }
    }

    private static void ApplyToHandle(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            // Rounded corners (Windows 11, build 22000+).
            try
            {
                var corner = DwmwcpRound;
                _ = DwmSetWindowAttribute(
                    hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            }
            catch (Exception exL186)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL186, "Win11Chrome.ApplyToHandle");
                // Older DWM: keep the WPF WindowChrome corner radius.
            }

            ApplyBackdrop(hwnd, ResolveBackdropKind());
        }
        catch (Exception exL193)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL193, "Win11Chrome.ApplyToHandle");
            // Best effort only.
        }
    }

    private static void ApplyBackdrop(IntPtr hwnd, BackdropKind kind)
    {
        // System backdrop (Windows 11 22H2, build 22621+). Where the
        // window background is opaque this is a no-op visually; it
        // tints any region the shell lets show through.
        try
        {
            var backdrop = kind switch
            {
                BackdropKind.MicaAlt => DwmsbtTransientWindow,
                BackdropKind.None => DwmsbtNone,
                _ => DwmsbtMainWindow
            };
            _ = DwmSetWindowAttribute(
                hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch (Exception exL215)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL215, "Win11Chrome.ApplyBackdrop");
            // Older DWM: no backdrop, window keeps its theme brush.
        }
    }
}
