// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Models;
using static FluentFlyout.Classes.NativeMethods;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace FluentFlyoutWPF.ViewModels;

/// <summary>
/// ViewModel for the volume mixer, exposing master volume, microphone, audio device switching, and per-application audio sessions.
/// </summary>
public partial class VolumeMixerViewModel : ObservableObject, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private MMDevice? _device;
    private MMDevice? _micDevice;
    private AudioSessionManager? _sessionManager;
    private DispatcherTimer? _pollTimer;
    private bool _isUpdatingFromDevice;
    private bool _isUpdatingFromMicDevice;
    private bool _isRefreshingDevices;

    private long _lastInternalVolumeChangeTime;
    private long _lastInternalMicVolumeChangeTime;

    public event EventHandler? MasterVolumeNotificationReceived;
    public event EventHandler<bool>? MasterMuteNotificationReceived;

    [ObservableProperty]
    public partial float MasterVolume { get; set; }

    [ObservableProperty]
    public partial bool IsMasterMuted { get; set; }

    [ObservableProperty]
    public partial float MicrophoneVolume { get; set; }

    [ObservableProperty]
    public partial bool IsMicrophoneMuted { get; set; }

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    public partial string MicrophoneDeviceName { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ObservableCollection<AudioSessionModel> Sessions { get; } = [];
    public ObservableCollection<AudioDeviceModel> PlaybackDevices { get; } = [];
    public ObservableCollection<AudioDeviceModel> RecordingDevices { get; } = [];

    [ObservableProperty]
    public partial AudioDeviceModel? SelectedPlaybackDevice { get; set; }

    [ObservableProperty]
    public partial AudioDeviceModel? SelectedRecordingDevice { get; set; }

    [ObservableProperty]
    public partial bool IsAppFlyoutActive { get; set; }

    [ObservableProperty]
    public partial string ActiveAppDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial System.Windows.Media.ImageSource? ActiveAppIcon { get; set; }

    [ObservableProperty]
    public partial float ActiveAppVolume { get; set; }

    [ObservableProperty]
    public partial bool IsActiveAppMuted { get; set; }

    private AudioSessionModel? _activeFlyoutSession;

    public void SetFlyoutApp(AudioSessionModel? session)
    {
        _activeFlyoutSession = session;
        if (session != null)
        {
            IsAppFlyoutActive = true;
            ActiveAppDisplayName = session.DisplayName;
            ActiveAppIcon = session.Icon;
            ActiveAppVolume = session.Volume;
            IsActiveAppMuted = session.IsMuted;
        }
        else
        {
            IsAppFlyoutActive = false;
            ActiveAppDisplayName = string.Empty;
            ActiveAppIcon = null;
        }
    }

    partial void OnActiveAppVolumeChanged(float value)
    {
        if (_activeFlyoutSession != null)
        {
            _activeFlyoutSession.Volume = value;
            IsActiveAppMuted = _activeFlyoutSession.IsMuted;
        }
    }

    public void AdjustActiveAppVolume(float delta)
    {
        if (_activeFlyoutSession != null)
        {
            _activeFlyoutSession.AdjustVolume(delta);
            ActiveAppVolume = _activeFlyoutSession.Volume;
            IsActiveAppMuted = _activeFlyoutSession.IsMuted;
        }
    }

    [RelayCommand]
    public void ToggleActiveAppMute()
    {
        if (_activeFlyoutSession != null)
        {
            _activeFlyoutSession.IsMuted = !_activeFlyoutSession.IsMuted;
            IsActiveAppMuted = _activeFlyoutSession.IsMuted;
            if (SettingsManager.Current.MuteConfirmationSoundEnabled)
            {
                SoundFeedbackHelper.PlayMuteFeedback(IsActiveAppMuted);
            }
        }
    }

    public VolumeMixerViewModel()
    {
        DeviceName = string.Empty;
        MicrophoneDeviceName = string.Empty;

        AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;
        AudioDeviceMonitor.Instance.DefaultCaptureDeviceChanged += OnDefaultCaptureDeviceChanged;

        AttachDevice(AudioDeviceMonitor.Instance.GetDefaultRenderDevice());
        AttachMicDevice(AudioDeviceMonitor.Instance.GetDefaultCaptureDevice());

        RefreshDevices();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    partial void OnIsExpandedChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue) return;
        if (newValue)
        {
            SetFlyoutApp(null);
            RefreshDevices();
            RefreshSessions();
        }
    }

    public void AttachDevice(MMDevice? device)
    {
        if (_device != null)
        {
            try
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnAudioEndpointVolumeNotification;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to unsubscribe from old AudioEndpointVolume notification");
            }
        }

        if (_sessionManager != null)
        {
            try
            {
                _sessionManager.OnSessionCreated -= OnSessionCreatedHandler;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to unsubscribe from old SessionManager");
            }
            _sessionManager = null;
        }

        _device = device;

        if (_device == null)
        {
            DeviceName = string.Empty;
            _isUpdatingFromDevice = true;
            try
            {
                MasterVolume = 0f;
                IsMasterMuted = false;
            }
            finally
            {
                _isUpdatingFromDevice = false;
            }
            Sessions.Clear();
            return;
        }

        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification += OnAudioEndpointVolumeNotification;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to register AudioEndpointVolume notification");
        }

        try
        {
            _sessionManager = _device.AudioSessionManager;
            if (_sessionManager != null)
            {
                _sessionManager.OnSessionCreated += OnSessionCreatedHandler;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to register SessionManager notification");
        }

        DeviceName = _device.FriendlyName;
        SyncMasterFromDevice();
        RefreshSessions();
    }

    public void AttachMicDevice(MMDevice? micDevice)
    {
        if (_micDevice != null)
        {
            try
            {
                _micDevice.AudioEndpointVolume.OnVolumeNotification -= OnMicAudioEndpointVolumeNotification;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to unsubscribe from old Mic AudioEndpointVolume notification");
            }
        }

        _micDevice = micDevice;

        if (_micDevice == null)
        {
            MicrophoneDeviceName = string.Empty;
            _isUpdatingFromMicDevice = true;
            try
            {
                MicrophoneVolume = 0f;
                IsMicrophoneMuted = false;
            }
            finally
            {
                _isUpdatingFromMicDevice = false;
            }
            return;
        }

        try
        {
            _micDevice.AudioEndpointVolume.OnVolumeNotification += OnMicAudioEndpointVolumeNotification;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to register mic AudioEndpointVolume notification");
        }

        MicrophoneDeviceName = _micDevice.FriendlyName;
        SyncMicFromDevice();
    }

    private void OnSessionCreatedHandler(object? sender, IAudioSessionControl newSession)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.InvokeAsync(() =>
        {
            RefreshSessions();
        });
    }

    private void OnAudioEndpointVolumeNotification(AudioVolumeNotificationData data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.InvokeAsync(() =>
        {
            bool isInternal = (Environment.TickCount64 - _lastInternalVolumeChangeTime) < 600;
            bool volumeChangedSignificant = MathF.Abs(MasterVolume - data.MasterVolume) > 0.009f;
            bool muteChanged = IsMasterMuted != data.Muted;
            bool shouldNotify = volumeChangedSignificant && !isInternal;

            _isUpdatingFromDevice = true;
            try
            {
                MasterVolume = data.MasterVolume;
                IsMasterMuted = data.Muted;
            }
            finally
            {
                _isUpdatingFromDevice = false;
            }

            if (muteChanged && !isInternal)
            {
                MasterMuteNotificationReceived?.Invoke(this, data.Muted);
            }

            if (shouldNotify)
            {
                MasterVolumeNotificationReceived?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void OnMicAudioEndpointVolumeNotification(AudioVolumeNotificationData data)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.InvokeAsync(() =>
        {
            _isUpdatingFromMicDevice = true;
            try
            {
                MicrophoneVolume = data.MasterVolume;
                IsMicrophoneMuted = data.Muted;
            }
            finally
            {
                _isUpdatingFromMicDevice = false;
            }
        });
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        Logger.Info("Default render device changed, reattaching volume mixer");
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            AttachDevice(AudioDeviceMonitor.Instance.GetDeviceById(e.DeviceId));
            RefreshDevices();
        });
    }

    private void OnDefaultCaptureDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        Logger.Info("Default capture device changed, reattaching microphone");
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            AttachMicDevice(AudioDeviceMonitor.Instance.GetDeviceById(e.DeviceId));
            RefreshDevices();
        });
    }

    public void RefreshDevices()
    {
        _isRefreshingDevices = true;
        try
        {
            var playbacks = AudioDeviceService.GetPlaybackDevices(_device?.ID);
            PlaybackDevices.Clear();
            foreach (var p in playbacks) PlaybackDevices.Add(p);
            SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(p => p.IsDefault) ?? PlaybackDevices.FirstOrDefault();

            var recordings = AudioDeviceService.GetRecordingDevices(_micDevice?.ID);
            RecordingDevices.Clear();
            foreach (var r in recordings) RecordingDevices.Add(r);
            SelectedRecordingDevice = RecordingDevices.FirstOrDefault(r => r.IsDefault) ?? RecordingDevices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh audio devices list");
        }
        finally
        {
            _isRefreshingDevices = false;
        }
    }

    partial void OnSelectedPlaybackDeviceChanged(AudioDeviceModel? value)
    {
        if (_isRefreshingDevices || value == null) return;
        Logger.Info("Switching default playback device to: {0} ({1})", value.Name, value.Id);
        AudioDeviceService.SetDefaultPlaybackDevice(value.Id);
    }

    partial void OnSelectedRecordingDeviceChanged(AudioDeviceModel? value)
    {
        if (_isRefreshingDevices || value == null) return;
        Logger.Info("Switching default recording device to: {0} ({1})", value.Name, value.Id);
        AudioDeviceService.SetDefaultRecordingDevice(value.Id);
    }

    [RelayCommand]
    public void ToggleMasterMute()
    {
        _lastInternalVolumeChangeTime = Environment.TickCount64;
        IsMasterMuted = !IsMasterMuted;
        if (SettingsManager.Current.MuteConfirmationSoundEnabled)
        {
            SoundFeedbackHelper.PlayMuteFeedback(IsMasterMuted);
        }
        MasterMuteNotificationReceived?.Invoke(this, IsMasterMuted);
    }

    [RelayCommand]
    public void ToggleMicrophoneMute()
    {
        _lastInternalMicVolumeChangeTime = Environment.TickCount64;
        IsMicrophoneMuted = !IsMicrophoneMuted;
        if (SettingsManager.Current.MuteConfirmationSoundEnabled)
        {
            SoundFeedbackHelper.PlayMuteFeedback(IsMicrophoneMuted);
        }
    }

    [RelayCommand]
    private void OpenVolumeMixer()
    {
        SetFlyoutApp(null);
        IsExpanded = !IsExpanded;
    }

    partial void OnMasterVolumeChanged(float value)
    {
        if (_isUpdatingFromDevice || _device == null) return;
        _lastInternalVolumeChangeTime = Environment.TickCount64;
        try
        {
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            if (MasterVolume == 0f)
            {
                if (!IsMasterMuted)
                {
                    IsMasterMuted = true;
                    MasterMuteNotificationReceived?.Invoke(this, true);
                }
            }
            else if (IsMasterMuted && value > 0f)
            {
                IsMasterMuted = false;
                MasterMuteNotificationReceived?.Invoke(this, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to set master volume level");
        }
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        if (_isUpdatingFromDevice || _device == null) return;
        _lastInternalVolumeChangeTime = Environment.TickCount64;
        try
        {
            _device.AudioEndpointVolume.Mute = value;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to set master mute state");
        }
    }

    partial void OnMicrophoneVolumeChanged(float value)
    {
        if (_isUpdatingFromMicDevice || _micDevice == null) return;
        _lastInternalMicVolumeChangeTime = Environment.TickCount64;
        try
        {
            _micDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            if (MicrophoneVolume == 0f)
            {
                IsMicrophoneMuted = true;
            }
            else if (IsMicrophoneMuted && value > 0f)
            {
                IsMicrophoneMuted = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to set microphone volume level");
        }
    }

    partial void OnIsMicrophoneMutedChanged(bool value)
    {
        if (_isUpdatingFromMicDevice || _micDevice == null) return;
        _lastInternalMicVolumeChangeTime = Environment.TickCount64;
        try
        {
            _micDevice.AudioEndpointVolume.Mute = value;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to set microphone mute state");
        }
    }

    public void SyncMasterFromDevice()
    {
        if (_device == null) return;

        try
        {
            var vol = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            var mute = _device.AudioEndpointVolume.Mute;

            _isUpdatingFromDevice = true;
            try
            {
                if (MathF.Abs(MasterVolume - vol) > 0.001f)
                    MasterVolume = vol;

                if (IsMasterMuted != mute)
                    IsMasterMuted = mute;
            }
            finally
            {
                _isUpdatingFromDevice = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sync master volume from device");
        }
    }

    public void SyncMicFromDevice()
    {
        if (_micDevice == null) return;

        try
        {
            var vol = _micDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
            var mute = _micDevice.AudioEndpointVolume.Mute;

            _isUpdatingFromMicDevice = true;
            try
            {
                if (MathF.Abs(MicrophoneVolume - vol) > 0.001f)
                    MicrophoneVolume = vol;

                if (IsMicrophoneMuted != mute)
                    IsMicrophoneMuted = mute;
            }
            finally
            {
                _isUpdatingFromMicDevice = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sync microphone volume from device");
        }
    }

    public void AdjustMasterVolume(float delta)
    {
        _lastInternalVolumeChangeTime = Environment.TickCount64;
        if (_device == null)
        {
            AttachDevice(AudioDeviceMonitor.Instance.GetDefaultRenderDevice());
            if (_device == null) return;
        }

        try
        {
            float current = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            float newVolume = Math.Clamp(current + delta, 0f, 1f);
            newVolume = MathF.Round(newVolume * 100f) / 100f;

            if (newVolume > 0f && IsMasterMuted)
            {
                IsMasterMuted = false;
            }

            MasterVolume = newVolume;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to adjust master volume");
        }
    }

    public void AdjustMicrophoneVolume(float delta)
    {
        _lastInternalMicVolumeChangeTime = Environment.TickCount64;
        if (_micDevice == null)
        {
            AttachMicDevice(AudioDeviceMonitor.Instance.GetDefaultCaptureDevice());
            if (_micDevice == null) return;
        }

        try
        {
            float current = _micDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
            float newVolume = Math.Clamp(current + delta, 0f, 1f);
            newVolume = MathF.Round(newVolume * 100f) / 100f;

            if (newVolume > 0f && IsMicrophoneMuted)
            {
                IsMicrophoneMuted = false;
            }

            MicrophoneVolume = newVolume;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to adjust microphone volume");
        }
    }

    public bool TryAdjustSessionVolume(int processId, string? appName, float delta, out AudioSessionModel? session)
    {
        session = FindSession(processId, appName);
        if (session == null)
        {
            RefreshSessions();
            session = FindSession(processId, appName);
        }

        if (session != null)
        {
            session.AdjustVolume(delta);
            if (_activeFlyoutSession == session)
            {
                ActiveAppVolume = session.Volume;
                IsActiveAppMuted = session.IsMuted;
            }
            return true;
        }

        return false;
    }

    public bool TryAdjustSessionVolume(int processId, string? appName, float delta, out float newVolume, out string sessionName)
    {
        newVolume = 0f;
        sessionName = string.Empty;

        if (TryAdjustSessionVolume(processId, appName, delta, out var session) && session != null)
        {
            newVolume = session.Volume;
            sessionName = session.DisplayName;
            return true;
        }

        return false;
    }

    private AudioSessionModel? FindSession(int processId, string? appName)
    {
        if (processId != 0)
        {
            var match = Sessions.FirstOrDefault(s => s.ProcessId == processId);
            if (match != null) return match;
        }

        if (!string.IsNullOrWhiteSpace(appName))
        {
            var cleanApp = appName.Trim();

            // 1. Exact match on DisplayName, AppName, or ProductName
            var match = Sessions.FirstOrDefault(s =>
                s.DisplayName.Equals(cleanApp, StringComparison.OrdinalIgnoreCase) ||
                s.AppName.Equals(cleanApp, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(s.ProductName) && s.ProductName.Equals(cleanApp, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(s.ProcessName) && s.ProcessName.Equals(cleanApp, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;

            // 2. Exact match with .exe
            match = Sessions.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.ProcessName) && (s.ProcessName + ".exe").Equals(cleanApp, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            // 3. Word-boundary prefix match (e.g. Telegram -> Telegram Desktop)
            match = Sessions.FirstOrDefault(s =>
                s.DisplayName.StartsWith(cleanApp + " ", StringComparison.OrdinalIgnoreCase) ||
                cleanApp.StartsWith(s.DisplayName + " ", StringComparison.OrdinalIgnoreCase) ||
                s.AppName.StartsWith(cleanApp + " ", StringComparison.OrdinalIgnoreCase) ||
                cleanApp.StartsWith(s.AppName + " ", StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return null;
    }

    [RelayCommand]
    public void RefreshSessions()
    {
        if (_device == null)
        {
            Sessions.Clear();
            return;
        }

        try
        {
            var sessions = GetAllActiveSessions(_device);
            var existingByPid = Sessions
                .GroupBy(s => s.ProcessId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var seenPids = new HashSet<int>();
            bool hasSystemSounds = false;

            string sysSoundsText = System.Windows.Application.Current?.TryFindResource("SystemSoundsText") as string ?? "System sounds";

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var sessionState = session.State;
                if (sessionState == AudioSessionState.AudioSessionStateExpired)
                    continue;

                int pid = (int)session.GetProcessID;
                bool isSystemSound = (pid == 0 || session.IsSystemSoundsSession);

                if (isSystemSound)
                {
                    if (hasSystemSounds)
                        continue;

                    hasSystemSounds = true;
                    pid = 0;
                }

                var info = GetSessionInfo(session);

                if (isSystemSound)
                {
                    info = (sysSoundsText, sysSoundsText, "System sounds", "", "");
                }
                else
                {
                    if (info.displayName == "FluentFlyout" || info.appName == "FluentFlyout" || info.processName == "FluentFlyout")
                        continue;

                    if (IsIgnoredSystemProcess(pid, info.processName, info.appName))
                        continue;
                }

                seenPids.Add(pid);

                if (existingByPid.TryGetValue(pid, out var existingList) && existingList.Count > 0)
                {
                    var existingSession = existingList.FirstOrDefault(s => s.DisplayName == info.displayName) ?? existingList[0];
                    existingSession.State = sessionState;
                    existingSession.SyncFromDevice();
                }
                else
                {
                    var icon = isSystemSound ? null : MediaPlayerData.GetAndCacheProcessIcon(pid, info.appName);
                    Sessions.Add(new AudioSessionModel(session, info.displayName, pid, sessionState, icon, info.appName, info.processName, info.windowTitle, info.productName));
                }
            }

            for (int i = Sessions.Count - 1; i >= 0; i--)
            {
                if (!seenPids.Contains(Sessions[i].ProcessId) || Sessions[i].State == AudioSessionState.AudioSessionStateExpired)
                {
                    Sessions.RemoveAt(i);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate audio sessions");
        }
    }

    private static List<AudioSessionControl> GetAllActiveSessions(MMDevice? defaultDevice)
    {
        var result = new List<AudioSessionControl>();
        if (defaultDevice == null) return result;

        try
        {
            var dev = AudioDeviceMonitor.Instance.GetDeviceById(defaultDevice.ID) ?? defaultDevice;
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var sList = mgr.Sessions;
            for (int i = 0; i < sList.Count; i++)
            {
                result.Add(sList[i]);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to get audio sessions for active device");
        }

        return result;
    }

    private static bool IsIgnoredSystemProcess(int pid, string processName, string appName)
    {
        if (pid <= 0) return false;

        // Process name blacklist of known background/system components that do not play audio
        if (processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("LockApp", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("smartscreen", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("audiodg", StringComparison.OrdinalIgnoreCase) ||
            processName.StartsWith("PowerToys.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Generic Windows OS description on background helper
        if (appName.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase) ||
            appName.Equals("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase))
        {
            if (processName is "TextInputHost" or "SearchHost" or "ShellExperienceHost" or "explorer")
            {
                return true;
            }
        }

        // Executable path check for SystemApps
        string? exePath = TryGetProcessPath(pid);
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            if (exePath.Contains(@"\Windows\SystemApps\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string displayName, string appName, string processName, string windowTitle, string productName) GetSessionInfo(AudioSessionControl session)
    {
        uint pid = session.GetProcessID;
        if (pid == 0)
        {
            return ("System sounds", "System sounds", "System sounds", "", "");
        }

        string appName = "";
        string processName = "";
        string windowTitle = "";
        string productName = "";

        try
        {
            var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;

            try
            {
                var mainModule = proc.MainModule;
                if (mainModule != null && !string.IsNullOrWhiteSpace(mainModule.FileVersionInfo.FileDescription))
                {
                    appName = mainModule.FileVersionInfo.FileDescription;
                    productName = mainModule.FileVersionInfo.ProductName ?? "";
                }
            }
            catch { }

            try
            {
                windowTitle = proc.MainWindowTitle;
            }
            catch { }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(productName))
        {
            string? exePath = TryGetProcessPath((int)pid);
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exePath);
                    if (string.IsNullOrWhiteSpace(appName) && !string.IsNullOrWhiteSpace(vi.FileDescription))
                        appName = vi.FileDescription;
                    if (string.IsNullOrWhiteSpace(productName) && !string.IsNullOrWhiteSpace(vi.ProductName))
                        productName = vi.ProductName;
                }
                catch { }
            }
        }

        // Fallback from session identifier if process inspection didn't get appName
        if (string.IsNullOrWhiteSpace(appName))
        {
            try
            {
                string id = session.GetSessionIdentifier;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    var parts = id.Split('|');
                    foreach (var part in parts)
                    {
                        if (part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            appName = System.IO.Path.GetFileNameWithoutExtension(part);
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = !string.IsNullOrWhiteSpace(productName) ? productName : (!string.IsNullOrWhiteSpace(processName) ? processName : "Application");
        }

        string displayName = !string.IsNullOrWhiteSpace(appName)
            ? appName
            : (!string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName : "Application");

        return (displayName, appName, processName, windowTitle, productName);
    }

    public void OnPollTick(object? sender, EventArgs e)
    {
        SyncMasterFromDevice();
        SyncMicFromDevice();

        foreach (var session in Sessions)
        {
            session.SyncFromDevice();
        }
    }

    public void Dispose()
    {
        _pollTimer?.Stop();
        _pollTimer = null;

        if (_device != null)
        {
            try
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnAudioEndpointVolumeNotification;
            }
            catch { }
        }

        if (_micDevice != null)
        {
            try
            {
                _micDevice.AudioEndpointVolume.OnVolumeNotification -= OnMicAudioEndpointVolumeNotification;
            }
            catch { }
        }

        if (_sessionManager != null)
        {
            try
            {
                _sessionManager.OnSessionCreated -= OnSessionCreatedHandler;
            }
            catch { }
            _sessionManager = null;
        }

        AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        AudioDeviceMonitor.Instance.DefaultCaptureDeviceChanged -= OnDefaultCaptureDeviceChanged;

        Sessions.Clear();

        GC.SuppressFinalize(this);
    }
}
