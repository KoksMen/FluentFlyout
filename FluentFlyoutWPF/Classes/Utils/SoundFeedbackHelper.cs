// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Media;

namespace FluentFlyoutWPF.Classes.Utils;

public static class SoundFeedbackHelper
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static byte[]? _muteWav;
    private static byte[]? _unmuteWav;

    private static byte[] CreateBeepWav(int frequency, int durationMs, double volume = 0.22)
    {
        const int sampleRate = 44100;
        int samples = sampleRate * durationMs / 1000;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(new char[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + samples * 2);
        bw.Write(new char[] { 'W', 'A', 'V', 'E' });
        bw.Write(new char[] { 'f', 'm', 't', ' ' });
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(new char[] { 'd', 'a', 't', 'a' });
        bw.Write(samples * 2);

        int fadeSamples = sampleRate * 12 / 1000; // 12ms smooth fade

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = 1.0;
            if (i < fadeSamples)
            {
                envelope = (double)i / fadeSamples;
            }
            else if (i > samples - fadeSamples)
            {
                envelope = (double)(samples - i) / fadeSamples;
            }

            short sample = (short)(Math.Sin(2.0 * Math.PI * frequency * t) * envelope * volume * short.MaxValue);
            bw.Write(sample);
        }

        return ms.ToArray();
    }

    public static void PlayMuteFeedback(bool isMuted)
    {
        _ = Task.Run(() =>
        {
            try
            {
                byte[] wavData;
                if (isMuted)
                {
                    _muteWav ??= CreateBeepWav(420, 110);
                    wavData = _muteWav;
                }
                else
                {
                    _unmuteWav ??= CreateBeepWav(840, 110);
                    wavData = _unmuteWav;
                }

                using var ms = new MemoryStream(wavData);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to play mute audio feedback");
            }
        });
    }
}
