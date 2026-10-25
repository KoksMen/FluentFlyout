// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Windows.Media;

namespace FluentFlyoutWPF.Models;

public partial class AudioSessionModel : ObservableObject
{
    private readonly AudioSessionControl _sessionControl;

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial int ProcessId { get; set; }

    [ObservableProperty]
    public partial AudioSessionState State { get; set; }

    [ObservableProperty]
    public partial float Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon != null;

    public bool IsActive => State == AudioSessionState.AudioSessionStateActive;

    public AudioSessionControl SessionControl => _sessionControl;

    public string AppName { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;

    public event EventHandler<float>? VolumeChanged;

    public AudioSessionModel(AudioSessionControl sessionControl, string displayName, int processId, AudioSessionState sessionState, ImageSource? icon, string appName = "", string processName = "", string windowTitle = "", string productName = "")
    {
        _sessionControl = sessionControl;
        DisplayName = displayName;
        ProcessId = processId;
        State = sessionState;
        Icon = icon;
        AppName = !string.IsNullOrWhiteSpace(appName) ? appName : displayName;
        ProcessName = processName;
        WindowTitle = windowTitle;
        ProductName = productName;
        try
        {
            Volume = _sessionControl.SimpleAudioVolume.Volume;
            IsMuted = _sessionControl.SimpleAudioVolume.Mute;
        }
        catch
        {
            Volume = 1.0f;
            IsMuted = false;
        }
    }

    partial void OnVolumeChanged(float value)
    {
        try
        {
            _sessionControl.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
            if (Volume == 0f)
            {
                IsMuted = true;
            }
            else if (IsMuted && value > 0f)
            {
                IsMuted = false;
            }
        }
        catch { }
        VolumeChanged?.Invoke(this, Volume);
    }

    partial void OnIsMutedChanged(bool value)
    {
        try
        {
            _sessionControl.SimpleAudioVolume.Mute = value;
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    public void AdjustVolume(float delta)
    {
        Volume = Math.Clamp(Volume + delta, 0f, 1f);
    }

    /// <summary>
    /// refreshes Volume and IsMuted from the audio session without pushing changes back
    /// </summary>
    public void SyncFromDevice()
    {
        try
        {
            var vol = _sessionControl.SimpleAudioVolume.Volume;
            var mute = _sessionControl.SimpleAudioVolume.Mute;

            if (MathF.Abs(Volume - vol) > 0.001f)
                Volume = vol;

            if (IsMuted != mute)
                IsMuted = mute;
        }
        catch { }
    }
}
