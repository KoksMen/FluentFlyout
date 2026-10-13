// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Handles checking for application updates from GitHub Releases (KoksMen/FluentFlyout)
/// </summary>
public static class UpdateCheckerService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public const string CurrentAppVersion = "2.15.0-f2.1.1";
    public const string GitHubRepoOwner = "KoksMen";
    public const string GitHubRepoName = "FluentFlyout";
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/releases/latest";
    public static string ReleasesWebUrl => $"https://github.com/{GitHubRepoOwner}/{GitHubRepoName}/releases";

    private static readonly Lazy<HttpClient> _httpClientLazy = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluentFlyout", CurrentAppVersion));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    });

    private static HttpClient HttpClient => _httpClientLazy.Value;

    /// <summary>
    /// Result of an update check
    /// </summary>
    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string NewestVersion { get; set; } = string.Empty;
        public string UpdateUrl { get; set; } = string.Empty;
        public string PackageFileName { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public DateTime CheckedAt { get; set; }
        public bool Success { get; set; }
    }

    public class AppUpdateProgressInfo
    {
        public string StageText { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int Percentage { get; set; }
    }

    /// <summary>
    /// Check for updates from GitHub Releases API
    /// </summary>
    /// <param name="currentVersion">The current app version (e.g., "2.15.0-f2.1.1")</param>
    /// <returns>UpdateCheckResult with update information</returns>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion)
    {
        var effectiveVersion = string.IsNullOrWhiteSpace(currentVersion) || currentVersion == "debug"
            ? CurrentAppVersion
            : currentVersion;

        var result = new UpdateCheckResult
        {
            CheckedAt = DateTime.Now
        };

        try
        {
            using var response = await HttpClient.GetAsync(ReleasesApiUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubReleaseDto>(json);

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                Logger.Warn("GitHub API returned an empty or invalid release payload.");
                result.Success = false;
                return result;
            }

            result.NewestVersion = release.TagName.Trim();
            result.ReleaseNotes = release.Body ?? string.Empty;

            // Select best asset
            var asset = SelectReleaseAsset(release);
            result.UpdateUrl = !string.IsNullOrWhiteSpace(asset?.BrowserDownloadUrl)
                ? asset.BrowserDownloadUrl
                : (!string.IsNullOrWhiteSpace(release.HtmlUrl) ? release.HtmlUrl : ReleasesWebUrl);
            result.PackageFileName = asset?.Name ?? (!string.IsNullOrEmpty(result.UpdateUrl) ? Path.GetFileName(new Uri(result.UpdateUrl).LocalPath) : "Portable_x64_AMD.zip");

            result.Success = true;
            result.IsUpdateAvailable = IsNewerVersion(effectiveVersion, result.NewestVersion);

            Logger.Info($"Update check complete. Current: {effectiveVersion}, Newest: {result.NewestVersion}, Update available: {result.IsUpdateAvailable}, Url: {result.UpdateUrl}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Info($"Failed to check for updates - network error: {ex.Message}");
            result.Success = false;
        }
        catch (TaskCanceledException ex)
        {
            Logger.Info(ex, "Failed to check for updates - request timed out.");
            result.Success = false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error checking for updates");
            result.Success = false;
        }

        return result;
    }

    private static GitHubReleaseAssetDto? SelectReleaseAsset(GitHubReleaseDto release)
    {
        if (release.Assets == null || release.Assets.Count == 0)
        {
            return null;
        }

        // Prioritize:
        // 1. FluentFlyout.exe
        // 2. Portable_x64*.zip or FluentFlyout*x64*.zip
        // 3. Any .zip or .exe
        var directExe = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "FluentFlyout.exe", StringComparison.OrdinalIgnoreCase));
        if (directExe != null) return directExe;

        var portableZip = release.Assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            (a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("Portable", StringComparison.OrdinalIgnoreCase)));
        if (portableZip != null) return portableZip;

        var anyZip = release.Assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (anyZip != null) return anyZip;

        var anyExe = release.Assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (anyExe != null) return anyExe;

        return release.Assets.FirstOrDefault();
    }

    public static void OpenUpdateUrl(string url)
    {
        var targetUrl = !string.IsNullOrEmpty(url) ? url : ReleasesWebUrl;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open update URL");
        }
    }

    /// <summary>
    /// Downloads the update package directly in-app, stages files, generates an updater script and restarts the application seamlessly.
    /// Matches the Portal-Windows seamless in-app updater architecture.
    /// </summary>
    public static async Task<bool> DownloadAndLaunchUpdateAsync(
        string downloadUrl,
        string fileName,
        IProgress<AppUpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            Logger.Warn("Download URL is empty.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "Portable_x64_AMD.zip";
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FluentFlyout_Update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            progress?.Report(new AppUpdateProgressInfo
            {
                StageText = "Подключение к серверу...",
                StatusText = "Запрос файла обновления...",
                Percentage = 0
            });

            using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            var packagePath = Path.Combine(tempDir, fileName);

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long bytesReceived = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesReceived += bytesRead;

                    int percent = totalBytes > 0 ? (int)((bytesReceived * 100) / totalBytes) : 0;
                    progress?.Report(new AppUpdateProgressInfo
                    {
                        StageText = "Загрузка обновления...",
                        StatusText = totalBytes > 0
                            ? $"{bytesReceived / 1024.0 / 1024.0:F1} МБ из {totalBytes / 1024.0 / 1024.0:F1} МБ ({percent}%)"
                            : $"{bytesReceived / 1024.0 / 1024.0:F1} МБ",
                        BytesReceived = bytesReceived,
                        TotalBytes = totalBytes,
                        Percentage = percent
                    });
                }
            }

            var stagingDir = Path.Combine(tempDir, "staging");
            Directory.CreateDirectory(stagingDir);

            progress?.Report(new AppUpdateProgressInfo
            {
                StageText = "Подготовка файлов...",
                StatusText = "Распаковка дистрибутива...",
                Percentage = 100
            });

            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(packagePath, stagingDir, overwriteFiles: true);
            }
            else
            {
                File.Copy(packagePath, Path.Combine(stagingDir, "FluentFlyout.exe"), true);
            }

            var resolvedSourceDir = stagingDir;
            var exeInSub = Directory.GetFiles(stagingDir, "FluentFlyout.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exeInSub != null)
            {
                resolvedSourceDir = Path.GetDirectoryName(exeInSub) ?? stagingDir;
            }

            var targetExePath = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FluentFlyout.exe");
            var targetAppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var currentPid = Environment.ProcessId;

            var updaterScriptPath = Path.Combine(tempDir, "apply_update.cmd");
            var scriptContent = $@"@echo off
chcp 65001 >nul
echo Обновление FluentFlyout...
timeout /t 1 /nobreak >nul
taskkill /F /PID {currentPid} >nul 2>&1
timeout /t 1 /nobreak >nul
xcopy ""{resolvedSourceDir}\*"" ""{targetAppDir}\"" /E /Y /I >nul
start """" ""{targetExePath}""
timeout /t 1 /nobreak >nul
exit
";
            await File.WriteAllTextAsync(updaterScriptPath, scriptContent, System.Text.Encoding.UTF8, cancellationToken);

            progress?.Report(new AppUpdateProgressInfo
            {
                StageText = "Завершение обновления...",
                StatusText = "Перезапуск приложения...",
                Percentage = 100
            });

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{updaterScriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                WorkingDirectory = tempDir
            };

            Process.Start(psi);
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download and execute in-app update, falling back to browser.");
            OpenUpdateUrl(downloadUrl);
            return false;
        }
    }

    public static bool IsNewerVersion(string currentVersion, string newestVersion)
    {
        try
        {
            if (ForkVersion.TryParse(currentVersion, out var current) &&
                ForkVersion.TryParse(newestVersion, out var newest))
            {
                return newest.CompareTo(current) > 0;
            }

            // Fallback to standard Version.Parse
            var curVer = Version.Parse(currentVersion.Trim().TrimStart('v', 'V'));
            var newVer = Version.Parse(newestVersion.Trim().TrimStart('v', 'V'));
            return newVer > curVer;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to compare versions: '{currentVersion}' vs '{newestVersion}'");
            return false;
        }
    }

    public readonly struct ForkVersion : IComparable<ForkVersion>
    {
        public Version BaseVersion { get; }
        public Version ForkSubVersion { get; }

        public ForkVersion(Version baseVersion, Version forkSubVersion)
        {
            BaseVersion = baseVersion;
            ForkSubVersion = forkSubVersion;
        }

        public static bool TryParse(string? input, out ForkVersion result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(input)) return false;

            input = input.Trim().TrimStart('v', 'V');

            string baseStr = input;
            string forkStr = "0.0.0";

            int dashIndex = input.IndexOf('-');
            if (dashIndex >= 0)
            {
                baseStr = input.Substring(0, dashIndex);
                forkStr = input.Substring(dashIndex + 1).TrimStart('f', 'F');
            }

            if (!Version.TryParse(baseStr, out var baseVer))
            {
                return false;
            }

            if (!Version.TryParse(forkStr, out var forkVer))
            {
                forkVer = new Version(0, 0, 0);
            }

            result = new ForkVersion(baseVer, forkVer);
            return true;
        }

        public int CompareTo(ForkVersion other)
        {
            int baseCmp = (BaseVersion ?? new Version(0, 0)).CompareTo(other.BaseVersion ?? new Version(0, 0));
            if (baseCmp != 0) return baseCmp;

            return (ForkSubVersion ?? new Version(0, 0)).CompareTo(other.ForkSubVersion ?? new Version(0, 0));
        }
    }

    internal sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAtUtc { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetDto> Assets { get; set; } = new();
    }

    internal sealed class GitHubReleaseAssetDto
    {
        [JsonPropertyName("url")]
        public string ApiUrl { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}