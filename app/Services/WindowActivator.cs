using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>
/// A window whose identity includes a MODE/KIND chosen at construction
/// (an invoice kind, a voucher kind, a report mode, a definition set…).
/// The mode is fixed for the life of the window — none of these windows
/// can change mode after construction (DatWin is the one exception: its
/// in-window cards switch its mode live, and its ModeKey tracks the
/// CURRENT mode, so the policy below stays correct for it).
/// </summary>
public interface IWindowMode
{
    /// <summary>Stable key for the window's current mode, compared
    /// ordinally. Two windows of the same type are "the same window" for
    /// activation purposes only when their ModeKeys are equal.</summary>
    string ModeKey { get; }
}

public enum WindowActivationDecision
{
    /// <summary>A visible window of the same type AND mode exists:
    /// bring it to the front; the newly constructed instance is dropped.</summary>
    ReactivateExisting,

    /// <summary>No same-mode instance is visible: show the new instance
    /// (alongside any different-mode instance of the same type).</summary>
    OpenNew
}

/// <summary>
/// THE router activation policy (Fix Phase Step 11, sweep S-2/S-3):
/// "the requested mode wins."
///
/// Before this step, RibbonNav.Show / RptNav.Show de-duplicated open
/// windows by runtime TYPE only, so requesting mode B while mode A of
/// the same window class was open silently re-activated mode A and
/// discarded the mode-B instance — InvoiceWin's nine kinds, CshWin
/// Receipt/Payment, OrdWin Sales/Purchase, MfgVarianceWin's four modes,
/// DatWin Import/Export, ComWin Startup/Management and every other
/// parameterised window (Step 8 fixed cheques locally with exactly this
/// (type, kind) rule; this is that rule, generalized).
///
/// The policy, applied centrally here and nowhere else:
///   * A window's identity is (runtime type, ModeKey). Single-mode
///     windows do not implement IWindowMode; their ModeKey is null on
///     both sides, so they keep the historical type-only behaviour.
///   * Requesting a mode with a visible same-(type, mode) instance
///     reactivates THAT instance.
///   * Requesting a mode with only a different-mode instance visible
///     opens a SECOND instance in the requested mode. The open instance
///     is never re-purposed and never silently shown in its place:
///     these windows hold per-document state, so re-targeting one
///     would discard the user's in-progress work; a second modeless
///     instance is the behaviour professional ERPs (and the Step-8
///     cheque fix) use.
/// This class is the pure decision core: it has no WPF-window state of
/// its own, so Kern.Tests can pin the policy without a UI thread.
/// </summary>
public static class WindowModePolicy
{
    public static string? ModeKeyOf(object? window) => (window as IWindowMode)?.ModeKey;

    public static bool IsMatch(Type existingType, string? existingModeKey, Type requestedType, string? requestedModeKey)
        => existingType == requestedType
           && string.Equals(existingModeKey ?? string.Empty, requestedModeKey ?? string.Empty, StringComparison.Ordinal);

    public static WindowActivationDecision Decide(
        IEnumerable<(Type Type, string? ModeKey)> openWindows, Type requestedType, string? requestedModeKey)
        => openWindows.Any(w => IsMatch(w.Type, w.ModeKey, requestedType, requestedModeKey))
            ? WindowActivationDecision.ReactivateExisting
            : WindowActivationDecision.OpenNew;
}

/// <summary>
/// Shows a router-opened window under the Step-11 policy (see
/// <see cref="WindowModePolicy"/>). Both ribbon routers delegate here;
/// no router keeps a private Show/dedupe implementation any more.
/// Failures are reported through the central mechanism (NavigationFailed)
/// and the new window is still shown — matching RptNav's historical
/// behaviour, minus RibbonNav's silent catch (sweep S-7).
/// </summary>
public static class WindowActivator
{
    public static void Show(Window window, Window? owner)
    {
        try
        {
            var open = System.Windows.Application.Current.Windows
                .Cast<Window>()
                .Where(w => w.IsVisible)
                .Select(w => (Window: w, Type: w.GetType(), ModeKey: WindowModePolicy.ModeKeyOf(w)))
                .ToList();
            var requestedMode = WindowModePolicy.ModeKeyOf(window);
            if (WindowModePolicy.Decide(
                    open.Select(o => (o.Type, o.ModeKey)), window.GetType(), requestedMode)
                == WindowActivationDecision.ReactivateExisting)
            {
                var existing = open.First(o =>
                    WindowModePolicy.IsMatch(o.Type, o.ModeKey, window.GetType(), requestedMode)).Window;
                existing.Activate();
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                // The newly constructed instance is intentionally never
                // shown: a same-mode window is already open. (Its
                // constructor ran — the historical router behaviour —
                // but no handle was created and nothing is disposed
                // because nothing was shown.)
                return;
            }
            window.Owner = owner ?? System.Windows.Application.Current?.MainWindow;
            window.Closed += (_, _) => RestoreOwnerAfterClose(window);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NavigationFailed, ex, "WindowActivator.Show");
        }
        window.Show();
    }

    /// <summary>Restores the owning shell after a modeless child window closes.</summary>
    public static void RestoreOwnerAfterClose(Window closedWindow)
    {
        var owner = closedWindow.Owner;
        if (owner == null || ReferenceEquals(owner, closedWindow))
            return;

        try
        {
            owner.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    if (!owner.IsVisible || owner.IsLoaded == false)
                        return;

                    if (owner.WindowState == WindowState.Minimized)
                        owner.WindowState = WindowState.Normal;

                    owner.Activate();
                    owner.Focus();
                }));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NavigationFailed, ex, "WindowActivator.RestoreOwnerAfterClose");
        }
    }

    /// <summary>
    /// Activates a visible window of type <typeparamref name="T"/> whose
    /// ModeKey equals <paramref name="modeKey"/> (null for single-mode
    /// windows). For shell call sites that cache or scan for a window
    /// themselves instead of constructing one through a router
    /// (MWin's DatWin cache, MainShVM's company window). Returns true
    /// when an instance was activated.
    /// </summary>
    public static bool TryActivateExisting<T>(string? modeKey) where T : Window
    {
        try
        {
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w is T && w.IsVisible
                    && WindowModePolicy.IsMatch(w.GetType(), WindowModePolicy.ModeKeyOf(w), typeof(T), modeKey))
                {
                    w.Activate();
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NavigationFailed, ex, "WindowActivator.TryActivateExisting");
        }
        return false;
    }
}
