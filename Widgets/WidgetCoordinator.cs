using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Owns the dock widgets: builds them into the strip, applies order and visibility from settings,
/// supports drag-and-drop reordering with fluid animations directly on the dock, and drives a shared 1 s tick.
/// </summary>
public sealed class WidgetCoordinator : IDisposable
{
    public sealed record WidgetItem(string Id, string LabelKey, DockWidget Widget, Func<DockSettings, bool> IsEnabled);

    public static readonly string[] DefaultWidgetOrder =
    [
        "Media",
        "Desktops",
        "SystemStats",
        "Focus",
        "Weather",
        "Audio",
        "QuickSettings",
        "Tray",
        "Clock"
    ];

    public static string? GetLabelKey(string id) => id switch
    {
        "Media" => "WidgetMedia",
        "Desktops" => "WidgetDesktops",
        "SystemStats" => "WidgetSystemStats",
        "Focus" => "WidgetFocus",
        "Weather" => "WidgetWeather",
        "Audio" => "WidgetAudio",
        "QuickSettings" => "WidgetQuickSettings",
        "Tray" => "WidgetTray",
        "Clock" => "WidgetClock",
        _ => null
    };

    public static bool HandlesWidget(string id) =>
        DefaultWidgetOrder.Contains(id, StringComparer.OrdinalIgnoreCase);

    private sealed class WidgetSlot(string id, string labelKey, Func<DockWidget> factory, Func<DockSettings, bool> isEnabled)
    {
        public string Id { get; } = id;
        public string LabelKey { get; } = labelKey;
        public Func<DockWidget> Factory { get; } = factory;
        public Func<DockSettings, bool> IsEnabled { get; } = isEnabled;
        public DockWidget? Widget { get; set; }
    }

    public sealed record WidgetDragPayload(string WidgetId);

    private readonly Panel _strip;
    private readonly Action<string> _reportError;
    private readonly List<WidgetSlot> _widgets;
    private readonly DispatcherTimer _timer;
    private readonly WidgetHoverCoordinator _hoverCoordinator;
    private readonly HashSet<DockWidget> _displacedWidgets = [];
    private Point _dragStartPoint;
    private DockWidget? _draggedWidget;
    private bool _disposed;

    public Action<IReadOnlyList<string>>? OrderChanged { get; set; }
    public event Action? WidgetsPlaced;
    public event Action<string>? CloseApplicationRequested;
    public event Action<string>? TerminateApplicationRequested;
    public event Action<string>? WidgetRemoveRequested;

    /// <summary>Lets the host wire its context-menu open/close handlers so auto-hide stays locked while the menu is up.</summary>
    public Action<ContextMenu>? ConfigureWidgetMenu { get; set; }
    public IReadOnlyList<string> CurrentOrder => _widgets.Select(w => w.Id).ToArray();

    /// <summary>When true, the host window owns widget placement so all dock item kinds share one strip.</summary>
    public bool ExternalLayout { get; set; }

    public IReadOnlyDictionary<string, DockWidget> ActiveWidgets => _widgets
        .Where(slot => slot.Widget is not null)
        .ToDictionary(slot => slot.Id, slot => slot.Widget!);

    /// <summary>Raised when a widget appears or disappears and the dock must refit its layout.</summary>
    public event Action? AvailabilityChanged;

    public bool IsHoverActive => _hoverCoordinator.IsHoverActive;
    public event Action<bool>? HoverActiveChanged
    {
        add => _hoverCoordinator.HoverActiveChanged += value;
        remove => _hoverCoordinator.HoverActiveChanged -= value;
    }
    public void CollapseHover() => _hoverCoordinator.CollapseActive();

    private static bool IsCatalogEnabled(DockSettings settings, string id) =>
        settings.Widgets?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;

    public WidgetCoordinator(
        Panel strip,
        Dispatcher dispatcher,
        Action<string> reportError,
        bool lazy = false,
        Func<DockWidget, bool>? canExpandInline = null,
        Action<bool>? onHoverLayoutChanged = null)
    {
        _strip = strip;
        _reportError = reportError;
        _hoverCoordinator = new WidgetHoverCoordinator(
            dispatcher,
            canExpandInline ?? (_ => false),
            onHoverLayoutChanged ?? (_ => { }));
        _widgets =
        [
            new("Media", "WidgetMedia", () => new MediaWidget(), s => IsCatalogEnabled(s, "Media")),
            new("Desktops", "WidgetDesktops", () => new DesktopsWidget(), s => IsCatalogEnabled(s, "Desktops")),
            new("SystemStats", "WidgetSystemStats", () => new SystemStatsWidget(), s => IsCatalogEnabled(s, "SystemStats")),
            new("Focus", "WidgetFocus", () => new FocusWidget(), s => IsCatalogEnabled(s, "Focus")),
            new("Weather", "WidgetWeather", () => new WeatherWidget(), s => IsCatalogEnabled(s, "Weather")),
            new("Audio", "WidgetAudio", () => new AudioWidget(), s => IsCatalogEnabled(s, "Audio")),
            new("QuickSettings", "WidgetQuickSettings", () => new QuickSettingsWidget(), s => IsCatalogEnabled(s, "QuickSettings")),
            new("Tray", "WidgetTray", CreateTrayWidget, s => IsCatalogEnabled(s, "Tray")),
            new("Clock", "WidgetClock", () => new ClockWidget(), s => IsCatalogEnabled(s, "Clock") || IsCatalogEnabled(s, "Date"))
        ];

        if (!lazy)
        {
            foreach (var slot in _widgets)
            {
                EnsureWidget(slot);
            }
        }

        PlaceWidgets();

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
    }

    private TrayWidget CreateTrayWidget()
    {
        var tray = new TrayWidget();
        tray.CloseApplicationRequested += path => CloseApplicationRequested?.Invoke(path);
        tray.TerminateApplicationRequested += path => TerminateApplicationRequested?.Invoke(path);
        return tray;
    }

    private DockWidget EnsureWidget(WidgetSlot slot)
    {
        if (slot.Widget is { } existing) return existing;
        var widget = slot.Factory();
        widget.ReportError = _reportError;
        widget.AllowDrop = true;
        widget.PreviewMouseLeftButtonDown += Widget_PreviewMouseLeftButtonDown;
        widget.PreviewMouseLeftButtonUp += Widget_PreviewMouseLeftButtonUp;
        widget.PreviewMouseMove += Widget_PreviewMouseMove;
        widget.DragOver += Widget_DragOver;
        widget.DragLeave += Widget_DragLeave;
        widget.Drop += Widget_Drop;
        widget.AvailabilityChanged += Widget_AvailabilityChanged;

        var removeItem = new MenuItem { Header = UiText.Get("WidgetRemove") };
        removeItem.Click += (_, _) => WidgetRemoveRequested?.Invoke(slot.Id);
        var menu = new ContextMenu();
        menu.Items.Add(removeItem);
        ConfigureWidgetMenu?.Invoke(menu);
        widget.ContextMenu = menu;

        _hoverCoordinator.Attach(widget);
        slot.Widget = widget;
        return widget;
    }

    public void Apply(DockSettings settings)
    {
        if (_disposed) return;

        if (settings.WidgetOrder is { Count: > 0 } order)
        {
            ApplyOrder(order);
        }

        var anyActive = false;
        var stripNeedsSync = false;
        foreach (var slot in _widgets)
        {
            if (slot.IsEnabled(settings))
            {
                var wasCreated = slot.Widget is null;
                var widget = EnsureWidget(slot);
                if (wasCreated) stripNeedsSync = true;
                widget.ApplySettings(settings);
                widget.Activate();
                anyActive = true;
            }
            else if (slot.Widget is { } widget)
            {
                widget.ApplySettings(settings);
                widget.Deactivate();
            }
        }

        if (stripNeedsSync)
        {
            PlaceWidgets();
        }

        if (anyActive)
        {
            Timer_Tick(null, EventArgs.Empty);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void ApplyOrder(IReadOnlyList<string> order)
    {
        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            orderMap[order[i]] = i;
        }

        _widgets.Sort((a, b) =>
        {
            var indexA = orderMap.TryGetValue(a.Id, out var ia) ? ia : int.MaxValue;
            var indexB = orderMap.TryGetValue(b.Id, out var ib) ? ib : int.MaxValue;
            return indexA.CompareTo(indexB);
        });

        PlaceWidgets();
    }

    public void ReorderWidgets(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _widgets.Count || newIndex < 0 || newIndex >= _widgets.Count || oldIndex == newIndex)
        {
            return;
        }

        var item = _widgets[oldIndex];
        _widgets.RemoveAt(oldIndex);
        _widgets.Insert(newIndex, item);

        PlaceWidgets();

        var newOrder = _widgets.Select(w => w.Id).ToArray();
        OrderChanged?.Invoke(newOrder);
    }

    internal static (ScaleTransform Scale, TranslateTransform Translate) GetWidgetTransforms(DockWidget widget)
    {
        if (widget.RenderTransform is TransformGroup { IsFrozen: false, IsSealed: false } group)
        {
            var s = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            var t = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (s is { IsFrozen: false, IsSealed: false } && t is { IsFrozen: false, IsSealed: false })
            {
                return (s, t);
            }
        }

        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        widget.RenderTransform = new TransformGroup { Children = { scale, translate } };
        widget.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    private void AnimateWidgetDrag(DockWidget widget, string widgetId)
    {
        widget.Opacity = 0.55;
        var (scale, translate) = GetWidgetTransforms(widget);
        var animDuration = TimeSpan.FromMilliseconds(130);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, animDuration) { EasingFunction = easeOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, animDuration) { EasingFunction = easeOut });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, animDuration) { EasingFunction = easeOut });

        try
        {
            DragDrop.DoDragDrop(widget, new WidgetDragPayload(widgetId), DragDropEffects.Move);
        }
        finally
        {
            widget.Opacity = 1.0;
            ResetWidgetDisplacements();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, animDuration) { EasingFunction = easeOut });
        }
    }

    private void UpdateWidgetDisplacement(DockWidget targetWidget, DragEventArgs e)
    {
        var pos = e.GetPosition(targetWidget);
        var isRightHalf = pos.X > targetWidget.ActualWidth / 2;
        var shift = isRightHalf ? -10.0 : 10.0;

        var (_, translate) = GetWidgetTransforms(targetWidget);
        var anim = new DoubleAnimation(shift, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(TranslateTransform.XProperty, anim);
        _displacedWidgets.Add(targetWidget);
    }

    private void ResetWidgetDisplacement(DockWidget widget)
    {
        if (!_displacedWidgets.Remove(widget)) return;
        var (_, translate) = GetWidgetTransforms(widget);
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void ResetWidgetDisplacements()
    {
        foreach (var widget in _displacedWidgets)
        {
            var (_, translate) = GetWidgetTransforms(widget);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 0;
        }
        _displacedWidgets.Clear();
    }

    private static void AnimateWidgetDropSettle(DockWidget widget)
    {
        var (scale, _) = GetWidgetTransforms(widget);
        var settleScale = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(200),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(0.98, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut })
            }
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleScale);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleScale);
    }

    private void Widget_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInteractiveControl(dep))
        {
            _draggedWidget = null;
            return;
        }

        if (sender is DockWidget widget)
        {
            _dragStartPoint = e.GetPosition(_strip);
            _draggedWidget = widget;
        }
    }

    internal static bool IsInteractiveControl(DependencyObject dep)
    {
        var current = dep;
        while (current is not null)
        {
            if (current is Slider or Thumb or TextBoxBase)
            {
                return true;
            }
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                current = VisualTreeHelper.GetParent(current);
            }
            else if (current is FrameworkContentElement fce)
            {
                current = fce.Parent;
            }
            else
            {
                break;
            }
        }
        return false;
    }

    private void Widget_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedWidget = null;
    }

    private void Widget_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedWidget is null || sender is not DockWidget widget)
        {
            return;
        }

        var currentPoint = e.GetPosition(_strip);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > DockMetrics.DragStartDistance
            || Math.Abs(diff.Y) > DockMetrics.DragStartDistance)
        {
            var targetWidget = _draggedWidget;
            _draggedWidget = null;
            var widgetId = _widgets.FirstOrDefault(w => ReferenceEquals(w.Widget, targetWidget))?.Id;
            if (widgetId is null) return;

            if (Mouse.Captured is UIElement captured)
            {
                captured.ReleaseMouseCapture();
            }
            e.Handled = true;
            AnimateWidgetDrag(targetWidget, widgetId);
        }
    }

    private void Widget_DragOver(object sender, DragEventArgs e)
    {
        if (ExternalLayout)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = false;
            return;
        }

        if (e.Data.GetDataPresent(typeof(WidgetDragPayload)) && sender is DockWidget targetWidget)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            UpdateWidgetDisplacement(targetWidget, e);
        }
        else if (e.Data.GetDataPresent(typeof(StartButtonDragData)) || e.Data.GetDataPresent("OpenTaskbar.StartButtonDragData"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = false;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void Widget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is DockWidget widget)
        {
            ResetWidgetDisplacement(widget);
        }
    }

    private void Widget_Drop(object sender, DragEventArgs e)
    {
        if (ExternalLayout)
        {
            e.Handled = false;
            return;
        }

        if (e.Data.GetDataPresent(typeof(StartButtonDragData)) || e.Data.GetDataPresent("OpenTaskbar.StartButtonDragData"))
        {
            e.Handled = false;
            return;
        }

        if (e.Data.GetData(typeof(WidgetDragPayload)) is WidgetDragPayload payload
            && sender is DockWidget targetWidget)
        {
            var oldIndex = _widgets.FindIndex(w => string.Equals(w.Id, payload.WidgetId, StringComparison.OrdinalIgnoreCase));
            var newIndex = _widgets.FindIndex(w => ReferenceEquals(w.Widget, targetWidget));

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                ResetWidgetDisplacements();
                ReorderWidgets(oldIndex, newIndex);
                AnimateWidgetDropSettle(targetWidget);
            }
        }
        ResetWidgetDisplacements();
        e.Handled = true;
    }

    /// <summary>Puts each widget in the dock strip or the overflow panel, keeping the user's order in both.</summary>
    /// <summary>Puts each active widget in the dock strip in the user's configured order.</summary>
    private void PlaceWidgets()
    {
        if (ExternalLayout)
        {
            WidgetsPlaced?.Invoke();
            return;
        }

        _strip.Children.Clear();
        foreach (var item in _widgets)
        {
            if (item.Widget is { } widget) _strip.Children.Add(widget);
        }
        WidgetsPlaced?.Invoke();
    }

    private void Widget_AvailabilityChanged(object? sender, EventArgs e) => AvailabilityChanged?.Invoke();

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var item in _widgets) item.Widget?.Tick(now);
    }

    /// <summary>
    /// Applies only the user's explicit density choice. Widgets are never auto-compacted to save space —
    /// the dock scrolls instead, so their content is always shown at full width.
    /// </summary>
    public bool UpdateAdaptiveDensity(
        double maxTotalWidth,
        double appsContentWidth,
        DockSettings settings,
        double hysteresisMargin = 32d,
        double widgetScale = 1.0)
    {
        if (_disposed) return false;

        var changed = false;
        foreach (var slot in _widgets)
        {
            if (slot.Widget is not { Visibility: Visibility.Visible } widget) continue;
            var mode = settings.WidgetDensityOverrides is not null
                && settings.WidgetDensityOverrides.TryGetValue(slot.Id, out var overrideMode)
                    ? overrideMode
                    : settings.GlobalWidgetDensity;

            var shouldCompact = widget.SupportsCompactMode && mode == WidgetDensityMode.Compact;
            if (widget.IsCompact != shouldCompact)
            {
                widget.SetCompactState(shouldCompact);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Kept for API compatibility; automatic compaction is intentionally disabled.</summary>
    public bool TryCompactNextAutoWidget(DockSettings settings) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hoverCoordinator.Dispose();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        foreach (var item in _widgets)
        {
            if (item.Widget is not { } widget) continue;
            widget.PreviewMouseLeftButtonDown -= Widget_PreviewMouseLeftButtonDown;
            widget.PreviewMouseLeftButtonUp -= Widget_PreviewMouseLeftButtonUp;
            widget.PreviewMouseMove -= Widget_PreviewMouseMove;
            widget.DragOver -= Widget_DragOver;
            widget.DragLeave -= Widget_DragLeave;
            widget.Drop -= Widget_Drop;
            widget.AvailabilityChanged -= Widget_AvailabilityChanged;
            widget.Dispose();
        }
    }
}
