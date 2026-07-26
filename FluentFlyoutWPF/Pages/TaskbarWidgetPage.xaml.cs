// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Linq;

namespace FluentFlyoutWPF.Pages;

public partial class TaskbarWidgetPage : Page
{
    public TaskbarWidgetPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        UpdateMonitorList();
    }

    private async void UnlockPremiumButton_Click(object sender, RoutedEventArgs e)
    {
        LicenseManager.UnlockPremium(sender);
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            TaskbarWidgetSelectedMonitorComboBox,
            () => SettingsManager.Current.TaskbarWidgetSelectedMonitor,
            value => SettingsManager.Current.TaskbarWidgetSelectedMonitor = value);
    }

    private static void SaveAndRefreshMedia()
    {
        SettingsManager.SaveSettings();
        var mainWindow = Application.Current.MainWindow as MainWindow;
        mainWindow?.RefreshFilteredMedia();
    }

    private static string NormalizeAppName(string app)
    {
        if (app.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase))
        {
            app = app[..^4];
        }

        var mainWindow = Application.Current.MainWindow as MainWindow;
        if (mainWindow?.mediaManager == null) return app;

        var match = mainWindow.mediaManager.CurrentMediaSessions.Values
            .Select(s => MediaPlayerData.GetAndCacheMediaPlayerData(s.Id).Item1)
            .FirstOrDefault(name => name.Equals(app, System.StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains(app, System.StringComparison.OrdinalIgnoreCase) ||
                                    app.Contains(name, System.StringComparison.OrdinalIgnoreCase));

        return match ?? app;
    }

    private void PopulateWidgetComboBox(ComboBox comboBox)
    {
        var mainWindow = Application.Current.MainWindow as MainWindow;
        if (mainWindow?.mediaManager == null) return;

        var apps = mainWindow.mediaManager.CurrentMediaSessions.Values
            .Select(s => MediaPlayerData.GetAndCacheMediaPlayerData(s.Id).Item1)
            .Distinct()
            .OrderBy(a => a)
            .ToList();

        comboBox.ItemsSource = apps;
    }

    private void WidgetBlockComboBox_DropDownOpened(object sender, System.EventArgs e)
    {
        PopulateWidgetComboBox(WidgetBlockComboBox);
    }

    private void AddWidgetBlock_Click(object sender, RoutedEventArgs e)
    {
        var app = WidgetBlockComboBox.SelectedItem?.ToString()?.Trim();
        if (string.IsNullOrEmpty(app) || SettingsManager.Current.WidgetBlockedApps.Any(b => b.Equals(app, System.StringComparison.OrdinalIgnoreCase))) return;

        SettingsManager.Current.WidgetBlockedApps.Add(app);
        WidgetBlockComboBox.SelectedIndex = -1;

        SaveAndRefreshMedia();
    }

    private void AddWidgetBlockManual_Click(object sender, RoutedEventArgs e)
    {
        var app = WidgetBlockTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(app)) return;

        app = NormalizeAppName(app);
        if (SettingsManager.Current.WidgetBlockedApps.Any(b => b.Equals(app, System.StringComparison.OrdinalIgnoreCase))) return;

        SettingsManager.Current.WidgetBlockedApps.Add(app);
        WidgetBlockTextBox.Text = string.Empty;

        SaveAndRefreshMedia();
    }

    private void RemoveWidgetBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string app }) return;

        SettingsManager.Current.WidgetBlockedApps.Remove(app);
        SaveAndRefreshMedia();
    }
}