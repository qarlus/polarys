using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>Dock items after the apps: folder stacks and the Recycle Bin.</summary>
public partial class MainWindow
{
    private const int StackEntryLimit = 16;
    private readonly ObservableCollection<DockStack> _stacks = [];
    private readonly DockFlyout _stackFlyout = new();
    private DispatcherTimer? _recycleBinTimer;
    private bool? _recycleBinFull;
    private Point _dragStackStartPoint;
    private DockStack? _draggedStack;

    private static string DownloadsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private void InitializeExtras()
    {
        StackItems.ItemsSource = _stacks;
        _stackFlyout.Closed += (_, _) => _autoHideController?.EvaluateState();
        _recycleBinTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        _recycleBinTimer.Tick += (_, _) => UpdateRecycleBin();
        ApplyExtrasSettings();
    }

    private static readonly Brush StackTileBrush = CreateFrozenStackBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
    private static readonly Brush StackTileStroke = CreateFrozenStackBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    private static Brush CreateFrozenStackBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private int StackIconPixels => ApplicationIconProvider.ComputeTargetPixels(Profile.Icon, VisualTreeHelper.GetDpi(this).DpiScaleX);

    private void ApplyExtrasSettings()
    {
        var showBin = _preferences.Current.ShowRecycleBin;
        RecycleBinButton.Visibility = showBin ? Visibility.Visible : Visibility.Collapsed;
        if (showBin)
        {
            UpdateRecycleBin();
            _recycleBinTimer?.Start();
        }
        else
        {
            _recycleBinTimer?.Stop();
        }

        var folders = ConfiguredStackFolders();
        if (!folders.SequenceEqual(_stacks.Select(stack => stack.Path), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var stack in _stacks) stack.Dispose();
            _stacks.Clear();
            foreach (var folder in folders.Where(Directory.Exists))
            {
                var stack = new DockStack(folder, Dispatcher);
                stack.Icon = CreateStackIcon(stack, StackIconPixels);
                stack.ContentsChanged += () => stack.Icon = CreateStackIcon(stack, StackIconPixels);
                _stacks.Add(stack);
            }
        }

        ExtrasDivider.Visibility = (showBin || _stacks.Count > 0) && (VisiblePinnedCount > 0 || _runningApps.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExtrasDividerVisibility()
    {
        ExtrasDivider.Visibility = (RecycleBinButton.Visibility == Visibility.Visible || _stacks.Count > 0)
            && (VisiblePinnedCount > 0 || _runningApps.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<string> ConfiguredStackFolders() => _preferences.Current.StackFolders ?? [DownloadsFolder];

    private int ExtraButtonCount => _stacks.Count + (RecycleBinButton.Visibility == Visibility.Visible ? 1 : 0);

    internal static BitmapSource? CreateStackIcon(DockStack stack, int targetSize = 128)
    {
        var size = Math.Max(16, targetSize);
        var entries = stack.RecentEntries(4);
        if (entries.Count == 0)
        {
            return ApplicationIconProvider.GetForShellItem(stack.Path, size, iconOnly: true);
        }

        var scale = size / 128.0;
        var padding = 6 * scale;
        var itemSize = 48 * scale;
        var x0 = 13 * scale;
        var x1 = 67 * scale;
        var y0 = 13 * scale;
        var y1 = 67 * scale;
        var corner = 22 * scale;
        var subIconRequest = Math.Max(16, (int)Math.Ceiling(itemSize));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var tilePen = new Pen(StackTileStroke, Math.Max(0.5, 1.5 * scale));
            tilePen.Freeze();
            dc.DrawRoundedRectangle(StackTileBrush, tilePen, new Rect(padding, padding, size - padding * 2, size - padding * 2), corner, corner);

            var positions = new[]
            {
                new Rect(x0, y0, itemSize, itemSize),
                new Rect(x1, y0, itemSize, itemSize),
                new Rect(x0, y1, itemSize, itemSize),
                new Rect(x1, y1, itemSize, itemSize)
            };

            for (var i = 0; i < entries.Count && i < positions.Length; i++)
            {
                var icon = ApplicationIconProvider.GetForShellItem(entries[i].FullName, subIconRequest, iconOnly: true);
                if (icon is not null)
                {
                    dc.DrawImage(icon, positions[i]);
                }
            }
        }

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private IEnumerable<Button> GetExtraDockButtons()
    {
        foreach (var stack in _stacks)
        {
            if (StackItems.ItemContainerGenerator.ContainerFromItem(stack) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }

        if (RecycleBinButton.Visibility == Visibility.Visible) yield return RecycleBinButton;
    }

    private void UpdateRecycleBin()
    {
        var count = RecycleBin.ItemCount();
        var full = count > 0;
        if (_recycleBinFull == full) return;
        _recycleBinFull = full;
        RefreshRecycleBinIcon();
        EmptyRecycleBinItem.IsEnabled = full;
    }

    private void RefreshRecycleBinIcon()
    {
        var full = _recycleBinFull == true;
        RecycleBinIcon.Source = ThemePacks.ApplyIcon(full ? RecycleBin.FullIcon : RecycleBin.EmptyIcon, null,
            specialKey: full ? IconPack.RecycleBinFullKey : IconPack.RecycleBinKey);
    }

    private void RecycleBin_Click(object sender, RoutedEventArgs e)
    {
        _thumbnailPreview.Hide();
        if (sender is Button button) AnimateActivationFeedback(button);
        ReportResult(RecycleBin.Open());
    }

    private void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        ReportResult(RecycleBin.Empty(_source?.Handle ?? 0));
        _recycleBinFull = null;
        UpdateRecycleBin();
    }

    private void RecycleBin_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = false;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void RecycleBin_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out var droppedToken))
        {
            MoveDockItem(droppedToken, GetDockInsertionIndex(e));
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        // Let the drag source finish before Windows shows its own confirmation UI.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            ReportResult(RecycleBin.Delete(paths, _source?.Handle ?? 0));
            _recycleBinFull = null;
            UpdateRecycleBin();
        });
    }

    private static void ReportResult(string? error)
    {
        if (error is not null) ReportError(error);
    }

    private void Stack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DockStack stack } button) return;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        ResetMagnification();
        stack.HasNewItems = false;
        _stackFlyout.Toggle(button, () => BuildStackContent(stack), _preferences.Current.AnimationsEnabled);
    }

    private UIElement BuildStackContent(DockStack stack)
    {
        var root = new StackPanel { Width = 4 * 92 };
        root.Children.Add(DockFlyout.Heading(stack.Name));
        var entries = stack.RecentEntries(StackEntryLimit);
        if (entries.Count == 0)
        {
            root.Children.Add(DockFlyout.Caption(UiText.Get("StackEmpty")));
        }

        var flyoutIconPixels = ApplicationIconProvider.ComputeTargetPixels(48, VisualTreeHelper.GetDpi(this).DpiScaleX);
        var grid = new WrapPanel();
        foreach (var entry in entries)
        {
            var image = new Image { Width = 48, Height = 48, Margin = new Thickness(0, 2, 0, 4) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var label = new TextBlock
            {
                Text = entry.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, FontSize = 11
            };
            var tile = new StackPanel { Width = 76 };
            tile.Children.Add(image);
            tile.Children.Add(label);
            var path = entry.FullName;
            var button = DockFlyout.FlatButton(tile, (_, _) =>
            {
                _stackFlyout.Close();
                OpenPath(path);
            }, entry.Name);
            button.Padding = new Thickness(4, 6, 4, 6);
            button.Margin = new Thickness(2);
            grid.Children.Add(button);
            // Thumbnails can be slow (videos, network drives); fill them in after the flyout is shown.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => image.Source = ApplicationIconProvider.GetForShellItem(path, flyoutIconPixels));
        }

        root.Children.Add(grid);
        var footer = new TextBlock { Text = UiText.Get("OpenFolderInExplorer"), FontWeight = FontWeights.SemiBold };
        var open = DockFlyout.FlatButton(footer, (_, _) =>
        {
            _stackFlyout.Close();
            OpenPath(stack.Path);
        });
        open.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(open);
        return root;
    }

    private void OpenPath(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Warning($"Cannot open {path}: {ex.Message}");
            ReportError("ErrorOpenFileOrFolder");
        }
    }

    private void StackOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockStack stack }) OpenPath(stack.Path);
    }

    private void StackRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockStack stack }) return;
        var folders = ConfiguredStackFolders()
            .Where(folder => !string.Equals(folder, stack.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        SaveStackFolders(folders);
    }

    private void AddStack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiText.Get("AddStack"), Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        var folders = ConfiguredStackFolders().ToList();
        if (folders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;
        folders.Add(dialog.FolderName);
        SaveStackFolders(folders);
    }

    private void SaveStackFolders(IReadOnlyList<string> folders)
    {
        var error = _preferences.Save(_preferences.Current with { StackFolders = folders });
        if (error is not null) ReportError(error);
    }

    private void StackButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: DockStack stack } && _stacks.Contains(stack))
        {
            _dragStackStartPoint = e.GetPosition(this);
            _draggedStack = stack;
        }
        else
        {
            _draggedStack = null;
        }
    }

    private void StackButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggedStack = null;
    }

    private void AnimateStackDrag(Button button, DockStack stack)
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
            DragDrop.DoDragDrop(button, stack, DragDropEffects.Move);
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

    private void StackButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggedStack is null || sender is not Button button)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var diff = _dragStackStartPoint - currentPoint;
        if (Math.Abs(diff.X) > Services.DockMetrics.DragStartDistance
            || Math.Abs(diff.Y) > Services.DockMetrics.DragStartDistance)
        {
            var stack = _draggedStack;
            _draggedStack = null;
            AnimateStackDrag(button, stack);
        }
    }

    private void StackButton_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = false;
            return;
        }

        if (e.Data.GetDataPresent(typeof(DockStack))
            && sender is Button targetButton
            && targetButton.DataContext is DockStack targetStack
            && _stacks.Contains(targetStack))
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

    private void StackButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button && GetButtonIcon(button) is { } icon)
        {
            ResetIconDisplacement(icon);
        }
    }

    private void StackButton_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out var droppedToken))
        {
            MoveDockItem(droppedToken, GetDockInsertionIndex(e));
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(typeof(DockStack)) is DockStack sourceStack
            && sender is Button targetButton
            && targetButton.DataContext is DockStack targetStack
            && _stacks.Contains(sourceStack)
            && _stacks.Contains(targetStack)
            && !ReferenceEquals(sourceStack, targetStack))
        {
            var oldIndex = _stacks.IndexOf(sourceStack);
            var newIndex = _stacks.IndexOf(targetStack);
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                ResetDisplacements();
                _stacks.Move(oldIndex, newIndex);
                SaveStackFolders(_stacks.Select(s => s.Path).ToList());
                AnimateDropSettle(targetButton);
            }
        }
        ResetDisplacements();
        e.Handled = true;
    }

    /// <summary>Pins apps from a settings backup that are not pinned yet and exist on this PC; returns how many were added.</summary>
    internal int ImportPins(IReadOnlyList<PinBackup> pins)
    {
        var added = 0;
        foreach (var pin in pins)
        {
            if (!File.Exists(pin.ExecutablePath)
                || _pinnedApps.Any(app => string.Equals(app.ExecutablePath, pin.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var (shortcutPath, error) = WindowsTaskbarPinImporter.AddPin(pin.Name, pin.ExecutablePath, pin.Arguments);
            if (error is not null || shortcutPath is null)
            {
                AppLog.Warning($"Import pin {pin.ExecutablePath}: {error}");
                continue;
            }

            var pinnedApp = new DockApp(pin.Name, pin.ExecutablePath)
            {
                LaunchArguments = pin.Arguments,
                ShortcutPath = shortcutPath
            };
            pinnedApp.Icon = ThemePacks.ApplyIcon(ResolvePinnedIcon(pinnedApp), pinnedApp.ExecutablePath);
            _pinnedApps.Add(pinnedApp);
            added++;
        }

        if (added > 0)
        {
            if (_windowTracker is not null) UpdateRunningApps(VisibleSnapshot);
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        }

        return added;
    }

    private void DisposeExtras()
    {
        _recycleBinTimer?.Stop();
        _stackFlyout.Close();
        foreach (var stack in _stacks) stack.Dispose();
        _stacks.Clear();
    }
}
