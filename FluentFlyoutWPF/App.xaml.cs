// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Windows;

namespace FluentFlyoutWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // log unhandled exceptions before crashing
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(args.ExceptionObject as Exception, "Unhandled exception occurred");
            NLog.LogManager.Flush(); // Ensure logs are written before application dies
        };

        DispatcherUnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(args.Exception, "Unhandled dispatcher exception occurred");
            NLog.LogManager.Flush();
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(args.Exception, "Unobserved task exception occurred");
            NLog.LogManager.Flush();
        };

        // Register AUMID for toast notifications
        ToastNotificationManagerCompat.OnActivated += Notifications.HandleNotificationActivation;

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            FluentFlyoutWPF.Windows.VolumeMixerWindow.ShowVolumeOsd();
        }
        catch
        {
        }

        base.OnExit(e);
    }
}