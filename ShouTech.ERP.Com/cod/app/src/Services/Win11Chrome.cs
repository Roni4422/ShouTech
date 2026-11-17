using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
/// The app ships a light theme only (see Themes/VisuId.xaml,
/// ThemeName = Light) and the theme service exposes no dark-mode
/// state, so DWMWA_USE_IMMERSIVE_DARK_MODE is deliberately not set;
/// the caption would otherwise disagree with the light client area.
/// </summary>
public static class Win11Chrome
{
    // DWM window attributes (dwmapi.h).
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    // DWM_WINDOW_CORNER_PREFERENCE: DWMWCP_ROUND.
    private const int DwmwcpRound = 2;

    // DWM_SYSTEMBACKDROP_TYPE: DWMSBT_MAINWINDOW (Mica).
    private const int DwmsbtMainWindow = 2;

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
        catch
        {
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
            catch
            {
                // Older DWM: keep the WPF WindowChrome corner radius.
            }

            // Mica backdrop (Windows 11 22H2, build 22621+). Where the
            // window background is opaque this is a no-op visually; it
            // tints any region the shell lets show through.
            try
            {
                var backdrop = DwmsbtMainWindow;
                _ = DwmSetWindowAttribute(
                    hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }
            catch
            {
                // Older DWM: no backdrop, window keeps its theme brush.
            }
        }
        catch
        {
            // Best effort only.
        }
    }
}
