using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Widgets;
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private readonly ObservableCollection<DockApp> _pinnedApps = [];
    private readonly ObservableCollection<DockApp> _runningApps = [];
    private readonly ObservableCollection<DockWidgetItem> _widgets = [];
    private readonly DockWidgetService _widgetService = new(TimeProvider.System);
    private readonly WidgetPanelHost _widgetPanel;
    private readonly DispatcherTimer _timedWidgetRefreshTimer;
    private readonly HashSet<string> _exitingRunningPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ApplicationPreferences _preferences = ((App)Application.Current).Preferences;
    private SettingsWindow? _settingsWindow;
    private readonly ApplicationJumpListService _jumpLists = new();
    private CancellationTokenSource? _jumpListLoad;
    private long? _jumpListSession;
    private readonly DispatcherTimer _previewHoverTimer;
    private readonly DispatcherTimer _previewCloseTimer;
    private Button? _pendingPreviewButton;
    private readonly DispatcherTimer _widgetRefreshTimer;
    private readonly DispatcherTimer _iconRetryTimer;
    private readonly RunningAppIconCache _runningAppIcons = new();
    private readonly WindowThumbnailPreview _thumbnailPreview;
    private readonly Dictionary<string, nint> _lastSelectedWindows = new(StringComparer.OrdinalIgnoreCase);
    private DockPosition _dockPosition = DockPosition.Left;
    private NativeWindowTracker? _windowTracker;
    private TaskbarReplacementSession? _replacementSession;
    private HwndSource? _source;
    private nint _foregroundWindow;
    private readonly List<FrameworkElement> _magnifiedIcons = [];
    private ContextMenu? _openContextMenu;
    private bool _contextTargetWasForeground;
    private nint _foregroundAtMenuOpen;
    private DateTime _contextMenuOpenedAtUtc;
    private Size _dockArea;
    private nint? _foregroundAtAppPress;
    private uint _taskbarCreatedMessage;
    private Point _dragStartPoint;
    private readonly HashSet<FrameworkElement> _displacedIcons = [];
    private DockApp? _draggedApp;
    private DockAutoHideController? _autoHideController;
    private double? _dockWidthOverride;
    private bool _resizingDock;
    private bool _resizePending;
    private bool _resizingFromLeft;
    private readonly bool _isPreview = ((App)Application.Current).IsPreview;
    private readonly bool _isInspection = ((App)Application.Current).IsInspection;
    // Failures are written to the log only; the dock never displays error popups.
    private readonly WidgetCoordinator _widgetCoordinator;
    private readonly Dictionary<string, FrameworkElement> _dockItemElements = new(StringComparer.OrdinalIgnoreCase);
    private bool _rebuildingDockOrder;
    private bool _instantDockResize;
    private double _dockTopGap;
    private (DockSize Size, double Scale)? _sizePreviewOverride;
    private AudioEndpointMonitor.SharedHandle? _wheelAudio;
    private int _wheelRemainder;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ToolTip),
            System.Windows.Controls.ToolTip.OpenedEvent,
            new RoutedEventHandler(OnToolTipOpened));
    }

    private static void OnToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ToolTip tooltip) return;

        void Elevate()
        {
            if (PresentationSource.FromVisual(tooltip) is HwndSource source && source.Handle != nint.Zero)
            {
                SetWindowPos(source.Handle, new nint(-1), 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
        }

        Elevate();
        tooltip.Dispatcher.BeginInvoke((Action)Elevate, DispatcherPriority.Loaded);
    }

    public MainWindow()
    {
        InitializeComponent();
        DockContents.LayoutUpdated += (_, _) => InvalidateMagnificationTargets();
        _widgetPanel = new WidgetPanelHost(this);
        _widgetPanel.IsOpenChanged += WidgetPanel_IsOpenChanged;
        _widgetPanel.OperationFailed += ReportError;
        ShowInTaskbar = _isPreview || _isInspection;
        if (((App)Application.Current).OpenSettingsOnStartup)
            Loaded += (_, _) => OpenSettings_Click(this, new RoutedEventArgs());
        PinnedApps.ItemsSource = _pinnedApps;
        RunningApps.ItemsSource = _runningApps;
        DockWidgets.ItemsSource = _widgets;
        _thumbnailPreview = new WindowThumbnailPreview((Style)FindResource("PreviewCloseButton"));
        _previewHoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _previewHoverTimer.Tick += PreviewHoverTimer_Tick;
        _previewCloseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _previewCloseTimer.Tick += PreviewCloseTimer_Tick;
        _widgetRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _widgetRefreshTimer.Tick += WidgetRefreshTimer_Tick;
        _iconRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = RunningAppIconCache.RetryDelay
        };
        _iconRetryTimer.Tick += IconRetryTimer_Tick;
        _timedWidgetRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timedWidgetRefreshTimer.Tick += TimedWidgetRefreshTimer_Tick;
        _widgetCoordinator = new WidgetCoordinator(
            WidgetStrip, Dispatcher, ReportError, lazy: true,
            canExpandInline: CanExpandWidgetInline,
            onHoverLayoutChanged: _ => ResizeDockToContent())
        {
            OrderChanged = OnWidgetOrderChanged
        };
        _widgetCoordinator.CloseApplicationRequested += CloseApplication;
        _widgetCoordinator.TerminateApplicationRequested += TerminateApplication;
        _widgetCoordinator.WidgetRemoveRequested += RemoveDockWidget;
        _widgetCoordinator.ConfigureWidgetMenu = menu =>
        {
            menu.Opened += DockContextMenu_Opened;
            menu.Closed += DockContextMenu_Closed;
        };
        _widgetCoordinator.HoverActiveChanged += _ => _autoHideController?.EvaluateState();
        _widgetCoordinator.ExternalLayout = true;
        _widgetCoordinator.WidgetsPlaced += () =>
        {
            ApplyStartPlacement();
            RebuildDockOrder();
        };
        Deactivated += (_, _) => _widgetCoordinator.CollapseHover();
        _widgetCoordinator.AvailabilityChanged += () =>
        {
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        };
        ApplyWidgets(_preferences.Current.Widgets);
        _thumbnailPreview.PointerEntered += ThumbnailPreview_PointerEntered;
        _thumbnailPreview.PointerLeft += ThumbnailPreview_PointerLeft;
        _thumbnailPreview.PreviewClicked += ThumbnailPreview_PreviewClicked;
        _thumbnailPreview.CloseRequested += ThumbnailPreview_CloseRequested;
        _dockPosition = _preferences.Current.Position;
        _thumbnailPreview.AnimationsEnabled = _preferences.Current.AnimationsEnabled;
        _preferences.Changed += Preferences_Changed;
        ShellShortcuts.IsMediaWidgetActive = () =>
            _preferences.Current.Widgets?.Contains("Media", StringComparer.OrdinalIgnoreCase) == true
            || _preferences.Current.ShowMedia;
        // App.OnStartup has already selected the theme pack; later changes come through ApplicationPreferences.Save.
        ThemePacks.Changed += ThemePacks_Changed;
        Closed += (_, _) => ThemePacks.Changed -= ThemePacks_Changed;
        UpdatePreferredIconPixels();
        ApplyStartIcon();
        LoadPinnedApps(_preferences.Current.PinOrder);
        if (_preferences.LoadError is not null)
        {
            ReportError(_preferences.LoadError);
        }

        _widgetCoordinator.Apply(_preferences.Current);
        InitializeExtras();
        RebuildDockOrder();
        ApplySizeProfile();
        ApplyLayoutMode();
        PositionDock();
        NotificationService.UpdateRequested += OnUpdateNotificationRequested;
        Closed += (_, _) => NotificationService.UpdateRequested -= OnUpdateNotificationRequested;
    }

    private void OnUpdateNotificationRequested()
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (_availableUpdate is { } release)
            {
                await StartUpdateAsync(release);
            }
        });
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        if (source is null)
        {
            ReportError("ErrorDockAttach");
            return;
        }

        _source = source;
        _source.AddHook(WindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (!_isPreview && !_isInspection && !ExcludeFromTaskSwitcher(source.Handle))
        {
            ReportError("ErrorAltTabExclude");
        }

        _windowTracker = new NativeWindowTracker(Dispatcher);
        _windowTracker.WindowsChanged += WindowTracker_WindowsChanged;
        _windowTracker.ForegroundWindowChanged += WindowTracker_ForegroundWindowChanged;
        try
        {
            _windowTracker.Start(source.Handle);
            if (!_windowTracker.NotificationsAvailable)
            {
                ReportError("ErrorNotificationBadge");
            }
        }
        catch (InvalidOperationException ex)
        {
            ReportError(ex.Message);
            _windowTracker.Dispose();
            _windowTracker = null;
        }

        _autoHideController = new DockAutoHideController(
            this,
            source.Handle,
            DockSurfaceTranslate,
            DockSurface,
            () => _widgetPanel.IsOpen || (_openContextMenu?.IsOpen == true) || _thumbnailPreview.IsVisible
                || DockScrollViewer.IsPanning || LeftResizeThumb.IsDragging || RightResizeThumb.IsDragging
                || _draggedApp is not null
                || (_settingsWindow is { IsVisible: true, WindowState: not WindowState.Minimized })
                || DockFlyout.IsAnyOpen || _keyboardNavigationActive);
        _autoHideController.DockBoundsProvider = GetDockPhysicalBounds;
        _autoHideController.AnimationsEnabled = _preferences.Current.AnimationsEnabled;
        _autoHideController.SetMode(_preferences.Current.AutoHide);
        ApplyBackdropHitTesting();

        // Dev mode uses the real taskbar replacement so the dock sits exactly where the taskbar is,
        // just like release. Only the data hot-reload service differs.
        if (!_isPreview) StartReplacementMode();
        InitializeFeatures();
#if DEBUG
        // Verifies crash recovery: the unhandled-exception handlers must restore the Windows taskbar.
        if (Environment.GetCommandLineArgs().Contains("--crash-test"))
        {
            var crashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            crashTimer.Tick += (_, _) => throw new InvalidOperationException("Crash test requested with --crash-test.");
            crashTimer.Start();
        }

        // Opens Settings at startup so its XAML and bindings can be exercised without clicking the dock.
        if (Environment.GetCommandLineArgs().Contains("--open-settings"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => OpenSettings_Click(this, new RoutedEventArgs()));
        }
#endif
    }

    private (int Left, int Top, int Right, int Bottom)? GetDockPhysicalBounds()
    {
        if (_source is null || _source.Handle == 0 || !IsLoaded || DockFrame.ActualWidth <= 0 || DockFrame.ActualHeight <= 0)
            return null;
        try
        {
            var topLeft = DockFrame.PointToScreen(new Point(0, 0));
            var bottomRight = DockFrame.PointToScreen(new Point(DockFrame.ActualWidth, DockFrame.ActualHeight));
            // Subtract DockSurfaceTranslate so auto-hide overlap checks always test the dock's resting position.
            var matrix = _source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var offsetX = DockSurfaceTranslate.X * matrix.M11;
            var offsetY = DockSurfaceTranslate.Y * matrix.M22;
            return (
                (int)Math.Floor(topLeft.X - offsetX),
                (int)Math.Floor(topLeft.Y - offsetY),
                (int)Math.Ceiling(bottomRight.X - offsetX),
                (int)Math.Ceiling(bottomRight.Y - offsetY));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void PositionDock()
    {
        if (_replacementSession?.IsActive == true)
        {
            return;
        }

        var targetDisplay = DisplayService.GetTargetDisplay(_preferences.Current.TargetDisplay);
        var workArea = targetDisplay.WorkArea;
        if (_isPreview)
        {
            _dockArea = new Size(workArea.Width, Height);
            Width = workArea.Width;
        }
        ResizeDockToContent();
        ApplyDockAlignment();
        Left = _dockPosition switch
        {
            DockPosition.Left => workArea.Left,
            DockPosition.Center => workArea.Left + (workArea.Width - Width) / 2,
            DockPosition.Right => workArea.Right - Width,
            _ => workArea.Left
        };
        Top = Math.Max(workArea.Top, workArea.Bottom - Height);
    }

    private void StartReplacementMode()
    {
        var source = _source;
        if (source is null)
        {
            ReportError("ErrorDockNotReady");
            return;
        }

        ResizeDockToContent();
        ApplyDockAlignment();
        var targetDisplay = DisplayService.GetTargetDisplay(_preferences.Current.TargetDisplay);
        var session = new TaskbarReplacementSession(this, source.Handle, _preferences.Current.AutoHide != AutoHideMode.Disabled);
        session.OperationReported += ReportOperationStatus;
        session.LayoutChanged += ApplyTaskbarLayout;
        session.FullScreenChanged += OnFullScreenChanged;
        session.ShelfHeight = Profile.Shelf;
        session.Edge = Edge;
        session.TargetMonitor = targetDisplay.Handle;
        _replacementSession = session;
        try
        {
            session.Start();
        }
        catch (InvalidOperationException ex)
        {
            session.Dispose();
            if (!session.IsActive) _replacementSession = null;
            PositionDock();
            ReportError(ex.Message);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && unchecked((uint)message) == _taskbarCreatedMessage)
        {
            _replacementSession?.HandleTaskbarCreated();
            handled = true;
            return IntPtr.Zero;
        }

        // Keep the application's foreground state intact on left/middle clicks until the dock command runs.
        // Right-clicks open a ContextMenu, which needs window activation so WPF Popup mouse capture and focus stay open.
        if (message == WmMouseActivate)
        {
            var mouseMessage = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
            const int wmRButtonDown = 0x0204;
            const int wmRButtonUp = 0x0205;
            if (mouseMessage != wmRButtonDown && mouseMessage != wmRButtonUp)
            {
                handled = true;
                return new IntPtr(MaNoActivate);
            }

            var currentFg = NativeWindowTracker.GetForegroundApplicationWindow();
            if (currentFg != 0) _foregroundWindow = currentFg;
        }

        // WM_DISPLAYCHANGE (displays added, removed, resized) or WM_SETTINGCHANGE/SPI_SETWORKAREA (taskbar strips
        // reserved or released) move the secondary docks.
        if (message == 0x007E || (message == 0x001A && wParam == 0x002F))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplySecondaryDocks);
        }

        if (HandleHotkeyMessage(message, wParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (_replacementSession?.HandleMessage(unchecked((uint)message), wParam, lParam) == true)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void LoadPinnedApps(IReadOnlyList<string>? preferredOrder = null)
    {
        var result = WindowsTaskbarPinImporter.Import();
        var apps = result.Apps.ToList();
        foreach (var app in apps)
        {
            app.Icon = ThemePacks.ApplyIcon(ResolvePinnedIcon(app), app.ExecutablePath);
        }

        if (preferredOrder is { Count: > 0 })
        {
            var orderMap = preferredOrder
                .Select((path, index) => (path, index))
                .ToDictionary(pair => pair.path, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            apps.Sort((a, b) =>
            {
                var hasA = orderMap.TryGetValue(a.ExecutablePath, out var indexA);
                var hasB = orderMap.TryGetValue(b.ExecutablePath, out var indexB);
                if (hasA && hasB) return indexA.CompareTo(indexB);
                if (hasA) return -1;
                if (hasB) return 1;
                return 0;
            });
        }

        foreach (var app in apps)
        {
            _pinnedApps.Add(app);
        }

        if (result.ErrorKey is not null)
        {
            ReportError(result.ErrorKey);
        }
        else if (result.SkippedCount > 0) ReportError("SkippedPins", true, result.SkippedCount);
        RebuildDockOrder();
    }

    // A pin's own icon location wins (that's the artwork the Windows taskbar showed); the executable's
    // resource icon is the fallback so the dock never substitutes a generic image for the real one.
    private static BitmapSource? ResolvePinnedIcon(DockApp app)
    {
        if (app.IconLocation is { } location)
        {
            var comma = location.LastIndexOf(',');
            var file = Environment.ExpandEnvironmentVariables((comma >= 0 ? location[..comma] : location).Trim().Trim('"'));
            var index = comma >= 0 && int.TryParse(location[(comma + 1)..].Trim(), out var parsed) ? parsed : 0;
            if (File.Exists(file)
                && (ApplicationIconProvider.GetForResource(file, index) ?? ApplicationIconProvider.GetForFile(file)) is { } icon)
            {
                return icon;
            }
        }

        return ApplicationIconProvider.GetForFile(app.ExecutablePath);
    }

    private void WindowTracker_WindowsChanged(object? sender, IReadOnlyList<TrackedWindow> windows)
    {
        _runningAppIcons.Retain(windows);
        windows = FilterWindows(windows);
        var foreground = NativeWindowTracker.GetForegroundApplicationWindow();
        if (foreground != 0)
        {
            _replacementSession?.UpdateForeground(foreground);
            _autoHideController?.CheckForegroundWindow(foreground);
        }
        var trackedPaths = windows.Where(window => window.ExecutablePath is not null)
            .Select(window => window.ExecutablePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _lastSelectedWindows.Keys.Where(path => !trackedPaths.Contains(path)).ToList())
        {
            _lastSelectedWindows.Remove(path);
        }
        UpdateNotificationBadges(_pinnedApps, windows);
        UpdateWindowIndicators(windows);
        if (UpdateRunningApps(windows))
        {
            RebuildDockOrder();
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        }

        UpdateSecondaryDocks();
        // Store app windows often publish their identity shortly after they appear, without another window event.
        if (_runningAppIcons.HasPendingLookups && !_iconRetryTimer.IsEnabled)
        {
            _iconRetryTimer.Start();
        }

        if (_thumbnailPreview.IsVisible)
        {
            var activeHandles = _thumbnailPreview.ActiveHandles;
            if (activeHandles.Count > 0 && !activeHandles.Any(h => windows.Any(w => w.Handle == h)))
            {
                _thumbnailPreview.Hide();
            }
        }
    }

    private void IconRetryTimer_Tick(object? sender, EventArgs e)
    {
        _iconRetryTimer.Stop();
        if (_windowTracker is not null)
        {
            WindowTracker_WindowsChanged(this, _windowTracker.Snapshot);
        }
    }

    private void UpdateNotificationBadges(
        ObservableCollection<DockApp> apps,
        IReadOnlyList<TrackedWindow> windows)
    {
        var notificationPaths = _preferences.Current.NotificationBadgesEnabled
            ? windows.Where(window => window.HasNotification && window.ExecutablePath is not null)
                .Select(window => window.ExecutablePath!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            var hasNotification = notificationPaths.Contains(app.ExecutablePath);
            if (app.HasNotification != hasNotification)
            {
                app.HasNotification = hasNotification;
            }
        }
    }

    private bool UpdateRunningApps(IReadOnlyList<TrackedWindow> windows)
    {
        // Apps hidden by the active profile still show up among running apps while they have windows.
        var pinnedPaths = _pinnedApps.Where(app => !IsHiddenByProfile(app)).Select(app => app.ExecutablePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = windows
            .Where(window => window.ExecutablePath is not null
                && File.Exists(window.ExecutablePath)
                && !pinnedPaths.Contains(window.ExecutablePath))
            .ToList();
        var desiredPaths = candidates.Select(window => window.ExecutablePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var executablePath in desiredPaths)
        {
            CancelRunningAppExit(executablePath);
        }

        var currentApps = _runningApps.ToDictionary(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        var desiredApps = candidates
            .GroupBy(window => window.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var executablePath = group.Key;
                var hasNotification = _preferences.Current.NotificationBadgesEnabled && group.Any(window => window.HasNotification);
                // The topmost window decides identity icons, as ApplicationFrameHost hosts several Store apps.
                var icon = _runningAppIcons.Get(executablePath, group.First().Handle);
                var isForeground = group.Any(window => window.Handle == _foregroundWindow);
                var windowCount = group.Count();
                if (currentApps.TryGetValue(executablePath, out var existingApp))
                {
                    existingApp.Icon = icon;
                    existingApp.HasNotification = hasNotification;
                    existingApp.IsForeground = isForeground;
                    existingApp.WindowCount = windowCount;
                    return existingApp;
                }

                return new DockApp(GetApplicationName(executablePath), executablePath)
                {
                    Icon = icon,
                    HasNotification = hasNotification,
                    IsForeground = isForeground,
                    WindowCount = windowCount
                };
            })
            .OrderBy(app =>
            {
                var existingIndex = -1;
                for (var i = 0; i < _runningApps.Count; i++)
                {
                    if (string.Equals(_runningApps[i].ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = i;
                        break;
                    }
                }
                return existingIndex >= 0 ? existingIndex : int.MaxValue;
            })
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var layoutChanged = !_runningApps.Select(app => app.ExecutablePath)
            .SequenceEqual(desiredApps.Select(app => app.ExecutablePath), StringComparer.OrdinalIgnoreCase);

        for (var desiredIndex = 0; desiredIndex < desiredApps.Count; desiredIndex++)
        {
            var desiredApp = desiredApps[desiredIndex];
            if (desiredIndex < _runningApps.Count
                && string.Equals(_runningApps[desiredIndex].ExecutablePath, desiredApp.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                if (_runningApps[desiredIndex] != desiredApp)
                {
                    _runningApps[desiredIndex] = desiredApp;
                }

                continue;
            }

            var existingIndex = -1;
            for (var index = desiredIndex + 1; index < _runningApps.Count; index++)
            {
                if (string.Equals(_runningApps[index].ExecutablePath, desiredApp.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _runningApps.Move(existingIndex, desiredIndex);
                if (_runningApps[desiredIndex] != desiredApp)
                {
                    _runningApps[desiredIndex] = desiredApp;
                }
            }
            else
            {
                _runningApps.Insert(desiredIndex, desiredApp);
            }
        }

        for (var index = _runningApps.Count - 1; index >= desiredApps.Count; index--)
        {
            AnimateRunningAppRemoval(_runningApps[index]);
        }

        RunningAppsDivider.Visibility = Visibility.Collapsed;
        UpdateExtrasDividerVisibility();
        return layoutChanged;
    }

    private void CancelRunningAppExit(string executablePath)
    {
        if (!_exitingRunningPaths.Remove(executablePath))
        {
            return;
        }

        var app = _runningApps.FirstOrDefault(candidate =>
            string.Equals(candidate.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
        var button = app is null ? null : FindVisualChild<Button>(RunningApps.ItemContainerGenerator.ContainerFromItem(app));
        if (button is null) return;

        button.BeginAnimation(UIElement.OpacityProperty, null);
        button.Opacity = 1;
        button.IsHitTestVisible = true;
        if (button.RenderTransform is ScaleTransform { IsFrozen: false, IsSealed: false } scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    private void AnimateRunningAppRemoval(DockApp app)
    {
        if (!_exitingRunningPaths.Add(app.ExecutablePath))
        {
            return;
        }

        AnimateDockAppRemoval(RunningApps, _runningApps, app, () =>
        {
            _exitingRunningPaths.Remove(app.ExecutablePath);
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        });
    }

    private void AnimateDockAppRemoval(
        ItemsControl owner,
        ObservableCollection<DockApp> apps,
        DockApp app,
        Action afterRemoval)
    {
        var button = FindVisualChild<Button>(owner.ItemContainerGenerator.ContainerFromItem(app));
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation || button?.RenderTransform is not ScaleTransform { IsFrozen: false, IsSealed: false } scale)
        {
            RemoveDockApp(apps, app);
            afterRemoval();
            return;
        }

        button.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(75))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(owner, RunningApps) && !_exitingRunningPaths.Contains(app.ExecutablePath))
            {
                return;
            }
            RemoveDockApp(apps, app);
            afterRemoval();
        };
        button.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(75))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(75))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void RemoveDockApp(ObservableCollection<DockApp> apps, DockApp app)
    {
        var index = apps.IndexOf(app);
        if (index >= 0)
        {
            apps.RemoveAt(index);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void DockAppButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation || sender is not Button button)
        {
            return;
        }

        var scale = new ScaleTransform();
        button.RenderTransform = scale;
        // Keep the new application visible on its first rendered frame.
        button.Opacity = 1;
        scale.ScaleX = 0.95;
        scale.ScaleY = 0.95;
        var duration = TimeSpan.FromMilliseconds(165);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = easing });
    }

    private void DockApp_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        _foregroundAtAppPress = null;
    }

    private void DockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp app })
        {
            return;
        }

        var foregroundAtPress = _foregroundAtAppPress;
        _foregroundAtAppPress = null;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        AnimateActivationFeedback((Button)sender);
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            LaunchApp(app, elevated: modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control));
            return;
        }

        OpenDockApp(app, toggleIfForeground: true, foregroundAtPress);
    }

    private void DockApp_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle || sender is not Button { DataContext: DockApp app }) return;
        e.Handled = true;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        AnimateActivationFeedback((Button)sender);
        LaunchApp(app, elevated: false);
    }

    /// <summary>Wheel over an app cycles through its windows; elsewhere on the dock it changes the volume.</summary>
    private void DockApp_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp app }) return;
        e.Handled = true;
        var windows = VisibleSnapshot
            .Where(window => string.Equals(window.ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(window => window.Handle.ToInt64())
            .ToList();
        if (windows is not { Count: > 0 }) return;
        var current = windows.FindIndex(window => window.Handle == _foregroundWindow);
        if (current < 0 && _lastSelectedWindows.TryGetValue(app.ExecutablePath, out var last))
            current = windows.FindIndex(window => window.Handle == last);
        var step = e.Delta < 0 ? 1 : -1;
        var next = windows[((current < 0 ? (step > 0 ? -1 : 0) : current) + step + windows.Count) % windows.Count];
        _thumbnailPreview.Hide();
        if (NativeWindowTracker.Activate(next.Handle)) _lastSelectedWindows[app.ExecutablePath] = next.Handle;
    }

    private void DockSurface_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || !_preferences.Current.WheelVolume) return;
        e.Handled = true;
        try
        {
            _wheelAudio ??= AudioEndpointMonitor.AcquireShared();
        }
        catch (COMException ex)
        {
            AppLog.Warning($"Core Audio unavailable for wheel volume: {ex.Message}");
            return;
        }

        if (!_wheelAudio.HasDevice) return;
        // One wheel notch (120) = 2%. Touchpads send many small deltas; accumulate them instead of 2% per event.
        _wheelRemainder += e.Delta;
        var notches = _wheelRemainder / 120;
        if (notches == 0) return;
        _wheelRemainder -= notches * 120;
        var level = Math.Clamp(_wheelAudio.Volume + notches * 0.02f, 0f, 1f);
        if (!_wheelAudio.SetVolume(level)) ReportError("ErrorVolumeChange");
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (_preferences.Current.StartAction == StartButtonAction.Launcher)
        {
            ToggleLauncher();
            return;
        }

        const uint wmSysCommand = 0x0112;
        const int scTaskList = 0xF130;
        if (_source is null || !PostMessage(_source.Handle, wmSysCommand, scTaskList, 0))
        {
            ReportError("ErrorStartMenuOpen");
        }
    }

    private void OpenDockApp(DockApp app, bool toggleIfForeground = false, nint? foregroundAtPress = null)
    {
        var executablePath = app.ExecutablePath;
        var existingWindow = foregroundAtPress is { } pressedWindow
            ? _windowTracker?.Snapshot.FirstOrDefault(window => window.Handle == pressedWindow
                && string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            : null;
        existingWindow ??= FindNextWindowForExecutable(executablePath);
        if (existingWindow is not null)
        {
            var activated = toggleIfForeground
                ? NativeWindowTracker.ToggleActivation(existingWindow.Handle, executablePath, foregroundAtPress)
                : NativeWindowTracker.Activate(existingWindow.Handle);
            if (!activated)
            {
                ReportError("ErrorWindowActivate");
                return;
            }

            _lastSelectedWindows[executablePath] = existingWindow.Handle;
            return;
        }

        LaunchApp(app, elevated: false);
    }

    /// <summary>Starts a new instance even when the app already has windows (Shift/middle click, Ctrl+Shift for admin).</summary>
    private void LaunchApp(DockApp app, bool elevated)
    {
        // The pinned shortcut is the most faithful launch: ShellExecute resolves its target, which also
        // covers packaged apps whose install path File.Exists cannot see. A dead click is worse than a
        // slower one, so the executable path is only the fallback.
        if (!string.IsNullOrEmpty(app.ShortcutPath) && File.Exists(app.ShortcutPath)
            && TryLaunch(app.ShortcutPath, null, elevated))
        {
            return;
        }

        var executablePath = app.ExecutablePath;
        if (!File.Exists(executablePath))
        {
            ReportError("ErrorAppFileNotFound");
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(executablePath)
            {
                Arguments = app.LaunchArguments ?? string.Empty,
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty
            });
        }
        catch (Win32Exception ex) when (elevated && ex.NativeErrorCode == 1223)
        {
            // The user declined the UAC prompt.
        }
        catch (Win32Exception)
        {
            ReportError("ErrorAppLaunchWin32");
        }
        catch (InvalidOperationException)
        {
            ReportError("ErrorAppLaunchFailed");
        }
        catch (UnauthorizedAccessException)
        {
            ReportError("ErrorAppLaunchDenied");
        }
        catch (IOException)
        {
            ReportError("ErrorAppFileInaccessible");
        }
    }

    private static bool TryLaunch(string path, string? arguments, bool elevated)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path)
            {
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty
            });
            return true;
        }
        catch (Win32Exception ex) when (elevated && ex.NativeErrorCode == 1223)
        {
            return true; // The user declined the UAC prompt.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning($"Launch failed for {path}: {ex.Message}");
            return false;
        }
    }

    private void DockApp_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCloseTimer.Stop();
        _previewHoverTimer.Stop();
        _pendingPreviewButton = null;
        if (_widgetPanel.IsOpen) return;
        if (!_preferences.Current.PreviewsEnabled)
        {
            if (sender is Button button) ToolTipService.SetIsEnabled(button, true);
            _thumbnailPreview.Hide();
            return;
        }
        if (sender is not Button { Tag: string executablePath } hoveredButton)
        {
            return;
        }

        var matchingWindows = VisibleSnapshot
            .Where(window => string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        ToolTipService.SetIsEnabled(hoveredButton, matchingWindows.Count == 0);
        if (matchingWindows.Count == 0)
        {
            _thumbnailPreview.Hide();
            return;
        }

        _pendingPreviewButton = hoveredButton;
        _previewHoverTimer.Interval = TimeSpan.FromMilliseconds(_thumbnailPreview.IsVisible ? 140 : 300);
        _previewHoverTimer.Start();
    }

    private void PreviewHoverTimer_Tick(object? sender, EventArgs e)
    {
        _previewHoverTimer.Stop();
        var hoveredButton = _pendingPreviewButton;
        _pendingPreviewButton = null;
        if (hoveredButton is null || !hoveredButton.IsMouseOver || _widgetPanel.IsOpen || !_preferences.Current.PreviewsEnabled)
        {
            return;
        }
        if (hoveredButton.Tag is not string executablePath)
        {
            return;
        }

        var matchingWindows = VisibleSnapshot
            .Where(window => string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        if (matchingWindows.Count == 0)
        {
            _thumbnailPreview.Hide();
            return;
        }

        var appIcon = hoveredButton.DataContext is DockApp app ? app.Icon : null;
        _thumbnailPreview.ShowForWindows(matchingWindows, hoveredButton, appIcon);
    }

    private void DockApp_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewHoverTimer.Stop();
        _pendingPreviewButton = null;
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void PreviewCloseTimer_Tick(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        _autoHideController?.EvaluateState();
    }

    private void ThumbnailPreview_PointerEntered(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
    }

    private void ThumbnailPreview_PointerLeft(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void ThumbnailPreview_PreviewClicked(object? sender, EventArgs e)
    {
        var sourceHandle = _thumbnailPreview.SourceHandle;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        _autoHideController?.EvaluateState();
        if (sourceHandle == 0 || !NativeWindowTracker.Activate(sourceHandle))
        {
            ReportError("ErrorPreviewActivate");
            return;
        }

    }

    private void ThumbnailPreview_CloseRequested(object? sender, EventArgs e)
    {
        var sourceHandle = _thumbnailPreview.SourceHandle;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        _autoHideController?.EvaluateState();
        if (sourceHandle == 0 || !NativeWindowTracker.RequestClose(sourceHandle))
        {
            ReportError("ErrorPreviewClose");
            return;
        }

    }

    private TrackedWindow? FindWindowForExecutable(string executablePath)
    {
        return VisibleSnapshot.FirstOrDefault(window => window.Handle == _foregroundWindow
            && string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            ?? VisibleSnapshot.FirstOrDefault(window =>
                string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
    }

    private TrackedWindow? FindNextWindowForExecutable(string executablePath)
    {
        var matchingWindows = VisibleSnapshot.Where(window =>
            string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matchingWindows is not { Count: > 0 })
        {
            return null;
        }

        var foreground = matchingWindows.FirstOrDefault(window => window.Handle == _foregroundWindow);
        if (foreground is not null)
        {
            return foreground;
        }

        matchingWindows.Sort((left, right) => left.Handle.ToInt64().CompareTo(right.Handle.ToInt64()));
        var previousIndex = _lastSelectedWindows.TryGetValue(executablePath, out var previousHandle)
            ? matchingWindows.FindIndex(window => window.Handle == previousHandle)
            : -1;
        return matchingWindows[(previousIndex + 1) % matchingWindows.Count];
    }

    private void UpdateWindowIndicators(IReadOnlyList<TrackedWindow> windows)
    {
        var states = new Dictionary<string, (int Count, bool IsForeground)>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in windows)
        {
            if (window.ExecutablePath is null) continue;
            states.TryGetValue(window.ExecutablePath, out var state);
            states[window.ExecutablePath] = (
                state.Count + 1,
                state.IsForeground || window.Handle == _foregroundWindow);
        }

        for (var index = 0; index < _pinnedApps.Count; index++)
        {
            var app = _pinnedApps[index];
            states.TryGetValue(app.ExecutablePath, out var state);
            var isForeground = state.IsForeground;
            var count = state.Count;
            if (app.IsRunning != (count > 0) || app.IsForeground != isForeground || app.WindowCount != count)
            {
                app.IsForeground = isForeground;
                app.WindowCount = count;
            }
        }
    }

    private static string GetApplicationName(string executablePath)
    {
        var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
        return string.IsNullOrWhiteSpace(description)
            ? Path.GetFileNameWithoutExtension(executablePath)
            : description.Trim();
    }

    private void WindowTracker_ForegroundWindowChanged(nint windowHandle)
    {
        _replacementSession?.UpdateForeground(windowHandle);
        _autoHideController?.CheckForegroundWindow(windowHandle);
        UpdateSecondaryDocks();
        GetWindowThreadProcessId(windowHandle, out var processId);
        // Dock buttons, thumbnail hosts, and menus must not replace the last external foreground window.
        if (processId == Environment.ProcessId) return;
        if (_openContextMenu is not null
            && windowHandle != 0
            && windowHandle != _foregroundWindow
            && windowHandle != _foregroundAtMenuOpen
            && (DateTime.UtcNow - _contextMenuOpenedAtUtc).TotalMilliseconds > 250)
        {
            _openContextMenu.IsOpen = false;
        }
        if (_foregroundWindow == windowHandle) return;
        _foregroundWindow = windowHandle;
        UpdateWindowIndicators(VisibleSnapshot);
        var foregroundPath = VisibleSnapshot.FirstOrDefault(window => window.Handle == windowHandle)?.ExecutablePath;
        foreach (var app in _runningApps)
        {
            app.IsForeground = foregroundPath is not null
                && string.Equals(foregroundPath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ReportError(string localizationKey) => AppLog.ReportError(localizationKey);

    private static void ReportOperationStatus(string localizationKey, bool isError) => ReportError(localizationKey, isError);

    /// <summary>Failures and operational notes go to the log only; the dock stays silent.</summary>
    private static void ReportError(string localizationKey, bool isError, params object[] arguments)
    {
        if (isError) AppLog.ReportError(localizationKey, arguments);
        else AppLog.Info(AppLog.ResolveMessage(localizationKey, arguments));
    }

    private async void DockContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _widgetPanel.Close(restoreFocus: false);
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        ResetMagnification();
        if (_openContextMenu is not null && !ReferenceEquals(_openContextMenu, contextMenu))
            _openContextMenu.IsOpen = false;
        _openContextMenu = contextMenu;
        _foregroundAtMenuOpen = _foregroundWindow;
        _contextMenuOpenedAtUtc = DateTime.UtcNow;
        if (_source is not null && _source.Handle != 0)
        {
            SetForegroundWindow(_source.Handle);
        }

        var pinnedApp = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as DockApp;
        contextMenu.DataContext = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext;
        _contextTargetWasForeground = pinnedApp?.IsForeground == true;
        var hasAppTarget = pinnedApp is not null;
        var isPinnedTarget = pinnedApp?.ShortcutPath is not null;
        var isRunning = pinnedApp?.IsRunning == true;
        SetMenuItemVisibility(contextMenu, "ActivateDockApp", hasAppTarget);
        SetMenuItemVisibility(contextMenu, "CloseDockApp", isRunning);
        SetMenuItemVisibility(contextMenu, "TerminateDockApp", isRunning);
        SetMenuItemVisibility(contextMenu, "PinDockApp", hasAppTarget && !isPinnedTarget);
        SetMenuItemVisibility(contextMenu, "UnpinDockApp", isPinnedTarget);
        SetSeparatorVisibility(contextMenu, "RunningCommandsDivider", isRunning);
        SetMenuItemVisibility(contextMenu, "HideInProfile", isPinnedTarget && ActiveProfile is not null);
        if (FindMenuItem(contextMenu, "ProfilesMenu") is { } profilesMenu) PopulateProfilesMenu(profilesMenu);
        if (FindMenuItem(contextMenu, "UpdateMenu") is { } updateMenu)
        {
            updateMenu.Header = _availableUpdate is { } update
                ? UiText.Format("UpdateDownload", update.Tag)
                : UiText.Get("CheckForUpdates");
        }

        if (pinnedApp is null)
        {
            FocusContextMenu(contextMenu);
            return;
        }

        SetMenuCommandParameter(contextMenu, pinnedApp.ExecutablePath);
        var targetWindow = FindWindowForExecutable(pinnedApp.ExecutablePath);
        if (FindMenuItem(contextMenu, "ActivateDockApp") is { } activateMenuItem)
        {
            activateMenuItem.CommandParameter = pinnedApp;
            activateMenuItem.Header = isRunning
                ? (pinnedApp.IsForeground ? UiText.Get("MinimizeWindow") : UiText.Get("ActivateWindow"))
                : UiText.Get("OpenApplication");
        }
        if (FindMenuItem(contextMenu, "PinDockApp") is { } pinMenuItem)
        {
            pinMenuItem.CommandParameter = pinnedApp;
        }
        if (FindMenuItem(contextMenu, "UnpinDockApp") is { } unpinMenuItem)
        {
            unpinMenuItem.CommandParameter = pinnedApp;
        }
        foreach (var tag in new[] { "HideInProfile", "NewInstance", "RunAsAdmin" })
        {
            if (FindMenuItem(contextMenu, tag) is { } item) item.CommandParameter = pinnedApp;
        }
        FocusContextMenu(contextMenu);
        await LoadJumpListAsync(contextMenu, targetWindow?.Handle ?? 0, pinnedApp.ShortcutPath);
    }

    private async Task LoadJumpListAsync(ContextMenu menu, nint window, string? shortcut)
    {
        var boundary = menu.Items.OfType<Separator>().FirstOrDefault(item => item.Tag as string == "JumpListCommands");
        if (boundary is null) return;
        _jumpListLoad?.Cancel();
        _jumpListLoad?.Dispose();
        var load = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _jumpListLoad = load;
        ReplaceJumpListItems(menu, boundary, [new MenuItem { Header = UiText.Get("TasksLoading"), IsEnabled = false, Focusable = false }]);
        try
        {
            var result = await _jumpLists.ReadAsync(window, shortcut, load.Token);
            if (!menu.IsOpen || !ReferenceEquals(_jumpListLoad, load))
            {
                await _jumpLists.ReleaseAsync(result.SessionId);
                return;
            }
            _jumpListSession = result.SessionId;
            var items = new List<Control>();
            foreach (var group in result.Groups)
            {
                if (items.Count > 0) items.Add(new Separator());
                var heading = new MenuItem { Header = group.Title.Replace("_", "__") };
                heading.SetResourceReference(StyleProperty, "MenuSectionHeading");
                items.Add(heading);
                foreach (var entry in group.Entries)
                {
                    if (entry.IsSeparator) { items.Add(new Separator()); continue; }
                    // Keep the session alive until Click queues invocation; WPF normally closes first.
                    var item = new MenuItem { Header = entry.Title.Replace("_", "__"), StaysOpenOnClick = true };
                    if (entry.Icon is not null) item.Icon = new Image { Source = entry.Icon, Width = 16, Height = 16 };
                    item.Click += async (_, args) =>
                    {
                        args.Handled = true;
                        var invocation = _jumpLists.InvokeAsync(result.SessionId, entry.CommandId, _source?.Handle ?? 0);
                        menu.IsOpen = false;
                        if (!await invocation && IsLoaded)
                            ReportError("ErrorAppTaskRun");
                    };
                    items.Add(item);
                }
            }
            if (result.Status != JumpListStatus.Ready)
            {
                var message = result.Status switch
                {
                    JumpListStatus.Empty => UiText.Get("TasksEmpty"),
                    JumpListStatus.IdentityUnavailable => UiText.Get("TasksIdentityUnavailable"),
                    JumpListStatus.Unsupported => UiText.Get("TasksUnsupported"),
                    _ => UiText.Get("TasksReadError")
                };
                items.Add(new MenuItem { Header = message, IsEnabled = false });
            }
            ReplaceJumpListItems(menu, boundary, items);
        }
        catch (OperationCanceledException)
        {
            if (menu.IsOpen && ReferenceEquals(_jumpListLoad, load))
            {
                ReplaceJumpListItems(menu, boundary, [new MenuItem { Header = UiText.Get("TasksTimeout"), IsEnabled = false }]);
            }
        }
        catch (COMException ex)
        {
            Trace.TraceWarning("Shell worker failed: 0x{0:X8}", ex.HResult);
            if (menu.IsOpen && ReferenceEquals(_jumpListLoad, load))
            {
                ReplaceJumpListItems(menu, boundary, [new MenuItem { Header = UiText.Get("TasksServiceError"), IsEnabled = false }]);
            }
        }
    }

    private static void ReplaceJumpListItems(ContextMenu menu, Separator boundary, IReadOnlyList<Control> items)
    {
        foreach (var previous in menu.Items.OfType<Control>().Where(item => item.Tag as string == "JumpListEntry").ToArray())
            menu.Items.Remove(previous);
        var index = menu.Items.IndexOf(boundary);
        foreach (var item in items)
        {
            item.Tag = "JumpListEntry";
            if (!item.IsEnabled) item.Focusable = false;
            menu.Items.Insert(index++, item);
        }
        boundary.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (menu.IsOpen && !menu.IsKeyboardFocusWithin)
        {
            menu.Focus();
        }
    }

    private static void FocusContextMenu(ContextMenu menu)
    {
        menu.Focus();
    }

    private async void DockContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu closedMenu)
        {
            closedMenu.DataContext = null;
            SetMenuCommandParameter(closedMenu, null);
            if (closedMenu.Items.OfType<Separator>().FirstOrDefault(item => item.Tag as string == "JumpListCommands") is { } boundary)
            {
                ReplaceJumpListItems(closedMenu, boundary, []);
            }
        }
        if (!ReferenceEquals(sender, _openContextMenu)) return;
        _openContextMenu = null;
        _autoHideController?.EvaluateState();
        _jumpListLoad?.Cancel();
        if (_jumpListSession is { } session)
        {
            _jumpListSession = null;
            await _jumpLists.ReleaseAsync(session);
        }
    }

    private void DockAppContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: DockApp app })
        {
            if (_contextTargetWasForeground && FindWindowForExecutable(app.ExecutablePath) is { } window)
                NativeWindowTracker.Activate(window.Handle);
            OpenDockApp(app, toggleIfForeground: _contextTargetWasForeground);
        }
    }

    private void PinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: DockApp app })
        {
            return;
        }

        PinRunningApp(app);
    }

    internal void PinRunningApp(DockApp app, int insertIndex = -1)
    {
        if (RevealHiddenPin(app)) return;
        var (shortcutPath, error) = WindowsTaskbarPinImporter.AddPin(app.Name, app.ExecutablePath, app.LaunchArguments);
        if (error is not null || shortcutPath is null)
        {
            ReportError(error ?? "ErrorAppPinFailed");
            return;
        }

        var pinnedApp = new DockApp(app.Name, app.ExecutablePath)
        {
            Icon = app.Icon,
            LaunchArguments = app.LaunchArguments,
            ShortcutPath = shortcutPath,
            HasNotification = app.HasNotification,
            IsForeground = app.IsForeground,
            WindowCount = app.WindowCount
        };

        _runningApps.Remove(app);
        if (insertIndex >= 0 && insertIndex <= _pinnedApps.Count)
        {
            _pinnedApps.Insert(insertIndex, pinnedApp);
        }
        else
        {
            _pinnedApps.Add(pinnedApp);
        }

        SavePinOrder();
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void SavePinOrder()
    {
        var order = _pinnedApps.Select(app => app.ExecutablePath).ToList();
        var error = _preferences.Save(_preferences.Current with { PinOrder = order });
        if (error is not null) ReportError(error);
    }

    private void OnWidgetOrderChanged(IReadOnlyList<string> newOrder)
    {
        var error = _preferences.Save(_preferences.Current with { WidgetOrder = newOrder });
        if (error is not null) ReportError(error);
    }

    private void UnpinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: DockApp { ShortcutPath: not null } app })
        {
            return;
        }

        var error = WindowsTaskbarPinImporter.RemovePin(app.ShortcutPath);
        if (error is not null)
        {
            ReportError(error);
            return;
        }

        AnimateDockAppRemoval(PinnedApps, _pinnedApps, app, () =>
        {
            SavePinOrder();
            if (_windowTracker is not null)
            {
                UpdateRunningApps(VisibleSnapshot);
            }

            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        });
    }

    private void CloseDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string executablePath })
        {
            return;
        }

        CloseApplication(executablePath);
    }

    private void CloseApplication(string executablePath)
    {
        var (processes, inaccessibleCount) = FindProcessesForExecutable(executablePath);
        var closedCount = 0;
        var failedCount = inaccessibleCount;
        foreach (var process in processes)
        {
            try
            {
                if (process.CloseMainWindow())
                {
                    closedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while the menu command was running.
            }
            catch (Win32Exception)
            {
                failedCount++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (closedCount == 0)
            ReportError("ErrorAppGracefulClose");
        else if (failedCount > 0)
            ReportError("ErrorAppPartialGracefulClose");
    }

    private void TerminateDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string executablePath })
        {
            return;
        }

        TerminateApplication(executablePath);
    }

    private void TerminateApplication(string executablePath)
    {
        var (processes, inaccessibleCount) = FindProcessesForExecutable(executablePath);
        if (processes.Count == 0)
        {
            ReportError(inaccessibleCount == 0
                ? "ErrorAppNoProcesses"
                : "ErrorAppProcessesInaccessible");
            return;
        }

        var appName = _pinnedApps.FirstOrDefault(app =>
            string.Equals(app.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))?.Name
            ?? Path.GetFileNameWithoutExtension(executablePath);
        var confirmation = MessageBox.Show(
            UiText.Format("TerminatePrompt", appName, processes.Count),
            UiText.Get("TerminateTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            DisposeProcesses(processes);
            return;
        }

        var terminatedCount = 0;
        var failedCount = inaccessibleCount;
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    failedCount++;
                    continue;
                }

                process.Kill(entireProcessTree: true);
                terminatedCount++;
            }
            catch (InvalidOperationException)
            {
                // The process exited while the menu command was running.
            }
            catch (Win32Exception)
            {
                failedCount++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (terminatedCount == 0)
            ReportError("ErrorAppTerminateFailed");
        else if (failedCount > 0)
            ReportError("ErrorAppPartialTerminate");
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        _widgetPanel.Close(restoreFocus: false);
        if (_settingsWindow is { IsClosingOrClosed: true }) _settingsWindow = null;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_preferences);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                if (_autoHideController is not null)
                {
                    _autoHideController.CheckForegroundWindow(NativeWindowTracker.GetForegroundApplicationWindow());
                    _autoHideController.EvaluateState();
                }
            };
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ThemePacks_Changed(object? sender, EventArgs e)
    {
        foreach (var app in _pinnedApps)
        {
            app.Icon = ThemePacks.ApplyIcon(ResolvePinnedIcon(app), app.ExecutablePath);
        }

        RefreshRecycleBinIcon();
        ApplyStartIcon();
        if (_windowTracker is not null)
        {
            WindowTracker_WindowsChanged(this, _windowTracker.Snapshot);
        }
    }

    // The theme's @start image replaces the Windows logo; without one the logo stays.
    private void ApplyStartIcon()
    {
        var image = ThemePacks.ApplyIcon(null, null, specialKey: IconPack.StartKey);
        StartImage.Source = image;
        StartImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        StartLogo.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        _sizePreviewOverride = null;
        DockSurfaceTranslate.X = 0;
        ApplyWidgets(_preferences.Current.Widgets);
        _dockPosition = _preferences.Current.Position;
        _thumbnailPreview.AnimationsEnabled = _preferences.Current.AnimationsEnabled;
        if (_autoHideController is not null) _autoHideController.AnimationsEnabled = _preferences.Current.AnimationsEnabled;
        _autoHideController?.SetMode(_preferences.Current.AutoHide);
        _replacementSession?.UpdateAutoHide(_preferences.Current.AutoHide != AutoHideMode.Disabled);
        ApplyBackdropHitTesting();
        _widgetCoordinator.Apply(_preferences.Current);
        RebuildDockOrder();
        ApplyExtrasSettings();
        ApplyFeatureSettings();
        ApplySizeProfile();
        ApplyLayoutMode();
        ResizeDockToContent();
        _thumbnailPreview.Hide();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        ResetMagnification();
        if (_windowTracker is not null)
        {
            UpdateNotificationBadges(_pinnedApps, VisibleSnapshot);
            UpdateNotificationBadges(_runningApps, VisibleSnapshot);
        }
        var targetDisplay = DisplayService.GetTargetDisplay(_preferences.Current.TargetDisplay);
        if (_replacementSession?.IsActive == true)
        {
            _replacementSession.SetTargetMonitor(targetDisplay.Handle);
            ApplyDockAlignment();
        }
        else
        {
            PositionDock();
        }
    }

    private void RemoveDockWidget(string id)
    {
        var settings = _preferences.Current;
        var widgets = (settings.Widgets ?? [])
            .Where(widget => !string.Equals(widget, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var order = settings.WidgetOrder?
            .Where(widget => !string.Equals(widget, id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (widgets.Length == (settings.Widgets?.Count ?? 0)) return;

        // Close the gap immediately instead of animating the dock width after a removal.
        _instantDockResize = true;
        try
        {
            var updated = settings with { Widgets = widgets, WidgetOrder = order };
            var error = _preferences.Save(updated);
            if (error is not null) ReportError(error);
            _widgetCoordinator.Apply(updated);
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        }
        finally
        {
            _instantDockResize = false;
        }
    }

    private void ApplyWidgets(IReadOnlyList<string>? selected)
    {
        _widgetPanel.Close(restoreFocus: false);
        foreach (var timed in _widgets.OfType<ITimedDockWidget>()) timed.PropertyChanged -= TimedWidget_PropertyChanged;
        _widgets.Clear();
        var clockStripActive = selected?.Contains(DockWidgetService.ClockId, StringComparer.OrdinalIgnoreCase) == true;
        var statsStripActive = selected?.Contains(DockWidgetService.SystemStatsId, StringComparer.OrdinalIgnoreCase) == true;
        var filtered = selected?.Where(id =>
            !WidgetCoordinator.HandlesWidget(id)
            && (!clockStripActive || id != DockWidgetService.DateId)
            && (!statsStripActive || id != DockWidgetService.BatteryId))
            .ToArray();
        foreach (var widget in _widgetService.CreateWidgets(filtered)) _widgets.Add(widget);
        foreach (var timed in _widgets.OfType<ITimedDockWidget>()) timed.PropertyChanged += TimedWidget_PropertyChanged;
        WidgetDivider.Visibility = _widgets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_widgets.Count > 0)
        {
            DockWidgetService.Refresh(_widgets, _preferences.Current.Language);
            if (!_widgetRefreshTimer.IsEnabled) _widgetRefreshTimer.Start();
        }
        else
        {
            _widgetRefreshTimer.Stop();
        }
        UpdateTimedWidgetRefresh();
        ResizeDockToContent();
    }

    private IReadOnlyList<string> DefaultDockOrder(DockSettings settings)
    {
        var tokens = new List<string>();
        if (settings.StartPlacement != StartButtonPlacement.Hidden) tokens.Add("start");

        var pins = (settings.PinOrder ?? _pinnedApps.Select(a => a.ExecutablePath).Where(p => p is not null).ToArray())
            .Where(p => p is not null).ToArray();
        foreach (var path in pins) tokens.Add("app:" + path);
        foreach (var app in _runningApps)
        {
            if (!pins.Contains(app.ExecutablePath, StringComparer.OrdinalIgnoreCase)) tokens.Add("app:" + app.ExecutablePath);
        }
        foreach (var stack in _stacks) tokens.Add("stack:" + stack.Path);
        if (settings.ShowRecycleBin) tokens.Add("bin");
        foreach (var id in _widgetCoordinator.CurrentOrder)
        {
            if (_widgetCoordinator.ActiveWidgets.ContainsKey(id)) tokens.Add("widget:" + id);
        }
        return tokens;
    }

    private DockApp? FindDockApp(string path) => _pinnedApps.Concat(_runningApps)
        .FirstOrDefault(app => string.Equals(app.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));

    private FrameworkElement? CreateDockItemElement(string token)
    {
        if (string.Equals(token, "start", StringComparison.OrdinalIgnoreCase)) return StartButton;
        if (string.Equals(token, "bin", StringComparison.OrdinalIgnoreCase)) return RecycleBinButton;
        if (token.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            var app = FindDockApp(token[4..]);
            if (app is null) return null;
            var element = (FrameworkElement)((DataTemplate)FindResource("DockAppIconTemplate")).LoadContent();
            element.DataContext = app;
            return element;
        }
        if (token.StartsWith("stack:", StringComparison.OrdinalIgnoreCase))
        {
            var stack = _stacks.FirstOrDefault(s => string.Equals(s.Path, token[6..], StringComparison.OrdinalIgnoreCase));
            if (stack is null) return null;
            var element = (FrameworkElement)((DataTemplate)FindResource("StackIconTemplate")).LoadContent();
            element.DataContext = stack;
            return element;
        }
        if (token.StartsWith("widget:", StringComparison.OrdinalIgnoreCase)
            && _widgetCoordinator.ActiveWidgets.TryGetValue(token[7..], out var widget))
        {
            return widget;
        }
        return null;
    }

    /// <summary>Rebuilds the single dock strip from the persisted free-form item order.</summary>
    private void RebuildDockOrder()
    {
        if (_rebuildingDockOrder || DockContents is null) return;
        _rebuildingDockOrder = true;
        try
        {
            var settings = _preferences.Current;
            var tokens = (settings.DockItemOrder ?? DefaultDockOrder(settings)).ToList();
            foreach (var token in DefaultDockOrder(settings))
            {
                if (!tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    if (token.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
                    {
                        var lastAppIndex = tokens.FindLastIndex(t =>
                            t.StartsWith("app:", StringComparison.OrdinalIgnoreCase)
                            && FindDockApp(t[4..]) is not null);

                        if (lastAppIndex >= 0)
                        {
                            tokens.Insert(lastAppIndex + 1, token);
                        }
                        else
                        {
                            var startIndex = tokens.FindIndex(t => string.Equals(t, "start", StringComparison.OrdinalIgnoreCase));
                            if (startIndex >= 0)
                            {
                                tokens.Insert(startIndex + 1, token);
                            }
                            else
                            {
                                tokens.Insert(0, token);
                            }
                        }
                    }
                    else
                    {
                        tokens.Add(token);
                    }
                }
            }

            var pairs = new List<(string Token, FrameworkElement Element)>();
            foreach (var token in tokens.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (CreateDockItemElement(token) is { } element) pairs.Add((token, element));
            }

            foreach (var element in _dockItemElements.Values) DockContents.Children.Remove(element);
            _dockItemElements.Clear();

            var insertAt = DockContents.Children.IndexOf(StatusText);
            if (insertAt < 0) insertAt = DockContents.Children.Count;
            foreach (var (token, element) in pairs)
            {
                // A UIElement cannot be a logical child of two panels; detach from the old grouped host first.
                if (element.Parent is Panel oldPanel) oldPanel.Children.Remove(element);
                DockContents.Children.Insert(insertAt++, element);
                _dockItemElements[token] = element;
            }
        }
        finally
        {
            _rebuildingDockOrder = false;
        }
    }

    /// <summary>Moves a dock item to an absolute position and persists the new order.</summary>
    private void MoveDockItem(string token, int index)
    {
        var settings = _preferences.Current;
        var tokens = (settings.DockItemOrder ?? DefaultDockOrder(settings)).ToList();
        tokens.RemoveAll(existing => string.Equals(existing, token, StringComparison.OrdinalIgnoreCase));
        tokens.Insert(Math.Clamp(index, 0, tokens.Count), token);
        _preferences.Save(settings with { DockItemOrder = tokens });
        RebuildDockOrder();
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void WidgetRefreshTimer_Tick(object? sender, EventArgs e)
    {
        DockWidgetService.Refresh(_widgets, _preferences.Current.Language);
        ResizeDockToContent();
    }

    private void DockWidgets_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ResizeDockToContent();
    }

    private void TimedWidget_DetailsRequested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TimedWidgetTile { DataContext: DockWidgetItem widget } tile) return;
        e.Handled = true;
        _widgetPanel.Open(tile, widget is CountdownDockWidget ? UiText.Get("WidgetCountdown") : UiText.Get("WidgetStopwatch"),
            new TimedWidgetPanel { DataContext = widget });
    }

    private void WidgetPanel_IsOpenChanged(object? sender, EventArgs e)
    {
        if (_widgetPanel.IsOpen)
        {
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
            ResetMagnification();
        }
        _autoHideController?.EvaluateState();
    }

    private void TimedWidget_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ITimedDockWidget.IsRunning)) UpdateTimedWidgetRefresh();
    }

    private void UpdateTimedWidgetRefresh()
    {
        if (_widgets.OfType<ITimedDockWidget>().Any(widget => widget.IsRunning)) _timedWidgetRefreshTimer.Start();
        else _timedWidgetRefreshTimer.Stop();
    }

    private void TimedWidgetRefreshTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var widget in _widgets.OfType<ITimedDockWidget>()) widget.Refresh();
    }

    private static MenuItem? FindMenuItem(ItemsControl owner, string tag)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                return item;
            }

            var nested = FindMenuItem(item, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SetMenuItemVisibility(ItemsControl owner, string tag, bool visible)
    {
        var item = FindMenuItem(owner, tag);
        if (item is not null)
        {
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetSeparatorVisibility(ItemsControl owner, string tag, bool visible)
    {
        var separator = owner.Items.OfType<Separator>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
        if (separator is not null)
        {
            separator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetMenuCommandParameter(ItemsControl owner, string? executablePath)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            item.CommandParameter = executablePath;
            SetMenuCommandParameter(item, executablePath);
        }
    }

    private static (List<Process> Processes, int InaccessibleCount) FindProcessesForExecutable(string executablePath)
    {
        var matchingProcesses = new List<Process>();
        var inaccessibleCount = 0;
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    matchingProcesses.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Win32Exception)
            {
                inaccessibleCount++;
                process.Dispose();
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        return (matchingProcesses, inaccessibleCount);
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private bool IsFullWidth => _preferences.Current.Layout == DockLayout.FullWidth;

    private void ApplyLayoutMode()
    {
        AppsColumn.Width = IsFullWidth ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        WidgetColumn.Width = IsFullWidth ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        WidgetIsland.HorizontalAlignment = IsFullWidth ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        ApplyDockMargin();
        ApplyDockAlignment();
        ResizeDockToContent();
    }

    private void ApplyDockMargin()
    {
        var side = IsFullWidth ? DockMetrics.FullWidthSideInset : DockMetrics.SideInset;
        DockSurface.Margin = Edge switch
        {
            DockEdge.Left => new Thickness(Profile.BottomGap, side, _dockTopGap, side),
            DockEdge.Right => new Thickness(_dockTopGap, side, Profile.BottomGap, side),
            _ => new Thickness(side, _dockTopGap, side, Profile.BottomGap)
        };
    }

    private DockSizeProfile Profile => _sizePreviewOverride is { } preview
        ? DockMetrics.For(preview.Size, preview.Scale)
        : DockMetrics.For(_preferences.Current.Size, _preferences.Current.DockScale);

    internal void SetSizePreview(DockSize? size, double? scale)
    {
        _sizePreviewOverride = size is { } s && scale is { } sc ? (s, sc) : null;
        ApplySizeProfile();
        if (_replacementSession?.IsActive == true)
        {
            var thickness = IsVertical ? _dockArea.Width : _dockArea.Height;
            if (thickness > 0) _dockTopGap = Math.Max(0.0, thickness - Profile.SurfaceHeight - Profile.BottomGap);
        }
        ApplyDockMargin();
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void ApplySizeProfile()
    {
        UpdatePreferredIconPixels();
        var profile = Profile;
        Application.Current.Resources["DockCellSize"] = profile.Cell;
        Application.Current.Resources["DockCellHeight"] = profile.Cell;
        Application.Current.Resources["DockIconSize"] = profile.Icon;
        Application.Current.Resources["StartLogoSize"] = Math.Max(14, Math.Round(profile.Icon * 0.68));
        DockFrame.Padding = new Thickness(10, profile.VerticalPadding, 10, profile.VerticalPadding);
        DockFrame.CornerRadius = new CornerRadius(profile.Radius);
        DockInnerBevel.CornerRadius = new CornerRadius(Math.Max(1, profile.Radius - 1));
        // The sheen sits on the inner top edge of the glass frame, inset past the rounded corners.
        var sheenHorizontal = Math.Max(4, profile.Radius - 8);
        DockSheen.Margin = new Thickness(sheenHorizontal, -profile.VerticalPadding + 1, sheenHorizontal, 0);
        var bevelH = -9;
        var bevelV = -profile.VerticalPadding + 1;
        DockInnerBevel.Margin = IsVertical ? new Thickness(bevelV, bevelH, bevelV, bevelH) : new Thickness(bevelH, bevelV, bevelH, bevelV);
        if (IsVertical) DockFrame.Padding = new Thickness(profile.VerticalPadding, 10, profile.VerticalPadding, 10);
        var scale = profile.WidgetScale;
        var widgetTransform = Math.Abs(scale - 1) > 0.001 ? new ScaleTransform(scale, scale) : Transform.Identity;
        WidgetStrip.LayoutTransform = widgetTransform;
        DockWidgets.LayoutTransform = widgetTransform;
        if (_replacementSession is not null)
        {
            _replacementSession.ShelfHeight = profile.Shelf;
        }
        else
        {
            Height = DockMetrics.Headroom + profile.Shelf;
        }
    }

    private void UpdatePreferredIconPixels()
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pixels = ApplicationIconProvider.ComputeTargetPixels(Profile.Icon, dpiScale);
        ApplicationIconProvider.PreferredIconPixels = pixels;
        ThemePacks.PreferredIconPixels = pixels;
    }

    private void WidgetStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void ApplyDockAlignment()
    {
        if (IsVertical)
        {
            DockSurface.HorizontalAlignment = Edge == DockEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            DockSurface.VerticalAlignment = IsFullWidth ? VerticalAlignment.Stretch : _dockPosition switch
            {
                DockPosition.Left => VerticalAlignment.Top,
                DockPosition.Right => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center
            };
            _replacementSession?.RefreshPosition();
            return;
        }

        DockSurface.VerticalAlignment = VerticalAlignment.Bottom;
        if (IsFullWidth)
        {
            DockSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
            _replacementSession?.RefreshPosition();
            return;
        }

        var alignment = _dockPosition switch
        {
            DockPosition.Left => HorizontalAlignment.Left,
            DockPosition.Center => HorizontalAlignment.Center,
            DockPosition.Right => HorizontalAlignment.Right,
            _ => throw new InvalidOperationException("ErrorDockPosition")
        };

        DockSurface.HorizontalAlignment = alignment;
        _replacementSession?.RefreshPosition();
    }

    private void ApplyBackdropHitTesting() => DockBackdrop.Visibility = !_isPreview
        && _preferences.Current.AutoHide == AutoHideMode.Disabled ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyTaskbarLayout(Size area)
    {
        _dockArea = area;
        var profile = Profile;
        var thickness = IsVertical ? area.Width : area.Height;
        _dockTopGap = Math.Max(0.0, thickness - profile.SurfaceHeight - profile.BottomGap);
        ApplyDockMargin();
        ResizeDockToContent();
    }

    private void OnFullScreenChanged(bool suppressed)
    {
        if (!suppressed) return;
        _widgetCoordinator.CollapseHover();
        _widgetPanel.Close(restoreFocus: false);
        _previewHoverTimer.Stop();
        _pendingPreviewButton = null;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        DockFlyout.CloseAll();
        ResetMagnification();
    }

    private void ResizeDockToContent()
    {
        // Measuring runs pending layout, whose SizeChanged handlers call back here. Those calls are coalesced into
        // one more fit by the outer call, which is capped so layout feedback cannot loop.
        if (_resizingDock)
        {
            _resizePending = true;
            return;
        }

        _resizingDock = true;
        try
        {
            var passes = 0;
            do
            {
                _resizePending = false;
                FitDockToContent();
            } while (_resizePending && ++passes < 2);
        }
        finally
        {
            _resizingDock = false;
            _resizePending = false;
        }
    }

    private bool CanExpandWidgetInline(OpenTaskbar.Widgets.DockWidget widget)
    {
        if (!IsVisible || _dockArea.Width <= 0) return false;
        var totalAvailableWidth = Math.Max(1, _dockArea.Width - DockSurface.Margin.Left - DockSurface.Margin.Right);
        var extraWidth = widget.FullWidthEstimate - widget.ActualWidth;
        return DockContents.DesiredSize.Width + Math.Max(0, extraWidth) <= totalAvailableWidth - 2;
    }

    private void FitDockToContent()
    {
        if (IsVertical)
        {
            ResizeSideDockToContent();
            return;
        }

        DockSurface.Height = double.NaN;
        var hasWidgets = _widgetCoordinator.ActiveWidgets.Values.Any(widget => widget.Visibility == Visibility.Visible);
        WidgetIsland.Visibility = hasWidgets ? Visibility.Visible : Visibility.Collapsed;
        var leadWidth = DockScrollViewer.MeasureContentWidth(DockLeadControls);
        var appsContentWidth = IsFullWidth ? 0 : DockScrollViewer.MeasureContentWidth(DockContents);
        var trailWidth = DockScrollViewer.MeasureContentWidth(DockTrailControls);
        var widgetsWidth = hasWidgets ? DockScrollViewer.MeasureContentWidth(WidgetIsland) : 0;
        var targetDisplay = DisplayService.GetTargetDisplay(_preferences.Current.TargetDisplay);
        var availableWidth = _dockArea.Width > 0 ? _dockArea.Width : targetDisplay.Width;
        var margin = DockSurface.Margin;
        var maximumSurfaceWidth = Math.Max(1, availableWidth - margin.Left - margin.Right);
        var maxLayoutWidth = Math.Max(1, maximumSurfaceWidth - DockHorizontalChrome);
        var contentFixedWidth = leadWidth + appsContentWidth + trailWidth + widgetsWidth;
        _widgetCoordinator.UpdateAdaptiveDensity(maxLayoutWidth, contentFixedWidth, _preferences.Current, widgetScale: Profile.WidgetScale);

        var maxAppsWidth = Math.Max(0, maxLayoutWidth - leadWidth - trailWidth - widgetsWidth);
        DockScrollViewer.MaxWidth = maxAppsWidth;
        if (appsContentWidth <= maxAppsWidth + 0.5 && DockScrollViewer.HorizontalOffset > 0)
            DockScrollViewer.ScrollToHorizontalOffset(0);

        if (IsFullWidth)
        {
            DockLayoutRoot.MinWidth = Math.Max(0, maximumSurfaceWidth - DockHorizontalChrome);
            DockSurface.Width = double.NaN;
            if (_dockArea.Width <= 0) Width = Math.Max(16, availableWidth);
            return;
        }

        var desiredWidth = Math.Ceiling(contentFixedWidth + DockHorizontalChrome);
        var targetWidth = Math.Clamp(_dockWidthOverride ?? desiredWidth,
            Math.Min(MinimumDockSurfaceWidth, maximumSurfaceWidth), maximumSurfaceWidth);

        DockSurface.BeginAnimation(WidthProperty, null);

        if (!double.IsNaN(DockSurface.Width)
            && DockSurface.ActualWidth > 0
            && _dockWidthOverride is null
            && LeftResizeThumb.IsDragging == false && RightResizeThumb.IsDragging == false
            && !_instantDockResize
            && _preferences.Current.AnimationsEnabled
            && SystemParameters.ClientAreaAnimation
            && Math.Abs(DockSurface.ActualWidth - targetWidth) >= 1.0)
        {
            var anim = new DoubleAnimation
            {
                From = DockSurface.ActualWidth,
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (_, _) =>
            {
                DockSurface.BeginAnimation(WidthProperty, null);
                DockSurface.Width = targetWidth;
            };
            DockSurface.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            DockSurface.Width = targetWidth;
        }
        if (_dockArea.Width <= 0)
            Width = targetWidth + margin.Left + margin.Right;
    }

    internal static bool ExcludeFromTaskSwitcher(nint windowHandle)
    {
        Marshal.SetLastPInvokeError(0);
        var currentStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
        if (currentStyle == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            return false;
        }

        var style = new nint((currentStyle.ToInt64() | WsExToolWindow) & ~WsExAppWindow);
        if (style != currentStyle)
        {
            Marshal.SetLastPInvokeError(0);
            var previousStyle = SetWindowLongPtr(windowHandle, GwlExStyle, style);
            if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }
        }

        return SetWindowPos(windowHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void ExitOpenTaskbar_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    internal string? RestoreWindowsTaskbar()
    {
        var session = _replacementSession;
        if (session is null) return null;
        var error = session.Stop();
        if (session.IsActive) return error;
        session.OperationReported -= ReportOperationStatus;
        session.LayoutChanged -= ApplyTaskbarLayout;
        session.FullScreenChanged -= OnFullScreenChanged;
        session.Dispose();
        _replacementSession = null;
        return error;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var error = RestoreWindowsTaskbar();
        if (_replacementSession?.IsActive == true)
        {
            e.Cancel = true;
            ReportError(error ?? "ErrorTaskbarRestoreFailed");
            return;
        }

        if (error is not null) Trace.TraceWarning(error);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _widgetPanel.IsOpenChanged -= WidgetPanel_IsOpenChanged;
        _widgetPanel.OperationFailed -= ReportError;
        _widgetPanel.Dispose();
        ShellShortcuts.IsMediaWidgetActive = null;
        _preferences.Changed -= Preferences_Changed;
        _settingsWindow?.CloseForShutdown();
        _jumpListLoad?.Cancel();
        _jumpListLoad?.Dispose();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        _openContextMenu = null;
        _magnifiedIcons.Clear();
        _source?.RemoveHook(WindowProc);
        _source = null;

        if (_windowTracker is not null)
        {
            _windowTracker.WindowsChanged -= WindowTracker_WindowsChanged;
            _windowTracker.ForegroundWindowChanged -= WindowTracker_ForegroundWindowChanged;
            _windowTracker.Dispose();
            _windowTracker = null;
        }

        _previewCloseTimer.Stop();
        _previewCloseTimer.Tick -= PreviewCloseTimer_Tick;
        _previewHoverTimer.Stop();
        _previewHoverTimer.Tick -= PreviewHoverTimer_Tick;
        _widgetRefreshTimer.Stop();
        _widgetRefreshTimer.Tick -= WidgetRefreshTimer_Tick;
        _iconRetryTimer.Stop();
        _iconRetryTimer.Tick -= IconRetryTimer_Tick;
        _timedWidgetRefreshTimer.Stop();
        _timedWidgetRefreshTimer.Tick -= TimedWidgetRefreshTimer_Tick;
        foreach (var widget in _widgets.OfType<ITimedDockWidget>()) widget.PropertyChanged -= TimedWidget_PropertyChanged;
        _thumbnailPreview.PointerEntered -= ThumbnailPreview_PointerEntered;
        _thumbnailPreview.PointerLeft -= ThumbnailPreview_PointerLeft;
        _thumbnailPreview.PreviewClicked -= ThumbnailPreview_PreviewClicked;
        _thumbnailPreview.CloseRequested -= ThumbnailPreview_CloseRequested;
        _thumbnailPreview.Dispose();
        _autoHideController?.Dispose();
        _autoHideController = null;
        _widgetCoordinator.Dispose();
        _jumpLists.Dispose();
        _wheelAudio?.Dispose();
        _wheelAudio = null;
        DisposeExtras();
        DisposeFeatures();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);
}
