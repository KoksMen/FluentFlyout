// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using static FluentFlyout.Classes.NativeMethods;
using System.Text;
using System.Runtime.InteropServices;

namespace FluentFlyoutWPF.Classes;

internal class FullscreenDetector
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets the current fullscreen state, identifying if it is exclusive fullscreen (D3D)
    /// or borderless windowed fullscreen.
    /// </summary>
    public static (bool isExclusive, bool isBorderless) GetFullscreenState()
    {
        if (!SettingsManager.Current.DisableIfFullscreen)
            return (false, false);

        bool isExclusive = false;
        bool isBorderless = false;

        try
        {
            // 1. Check if Windows reports exclusive DirectX fullscreen
            QUERY_USER_NOTIFICATION_STATE state;
            int result = SHQueryUserNotificationState(out state);

            if (result == 0 && state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                isExclusive = true;
            }

            // 2. Check if the active window occupies the entire monitor (borderless fullscreen)
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                StringBuilder className = new(256);
                GetClassName(hwnd, className, className.Capacity);
                string cls = className.ToString();

                // Skip desktop shell windows (desktop wallpaper and taskbar)
                if (cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd")
                {
                    if (GetWindowRect(hwnd, out RECT windowRect))
                    {
                        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                        if (monitor != IntPtr.Zero)
                        {
                            MONITORINFOEX monitorInfo = new() { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                            if (GetMonitorInfo(monitor, ref monitorInfo))
                            {
                                var screen = monitorInfo.rcMonitor;
                                // Allow minor tolerance (e.g. 5 pixels) for window borders or DPI rounding
                                bool coversScreen = windowRect.Left <= screen.Left + 5 &&
                                                     windowRect.Top <= screen.Top + 5 &&
                                                     windowRect.Right >= screen.Right - 5 &&
                                                     windowRect.Bottom >= screen.Bottom - 5;
                                if (coversScreen)
                                {
                                    if (isExclusive)
                                    {
                                        // Already marked as exclusive
                                    }
                                    else
                                    {
                                        isBorderless = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error detecting fullscreen state details");
        }

        return (isExclusive, isBorderless);
    }

    /// <summary>
    /// Checks if a DirectX exclusive fullscreen application, borderless window game,
    /// or fullscreen video is currently running.
    /// </summary>
    public static bool IsFullscreenApplicationRunning()
    {
        var state = GetFullscreenState();
        return state.isExclusive || state.isBorderless;
    }

    /// <summary>
    /// Checks if the caller should be suppressed because of fullscreen state.
    /// </summary>
    /// <param name="showInExclusive">Per-flyout show in exclusive fullscreen setting.</param>
    /// <param name="showInBorderless">Per-flyout show in borderless fullscreen setting.</param>
    /// <returns>true if the caller should be suppressed (DO NOT show the flyout).</returns>
    public static bool ShouldSuppressForFullscreen(bool showInExclusive, bool showInBorderless)
    {
        var (isExclusive, isBorderless) = GetFullscreenState();

        if (isExclusive && !showInExclusive) return true;
        if (isBorderless && !showInBorderless) return true;

        return false;
    }
}