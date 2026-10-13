// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Net.Http;
using System.Net.Http.Json;

namespace FluentFlyoutWPF.Classes.Clients;

public sealed class FluentFlyoutApiClient
{
    private static readonly object _lock = new();
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);
    private static readonly Uri _uri = new("https://fluentflyout.com/api/");
    private const int MaxConsecutiveTimeouts = 1;

    private static int _consecutiveTimeouts;
    private static HttpClient _client;

    static FluentFlyoutApiClient()
    {
        _client = CreateClient();
    }

    public static Task<string> GetStringAsync(string endpoint)
    {
        return Task.FromResult(string.Empty);
    }

    public static Task PostAsJsonAsync<T>(string endpoint, T content)
    {
        return Task.CompletedTask;
    }

    private static bool IsTimeout(Exception ex)
    {
        return ex is TaskCanceledException { InnerException: TimeoutException }
            || ex is TimeoutException;
    }

    private static void OnRequestSucceeded()
    {
        Interlocked.Exchange(ref _consecutiveTimeouts, 0);
    }

    private static void OnRequestTimedOut()
    {
        int count = Interlocked.Increment(ref _consecutiveTimeouts);
        if (count >= MaxConsecutiveTimeouts)
        {
            RenewClient();
        }
    }


    private static HttpClient CreateClient()
    {
        return new HttpClient
        {
            Timeout = _timeout,
            BaseAddress = _uri
        };
    }

    private static void UpdateUserAgent()
    {
        string appVersion = SettingsManager.Current.LastKnownVersion;
        string normalizedVersion = string.IsNullOrWhiteSpace(appVersion) ? "unknown" : appVersion;

        _client.DefaultRequestHeaders.UserAgent.Clear();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"FluentFlyout/{normalizedVersion}");
    }

    private static void RenewClient()
    {
        lock (_lock)
        {
            if (Interlocked.CompareExchange(ref _consecutiveTimeouts, 0, MaxConsecutiveTimeouts) < MaxConsecutiveTimeouts)
                return;

            _client.Dispose();
            _client = CreateClient();
            Interlocked.Exchange(ref _consecutiveTimeouts, 0);
        }
    }
}