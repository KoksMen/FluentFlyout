// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using FluentFlyoutWPF.ViewModels;
using FluentFlyoutWPF.Windows;
using NLog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Classes.Services;

public sealed partial class TaskbarAppVolumeService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly MainWindow _mainWindow;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDisposed;

    // Cache to avoid querying UI Automation repeatedly on every mouse wheel tick
    private Rect _cachedBounds = Rect.Empty;
    private string? _cachedAppName;
    private AudioSessionModel? _cachedSession;
    private long _lastHoverTick;
    private bool _cachedSessionResolved;

    private static readonly HashSet<string> SystemTaskbarNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Start", "Пуск",
        "Search", "Поиск",
        "Task View", "Представление задач",
        "Widgets", "Мини-приложения",
        "Copilot",
        "System tray", "Область уведомлений",
        "Notification Chevron",
        "Show hidden icons", "Отображать скрытые значки",
        "Clock", "Часы",
        "Action center", "Центр уведомлений",
        "Show desktop", "Свернуть все окна",
        "Running applications", "Приложения на панели задач",
        "Taskbar", "Панель задач",
        "ReBarWindow32", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow", "DesktopWindowTarget"
    };

    private static readonly Dictionary<string, string[]> AppProcessAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Yandex", new[] { "browser", "yandex" } },
        { "Яндекс", new[] { "browser", "yandex" } },
        { "Chrome", new[] { "chrome" } },
        { "Google Chrome", new[] { "chrome" } },
        { "Edge", new[] { "msedge" } },
        { "Microsoft Edge", new[] { "msedge" } },
        { "Firefox", new[] { "firefox" } },
        { "Mozilla Firefox", new[] { "firefox" } },
        { "Opera", new[] { "opera", "opera_gx" } },
        { "Discord", new[] { "discord" } },
        { "Telegram", new[] { "telegram" } },
        { "Telegram Desktop", new[] { "telegram" } },
        { "Spotify", new[] { "spotify" } },
        { "Steam", new[] { "steam", "steamwebhelper" } },
    };

    public TaskbarAppVolumeService(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        _mouseProc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule != null)
            {
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(curModule.ModuleName), 0);
                if (_hookId == IntPtr.Zero)
                {
                    Logger.Warn("Failed to install taskbar mouse hook.");
                }
                else
                {
                    Logger.Info("Taskbar app volume mouse hook installed successfully.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Exception installing taskbar mouse hook");
        }
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            Logger.Info("Taskbar app volume mouse hook uninstalled.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
        {
            if (SettingsManager.Current.TaskbarAppVolumeScrollEnabled)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                short delta = (short)((hookStruct.mouseData >> 16) & 0xffff);

                if (HandleTaskbarScroll(hookStruct.pt, delta))
                {
                    // Swallowed: prevent Windows default scroll behavior
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool HandleTaskbarScroll(POINT pt, short delta)
    {
        try
        {
            IntPtr hWndAtPoint = WindowFromPoint(pt);
            if (hWndAtPoint == IntPtr.Zero) return false;

            // Don't intercept if mouse is over our own taskbar widget or mixer window
            uint currentPid = (uint)Process.GetCurrentProcess().Id;
            GetWindowThreadProcessId(hWndAtPoint, out uint hitPid);
            if (hitPid == currentPid) return false;

            IntPtr mainTray = FindWindow("Shell_TrayWnd", null);
            uint explorerPid = 0;
            if (mainTray != IntPtr.Zero)
            {
                GetWindowThreadProcessId(mainTray, out explorerPid);
            }
            if (explorerPid == 0 || hitPid != explorerPid) return false;

            IntPtr root = GetAncestor(hWndAtPoint, GA_ROOT);
            if (root == IntPtr.Zero) root = hWndAtPoint;

            StringBuilder sb = new(256);
            GetClassName(root, sb, 256);
            string rootClass = sb.ToString();

            sb.Clear();
            GetClassName(hWndAtPoint, sb, 256);
            string directClass = sb.ToString();

            bool isTaskbar = rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            bool isThumbnail = false;

            if (!isTaskbar)
            {
                if (rootClass is "TaskListThumbnailWnd" or "XamlExplorerHostIslandWindow"
                    || directClass.StartsWith("TaskListThumbnailWnd")
                    || directClass.StartsWith("TaskList")
                    || directClass is "XamlExplorerHostIslandWindow")
                {
                    if (GetWindowRect(root, out RECT rootRect) && mainTray != IntPtr.Zero && GetWindowRect(mainTray, out RECT trayRect))
                    {
                        if (rootRect.Bottom <= trayRect.Bottom + 15 && rootRect.Top >= trayRect.Top - 600)
                        {
                            isThumbnail = true;
                        }
                    }
                }
            }

            if (!isTaskbar && !isThumbnail) return false;

            IntPtr taskbarHwnd = isTaskbar ? root : mainTray;

            if (isThumbnail)
            {
                ShowWindow(root, SW_HIDE);
                if (mainTray != IntPtr.Zero && GetWindowRect(mainTray, out RECT tr))
                {
                    pt.Y = tr.Top + (tr.Bottom - tr.Top) / 2;
                }
            }

            MainWindow.DismissTaskbarThumbnails(null, taskbarHwnd);

            // Dispatch scroll to UI thread non-blockingly
            _mainWindow.Dispatcher.InvokeAsync(() =>
            {
                ProcessTaskbarScroll(pt, delta, taskbarHwnd);
            });

            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error handling taskbar scroll hit-test");
            return false;
        }
    }

    private void ProcessTaskbarScroll(POINT pt, short delta, IntPtr taskbarHwnd)
    {
        var mixer = _mainWindow.VolumeMixerWindow;
        if (mixer == null) return;

        var point = new System.Windows.Point(pt.X, pt.Y);
        long now = Environment.TickCount64;

        // 1. Check if point is inside cached taskbar button (only for single button sized bounds)
        bool isButtonSized = _cachedBounds.Width > 0 && _cachedBounds.Width <= 120 && _cachedBounds.Height > 0 && _cachedBounds.Height <= 80;
        bool useCache = _cachedBounds != Rect.Empty 
                        && isButtonSized
                        && _cachedBounds.Contains(point) 
                        && (now - _lastHoverTick) < 400;

        Rect anchorRect = _cachedBounds;
        string? appName = _cachedAppName;
        AudioSessionModel? session = _cachedSession;

        if (!useCache)
        {
            (appName, anchorRect) = ResolveTaskbarElement(point, taskbarHwnd);
            bool newIsButtonSized = anchorRect.Width > 0 && anchorRect.Width <= 120 && anchorRect.Height > 0 && anchorRect.Height <= 80;
            _cachedBounds = newIsButtonSized ? anchorRect : Rect.Empty;
            _cachedAppName = appName;
            _lastHoverTick = now;
            session = null;
            _cachedSession = null;
            _cachedSessionResolved = false;
            Logger.Info($"[TaskbarAppVolume] Fresh resolution at ({pt.X},{pt.Y}): appName='{appName}', anchorRect={anchorRect}");
        }

        float stepFraction = Math.Clamp(SettingsManager.Current.VolumeScrollStep, 1, 20) / 100.0f;
        float step = (delta > 0 ? 1 : -1) * stepFraction;

        if (!string.IsNullOrEmpty(appName) && session == null && !_cachedSessionResolved)
        {
            session = ResolveSession(appName, mixer.ViewModel);
            _cachedSession = session;
            _cachedSessionResolved = true;
        }

        if (session != null)
        {
            Logger.Info($"[TaskbarAppVolume] Adjusting per-app volume for '{session.DisplayName}' (PID {session.ProcessId}) by {step}");
            session.AdjustVolume(step);

            if (mixer.ViewModel.IsAppFlyoutActive && mixer.ViewModel.ActiveAppDisplayName == session.DisplayName)
            {
                mixer.ViewModel.ActiveAppVolume = session.Volume;
                mixer.ViewModel.IsActiveAppMuted = session.IsMuted;
            }

            if (anchorRect != Rect.Empty)
            {
                MainWindow.DismissTaskbarThumbnails(anchorRect, taskbarHwnd);
            }

            if (!mixer.IsTaskbarAnchored && !mixer.ViewModel.IsExpanded)
            {
                mixer.ShowAppFlyout(session, anchorRect != Rect.Empty ? anchorRect : null, isTaskbarApp: true);
            }
        }
        else
        {
            Logger.Info($"[TaskbarAppVolume] No app session found for appName='{appName}', skipping volume adjustment.");
        }
    }

    private static (string? appName, Rect bounds) ResolveTaskbarElement(System.Windows.Point point, IntPtr taskbarHwnd)
    {
        // Strategy 1: FromPoint with RawViewWalker traversal
        try
        {
            var element = AutomationElement.FromPoint(point);
            if (element != null)
            {
                var current = element;
                for (int i = 0; i < 12 && current != null; i++)
                {
                    try
                    {
                        string name = current.Current.Name;
                        string autoId = current.Current.AutomationId;
                        string helpText = current.Current.HelpText;
                        Rect bounds = current.Current.BoundingRectangle;

                        // Check Name
                        if (!string.IsNullOrWhiteSpace(name) && !SystemTaskbarNames.Contains(name))
                        {
                            string cleaned = CleanTaskbarAppName(name);
                            if (!string.IsNullOrWhiteSpace(cleaned) && !SystemTaskbarNames.Contains(cleaned))
                            {
                                Logger.Info($"[TaskbarAppVolume] Found by Name at raw level {i}: '{name}' -> '{cleaned}', bounds: {bounds}");
                                return (cleaned, bounds);
                            }
                        }

                        // Check AutomationId
                        if (!string.IsNullOrWhiteSpace(autoId) && !autoId.StartsWith("Taskbar") && !SystemTaskbarNames.Contains(autoId))
                        {
                            string cleanedId = CleanAutomationId(autoId);
                            if (!string.IsNullOrWhiteSpace(cleanedId) && !SystemTaskbarNames.Contains(cleanedId))
                            {
                                Logger.Info($"[TaskbarAppVolume] Found by AutomationId at raw level {i}: '{autoId}' -> '{cleanedId}', bounds: {bounds}");
                                return (cleanedId, bounds);
                            }
                        }

                        // Check HelpText
                        if (!string.IsNullOrWhiteSpace(helpText) && !SystemTaskbarNames.Contains(helpText))
                        {
                            string cleanedHelp = CleanTaskbarAppName(helpText);
                            if (!string.IsNullOrWhiteSpace(cleanedHelp))
                            {
                                Logger.Info($"[TaskbarAppVolume] Found by HelpText at raw level {i}: '{helpText}' -> '{cleanedHelp}', bounds: {bounds}");
                                return (cleanedHelp, bounds);
                            }
                        }

                        current = TreeWalker.RawViewWalker.GetParent(current);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "FromPoint resolution failed");
        }

        // Strategy 2: Taskbar children bounding box hit-test
        if (taskbarHwnd != IntPtr.Zero)
        {
            try
            {
                var root = AutomationElement.FromHandle(taskbarHwnd);
                if (root != null)
                {
                    var buttons = root.FindAll(
                        TreeScope.Descendants,
                        new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom)
                        )
                    );

                    for (int i = 0; i < buttons.Count; i++)
                    {
                        var btn = buttons[i];
                        try
                        {
                            Rect rect = btn.Current.BoundingRectangle;
                            if (rect != Rect.Empty && rect.Contains(point))
                            {
                                string name = btn.Current.Name;
                                string autoId = btn.Current.AutomationId;

                                if (!string.IsNullOrWhiteSpace(name) && !SystemTaskbarNames.Contains(name))
                                {
                                    string cleaned = CleanTaskbarAppName(name);
                                    if (!string.IsNullOrWhiteSpace(cleaned) && !SystemTaskbarNames.Contains(cleaned))
                                    {
                                        Logger.Info($"[TaskbarAppVolume] Hit-test matched button: '{name}' -> '{cleaned}', bounds: {rect}");
                                        return (cleaned, rect);
                                    }
                                }

                                if (!string.IsNullOrWhiteSpace(autoId))
                                {
                                    string cleanedId = CleanAutomationId(autoId);
                                    if (!string.IsNullOrWhiteSpace(cleanedId) && !SystemTaskbarNames.Contains(cleanedId))
                                    {
                                        Logger.Info($"[TaskbarAppVolume] Hit-test matched autoId: '{autoId}' -> '{cleanedId}', bounds: {rect}");
                                        return (cleanedId, rect);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Taskbar children hit-test failed");
            }
        }

        return (null, Rect.Empty);
    }

    public static string CleanTaskbarAppName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        // Replace all non-breaking and special spaces with standard space
        string cleaned = Regex.Replace(rawName, @"[\u00A0\u1680\u2000-\u200B\u202F\u205F\u3000]", " ").Trim();

        // 1. Remove prefixes like "Приложение ", "App "
        cleaned = Regex.Replace(cleaned, @"^(?:Приложение|App)\s+", "", RegexOptions.IgnoreCase);

        // 2. Remove " (не отвечает)" / " (not responding)"
        cleaned = Regex.Replace(cleaned, @"\s*\((?:не отвечает|not responding)\)", "", RegexOptions.IgnoreCase);

        // 3. Remove pinned status: "закреплено", "pinned"
        cleaned = Regex.Replace(cleaned, @"\s*[,–—\-]?\s*(?:закреплено|закреплен[а-я]*|pinned)\s*$", "", RegexOptions.IgnoreCase);

        // 4. Remove window count suffixes:
        // e.g. " —1 запущенное окно", " - 1 running window", " — запущенных окон: 2", " - 2 running windows"
        cleaned = Regex.Replace(cleaned, @"\s*[-–—]\s*(?:запущенн\w+|открыт\w+|running)?\s*(?:окон|ок\w*|windows?)?[:\s]*\d+\s*(?:запущенн\w+|открыт\w+|running)?\s*(?:ок\w*|windows?)?", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*[-–—]\s*\d+\s*(?:запущенн\w*|открыт\w*|running)?\s*(?:windows?|ок\w*)?", "", RegexOptions.IgnoreCase);

        // 5. Remove trailing pinned if left over
        cleaned = Regex.Replace(cleaned, @"\s*[,–—\-]?\s*(?:закреплено|закреплен[а-я]*|pinned)\s*$", "", RegexOptions.IgnoreCase);

        // 6. Remove badges like "(12)" at the end: e.g. "Telegram (12)" -> "Telegram"
        cleaned = Regex.Replace(cleaned, @"\s*\(\d+\)$", "");

        // 7. Safety fallback: if there is still a dash like " — ", take the left side
        int dashIdx = cleaned.IndexOfAny(new[] { '—', '–' });
        if (dashIdx > 0)
        {
            cleaned = cleaned.Substring(0, dashIdx).Trim();
        }

        return cleaned.Trim();
    }

    public static string CleanAutomationId(string autoId)
    {
        if (string.IsNullOrWhiteSpace(autoId)) return string.Empty;

        string trimmed = autoId.Trim();

        if (trimmed.Contains('\\') || trimmed.Contains('/'))
        {
            try
            {
                string fn = System.IO.Path.GetFileNameWithoutExtension(trimmed);
                if (!string.IsNullOrWhiteSpace(fn)) return fn;
            }
            catch { }
        }

        if (trimmed.StartsWith("App_AppUserModelId_", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed.Substring("App_AppUserModelId_".Length);
            int dot = rest.LastIndexOf('.');
            if (dot >= 0 && dot < rest.Length - 1)
            {
                rest = rest.Substring(dot + 1);
            }
            return rest;
        }

        return trimmed;
    }

    private static AudioSessionModel? ResolveSession(string appName, VolumeMixerViewModel viewModel)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;

        Logger.Info($"[TaskbarAppVolume] Resolving session for '{appName}'. Current sessions count: {viewModel.Sessions.Count}");
        foreach (var s in viewModel.Sessions)
        {
            Logger.Info($"[TaskbarAppVolume]   Session: DisplayName='{s.DisplayName}', AppName='{s.AppName}', ProcessName='{s.ProcessName}', WindowTitle='{s.WindowTitle}', PID={s.ProcessId}");
        }

        var session = MatchSession(appName, viewModel.Sessions);
        if (session != null)
        {
            Logger.Info($"[TaskbarAppVolume] Matched session: '{session.DisplayName}' (PID {session.ProcessId})");
            return session;
        }

        // Try refreshing sessions once if not found
        Logger.Info("[TaskbarAppVolume] Session not in current list, calling RefreshSessions...");
        viewModel.RefreshSessions();

        session = MatchSession(appName, viewModel.Sessions);
        if (session != null)
        {
            Logger.Info($"[TaskbarAppVolume] Matched session after refresh: '{session.DisplayName}' (PID {session.ProcessId})");
            return session;
        }

        Logger.Warn($"[TaskbarAppVolume] Could not match audio session for appName '{appName}'");
        return null;
    }

    private static AudioSessionModel? MatchSession(string appName, IEnumerable<AudioSessionModel> sessions)
    {
        var list = sessions.Where(s => s.ProcessId > 0 && !s.SessionControl.IsSystemSoundsSession).ToList();
        if (list.Count == 0) return null;

        string clean = appName.Trim();

        // 1. Exact match with DisplayName, AppName, ProductName, or ProcessName
        var match = list.FirstOrDefault(s =>
            s.DisplayName.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            s.AppName.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(s.ProductName) && s.ProductName.Equals(clean, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(s.ProcessName) && s.ProcessName.Equals(clean, StringComparison.OrdinalIgnoreCase)));
        if (match != null) return match;

        // 2. Exact match with .exe
        match = list.FirstOrDefault(s =>
            !string.IsNullOrEmpty(s.ProcessName) && (s.ProcessName + ".exe").Equals(clean, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 3. Known aliases match (e.g. Yandex -> browser.exe, Chrome -> chrome.exe) - exact alias match only
        foreach (var kvp in AppProcessAliases)
        {
            if (clean.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                match = list.FirstOrDefault(s => kvp.Value.Any(alias => s.ProcessName.Equals(alias, StringComparison.OrdinalIgnoreCase)));
                if (match != null) return match;
            }
        }

        // 4. Forward prefix match only (e.g. shortcut "Telegram" matches session "Telegram Desktop", "VLC" matches "VLC media player")
        // NEVER do reverse prefix match where clean starts with session name, to prevent matching unrelated shortcuts!
        match = list.FirstOrDefault(s =>
            s.DisplayName.StartsWith(clean + " ", StringComparison.OrdinalIgnoreCase) ||
            s.AppName.StartsWith(clean + " ", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(s.ProductName) && s.ProductName.StartsWith(clean + " ", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(s.ProcessName) && s.ProcessName.StartsWith(clean + " ", StringComparison.OrdinalIgnoreCase)));
        if (match != null) return match;

        // 5. WindowTitle forward prefix match
        match = list.FirstOrDefault(s =>
            !string.IsNullOrEmpty(s.WindowTitle) &&
            s.WindowTitle.StartsWith(clean + " ", StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // NO TOKEN MATCHING: If not found, return null to avoid false matches on apps without sound!
        return null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
