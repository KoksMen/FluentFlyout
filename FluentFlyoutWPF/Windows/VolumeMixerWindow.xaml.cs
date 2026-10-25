// Copyright (c) 2024-2026 The FluentFlyout Authors

// SPDX-License-Identifier: GPL-3.0-or-later



// Portions of this code are derived from:

// - gpkgpk/HideVolumeOSD: https://github.com/gpkgpk/HideVolumeOSD

//

// Copyright (c) 2022 gpkgpk

// Modifications copyright (c) 2026 The FluentFlyout Authors



using FluentFlyout.Classes;

using FluentFlyout.Classes.Settings;

using FluentFlyoutWPF.Classes;

using FluentFlyoutWPF.Classes.Utils;

using FluentFlyoutWPF.Models;

using FluentFlyoutWPF.ViewModels;

using MicaWPF.Controls;

using NLog;

using System.Windows;

using System.Windows.Media.Animation;

using static FluentFlyout.Classes.NativeMethods;



namespace FluentFlyoutWPF.Windows;



/// <summary>

/// Interaction logic for VolumeMixerWindow.xaml

/// </summary>

public partial class VolumeMixerWindow : MicaWindow

{

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public VolumeMixerViewModel ViewModel { get; } = new();

    public UserSettings UserSettings => SettingsManager.Current;



    private static IntPtr _nativeOsdElement = IntPtr.Zero;

    private static int _nativeOsdOriginalExStyle;

    private CancellationTokenSource _cts;

    private MainWindow _mainWindow;

    private readonly double _collapsedHeight = 52;

    private Rect? _currentAnchorRect;

    private readonly double _normalWidth;

    private bool _isHiding = true;

    private bool _isTaskbarAnchored;

    public bool IsTaskbarAnchored => _isTaskbarAnchored;

    private bool _taskbarExpandDownward;

    private bool _suppressExpandCollapseAnimation;

    private int _taskbarAnimationGeneration;



    private long _lastFlyoutTime = 0;

    private readonly TimeSpan _flyoutCooldown = TimeSpan.FromMilliseconds(100);



    public VolumeMixerWindow()

    {

        DataContext = this;

        WindowHelper.SetNoActivate(this);

        InitializeComponent();

        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

        WindowHelper.SetTopmost(this);

        CustomWindowChrome.CaptionHeight = 0;

        CustomWindowChrome.UseAeroCaptionButtons = false;

        CustomWindowChrome.GlassFrameThickness = new Thickness(0);



        WindowStartupLocation = WindowStartupLocation.Manual;

        Top = -9999; // start off-screen to prevent flickering on startup



        _mainWindow = (MainWindow)Application.Current.MainWindow;

        _cts = new CancellationTokenSource();

        _normalWidth = Width;



        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        ViewModel.MasterVolumeNotificationReceived += OnMasterVolumeNotificationReceived;

    }



    private void OnMasterVolumeNotificationReceived(object? sender, EventArgs e)

    {

        if (SettingsManager.Current.VolumeControlEnabled && !_isTaskbarAnchored)

        {

            ShowFlyout();

        }

    }



    private bool _isTaskbarApp;

    public void ShowAppFlyout(AudioSessionModel session, Rect? anchorRect = null, bool isTaskbarApp = false)

    {

        _currentAnchorRect = anchorRect;

        _isTaskbarApp = isTaskbarApp;

        ViewModel.SetFlyoutApp(session);

        ShowFlyout(anchorRect, isTaskbarApp);

    }



    // one day we might want to convert these to an interface

public void HideFlyout()
    {
        if (!_isHiding && !_isTaskbarAnchored && !ViewModel.IsExpanded)
        {
            _cts.Cancel();
            _mainWindow.CloseAnimation(this);
            _isHiding = true;
            WindowHelper.SetVisibility(this, false);
        }
    }

    public async void ShowFlyout(Rect? anchorRect = null, bool isTaskbarApp = false)

    {

        if (anchorRect.HasValue)

        {

            _currentAnchorRect = anchorRect;

            _isTaskbarApp = isTaskbarApp;

        }

        else

        {

            _isTaskbarApp = false;

        }



        if (_isTaskbarAnchored)

        {

            return;

        }



        if (!anchorRect.HasValue)

        {

            ViewModel.SetFlyoutApp(null);

        }



        if (FullscreenDetector.ShouldSuppressForFullscreen(SettingsManager.Current.VolumeFlyoutShowInExclusiveFullscreen, SettingsManager.Current.VolumeFlyoutShowInBorderless))

        {

            // If the custom flyout is suppressed in fullscreen, we restore the native Windows OSD

            // so the user can still see the volume change indicator.

            _ = Task.Run(() => ShowVolumeOsd());

            return;

        }



        long currentTime = Environment.TickCount64;



        if (_isHiding && currentTime - _lastFlyoutTime < _flyoutCooldown.TotalMilliseconds)

        {

            return;

        }



        _lastFlyoutTime = currentTime;



        if (_isHiding)

        {

            EnsureVolumeOsdHidden();



            _isHiding = false;

            if (SettingsManager.Current.VolumeMixerAcrylicWindowEnabled)

            {

                WindowBlurHelper.EnableBlur(this);

            }

            else

            {

                WindowBlurHelper.DisableBlur(this);

            }



            // refresh all data

            ViewModel.OnPollTick(null, EventArgs.Empty);



            bool aboveMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout && !_currentAnchorRect.HasValue;

            if (aboveMedia)

            {

                Width = _mainWindow.Width;

                _mainWindow.OpenAnimation(this, aboveReference: _mainWindow);

            }

            else

            {

                Width = _normalWidth;

                _mainWindow.OpenAnimation(this, alwaysBottom: true, anchorRect: _currentAnchorRect, isTaskbarApp: _isTaskbarApp);

            }



            // Removed Show() call to prevent window activation/focus stealing which minimizes fullscreen apps.

            // OpenAnimation already shows the window non-activatively using Win32 SetWindowPos.

            WindowHelper.SetTopmost(this);

        }

        else if (anchorRect.HasValue && anchorRect != _currentAnchorRect)

        {

            _currentAnchorRect = anchorRect;

            _isTaskbarApp = isTaskbarApp;

            _mainWindow.OpenAnimation(this, alwaysBottom: true, anchorRect: _currentAnchorRect, isTaskbarApp: _isTaskbarApp);

        }



        _cts.Cancel();

        _cts = new CancellationTokenSource();

        var token = _cts.Token;



        try

        {

            while (!token.IsCancellationRequested)

            {

                await Task.Delay(100, token); // check if mouse is over every 100ms


                // update master volume again because it can be slow to update when coming from a hardware key press

                if (_isTaskbarApp)
                {
                    MainWindow.DismissTaskbarThumbnails(_currentAnchorRect);
                }

                ViewModel.SyncMasterFromDevice();



                // If expanded, do not auto-close on mouse leave; stay open until focus shifts

                if (ViewModel.IsExpanded)

                {

                    IntPtr fgHwnd = GetForegroundWindow();

                    GetWindowThreadProcessId(fgHwnd, out uint fgPid);

                    uint currentPid = (uint)Environment.ProcessId;

                    bool isOurProcess = (fgPid == currentPid && fgPid != 0);

                    bool mouseOverExpanded = WindowHelper.IsMouseOverWindow(this);



                    if (!isOurProcess && !mouseOverExpanded && (Environment.TickCount64 - _lastFlyoutTime > 1500))

                    {

                        _mainWindow.CloseAnimation(this);

                        _isHiding = true;

                        await Task.Delay(MainWindow.getDuration());

                        if (_isHiding == false) return;



                        WindowHelper.SetVisibility(this, false);

                        ViewModel.IsExpanded = false;

                        ViewModel.SetFlyoutApp(null);

                        _currentAnchorRect = null;

                        _isHiding = true;

                        break;

                    }

                    continue;

                }



                bool mouseOverThis = WindowHelper.IsMouseOverWindow(this);

                bool mouseOverMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout

                    && _mainWindow.Visibility == Visibility.Visible

                    && WindowHelper.IsMouseOverWindow(_mainWindow); // sync with media flyout



                if (!mouseOverThis && !mouseOverMedia)

                {

                    await Task.Delay(SettingsManager.Current.VolumeControlDuration, token);



                    mouseOverThis = WindowHelper.IsMouseOverWindow(this);

                    mouseOverMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout

                        && _mainWindow.Visibility == Visibility.Visible

                        && WindowHelper.IsMouseOverWindow(_mainWindow);



                    if (!mouseOverThis && !mouseOverMedia)

                    {

                        _mainWindow.CloseAnimation(this);

                        _isHiding = true;

                        await Task.Delay(MainWindow.getDuration());

                        if (_isHiding == false) return;



                        WindowHelper.SetVisibility(this, false);

                        ViewModel.IsExpanded = false;

                        ViewModel.SetFlyoutApp(null);

                        _currentAnchorRect = null;

                        _isHiding = true;

                        break;

                    }

                }

            }

        }

        catch (TaskCanceledException)

        {

            // do nothing

        }

    }



    private DateTime _lastTaskbarCloseTime = DateTime.MinValue;



    public async void ShowFromTaskbar(Rect anchorRect, Rect taskbarRect)

    {

        if ((DateTime.UtcNow - _lastTaskbarCloseTime).TotalMilliseconds < 150)

        {

            return;

        }



        if (_isTaskbarAnchored && !_isHiding)

        {

            HideTaskbarFlyout();

            return;

        }



        _cts.Cancel();

        _taskbarAnimationGeneration++;

        BeginAnimation(TopProperty, null);

        BeginAnimation(OpacityProperty, null);

        WindowHelper.SetVisibility(this, false);

        _isHiding = false;

        _isTaskbarAnchored = true;



        if (SettingsManager.Current.VolumeMixerAcrylicWindowEnabled)

        {

            WindowBlurHelper.EnableBlur(this);

        }

        else

        {

            WindowBlurHelper.DisableBlur(this);

        }



        Width = _normalWidth;

        ViewModel.SetFlyoutApp(null);

        ViewModel.RefreshDevices();

        ViewModel.RefreshSessions();

        ViewModel.OnPollTick(null, EventArgs.Empty);

        SetExpandedImmediately(true);

        PositionAtTaskbarAnchor(anchorRect, taskbarRect);



        AnimateTaskbarOpen();

        SetTaskbarActivation(true);

        IntPtr mixerHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        SetForegroundWindow(mixerHandle);



        _cts = new CancellationTokenSource();

        var token = _cts.Token;

        DateTime openTime = DateTime.UtcNow;

        uint currentPid = (uint)Environment.ProcessId;



        try

        {

            while (!token.IsCancellationRequested)

            {

                await Task.Delay(100, token);


                ViewModel.SyncMasterFromDevice();



                // Grace period on open so initial focus transition doesn't immediately close it

                if ((DateTime.UtcNow - openTime).TotalMilliseconds < 500)

                {

                    continue;

                }



                IntPtr fgHwnd = GetForegroundWindow();

                if (fgHwnd != mixerHandle)

                {

                    // Check if foreground belongs to our process (e.g. ComboBox popup or child window)

                    GetWindowThreadProcessId(fgHwnd, out uint fgPid);

                    bool isOurProcess = (fgPid == currentPid && fgPid != 0);

                    bool mouseOverThis = WindowHelper.IsMouseOverWindow(this);



                    if (!isOurProcess && !mouseOverThis)

                    {

                        HideTaskbarFlyout();

                        break;

                    }

                }

            }

        }

        catch (TaskCanceledException)

        {

            // A standard volume event or another taskbar click replaced this display mode.

        }

    }



    private void PositionAtTaskbarAnchor(Rect anchorRect, Rect taskbarRect)

    {

        var monitor = MonitorUtil.GetSelectedMonitor(SettingsManager.Current.TaskbarWidgetSelectedMonitor);

        Rect workArea = monitor.workArea;

        Rect monitorArea = monitor.monitorArea;



        WindowHelper.SetPosition(this, workArea.Left, workArea.Top);

        UpdateLayout();

        Rect windowRect = WindowHelper.GetPlacement(this);



        const double gap = 8;

        const double edgeMargin = 8;

        bool horizontalTaskbar = taskbarRect.Width >= taskbarRect.Height;

        double left;

        double top;



        if (horizontalTaskbar)

        {

            bool taskbarAtBottom = taskbarRect.Top + taskbarRect.Height / 2

                >= monitorArea.Top + monitorArea.Height / 2;

            _taskbarExpandDownward = !taskbarAtBottom;



            left = anchorRect.Left + anchorRect.Width / 2 - windowRect.Width / 2;

            top = taskbarAtBottom

                ? taskbarRect.Top - windowRect.Height - gap

                : taskbarRect.Bottom + gap;

        }

        else

        {

            bool taskbarAtRight = taskbarRect.Left + taskbarRect.Width / 2

                >= monitorArea.Left + monitorArea.Width / 2;

            _taskbarExpandDownward = anchorRect.Top + anchorRect.Height / 2

                < monitorArea.Top + monitorArea.Height / 2;



            left = taskbarAtRight

                ? taskbarRect.Left - windowRect.Width - gap

                : taskbarRect.Right + gap;

            top = anchorRect.Top + anchorRect.Height / 2 - windowRect.Height / 2;

        }



        left = Math.Clamp(left, monitorArea.Left + edgeMargin, monitorArea.Right - windowRect.Width - edgeMargin);

        top = Math.Clamp(top, monitorArea.Top + edgeMargin, monitorArea.Bottom - windowRect.Height - edgeMargin);

        WindowHelper.SetPosition(this, left, top);

    }



    private void ExitTaskbarMode()

    {

        _cts.Cancel();

        SetTaskbarActivation(false);

        _taskbarAnimationGeneration++;

        BeginAnimation(TopProperty, null);

        BeginAnimation(OpacityProperty, null);

        WindowHelper.SetVisibility(this, false);

        _isTaskbarAnchored = false;

        _isHiding = true;

        _lastFlyoutTime = 0;

        ViewModel.SetFlyoutApp(null);

        _currentAnchorRect = null;

        SetExpandedImmediately(false);

    }



    private async void HideTaskbarFlyout()

    {

        _lastTaskbarCloseTime = DateTime.UtcNow;

        _cts.Cancel();

        SetTaskbarActivation(false);

        _isTaskbarAnchored = false;

        _isHiding = true;

        int generation = ++_taskbarAnimationGeneration;



        int duration = MainWindow.getDuration();

        if (duration > 0)

        {

            double dpiScale = MonitorUtil.GetSelectedMonitor(SettingsManager.Current.TaskbarWidgetSelectedMonitor).dpiY / 96.0;

            double offset = (_taskbarExpandDownward ? -20 : 20) / dpiScale;

            EasingFunctionBase? easing = SettingsManager.Current.FlyoutAnimationEasingStyle == 0

                ? null

                : _mainWindow.getEasingStyle(false);



            BeginAnimation(TopProperty, new DoubleAnimation(Top, Top + offset, TimeSpan.FromMilliseconds(duration))

            {

                EasingFunction = easing

            });

            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(duration))

            {

                EasingFunction = easing

            });



            await Task.Delay(duration);

            if (generation != _taskbarAnimationGeneration)

                return;

        }



        WindowHelper.SetVisibility(this, false);

        BeginAnimation(TopProperty, null);

        BeginAnimation(OpacityProperty, null);

        Opacity = 1;

        SetExpandedImmediately(false);

        _isHiding = true;

    }



    private void SetTaskbarActivation(bool allowActivation)

    {

        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        int exStyle = GetWindowLong(handle, GWL_EXSTYLE);

        exStyle = allowActivation

            ? exStyle & ~WS_EX_NOACTIVATE

            : exStyle | WS_EX_NOACTIVATE;

        SetWindowLong(handle, GWL_EXSTYLE, exStyle);

    }



    private void AnimateTaskbarOpen()

    {

        int generation = ++_taskbarAnimationGeneration;

        int duration = MainWindow.getDuration();

        double dpiScale = MonitorUtil.GetSelectedMonitor(SettingsManager.Current.TaskbarWidgetSelectedMonitor).dpiY / 96.0;

        Rect placement = WindowHelper.GetPlacement(this);

        double targetTop = placement.Top / dpiScale;

        double offset = (_taskbarExpandDownward ? -20 : 20) / dpiScale;



        BeginAnimation(TopProperty, null);

        BeginAnimation(OpacityProperty, null);

        Top = targetTop;

        Opacity = duration == 0 ? 1 : 0;

        WindowHelper.SetVisibility(this, true);

        WindowHelper.SetTopmost(this);



        if (duration == 0)

            return;



        EasingFunctionBase? easing = SettingsManager.Current.FlyoutAnimationEasingStyle == 0

            ? null

            : _mainWindow.getEasingStyle(true);

        DoubleAnimation move = new(targetTop + offset, targetTop, TimeSpan.FromMilliseconds(duration))

        {

            EasingFunction = easing

        };

        DoubleAnimation fade = new(0, 1, TimeSpan.FromMilliseconds(duration))

        {

            EasingFunction = easing

        };

        fade.Completed += (_, _) =>

        {

            if (generation != _taskbarAnimationGeneration)

                return;



            BeginAnimation(TopProperty, null);

            BeginAnimation(OpacityProperty, null);

            Top = targetTop;

            Opacity = 1;

        };

        BeginAnimation(TopProperty, move);

        BeginAnimation(OpacityProperty, fade);

    }



    private void SetExpandedImmediately(bool expand)

    {

        _suppressExpandCollapseAnimation = true;

        ViewModel.IsExpanded = expand;

        _suppressExpandCollapseAnimation = false;



        BeginAnimation(TopProperty, null);

        BeginAnimation(HeightProperty, null);



        if (expand)

        {

            SessionsExpanded.Visibility = Visibility.Visible;

            SessionsSeparator.Visibility = Visibility.Visible;

            SessionsExpanded.Measure(new Size(ActualWidth > 0 ? ActualWidth : _normalWidth, double.PositiveInfinity));

            var monitor = MonitorUtil.GetSelectedMonitor(SettingsManager.Current.TaskbarWidgetSelectedMonitor);

            double maxAllowedHeight = Math.Max(200, monitor.workArea.Height - 60);

            Height = Math.Min(_collapsedHeight + SessionsExpanded.DesiredSize.Height, maxAllowedHeight);

        }

        else

        {

            SessionsExpanded.Visibility = Visibility.Collapsed;

            SessionsSeparator.Visibility = Visibility.Collapsed;

            Height = _collapsedHeight;

        }



        UpdateLayout();

    }



    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)

    {

        if (e.PropertyName == nameof(VolumeMixerViewModel.IsExpanded))

        {

            if (_suppressExpandCollapseAnimation)

                return;



            AnimateExpandCollapse(ViewModel.IsExpanded);

        }

    }



    // derived from gpkgpk/HideVolumeOSD: https://github.com/gpkgpk/HideVolumeOSD

    public static void EnsureVolumeOsdHidden()

    {

        if (!SettingsManager.Current.VolumeControlEnabled) return;



        _ = Task.Run(() =>

        {

            try

            {

                HideVolumeOsd();

            }

            catch (Exception ex)

            {

                Logger.Error(ex, "Failed to ensure volume OSD is hidden");

            }

        });

    }



    private static IntPtr FindNativeVolumeOsdWindow()

    {

        // Windows 10 / Legacy Windows OSD: NativeHWNDHost with child DirectUIHWND

        IntPtr hwndHost = IntPtr.Zero;

        while ((hwndHost = FindWindowEx(IntPtr.Zero, hwndHost, "NativeHWNDHost", "")) != IntPtr.Zero)

        {

            if (FindWindowEx(hwndHost, IntPtr.Zero, "DirectUIHWND", "") != IntPtr.Zero)

            {

                return hwndHost;

            }

        }



        return IntPtr.Zero;

    }



    private static void HideVolumeOsd()

    {

        if (_nativeOsdElement != IntPtr.Zero)

        {

            return;

        }



        IntPtr osdWindow = FindNativeVolumeOsdWindow();

        if (osdWindow == IntPtr.Zero)

        {

            return;

        }



        _nativeOsdElement = osdWindow;

        _nativeOsdOriginalExStyle = GetWindowLong(_nativeOsdElement, GWL_EXSTYLE);

        SetWindowLong(_nativeOsdElement, GWL_EXSTYLE,

            _nativeOsdOriginalExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);

        SetWindowPos(_nativeOsdElement, 0, -99999, -99999, 0, 0,

            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        ShowWindow(_nativeOsdElement, SW_MINIMIZE);

        Logger.Info("Successfully hid legacy volume OSD.");

    }



    public static void ShowVolumeOsd()

    {

        if (_nativeOsdElement == IntPtr.Zero)

        {

            return;

        }



        SetWindowLong(_nativeOsdElement, GWL_EXSTYLE, _nativeOsdOriginalExStyle);

        SetWindowPos(_nativeOsdElement, 0, 0, 0, 0, 0,

            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        ShowWindow(_nativeOsdElement, SW_RESTORE);

        _nativeOsdElement = IntPtr.Zero;

        Logger.Info("Successfully restored native volume OSD.");

    }



    private void AnimateExpandCollapse(bool expand)

    {

        int msDuration = MainWindow.getDuration();

        var easing = msDuration > 0 ? _mainWindow.getEasingStyle(true) : null;

        var duration = new Duration(TimeSpan.FromMilliseconds(msDuration > 0 ? msDuration / 1.4 : 1));



        bool isTop = _isTaskbarAnchored && _taskbarExpandDownward;



        // check if the media flyout is at the top or bottom of the screen if applicable

        if (!_isTaskbarAnchored && SettingsManager.Current.VolumeControlAboveMediaFlyout)

        {

            isTop = SettingsManager.Current.Position switch

            {

                3 or 4 or 5 => true,

                _ => false

            };

        }



        double expandedHeight;

        if (expand)

        {

            SessionsExpanded.Visibility = Visibility.Visible;

            SessionsSeparator.Visibility = Visibility.Visible;

            SessionsExpanded.UpdateLayout();

        }



        // measure desired size

        SessionsExpanded.Measure(new Size(ActualWidth > 0 ? ActualWidth : _normalWidth, double.PositiveInfinity));

        var monitor = MonitorUtil.GetSelectedMonitor(SettingsManager.Current.TaskbarWidgetSelectedMonitor);

        double maxAllowedHeight = Math.Max(200, monitor.workArea.Height - 60);

        expandedHeight = Math.Min(_collapsedHeight + SessionsExpanded.DesiredSize.Height, maxAllowedHeight);



        double targetHeight = expand ? expandedHeight : _collapsedHeight;

        double currentHeight = ActualHeight;

        double heightDelta = targetHeight - currentHeight;



        // When at the top, chevron points down (0°) when collapsed and up (180°) when expanded.

        // When at the bottom, chevron points up (180°) when expanded and down (0°) when collapsed.

        var chevronAnimation = new DoubleAnimation

        {

            To = isTop ? (expand ? 0 : 180) : (expand ? 180 : 0),

            Duration = duration,

            EasingFunction = easing

        };

        Dispatcher.Invoke(() =>

        {

            ChevronRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, chevronAnimation);

        });



        var heightAnimation = new DoubleAnimation

        {

            From = currentHeight,

            To = targetHeight,

            Duration = duration,

            EasingFunction = easing

        };



        // When at the top, the window grows downward so Top stays fixed.

        // When at the bottom, the window grows upward so Top shifts up by heightDelta.

        var topAnimation = new DoubleAnimation

        {

            From = Top,

            To = isTop ? Top : Top - heightDelta,

            Duration = duration,

            EasingFunction = easing

        };



        if (!expand)

        {

            heightAnimation.Completed += (s, e) =>

            {

                SessionsExpanded.Visibility = Visibility.Collapsed;

                SessionsSeparator.Visibility = Visibility.Collapsed;

            };

        }



        Dispatcher.Invoke(() =>

        {

            BeginAnimation(TopProperty, topAnimation);

            BeginAnimation(HeightProperty, heightAnimation);

        });

    }



    private void Window_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)

    {

        if (SessionsScrollViewer != null && (SessionsScrollViewer.IsMouseOver || (SessionsExpanded != null && SessionsExpanded.IsMouseOver)))

        {

            double offset = SessionsScrollViewer.VerticalOffset - (e.Delta / 120.0) * 48.0;

            SessionsScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SessionsScrollViewer.ScrollableHeight));

            e.Handled = true;

            return;

        }



        e.Handled = true;

        float stepFraction = (SettingsManager.Current.VolumeScrollStep > 0 ? SettingsManager.Current.VolumeScrollStep : 2) / 100f;

        float step = (e.Delta > 0 ? 1 : -1) * stepFraction;



        if (ViewModel.IsAppFlyoutActive)

        {

            ViewModel.AdjustActiveAppVolume(step);

        }

        else

        {

            ViewModel.AdjustMasterVolume(step);

        }



        if (!_isHiding && !_isTaskbarAnchored && !ViewModel.IsExpanded)

        {

            ShowFlyout();

        }

    }



    private void SessionsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)

    {

        if (SessionsScrollViewer != null)

        {

            double offset = SessionsScrollViewer.VerticalOffset - (e.Delta / 120.0) * 48.0;

            SessionsScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SessionsScrollViewer.ScrollableHeight));

            e.Handled = true;

        }

    }



    private void SessionRow_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)

    {

        if (sender is FrameworkElement fe && fe.DataContext is AudioSessionModel session)

        {

            e.Handled = true;

            float step = (SettingsManager.Current.VolumeScrollStep > 0 ? SettingsManager.Current.VolumeScrollStep : 2) / 100f;

            session.AdjustVolume(e.Delta > 0 ? step : -step);

        }

    }



}

