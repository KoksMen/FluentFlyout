// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Models;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace FluentFlyoutWPF.Classes.Services;

public enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class CPolicyConfigClient { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint(
        [In, MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
        [In, MarshalAs(UnmanagedType.U4)] ERole eRole);
    [PreserveSig] int SetEndpointVisibility();
}

public static class AudioDeviceService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static List<AudioDeviceModel> GetPlaybackDevices(string? activeDefaultId = null)
    {
        var list = new List<AudioDeviceModel>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? currentDefault = activeDefaultId;
            if (string.IsNullOrEmpty(currentDefault))
            {
                try
                {
                    currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
                }
                catch { }
            }

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices)
            {
                list.Add(new AudioDeviceModel
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    IsDefault = dev.ID.Equals(currentDefault, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate playback devices");
        }
        return list;
    }

    public static List<AudioDeviceModel> GetRecordingDevices(string? activeDefaultId = null)
    {
        var list = new List<AudioDeviceModel>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? currentDefault = activeDefaultId;
            if (string.IsNullOrEmpty(currentDefault))
            {
                try
                {
                    currentDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)?.ID
                                  ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)?.ID;
                }
                catch { }
            }

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var dev in devices)
            {
                list.Add(new AudioDeviceModel
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    IsDefault = dev.ID.Equals(currentDefault, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate recording devices");
        }
        return list;
    }

    public static bool SetDefaultPlaybackDevice(string deviceId)
    {
        return SetDefaultEndpoint(deviceId);
    }

    public static bool SetDefaultRecordingDevice(string deviceId)
    {
        return SetDefaultEndpoint(deviceId);
    }

    private static bool SetDefaultEndpoint(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;

        try
        {
            var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            Logger.Info("Successfully set default audio endpoint to {0}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set default endpoint {0}", deviceId);
            return false;
        }
    }
}
