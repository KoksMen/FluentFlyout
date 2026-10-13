// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Threading.Tasks;

namespace FluentFlyout.Classes;

/// <summary>
/// App licensing manager for FluentFlyout KoksMen Edition.
/// All features are 100% unlocked and free without Microsoft Store dependencies.
/// </summary>
public class LicenseManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static LicenseManager? _instance;
    private static readonly object _lock = new();

    public static LicenseManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new LicenseManager();
                }
            }
            return _instance;
        }
    }

    public bool IsStoreVersion => false;
    public bool IsPremiumUnlocked => true;

    private LicenseManager()
    {
    }

    public Task InitializeAsync()
    {
        SettingsManager.Current.IsPremiumUnlocked = true;
        SettingsManager.Current.IsStoreVersion = false;
        Logger.Info("LicenseManager (Fork Edition): All features unlocked permanently.");
        return Task.CompletedTask;
    }

    public Task RefreshLicenseAsync()
    {
        SettingsManager.Current.IsPremiumUnlocked = true;
        return Task.CompletedTask;
    }

    public static void GetPremiumProductInfo()
    {
        SettingsManager.Current.PremiumPrice = "Free (Fork Edition)";
    }

    public static void UnlockPremium(object sender)
    {
        SettingsManager.Current.IsPremiumUnlocked = true;
    }
}