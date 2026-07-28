// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentFlyoutWPF.Classes
{
    public class Visualizer : IDisposable
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static int BarCount = 10;
        private readonly int ImageWidth = 76 * 3;
        private readonly int ImageHeight = 32 * 3;
        private readonly int BarSpacing = 2 * 3;

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _renderDevice;
        private static float[]? _barValues;
        private WriteableBitmap? _bitmap;
        private bool _isRunning;
        private readonly object _lock = new();

        private readonly int _fftLength = 4096;
        private int _fftPos = 0;
        private readonly Complex[] _fftBuffer;

        private readonly int _targetFps = 30;
        private DateTime _lastUpdateTime = DateTime.MinValue;

        private System.Timers.Timer? _captureWatchdog;
        private DateTime _lastDataAvailableUtc = DateTime.MinValue;
        private int _restartInProgress; // 0=false, 1=true (Interlocked)
        private string? _deviceId; // track current device ID for restart logic

        private readonly struct BarGeometry
        {
            public readonly float Left, Right, Top, Bottom;
            public readonly float InnerLeft, InnerRight, InnerTop, InnerBottom;

            public BarGeometry(int x, int width, int y, int endY, float radius)
            {
                Left = x;
                Right = x + width;
                Top = y;
                Bottom = endY;

                InnerLeft = Left + radius;
                InnerRight = Right - radius;
                InnerTop = Top + radius;
                InnerBottom = Bottom - radius;
            }
        }

        public WriteableBitmap? Bitmap
        {
            get
            {
                lock (_lock)
                {
                    return _bitmap;
                }
            }
        }

        public Visualizer()
        {
            InitializeBitmap();

            _fftBuffer = new Complex[_fftLength];

            ResizeBarList(SettingsManager.Current.TaskbarVisualizerBarCount);
            AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;
            TryRegisterSystemEvents();
        }

        private void TryRegisterSystemEvents()
        {
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                // On some environments (e.g. non-interactive sessions), SystemEvents may not be available.
                Logger.Warn(ex, "Failed to register SystemEvents handlers for visualizer auto-restart");
            }
        }

        private void TryUnregisterSystemEvents()
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to unregister SystemEvents handlers for visualizer auto-restart");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
                return;

            // When unlocking after device disconnect (e.g. Bluetooth earbuds), WASAPI loopback can get stuck.
            // Restart capture on unlock / logon to recover without user action.
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
            {
                RequestRestart($"session switch: {e.Reason}");
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
                return;

            if (e.Mode == PowerModes.Resume)
            {
                RequestRestart("power resume");
            }
        }

        private void InitializeBitmap()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    _bitmap = new WriteableBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Bgra32, null);
                }
            });
        }

        private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
        {
            _deviceId = e.DeviceId;

            // Even if capture isn't currently running (e.g. restart attempt failed while the device was reconfiguring),
            // we still want to try restarting as soon as Windows reports a usable default endpoint again.
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
                return;
            RequestRestart("default audio output device changed");
        }

        private void RequestRestart(string reason)
        {
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
                return;

            if (Interlocked.Exchange(ref _restartInProgress, 1) == 1)
                return;

            Logger.Info($"Restarting visualizer ({reason})");

            Task.Run(async () =>
            {
                try
                {
                    Stop();

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(500);
                        Start();
                        if (_isRunning)
                            return;
                        Logger.Warn($"Visualizer restart attempt {attempt + 1} failed, retrying...");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Visualizer restart failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _restartInProgress, 0);
                }
            });
        }

        public static void ResizeBarList(int newBarCount)
        {
            BarCount = newBarCount;
            _barValues = new float[BarCount];
        }

        public void Start()
        {
            if (_isRunning)
                return;

            float barCount = BarCount >= 0 ? BarCount : 8;
            _barValues = new float[(int)barCount];

            try
            {
                // Explicitly bind to the current default render endpoint.
                // Using the parameterless capture can throw transient COM errors when the default endpoint is
                // reconfiguring (e.g. Bluetooth earbuds disconnect/reconnect around lock/unlock).
                _renderDevice?.Dispose();
                _renderDevice = string.IsNullOrWhiteSpace(_deviceId)
                     ? AudioDeviceMonitor.Instance.GetDefaultRenderDevice()
                     : AudioDeviceMonitor.Instance.GetDeviceById(_deviceId) ?? AudioDeviceMonitor.Instance.GetDefaultRenderDevice();

                if (_renderDevice == null)
                {
                    return;
                }

                _capture = new WasapiLoopbackCapture(_renderDevice);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _isRunning = true;
                _lastDataAvailableUtc = DateTime.UtcNow;

                // automatic update timer in case audio data is not updated
                _captureWatchdog = new(500)
                {
                    AutoReset = false
                };
                _captureWatchdog.Elapsed += (_, _) =>
                {
                    if (_isRunning)
                    {
                        for (int i = 0; i < _barValues.Length; i++)
                        {
                            _barValues[i] = 0;
                        }
                        UpdateBitmap();

                        if (!SettingsManager.Current.TaskbarVisualizerBaseline || SettingsManager.Current.TaskbarVisualizerBaselineAutoHide) // if baseline is enabled and autohide is off, condition is false
                            SettingsManager.Current.TaskbarVisualizerHasContent = false;

                        // If we stop receiving loopback callbacks entirely (common after lock/unlock + device changes),
                        // the timer fires once and then never again. Use it as a recovery trigger.
                        var silenceFor = DateTime.UtcNow - _lastDataAvailableUtc;
                        if (silenceFor > TimeSpan.FromSeconds(2))
                        {
                            RequestRestart($"no audio callbacks for {silenceFor.TotalSeconds:0.0}s");
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start visualizer");
            }
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;

            _capture?.DataAvailable -= OnDataAvailable;
            _capture?.RecordingStopped -= OnRecordingStopped;
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            _renderDevice?.Dispose();
            _renderDevice = null;

            _captureWatchdog?.Stop();
            _captureWatchdog?.Dispose();
            _captureWatchdog = null;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isRunning || e.BytesRecorded == 0)
                return;

            _lastDataAvailableUtc = DateTime.UtcNow;

            _captureWatchdog.Stop();
            _captureWatchdog.Start();

            int bytesPerSample = _capture!.WaveFormat.BitsPerSample / 8;
            int samplesRecorded = e.BytesRecorded / bytesPerSample;

            for (int i = 0; i < samplesRecorded; i++)
            {
                float sampleValue = 0;
                if (bytesPerSample == 4)
                {
                    sampleValue = BitConverter.ToSingle(e.Buffer, i * 4);
                }
                else if (bytesPerSample == 2)
                {
                    sampleValue = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                }

                _fftBuffer[_fftPos].X = (float)(sampleValue * FastFourierTransform.HammingWindow(_fftPos, _fftLength));
                _fftBuffer[_fftPos].Y = 0;
                _fftPos++;

                // When buffer isn't full, skip processing and continue filling
                if (_fftPos < _fftLength)
                    continue;

                // perform FFT
                _fftPos = 0;
                ProcessFftData();

                // Update UI with frame rate limiting
                DateTime now = DateTime.UtcNow;
                double minFrameTime = 1000.0 / _targetFps;
                double timeSinceLastUpdate = (now - _lastUpdateTime).TotalMilliseconds;

                if (timeSinceLastUpdate < minFrameTime)
                    continue;

                _lastUpdateTime = now;

                if (SettingsManager.Current.TaskbarVisualizerOnlyWhenPlaying && !MainWindow.IsMediaPlaying)
                {
                    SettingsManager.Current.TaskbarVisualizerHasContent = false;
                    bool hasActiveBars = false;
                    if (_barValues != null)
                    {
                        for (int j = 0; j < BarCount; j++)
                        {
                            if (_barValues[j] > 0.001f)
                            {
                                hasActiveBars = true;
                                break;
                            }
                        }

                        if (hasActiveBars)
                        {
                            Array.Clear(_barValues, 0, _barValues.Length);
                            UpdateBitmap();
                        }
                    }
                    continue;
                }

                SettingsManager.Current.TaskbarVisualizerHasContent = true;

                if (SettingsManager.Current.TaskbarVisualizerBaseline && !SettingsManager.Current.TaskbarVisualizerBaselineAutoHide)
                {
                    // if baseline is enabled and autohide is off, we want to keep showing the bars even when they are all zero
                    UpdateBitmap();
                    break;
                }

                // check if bars are all zero, if so set has content to false to disable hover effect
                bool allZero = true;
                for (int j = 0; j < BarCount; j++)
                {
                    if (_barValues[j] > 0.01f)
                    {
                        allZero = false;
                        break;
                    }
                }

                // update bars if they have content
                if (!allZero)
                    UpdateBitmap();
                else
                    SettingsManager.Current.TaskbarVisualizerHasContent = false;
            }
        }

        private void ProcessFftData()
        {
            FastFourierTransform.FFT(true, (int)Math.Log(_fftLength, 2.0), _fftBuffer);

            int sampleRate = _capture.WaveFormat.SampleRate;
            double frequencyPerBin = (double)sampleRate / _fftLength;

            double minFreq = 40;   // Hz
            double maxFreq = 8000; // Hz
            //double minFreq = 40;  // Hz // could be a setting to be bass only
            //double maxFreq = 120; // Hz
            float minDb = (SettingsManager.Current.TaskbarVisualizerAudioSensitivity * -10f) - 30f;
            float maxDb = (SettingsManager.Current.TaskbarVisualizerAudioPeakLevel * 10f) - 30f;

            float[] currentBars = new float[BarCount];

            for (int i = 0; i < BarCount; i++)
            {
                double startFreq = minFreq * Math.Pow(maxFreq / minFreq, (double)i / BarCount);
                double endFreq = minFreq * Math.Pow(maxFreq / minFreq, (double)(i + 1) / BarCount);

                int startBin = (int)(startFreq / frequencyPerBin);
                int endBin = (int)(endFreq / frequencyPerBin);

                if (endBin <= startBin) endBin = startBin + 1;
                if (endBin >= _fftBuffer.Length / 2) endBin = _fftBuffer.Length / 2 - 1;

                float maxAmplitude = 0;

                // Find max amplitude
                for (int j = startBin; j < endBin; j++)
                {
                    float amplitude = (float)Math.Sqrt(_fftBuffer[j].X * _fftBuffer[j].X + _fftBuffer[j].Y * _fftBuffer[j].Y);
                    if (amplitude > maxAmplitude)
                        maxAmplitude = amplitude;
                }

                float progress = (float)i / BarCount;
                float linearBoost = 1.0f + (progress * 75.0f);
                maxAmplitude *= linearBoost;

                if (maxAmplitude < 0.001f) maxAmplitude = 0.001f;

                float db = 20f * (float)Math.Log10(maxAmplitude);

                float intensity = (db - minDb) / (maxDb - minDb);
                intensity = Math.Clamp(intensity, 0f, 1f);

                currentBars[i] = intensity;
            }

            for (int i = 0; i < BarCount; i++)
            {
                if (currentBars[i] > _barValues[i])
                {
                    // Jump up quickly
                    _barValues[i] = currentBars[i];
                }
                else
                {
                    // Fall down slowly
                    //_barValues[i] = (_barValues[i] * 0.9f) + (currentBars[i] * 0.1f);
                    _barValues[i] = (_barValues[i] * 0.8f) + (currentBars[i] * 0.2f);
                    //_barValues[i] = (_barValues[i] * 0.7f) + (currentBars[i] * 0.3f); // could be options for smoothening
                    //_barValues[i] = (_barValues[i] * 0.6f) + (currentBars[i] * 0.4f);
                }
            }
        }

        private void UpdateBitmap()
        {
            if (_bitmap == null)
                return;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                lock (_lock)
                {
                    if (_bitmap == null)
                        return;

                    _bitmap.Lock();

                    try
                    {
                        unsafe
                        {
                            IntPtr pBackBuffer = _bitmap.BackBuffer;
                            int stride = _bitmap.BackBufferStride;
                            int bufferSize = stride * ImageHeight;

                            Span<byte> buffer = new Span<byte>(pBackBuffer.ToPointer(), bufferSize);

                            buffer.Clear();

                            DrawBars(stride, buffer);
                        }

                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, ImageWidth, ImageHeight));
                    }
                    finally
                    {
                        _bitmap.Unlock();
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private unsafe void DrawBars(int stride, Span<byte> buffer)
        {
            // Resolve brush once 
            SolidColorBrush brush = BitmapHelper.SavedDominantColors.Count > 0
                ? BitmapHelper.SavedDominantColors.Last()
                : (SolidColorBrush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorTertiary");

            byte b = brush.Color.B;
            byte g = brush.Color.G;
            byte r = brush.Color.R;

            bool centeredBars = SettingsManager.Current.TaskbarVisualizerCenteredBars;
            int barBaseline = SettingsManager.Current.TaskbarVisualizerBaseline ? 4 : 0;

            int centerY = ImageHeight / 2;

            // Horizontal layout 
            ComputeLayout(ImageWidth, BarCount, BarSpacing,
                out int barWidth,
                out int offsetX);

            // Radius 
            float baseRadius = GetCornerRadius();

            // AA constants 
            const float aa = 1.25f;
            float invAA = 1f / aa;

            bool outlineEnabled = SettingsManager.Current.TaskbarVisualizerOutlineEnabled;
            int outlineThickness = Math.Clamp(SettingsManager.Current.TaskbarVisualizerOutlineThickness, 1, 5);
            byte ob = 0, og = 0, or = 0, oa = 255;
            if (outlineEnabled)
            {
                if (SettingsManager.Current.TaskbarVisualizerOutlineDynamicColor)
                {
                    const double darkenFactor = 0.72;
                    ob = (byte)Math.Round(b * darkenFactor);
                    og = (byte)Math.Round(g * darkenFactor);
                    or = (byte)Math.Round(r * darkenFactor);
                }
                else
                {
                    try
                    {
                        string hex = SettingsManager.Current.TaskbarVisualizerOutlineColor ?? "#000000";
                        if (!hex.StartsWith("#") && hex.Length >= 6) hex = "#" + hex;
                        var parsedColor = (Color)ColorConverter.ConvertFromString(hex);
                        ob = parsedColor.B;
                        og = parsedColor.G;
                        or = parsedColor.R;
                        oa = parsedColor.A;
                    }
                    catch
                    {
                        ob = 0; og = 0; or = 0; oa = 255;
                    }
                }
            }

            for (int i = 0; i < BarCount; i++)
            {
                int barX = offsetX + i * (barWidth + BarSpacing);

                int barHeight = GetBarHeight(_barValues[i], barBaseline);

                if (barHeight <= 0)
                    continue;

                ComputeVertical(centeredBars, centerY, barHeight, out int barY, out int barEndY);

                // Clamp radius per bar
                float radius = ClampRadius(baseRadius, barWidth, barHeight);
                float radiusSq = radius * radius;

                RasterizeBar(
                    buffer, stride,
                    barX, barWidth,
                    barY, barEndY,
                    centeredBars,
                    radius, radiusSq, invAA,
                    b, g, r,
                    outlineEnabled, outlineThickness, ob, og, or, oa);
            }
        }

        private static void ComputeLayout(
            int imageWidth,
            int barCount,
            int spacing,
            out int barWidth,
            out int offsetX)
        {
            int totalSpacing = (barCount - 1) * spacing;

            int availableWidth = imageWidth - totalSpacing - 1;

            barWidth = availableWidth / barCount;

            int usedWidth = barWidth * barCount + totalSpacing;

            // Center safely
            offsetX = (imageWidth - usedWidth) >> 1;
        }

        private void ComputeVertical(bool centered, int centerY, int height, out int y, out int endY)
        {
            if (centered)
            {
                int half = height >> 1; // faster than /2
                y = centerY - half;
                endY = centerY + half;
            }
            else
            {
                y = ImageHeight - height;
                endY = ImageHeight;
            }
        }

        private int GetBarHeight(float value, int baseline)
        {
            return Math.Max((int)(Math.Clamp(value, 0f, 1f) * ImageHeight), baseline);
        }
        private static float GetCornerRadius()
        {
            return 6f / MathF.Max(1f, SettingsManager.Current.TaskbarVisualizerBarCount / 10f);
        }

        private static float ClampRadius(float r, int width, int height)
        {
            float max = MathF.Min(width, height) * 0.5f;
            return r > max ? max : r;
        }

        private unsafe void RasterizeBar(
            Span<byte> buffer,
            int stride,
            int barX,
            int barWidth,
            int barY,
            int barEndY,
            bool centeredBars,
            float radius,
            float radiusSq,
            float invAA,
            byte b, byte g, byte r,
            bool outlineEnabled, int outlineThickness,
            byte ob, byte og, byte or, byte oa)
        {
            float rectX = barX;
            float rectW = barWidth;
            float rectY = barY;
            float rectH = barEndY - barY;

            if (!outlineEnabled || outlineThickness <= 0)
            {
                // Fast path: standard bar without outline
                for (int y = barY; y < barEndY && y < ImageHeight && y >= 0; y++)
                {
                    int row = y * stride;
                    for (int x = barX; x < barX + barWidth && x < ImageWidth; x++)
                    {
                        int index = row + (x << 2);
                        if (index + 3 >= buffer.Length) continue;

                        float innerLeft = rectX + radius;
                        float innerRight = (rectX + rectW) - radius;
                        float innerTop = rectY + radius;
                        float innerBottom = (rectY + rectH) - (centeredBars ? radius : 0f);

                        if (x >= innerLeft && x <= innerRight) { WritePixel(buffer, index, b, g, r, 255); continue; }
                        if (y >= innerTop && y <= innerBottom) { WritePixel(buffer, index, b, g, r, 255); continue; }
                        if (!centeredBars && y >= innerBottom) { WritePixel(buffer, index, b, g, r, 255); continue; }

                        float cx = x < innerLeft ? innerLeft : (x > innerRight ? innerRight : x);
                        float cy = y < innerTop ? innerTop : (y > innerBottom ? innerBottom : y);
                        float dx = x - cx; float dy = y - cy;
                        float distSq = dx * dx + dy * dy;
                        float sdf = (distSq - radiusSq) / (2f * radius);
                        float alpha = 0.5f - sdf * invAA;
                        if (alpha <= 0f) continue;
                        if (alpha > 1f) alpha = 1f;
                        WritePixel(buffer, index, b, g, r, (byte)(255 * alpha));
                    }
                }
                return;
            }

            float stroke = MathF.Min((float)outlineThickness, MathF.Min(rectW, rectH) * 0.35f);

            // Outer rect parameters
            float outerR = MathF.Min(radius, MathF.Min(rectW, rectH) * 0.5f);
            if (outerR < 0.5f) outerR = 0.5f;
            float outerRSq = outerR * outerR;
            float inv2OuterR = 1f / (2f * outerR);
            float outerInnerLeft = rectX + outerR;
            float outerInnerRight = (rectX + rectW) - outerR;
            float outerInnerTop = rectY + outerR;
            float outerInnerBottom = (rectY + rectH) - (centeredBars ? outerR : 0f);

            // Inner rect parameters
            float innerX = rectX + stroke;
            float innerW = rectW - 2f * stroke;
            float innerY = rectY + stroke;
            float innerH = rectH - (centeredBars ? 2f * stroke : stroke);
            float innerR = MathF.Max(0.5f, outerR - stroke);
            float innerRSq = innerR * innerR;
            float inv2InnerR = 1f / (2f * innerR);
            float innerInnerLeft = innerX + innerR;
            float innerInnerRight = (innerX + innerW) - innerR;
            float innerInnerTop = innerY + innerR;
            float innerInnerBottom = (innerY + innerH) - (centeredBars ? innerR : 0f);

            float borderAlphaFactor = oa / 255f;

            // Fast inner bounds for 100% solid fill core
            float fastFillMinX = innerX + 1.5f;
            float fastFillMaxX = (innerX + innerW) - 1.5f;
            float fastFillMinY = innerY + 1.5f;
            float fastFillMaxY = (innerY + innerH) - 1.5f;
            bool hasFastFillCore = fastFillMaxX > fastFillMinX && fastFillMaxY > fastFillMinY;

            for (int y = barY; y < barEndY && y < ImageHeight && y >= 0; y++)
            {
                int row = y * stride;
                float pixelY = y + 0.5f;

                for (int x = barX; x < barX + barWidth && x < ImageWidth; x++)
                {
                    int index = row + (x << 2);
                    if (index + 3 >= buffer.Length)
                        continue;

                    float pixelX = x + 0.5f;

                    // FAST-PATH: 100% Solid Fill Core (instant 1-cycle write!)
                    if (hasFastFillCore && pixelX >= fastFillMinX && pixelX <= fastFillMaxX && pixelY >= fastFillMinY && pixelY <= fastFillMaxY)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    // Outer Alpha
                    float outerAlpha;
                    if (!centeredBars && pixelY >= outerInnerBottom && pixelY <= (rectY + rectH) && pixelX >= rectX && pixelX <= (rectX + rectW))
                    {
                        outerAlpha = 1.0f;
                    }
                    else
                    {
                        float cx = pixelX < outerInnerLeft ? outerInnerLeft : (pixelX > outerInnerRight ? outerInnerRight : pixelX);
                        float cy = pixelY < outerInnerTop ? outerInnerTop : (pixelY > outerInnerBottom ? outerInnerBottom : pixelY);
                        float dx = pixelX - cx;
                        float dy = pixelY - cy;
                        float distSq = dx * dx + dy * dy;
                        float sdf = (distSq - outerRSq) * inv2OuterR;
                        outerAlpha = Math.Clamp(0.5f - sdf * invAA, 0f, 1f);
                    }

                    if (outerAlpha <= 0f)
                        continue;

                    // Inner Alpha
                    float innerAlpha;
                    if (innerW <= 0f || innerH <= 0f)
                    {
                        innerAlpha = 0f;
                    }
                    else if (!centeredBars && pixelY >= innerInnerBottom && pixelY <= (innerY + innerH) && pixelX >= innerX && pixelX <= (innerX + innerW))
                    {
                        innerAlpha = 1.0f;
                    }
                    else
                    {
                        float cx = pixelX < innerInnerLeft ? innerInnerLeft : (pixelX > innerInnerRight ? innerInnerRight : pixelX);
                        float cy = pixelY < innerInnerTop ? innerInnerTop : (pixelY > innerInnerBottom ? innerInnerBottom : pixelY);
                        float dx = pixelX - cx;
                        float dy = pixelY - cy;
                        float distSq = dx * dx + dy * dy;
                        float sdf = (distSq - innerRSq) * inv2InnerR;
                        innerAlpha = Math.Clamp(0.5f - sdf * invAA, 0f, 1f);
                    }

                    // Composite
                    float borderCoverage = MathF.Max(0f, outerAlpha - innerAlpha);
                    float fillCoverage = innerAlpha;
                    float borderEffectiveA = borderCoverage * borderAlphaFactor;
                    float totalA = borderEffectiveA + fillCoverage;

                    if (totalA <= 0f)
                        continue;

                    float resR = (or * borderEffectiveA + r * fillCoverage) / totalA;
                    float resG = (og * borderEffectiveA + g * fillCoverage) / totalA;
                    float resB = (ob * borderEffectiveA + b * fillCoverage) / totalA;

                    byte finalR = (byte)Math.Clamp(resR, 0f, 255f);
                    byte finalG = (byte)Math.Clamp(resG, 0f, 255f);
                    byte finalB = (byte)Math.Clamp(resB, 0f, 255f);
                    byte finalA = (byte)Math.Clamp(totalA * outerAlpha * 255f, 0f, 255f);

                    WritePixel(buffer, index, finalB, finalG, finalR, finalA);
                }
            }
        }

        private static void WritePixel(Span<byte> buffer, int index, byte b, byte g, byte r, byte a)
        {
            buffer[index] = b;
            buffer[index + 1] = g;
            buffer[index + 2] = r;
            buffer[index + 3] = a;
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Logger.Error(e.Exception, "Visualizer recording stopped due to an error");
            }
        }

        public void Dispose()
        {
            Stop();

            AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            TryUnregisterSystemEvents();

            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}
