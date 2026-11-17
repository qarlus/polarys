using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenTaskbar.Models;
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

public partial class MainWindow
{
    private void DockApp_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Snapshot before Button focus handling and preview dismissal can change foreground activation.
        var foreground = OpenTaskbar.Services.NativeWindowTracker.GetForegroundApplicationWindow();
        _foregroundAtAppPress = foreground != 0 ? foreground : _foregroundWindow;

        if (sender is Button { DataContext: DockApp app } && (_pinnedApps.Contains(app) || _runningApps.Contains(app)))
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedApp = app;
        }
        else
        {
            _draggedApp = null;
        }
    }

    private void DockApp_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggedApp = null;
    }

    private void DockApp_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggedApp is null || sender is not Button button)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > Services.DockMetrics.DragStartDistance
            || Math.Abs(diff.Y) > Services.DockMetrics.DragStartDistance)
        {
            var app = _draggedApp;
            _draggedApp = null;
            _foregroundAtAppPress = null;
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            ResetMagnification();
            AnimateAppDrag(button, app);
        }
    }

    private void AnimateAppDrag(Button button, DockApp app)
    {
        var icon = GetButtonIcon(button);
        var (scale, translate) = icon != null ? GetIconTransforms(icon) : (null, null);
        var animDuration = TimeSpan.FromMilliseconds(130);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        button.Opacity = 0.55;
        if (scale is not null && translate is not null && _preferences.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.10, animDuration) { EasingFunction = easeOut });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.10, animDuration) { EasingFunction = easeOut });
            translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                new DoubleAnimation(-4 * BounceDirection, animDuration) { EasingFunction = easeOut });
        }

        try
        {
            DragDrop.DoDragDrop(button, app, DragDropEffects.Move);
        }
        finally
        {
            button.Opacity = 1.0;
            ResetDisplacements();
            if (scale is not null && translate is not null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
                translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                    new DoubleAnimation(0, animDuration) { EasingFunction = easeOut });
            }
        }
    }

    private void UpdateDragDisplacements(Button targetButton, DragEventArgs e)
    {
        var icon = GetButtonIcon(targetButton);
        if (icon is null) return;

        var pos = e.GetPosition(targetButton);
        var isPastHalf = IsVertical ? pos.Y > targetButton.ActualHeight / 2 : pos.X > targetButton.ActualWidth / 2;
        var shift = isPastHalf ? -12.0 : 12.0;

        var (_, translate) = GetIconTransforms(icon);
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        var anim = new DoubleAnimation(shift, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(prop, anim);
        _displacedIcons.Add(icon);
    }

    private void ResetIconDisplacement(FrameworkElement icon)
    {
        if (!_displacedIcons.Remove(icon)) return;
        var (_, translate) = GetIconTransforms(icon);
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(prop, anim);
    }

    private void ResetDisplacements()
    {
        foreach (var icon in _displacedIcons)
        {
            var (_, translate) = GetIconTransforms(icon);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.X = 0;
            translate.Y = 0;
        }
        _displacedIcons.Clear();
    }

    private void AnimateDropSettle(Button button)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var icon = GetButtonIcon(button);
            if (icon is null) return;

            var (scale, _) = GetIconTransforms(icon);
            var settleScale = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(200),
                KeyFrames =
                {
                    new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new CubicEase { EasingMode = EasingMode.EaseOut }),
                    new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut })
                }
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleScale);
        });
    }

    private void DockApp_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button && GetButtonIcon(button) is { } icon)
        {
            ResetIconDisplacement(icon);
        }
    }

    private void DockApp_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(DockApp))
            && sender is Button targetButton
            && targetButton.DataContext is DockApp)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (_preferences.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
            {
                UpdateDragDisplacements(targetButton, e);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void DockApp_Drop(object sender, DragEventArgs e)
    {
        // In the unified model every drop is an insert into the single item order.
        if (TryGetDockItemToken(e, out var droppedToken))
        {
            ResetDisplacements();
            MoveDockItem(droppedToken, GetDockInsertionIndex(e));
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(typeof(DockApp)) is DockApp sourceApp
            && sender is Button targetButton
            && targetButton.DataContext is DockApp targetApp
            && !ReferenceEquals(sourceApp, targetApp))
        {
            var isSourcePinned = _pinnedApps.Contains(sourceApp);
            var isTargetPinned = _pinnedApps.Contains(targetApp);

            if (isSourcePinned && isTargetPinned)
            {
                var oldIndex = _pinnedApps.IndexOf(sourceApp);
                var newIndex = _pinnedApps.IndexOf(targetApp);
                if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                {
                    ResetDisplacements();
                    _pinnedApps.Move(oldIndex, newIndex);
                    SavePinOrder();
                    _replacementSession?.RefreshPosition();
                    AnimateDropSettle(targetButton);
                }
            }
            else if (!isSourcePinned && isTargetPinned)
            {
                var targetIndex = _pinnedApps.IndexOf(targetApp);
                ResetDisplacements();
                PinRunningApp(sourceApp, targetIndex >= 0 ? targetIndex : -1);
                AnimateDropSettle(targetButton);
            }
            else if (!isSourcePinned && !isTargetPinned)
            {
                var oldIndex = _runningApps.IndexOf(sourceApp);
                var newIndex = _runningApps.IndexOf(targetApp);
                if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                {
                    ResetDisplacements();
                    _runningApps.Move(oldIndex, newIndex);
                    AnimateDropSettle(targetButton);
                }
            }
            else if (isSourcePinned && !isTargetPinned)
            {
                var oldIndex = _pinnedApps.IndexOf(sourceApp);
                if (oldIndex >= 0 && oldIndex != _pinnedApps.Count - 1)
                {
                    ResetDisplacements();
                    _pinnedApps.Move(oldIndex, _pinnedApps.Count - 1);
                    SavePinOrder();
                    _replacementSession?.RefreshPosition();
                    AnimateDropSettle(targetButton);
                }
            }
        }

        ResetDisplacements();
        e.Handled = true;
    }

    // ---- Start Button Drag & Drop ----

    private Point _startDragStartPoint;
    private bool _isStartPressing;

    private void StartButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _startDragStartPoint = e.GetPosition(this);
        _isStartPressing = true;
    }

    private void StartButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isStartPressing = false;
    }

    private void StartButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || !_isStartPressing) return;

        var currentPoint = e.GetPosition(this);
        var diff = _startDragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > Services.DockMetrics.DragStartDistance
            || Math.Abs(diff.Y) > Services.DockMetrics.DragStartDistance)
        {
            _isStartPressing = false;
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            ResetMagnification();

            StartButton.Opacity = 0.55;
            try
            {
                DragDrop.DoDragDrop(StartButton, new StartButtonDragData(), DragDropEffects.Move);
            }
            finally
            {
                StartButton.Opacity = 1.0;
            }
        }
    }

    private static bool TryGetDockItemToken(DragEventArgs e, out string token)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            token = "start";
            return true;
        }
        if (e.Data.GetData(typeof(DockApp)) is DockApp app)
        {
            token = "app:" + app.ExecutablePath;
            return true;
        }
        if (e.Data.GetData(typeof(DockStack)) is DockStack stack)
        {
            token = "stack:" + stack.Path;
            return true;
        }
        if (e.Data.GetData(typeof(WidgetCoordinator.WidgetDragPayload)) is WidgetCoordinator.WidgetDragPayload widget)
        {
            token = "widget:" + widget.WidgetId;
            return true;
        }
        token = string.Empty;
        return false;
    }

    private int GetDockInsertionIndex(DragEventArgs e)
    {
        var point = e.GetPosition(DockContents);
        var cursor = IsVertical ? point.Y : point.X;
        var index = 0;
        foreach (var element in _dockItemElements.Values)
        {
            var origin = element.TranslatePoint(default, DockContents);
            var center = (IsVertical ? origin.Y : origin.X)
                + (IsVertical ? element.ActualHeight : element.ActualWidth) / 2;
            if (cursor > center) index++;
            else break;
        }
        return index;
    }

    private void DockContents_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void DockContents_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetDockItemToken(e, out var token)) return;
        ResetDisplacements();
        MoveDockItem(token, GetDockInsertionIndex(e));
        e.Handled = true;
    }

    private void DockLeadControls_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void DockLeadControls_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        if (_preferences.Current.StartPlacement != StartButtonPlacement.Start)
        {
            _preferences.Save(_preferences.Current with { StartPlacement = StartButtonPlacement.Start });
            ApplyStartPlacement();
            AnimateDropSettle(StartButton);
        }

        e.Handled = true;
    }

    private void DockTrailControls_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void DockTrailControls_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        if (_preferences.Current.StartPlacement != StartButtonPlacement.End)
        {
            _preferences.Save(_preferences.Current with { StartPlacement = StartButtonPlacement.End });
            ApplyStartPlacement();
            AnimateDropSettle(StartButton);
        }

        e.Handled = true;
    }

    private void WidgetStrip_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void WidgetStrip_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        var dropPos = e.GetPosition(WidgetStrip);
        var dropCoord = IsVertical ? dropPos.Y : dropPos.X;

        var targetIndex = 0;
        var validIndex = 0;
        for (var i = 0; i < WidgetStrip.Children.Count; i++)
        {
            if (WidgetStrip.Children[i] is not FrameworkElement child || ReferenceEquals(child, StartButton))
            {
                continue;
            }

            var childPos = child.TranslatePoint(new Point(0, 0), WidgetStrip);
            var childCenter = IsVertical
                ? childPos.Y + (child.ActualHeight / 2)
                : childPos.X + (child.ActualWidth / 2);

            if (dropCoord > childCenter)
            {
                targetIndex = validIndex + 1;
            }
            validIndex++;
        }

        _preferences.Save(_preferences.Current with
        {
            StartPlacement = StartButtonPlacement.InWidgets,
            StartWidgetIndex = targetIndex
        });
        ApplyStartPlacement();
        AnimateDropSettle(StartButton);
        e.Handled = true;
    }
}

public sealed class StartButtonDragData;
