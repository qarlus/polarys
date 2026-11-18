using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private readonly ObservableCollection<DockWidgetOption> _widgetOptions = [];
    private readonly Dictionary<string, ComboBox> _widgetDensityBoxes = [];
    private readonly DockWidgetService _widgetPreviewService = new(TimeProvider.System);
    private readonly DispatcherTimer _widgetPreviewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _startupEnabled;
    private bool _startupAvailable;
    private DockSettings _applied;
    private bool _closingApplication;
    private bool _initializing = true;
    private bool _closed;
    private bool _sourceReady;
    private HwndSource? _source;
    private string? _editingThemeId;
    private ThemePack? _editingPack;
    private WidgetPackManifest _editorManifest = new();
    private ThemeEditScope _editorScope = ThemeEditScope.Dark;
    private IReadOnlyList<ThemeToken>? _editorTokens;
    private bool _editorBusy;
    private readonly DispatcherTimer _themeEditTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private (ThemeToken Token, Func<JsonElement?> Parse, Action Refresh)? _pendingTokenEdit;
    private readonly string _radioGroupScope = Guid.NewGuid().ToString("N");
    private readonly RoutedEventHandler _preferenceEditedHandler;

    internal bool IsClosingOrClosed => _isClosing || _closed;

    public SettingsWindow(ApplicationPreferences preferences)
    {
        _preferences = preferences;
        _applied = preferences.Current;
        _preferenceEditedHandler = PreferenceEdited;
        InitializeComponent();
        ScopeRadioGroups(this);
        foreach (var option in _widgetPreviewService.CreateOptions(_applied.InstalledWidgets, _applied.Widgets))
        {
            _widgetOptions.Add(option);
        }
        AttachWidgetSettingsPanels();
        WidgetOptionsList.ItemsSource = _widgetOptions;
        _preferences.Changed += Preferences_Changed;
        _widgetPreviewTimer.Tick += WidgetPreviewTimer_Tick;
        _themeEditTimer.Tick += (_, _) => FlushPendingTokenEdit();
        ThemeEditorScopeDark.Checked += ThemeEditorScope_Changed;
        ThemeEditorScopeLight.Checked += ThemeEditorScope_Changed;
        ThemeEditorScopeShared.Checked += ThemeEditorScope_Changed;
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        };
        IsVisibleChanged += Settings_IsVisibleChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            RemoveHandler(ToggleButton.CheckedEvent, _preferenceEditedHandler);
            RemoveHandler(ToggleButton.UncheckedEvent, _preferenceEditedHandler);
            _autoSaveTimer.Stop();
            _widgetPreviewTimer.Stop();
            _widgetPreviewTimer.Tick -= WidgetPreviewTimer_Tick;
            IsVisibleChanged -= Settings_IsVisibleChanged;
            _preferences.Changed -= Preferences_Changed;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            WidgetOptionsList.ItemsSource = null;
            _widgetOptions.Clear();
            _widgetDensityBoxes.Clear();
            ThemeManager.SetSurfaceOpacity(_preferences.Current.SurfaceOpacity);
            (Application.Current.MainWindow as MainWindow)?.SetSizePreview(null, null);
            _widgetOptions.Clear();
        };
        DarkTheme.IsChecked = _applied.Theme == AppTheme.Dark;
        LightTheme.IsChecked = _applied.Theme == AppTheme.Light;
        LanguageBox.ItemsSource = UiText.Languages;
        SelectLanguage(_applied.Language);
        SelectAlignment(_applied);
        AnimationsToggle.IsChecked = _applied.AnimationsEnabled;
        PreviewsToggle.IsChecked = _applied.PreviewsEnabled;
        BadgesToggle.IsChecked = _applied.NotificationBadgesEnabled;
        AutoHideDisabled.IsChecked = _applied.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = _applied.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = _applied.AutoHide == AutoHideMode.Always;
        OpacitySlider.Value = _applied.SurfaceOpacity * 100;
        UpdateOpacityLabel();
        MagnificationToggle.IsChecked = _applied.MagnificationEnabled;
        CompactSize.IsChecked = _applied.Size == DockSize.Compact;
        MediumSize.IsChecked = _applied.Size == DockSize.Medium;
        LargeSize.IsChecked = _applied.Size == DockSize.Large;
        DockScaleSlider.Value = Math.Round(_applied.DockScale * 100);
        UpdateDockScaleLabel();
        FocusMinutesSlider.Value = _applied.FocusMinutes;
        BreakMinutesSlider.Value = _applied.FocusBreakMinutes;
        FocusSessionsSlider.Value = _applied.FocusSessions;
        LoadFeatureSelection(_applied);
        LoadThemePacks(_applied.ThemePack);
        UpdateWidgetOptionState();
        var startup = _startup.Read();
        _startupEnabled = startup.Enabled;
        _startupAvailable = startup.Error is null;
        StartupToggle.IsEnabled = _startupAvailable;
        if (startup.Error is not null)
        {
            StartupToggle.ToolTip = UiText.Get(startup.Error);
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(StartupToggle, true);
        }
        StartupToggle.IsChecked = _startupEnabled;
        var errors = new[] { preferences.LoadError, startup.Error }.OfType<string>();
        var message = string.Join(" ", errors.Select(UiText.Get));
        if (message.Length > 0) Trace.TraceWarning("Settings initialization warning: {0}", message);
        var workArea = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        AddHandler(ToggleButton.CheckedEvent, _preferenceEditedHandler);
        AddHandler(ToggleButton.UncheckedEvent, _preferenceEditedHandler);
        DensityAuto.Checked += (_, _) => TriggerAutoSave(immediate: true);
        DensityCompact.Checked += (_, _) => TriggerAutoSave(immediate: true);
        DensityFull.Checked += (_, _) => TriggerAutoSave(immediate: true);
        _initializing = false;
    }

    // Scope RadioButton.GroupName per window instance so closing one SettingsWindow cannot uncheck
    // another instance's radio buttons via WPF's global group registry.
    private void ScopeRadioGroups(DependencyObject root)
    {
        if (root is Visual)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is RadioButton radio && !string.IsNullOrEmpty(radio.GroupName)
                    && !radio.GroupName.EndsWith(_radioGroupScope, StringComparison.Ordinal))
                {
                    radio.GroupName = $"{radio.GroupName}_{_radioGroupScope}";
                }
                ScopeRadioGroups(child);
            }
        }
        if (root is FrameworkElement fe)
        {
            foreach (var obj in LogicalTreeHelper.GetChildren(fe))
            {
                if (obj is DependencyObject dep && !IsVisualChild(fe, dep))
                {
                    if (dep is RadioButton radio && !string.IsNullOrEmpty(radio.GroupName)
                        && !radio.GroupName.EndsWith(_radioGroupScope, StringComparison.Ordinal))
                    {
                        radio.GroupName = $"{radio.GroupName}_{_radioGroupScope}";
                    }
                    ScopeRadioGroups(dep);
                }
            }
        }

        static bool IsVisualChild(DependencyObject parent, DependencyObject candidate) =>
            candidate is Visual && VisualTreeHelper.GetParent(candidate) == parent;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        _sourceReady = true;
        UpdateBackdrop();
        if (_preferences.Current.AnimationsEnabled)
        {
            PlayOpenAnimation();
        }
    }

    private void PlayOpenAnimation()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(0.96, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var transform = new ScaleTransform(0.96, 0.96);
        SettingsFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        SettingsFrame.RenderTransform = transform;
        SettingsFrame.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            Trace.TraceWarning("Settings monitor work area unavailable: {0}", Marshal.GetLastWin32Error());
            return 0;
        }
        var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        sizing.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        sizing.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var dpi = GetDpiForWindow(window);
        var scale = dpi > 0 ? dpi / 96d : 1;
        sizing.MinTrackSize = new NativePoint((int)Math.Ceiling(MinWidth * scale), (int)Math.Ceiling(MinHeight * scale));
        if (MaxWidth > 0 && !double.IsInfinity(MaxWidth) && !double.IsNaN(MaxWidth))
            sizing.MaxTrackSize = new NativePoint((int)Math.Ceiling(MaxWidth * scale), (int)Math.Ceiling(MaxHeight * scale));
        Marshal.StructureToPtr(sizing, lParam, false);
        handled = true;
        return 0;
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        if (_applied.Language != _preferences.Current.Language)
        {
            var localizedOptions = _widgetPreviewService.CreateOptions([], []).ToDictionary(option => option.Id, StringComparer.Ordinal);
            foreach (var option in _widgetOptions)
            {
                var localized = localizedOptions[option.Id];
                option.UpdateText(localized.Label, localized.Description);
            }
            RefreshWidgetPreviews();
        }
        ThemeManager.SetSurfaceOpacity(Math.Round(OpacitySlider.Value) / 100);
        UpdateOpacityLabel();
        UpdateDockScaleLabel();
        UpdateWidgetOptionState();
        UpdateBackdrop();
    }

    private void UpdateBackdrop()
    {
        if (!_sourceReady) return;
        var glass = SettingsBackdrop.TryEnable(this, _preferences.Current.Theme);
        SettingsFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, glass ? "SettingsGlassMaterial" : "SettingsBackground");
        if (glass)
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(-1);
            SettingsFrame.ClearValue(System.Windows.Controls.Border.CornerRadiusProperty);
        }
        else
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
            SettingsFrame.CornerRadius = new CornerRadius(0);
        }
    }

    private DockSettings ReadSelection()
    {
        var enabledWidgets = _widgetOptions.Where(option => option.IsInstalled && option.IsEnabled).Select(option => option.Id).ToArray();
        var installedWidgets = _widgetOptions.Where(option => option.IsInstalled).Select(option => option.Id).ToArray();
        bool IsEnabled(string id) => enabledWidgets.Contains(id, StringComparer.OrdinalIgnoreCase);
        return _preferences.Current with
        {
            Theme = LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.Dark,
            Language = LanguageBox.SelectedItem is AppLanguage language ? language : _preferences.Current.Language,
            // Full width keeps the last floating alignment so switching back restores it.
            Position = RightPosition.IsChecked == true ? DockPosition.Right : LeftPosition.IsChecked == true ? DockPosition.Left
                : CenterPosition.IsChecked == true ? DockPosition.Center : _preferences.Current.Position,
            AnimationsEnabled = AnimationsToggle.IsChecked == true,
            PreviewsEnabled = PreviewsToggle.IsChecked == true,
            NotificationBadgesEnabled = BadgesToggle.IsChecked == true,
            AutoHide = AutoHideAlways.IsChecked == true ? AutoHideMode.Always : AutoHideSmart.IsChecked == true ? AutoHideMode.Smart : AutoHideMode.Disabled,
            GlobalWidgetDensity = DensityCompact.IsChecked == true ? WidgetDensityMode.Compact
                : DensityFull.IsChecked == true ? WidgetDensityMode.Full : WidgetDensityMode.Auto,
            WidgetDensityOverrides = ReadWidgetDensityOverrides(),
            SurfaceOpacity = Math.Round(OpacitySlider.Value) / 100,
            Widgets = enabledWidgets,
            InstalledWidgets = installedWidgets,
            MagnificationEnabled = MagnificationToggle.IsChecked == true,
            Layout = FullWidthLayout.IsChecked == true ? DockLayout.FullWidth : DockLayout.Floating,
            Size = LargeSize.IsChecked == true ? DockSize.Large : MediumSize.IsChecked == true ? DockSize.Medium : DockSize.Compact,
            DockScale = Math.Round(DockScaleSlider.Value) / 100,
            ShowClock = IsEnabled("Clock"),
            ShowMedia = IsEnabled("Media"),
            ShowSystemStats = IsEnabled("SystemStats"),
            ShowAudio = IsEnabled("Audio"),
            ShowQuickSettings = IsEnabled("QuickSettings"),
            ShowFocus = IsEnabled("Focus"),
            FocusMinutes = (int)Math.Round(FocusMinutesSlider.Value),
            FocusBreakMinutes = (int)Math.Round(BreakMinutesSlider.Value),
            FocusSessions = (int)Math.Round(FocusSessionsSlider.Value),
            ShowRecycleBin = RecycleBinToggle.IsChecked == true,
            TargetDisplay = (TargetDisplayBox.SelectedItem as DisplayChoice)?.DeviceName,
            ShowOnAllMonitors = AllMonitorsToggle.IsChecked == true,
            SecondaryApps = (SecondaryAppsBox.SelectedItem as SecondaryAppChoice)?.Scope ?? SecondaryAppScope.CurrentMonitorOnly,
            SecondaryWidgets = SecondaryWidgetsToggle.IsChecked == true,
            CurrentDesktopOnly = CurrentDesktopToggle.IsChecked == true,
            CheckUpdates = UpdatesToggle.IsChecked == true,
            Edge = LeftEdge.IsChecked == true ? DockEdge.Left : RightEdge.IsChecked == true ? DockEdge.Right : DockEdge.Bottom,
            ShowVirtualDesktops = IsEnabled("Desktops"),
            ShowTray = IsEnabled("Tray"),
            ShowWeather = IsEnabled("Weather"),
            WeatherCity = WeatherCityBox.Text.Trim(),
            LauncherHotkey = LauncherAltSpace.IsChecked == true ? HotkeyChoice.AltSpace
                : LauncherCtrlSpace.IsChecked == true ? HotkeyChoice.CtrlSpace
                : LauncherCtrlAltSpace.IsChecked == true ? HotkeyChoice.CtrlAltSpace : HotkeyChoice.Disabled,
            DockHotkey = DockCtrlAltD.IsChecked == true ? HotkeyChoice.CtrlAltD : DockWinAltD.IsChecked == true ? HotkeyChoice.WinAltD : HotkeyChoice.Disabled,
            StartAction = StartOpensLauncher.IsChecked == true ? StartButtonAction.Launcher : StartButtonAction.WindowsStart,
            StartPlacement = StartHidden.IsChecked == true ? StartButtonPlacement.Hidden
                : StartLast.IsChecked == true ? StartButtonPlacement.End
                : StartFirst.IsChecked == true ? StartButtonPlacement.Start
                : _preferences.Current.StartPlacement,
            StartWidgetIndex = _preferences.Current.StartWidgetIndex,
            WheelVolume = WheelVolumeToggle.IsChecked == true,
            Clock24Hour = Clock12HourRadio.IsChecked != true,
            ClockShowSeconds = ClockSecondsToggle.IsChecked == true,
            DateStyle = DateStyleHidden.IsChecked == true ? ClockDateStyle.Hidden
                : DateStyleNumeric.IsChecked == true ? ClockDateStyle.NumericDate
                : DateStyleShort.IsChecked == true ? ClockDateStyle.ShortDateOnly
                : ClockDateStyle.DayAndShortDate,
            StatsMeters = ReadStatsMeters(),
            ThemePack = (ThemePackBox.SelectedItem as ThemePackChoice)?.Id
        };
    }

    private sealed record ThemePackChoice(string? Id, string Name, string Details);
    private sealed record DisplayChoice(string? DeviceName, string DisplayName);
    private sealed record SecondaryAppChoice(SecondaryAppScope Scope, string DisplayName);

    private void LoadDisplayChoices(string? selectedDevice)
    {
        var primaryLabel = UiText.Get("TargetDisplayPrimary");
        var choices = new List<DisplayChoice> { new(null, primaryLabel) };
        try
        {
            var displays = DisplayService.GetDisplays();
            foreach (var d in displays)
            {
                var suffix = d.IsPrimary ? $" ({primaryLabel})" : "";
                var label = $"{d.Index}: {d.Bounds.Width:0}x{d.Bounds.Height:0}{suffix}";
                choices.Add(new DisplayChoice(d.DeviceName, label));
            }
        }
        catch (Exception ex)
        {
            AppLog.ReportError("ErrorDisplayDimensions", ex);
        }

        TargetDisplayBox.ItemsSource = choices;
        TargetDisplayBox.SelectedItem = !string.IsNullOrWhiteSpace(selectedDevice)
            ? choices.FirstOrDefault(c => string.Equals(c.DeviceName, selectedDevice, StringComparison.OrdinalIgnoreCase)) ?? choices[0]
            : choices[0];
    }

    private void LoadSecondaryAppChoices(SecondaryAppScope selectedScope)
    {
        var choices = new List<SecondaryAppChoice>
        {
            new(SecondaryAppScope.CurrentMonitorOnly, UiText.Get("SecondaryAppsCurrent")),
            new(SecondaryAppScope.AllMonitors, UiText.Get("SecondaryAppsAll"))
        };
        SecondaryAppsBox.ItemsSource = choices;
        SecondaryAppsBox.SelectedItem = choices.FirstOrDefault(c => c.Scope == selectedScope) ?? choices[0];
    }

    private void UpdateMultiMonitorControls()
    {
        var allMonitors = AllMonitorsToggle.IsChecked == true;
        SecondaryAppsRow.IsEnabled = allMonitors;
        SecondaryWidgetsRow.IsEnabled = allMonitors;
    }

    private void TargetDisplayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
    }

    private void SecondaryAppsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
    }

    private void SelectLanguage(AppLanguage language)
    {
        var selected = UiText.Languages.FirstOrDefault(candidate => candidate == language);
        LanguageBox.SelectedItem = LanguageCatalog.IsSupported(selected) ? selected : AppLanguage.English;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PreferenceEdited(sender, e);

    private void LoadThemePacks(string? selected)
    {
        var choices = new List<ThemePackChoice> { new(null, UiText.Get("ThemePackNone"), string.Empty) };
        choices.AddRange(ThemePacks.Discover().Select(pack => new ThemePackChoice(pack.Id, pack.DisplayName, DescribeThemePack(pack))));
        // A stored theme whose folder is gone stays listed so an unrelated edit does not silently clear it.
        if (!string.IsNullOrWhiteSpace(selected) && !choices.Any(choice => string.Equals(choice.Id, selected, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new ThemePackChoice(selected, selected + " (?)", UiText.Get("ThemePackMissing")));
        }

        ThemePackBox.ItemsSource = choices;
        ThemePackBox.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Id, selected, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
        UpdateThemePackDetails();
    }

    private static string DescribeThemePack(ThemePack pack)
    {
        var parts = new List<string>();
        if (pack.Icons is not null) parts.Add(UiText.Get("ThemePackIcons"));
        if (pack.Widgets is not null) parts.Add(UiText.Get("ThemePackWidgets"));
        var contents = string.Join(" · ", parts);
        var author = string.IsNullOrWhiteSpace(pack.Manifest.Author) ? string.Empty : " — " + pack.Manifest.Author;
        return contents + author;
    }

    private void UpdateThemePackDetails()
    {
        var details = (ThemePackBox.SelectedItem as ThemePackChoice)?.Details ?? string.Empty;
        ThemePackDetails.Text = details;
        ThemePackDetails.Visibility = details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ThemePackBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateThemePackDetails();
        PreferenceEdited(sender, e);
    }

    private void ThemePackOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ThemePacks.Root);
            using var _ = Process.Start(new ProcessStartInfo(ThemePacks.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.ReportError("ErrorThemeFolderOpen", ex);
        }
    }

    private void ThemePackReload_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ThemePackBox.SelectedItem as ThemePackChoice)?.Id;
        _initializing = true;
        LoadThemePacks(selected);
        _initializing = false;
        ThemePacks.Select(selected, reload: true);
        ThemePacks.ApplyWidgets(_preferences.Current.Theme);
    }

    private void ThemePackNew_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPrompt.Ask(UiText.Get("ThemePackNew"), UiText.Get("ThemePackNew"), UiText.Get("ThemePackNewDefault"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = ThemePackTransfer.CreateTheme(name, null, null);
        ReloadThemePacks(id);
        OpenThemeEditor(id);
    }

    private void ThemePackEdit_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ThemePackBox.SelectedItem as ThemePackChoice)?.Id;
        if (selected is null)
        {
            // Restyling the built-in look starts a fresh theme the user can name.
            var name = TextPrompt.Ask(UiText.Get("ThemePackEdit"), UiText.Get("ThemePackNew"), UiText.Get("ThemePackNewDefault"));
            if (string.IsNullOrWhiteSpace(name)) return;
            selected = ThemePackTransfer.CreateTheme(name, null, null);
            ReloadThemePacks(selected);
        }
        OpenThemeEditor(selected);
    }

    private void ThemePackDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ThemePackBox.SelectedItem as ThemePackChoice)?.Id;
        if (string.IsNullOrWhiteSpace(selected)) { AppLog.ReportError("ErrorThemeMissing"); return; }
        var pack = ThemePacks.Discover().FirstOrDefault(p => string.Equals(p.Id, selected, StringComparison.OrdinalIgnoreCase));
        var name = TextPrompt.Ask(UiText.Get("ThemePackDuplicate"), UiText.Get("ThemePackDuplicate"),
            (pack?.DisplayName ?? selected) + " " + UiText.Get("ThemePackCopySuffix"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = ThemePackTransfer.CreateTheme(name, null, selected);
        ReloadThemePacks(id);
        MessageBox.Show(this, UiText.Get("ThemePackDuplicated"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackExport_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ThemePackBox.SelectedItem as ThemePackChoice)?.Id;
        if (string.IsNullOrWhiteSpace(selected)) { AppLog.ReportError("ErrorThemeMissing"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = UiText.Get("ThemePackSaveFile"),
            Filter = UiText.Get("ThemePackFileFilter"),
            FileName = selected + ThemePackTransfer.ExportExtension,
            DefaultExt = ThemePackTransfer.ExportExtension
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = ThemePackTransfer.ExportTheme(selected, dialog.FileName);
        if (error is not null) { AppLog.ReportError(error); return; }
        MessageBox.Show(this, UiText.Get("ThemePackExported"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = UiText.Get("ThemePackChooseFile"),
            Filter = UiText.Get("ThemePackFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var (id, error) = ThemePackTransfer.ImportTheme(dialog.FileName);
        if (error is not null) { AppLog.ReportError(error); return; }
        ReloadThemePacks(id);
        MessageBox.Show(this, UiText.Get("ThemePackImported"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ThemePackDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ThemePackBox.SelectedItem as ThemePackChoice)?.Id;
        if (string.IsNullOrWhiteSpace(selected)) { AppLog.ReportError("ErrorThemeMissing"); return; }
        var result = MessageBox.Show(this, UiText.Format("ThemePackDeleteConfirm", selected), "OpenTaskbar",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        var error = ThemePackTransfer.DeleteTheme(selected);
        if (error is not null) { AppLog.ReportError(error); return; }
        ReloadThemePacks(null);
        MessageBox.Show(this, UiText.Get("ThemePackDeleted"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ReloadThemePacks(string? selected)
    {
        _initializing = true;
        LoadThemePacks(selected);
        _initializing = false;
        ThemePacks.Select(selected, reload: true);
        ThemePacks.ApplyWidgets(_preferences.Current.Theme);
    }

    private void OpenThemeEditor(string themeId)
    {
        var pack = ThemePacks.Discover().FirstOrDefault(p => string.Equals(p.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (pack is null) { AppLog.ReportError("ErrorThemeMissing"); return; }
        _editingThemeId = themeId;
        _editingPack = pack;
        _editorManifest = ThemeEditor.LoadManifest(pack);
        _editorTokens ??= ThemeEditor.Catalog();
        _editorScope = _preferences.Current.Theme == AppTheme.Light ? ThemeEditScope.Light : ThemeEditScope.Dark;
        ThemeEditorTitle.Text = $"{UiText.Get("ThemeEditorTitle")} — {pack.DisplayName}";
        ThemeEditorHint.Text = UiText.Get("ThemeEditorHint");
        _editorBusy = true;
        ThemeEditorScopeDark.IsChecked = _editorScope == ThemeEditScope.Dark;
        ThemeEditorScopeLight.IsChecked = _editorScope == ThemeEditScope.Light;
        ThemeEditorScopeShared.IsChecked = _editorScope == ThemeEditScope.Shared;
        _editorBusy = false;
        BuildThemeEditorRows();
        ThemeEditorPanel.Visibility = Visibility.Visible;
    }

    private void ThemeEditorBack_Click(object sender, RoutedEventArgs e)
    {
        FlushPendingTokenEdit();
        ThemeEditorPanel.Visibility = Visibility.Collapsed;
        _editingThemeId = null;
        _editingPack = null;
    }

    private void ThemeEditorScope_Changed(object sender, RoutedEventArgs e)
    {
        if (_editorBusy || sender is not RadioButton { IsChecked: true }) return;
        FlushPendingTokenEdit();
        _editorScope = ReferenceEquals(sender, ThemeEditorScopeDark) ? ThemeEditScope.Dark
            : ReferenceEquals(sender, ThemeEditorScopeLight) ? ThemeEditScope.Light
            : ThemeEditScope.Shared;
        BuildThemeEditorRows();
    }

    private Dictionary<string, JsonElement> EditorSection() => ThemeEditor.Section(_editorManifest, _editorScope);

    private void BuildThemeEditorRows()
    {
        ThemeEditorRows.Children.Clear();
        if (_editorTokens is null || _editingPack is null) return;
        var section = EditorSection();
        var active = _preferences.Current.Theme;
        var first = true;
        string? lastGroup = null;
        foreach (var token in _editorTokens)
        {
            if (token.Group != lastGroup)
            {
                lastGroup = token.Group;
                ThemeEditorRows.Children.Add(new TextBlock
                {
                    Text = UiText.Get("ThemeEditorGroup" + lastGroup),
                    Style = (Style)FindResource("SettingsSectionHeading"),
                    Margin = new Thickness(0, first ? 0 : 14, 0, 8)
                });
                first = false;
            }
            ThemeEditorRows.Children.Add(BuildTokenRow(token, section, active));
        }
    }

    private FrameworkElement BuildTokenRow(ThemeToken token, Dictionary<string, JsonElement> section, AppTheme active)
    {
        var baseValue = token.BaseFor(_editorScope, active);
        var effective = TokenValue(token, baseValue, section);

        var label = new TextBlock { Style = (Style)FindResource("SettingsRowLabel"), VerticalAlignment = VerticalAlignment.Center };
        var reset = new Button
        {
            Content = "\uE777",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Style = (Style)FindResource("SettingsButton"),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = UiText.Get("ThemeEditorReset")
        };
        System.Windows.Automation.AutomationProperties.SetName(reset, UiText.Get("ThemeEditorReset"));

        void MarkOverridden()
        {
            var hasOverride = EditorSection().ContainsKey(token.Key);
            label.Text = token.Label + (hasOverride ? " \u2022" : string.Empty);
            reset.Visibility = hasOverride ? Visibility.Visible : Visibility.Collapsed;
        }

        var editor = BuildTokenEditor(token, baseValue, effective, MarkOverridden);
        MarkOverridden();
        reset.Click += (_, _) =>
        {
            FlushPendingTokenEdit();
            CommitToken(token, null);
            BuildThemeEditorRows();
        };

        var dock = new DockPanel();
        DockPanel.SetDock(reset, Dock.Right);
        DockPanel.SetDock(editor, Dock.Right);
        dock.Children.Add(reset);
        dock.Children.Add(editor);
        dock.Children.Add(label);
        return new Border { Style = (Style)FindResource("SettingsRow"), Child = dock };
    }

    private object TokenValue(ThemeToken token, object baseValue, Dictionary<string, JsonElement>? section = null)
    {
        section ??= EditorSection();
        return section.TryGetValue(token.Key, out var json) ? WidgetPack.Convert(json, baseValue) ?? baseValue : baseValue;
    }

    /// <summary>The right-hand control for a token: color fields (gradient stops plus a direction
    /// picker) for brushes, a text field for radii, thicknesses, sizes and fonts.</summary>
    private FrameworkElement BuildTokenEditor(ThemeToken token, object baseValue, object effective, Action markOverridden)
    {
        if (effective is not Brush brush)
        {
            var field = TokenField(ThemeEditor.Display(effective), token, effective is FontFamily ? 220 : 140);
            field.TextChanged += (_, _) => QueueTokenEdit(token, () => ParseScalar(field, baseValue), markOverridden);
            field.LostFocus += (_, _) => CommitScalar(token, field, baseValue, markOverridden);
            return field;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var swatch = new Border
        {
            Width = 42,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = brush,
            BorderBrush = (Brush)FindResource("StrokeSubtle"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(swatch);

        if (brush is GradientBrush)
        {
            var fields = ((GradientBrush)brush).GradientStops.Select(stop => TokenField(stop.Color.ToString(), token, 84)).ToList();
            var direction = new ComboBox { Width = 104, VerticalAlignment = VerticalAlignment.Center };
            var names = new[] { "Vertical", "Horizontal", "Diagonal" };
            direction.ItemsSource = names.Select(n => new ComboBoxItem { Content = UiText.Get("ThemeEditorDirection" + n), Tag = n.ToLowerInvariant() }).ToList();
            var current = brush is LinearGradientBrush linear
                ? linear.EndPoint is { X: 1, Y: 0 } ? "horizontal" : linear.EndPoint is { X: 1, Y: 1 } ? "diagonal" : "vertical"
                : "vertical";
            direction.SelectedIndex = Array.IndexOf(names, char.ToUpperInvariant(current[0]) + current[1..]);

            JsonElement? Parse()
            {
                var raw = JsonSerializer.SerializeToElement(new
                {
                    stops = fields.Select(f => f.Text.Trim()).ToArray(),
                    direction = (direction.SelectedItem as ComboBoxItem)?.Tag as string ?? "vertical"
                });
                return WidgetPack.Convert(raw, baseValue) is { } converted ? ThemeEditor.Serialize(converted) : null;
            }

            void Commit()
            {
                FlushPendingTokenEdit();
                if (Parse() is not { } json)
                {
                    if (TokenValue(token, baseValue) is GradientBrush restored)
                        for (var i = 0; i < fields.Count && i < restored.GradientStops.Count; i++)
                            fields[i].Text = restored.GradientStops[i].Color.ToString();
                    return;
                }
                CommitToken(token, json);
                swatch.Background = WidgetPack.Convert(json, baseValue) as Brush ?? swatch.Background;
                markOverridden();
            }

            foreach (var field in fields)
            {
                panel.Children.Add(field);
                field.TextChanged += (_, _) => QueueTokenEdit(token, Parse, () =>
                {
                    if (Parse() is { } json) swatch.Background = WidgetPack.Convert(json, baseValue) as Brush ?? swatch.Background;
                    markOverridden();
                });
                field.LostFocus += (_, _) => Commit();
            }
            panel.Children.Add(direction);
            direction.SelectionChanged += (_, _) => Commit();
            return panel;
        }

        var hex = TokenField(ThemeEditor.Display(effective), token, 96);
        panel.Children.Add(hex);

        JsonElement? ParseSolid()
        {
            var raw = JsonSerializer.SerializeToElement(hex.Text.Trim());
            return WidgetPack.Convert(raw, baseValue) is { } converted ? ThemeEditor.Serialize(converted) : null;
        }

        void CommitSolid()
        {
            FlushPendingTokenEdit();
            if (ParseSolid() is not { } json)
            {
                hex.Text = ThemeEditor.Display(TokenValue(token, baseValue));
                return;
            }
            CommitToken(token, json);
            swatch.Background = WidgetPack.Convert(json, baseValue) as Brush ?? swatch.Background;
            markOverridden();
        }

        hex.TextChanged += (_, _) => QueueTokenEdit(token, ParseSolid, () =>
        {
            if (ParseSolid() is { } json) swatch.Background = WidgetPack.Convert(json, baseValue) as Brush ?? swatch.Background;
            markOverridden();
        });
        hex.LostFocus += (_, _) => CommitSolid();
        return panel;
    }

    private TextBox TokenField(string text, ThemeToken token, double width)
    {
        var field = new TextBox
        {
            Text = text,
            Width = width,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = token.Key
        };
        System.Windows.Automation.AutomationProperties.SetName(field, token.Label);
        return field;
    }

    private void QueueTokenEdit(ThemeToken token, Func<JsonElement?> parse, Action refresh)
    {
        _pendingTokenEdit = (token, parse, refresh);
        _themeEditTimer.Stop();
        _themeEditTimer.Start();
    }

    private void FlushPendingTokenEdit()
    {
        _themeEditTimer.Stop();
        if (_pendingTokenEdit is { } pending)
        {
            _pendingTokenEdit = null;
            if (pending.Parse() is { } json)
            {
                CommitToken(pending.Token, json);
                pending.Refresh();
            }
        }
    }

    private JsonElement? ParseScalar(TextBox box, object baseValue)
    {
        var text = box.Text.Trim();
        JsonElement raw;
        if (baseValue is double or CornerRadius or Thickness
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            raw = JsonSerializer.SerializeToElement(number);
        else
            raw = JsonSerializer.SerializeToElement(text);
        return WidgetPack.Convert(raw, baseValue) is { } converted ? ThemeEditor.Serialize(converted) : null;
    }

    private void CommitScalar(ThemeToken token, TextBox box, object baseValue, Action refresh)
    {
        FlushPendingTokenEdit();
        if (ParseScalar(box, baseValue) is not { } json)
        {
            box.Text = ThemeEditor.Display(TokenValue(token, baseValue));
            return;
        }
        CommitToken(token, json);
        refresh();
    }

    private void CommitToken(ThemeToken token, JsonElement? json)
    {
        if (_editingPack is null || _editingThemeId is null) return;
        var section = EditorSection();
        if (json is null) section.Remove(token.Key);
        else section[token.Key] = json.Value;
        _editorManifest = ThemeEditor.WithSection(_editorManifest, _editorScope, section);
        if (ThemeEditor.SaveManifest(_editingPack, _editorManifest) is { } error)
        {
            AppLog.ReportError(error);
            return;
        }
        ThemePacks.Select(_editingThemeId, reload: true);
        ThemePacks.ApplyWidgets(_preferences.Current.Theme);
        UpdateThemePackDetails();
    }

    private SystemMeters ReadStatsMeters() =>
        (StatsCpuToggle.IsChecked == true ? SystemMeters.Cpu : 0)
        | (StatsMemoryToggle.IsChecked == true ? SystemMeters.Memory : 0)
        | (StatsBatteryToggle.IsChecked == true ? SystemMeters.Battery : 0)
        | (StatsCpuTemperatureToggle.IsChecked == true ? SystemMeters.CpuTemperature : 0)
        | (StatsGpuToggle.IsChecked == true ? SystemMeters.Gpu : 0)
        | (StatsGpuTemperatureToggle.IsChecked == true ? SystemMeters.GpuTemperature : 0);

    private void RefreshWidgetPreviews() => DockWidgetService.Refresh(
        _widgetOptions.Select(option => option.Preview), _preferences.Current.Language);

    private void WidgetPreviewTimer_Tick(object? sender, EventArgs e) => RefreshWidgetPreviews();

    private void Settings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateWidgetPreviewRefresh();

    private void SettingsTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initializing && ReferenceEquals(e.Source, SettingsTabs))
        {
            UpdateWidgetPreviewRefresh();
            if (_preferences.Current.AnimationsEnabled && SettingsTabs.Template?.FindName("PART_SelectedContentHost", SettingsTabs) is ContentPresenter host)
            {
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                easing.Freeze();

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing };
                fade.Completed += (_, _) => host.BeginAnimation(UIElement.OpacityProperty, null);
                host.BeginAnimation(UIElement.OpacityProperty, fade);

                // A short upward slide gives page changes a sense of direction without moving the sidebar.
                var transform = host.RenderTransform as TranslateTransform ?? new TranslateTransform();
                host.RenderTransform = transform;
                var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing };
                slide.Completed += (_, _) =>
                {
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    transform.Y = 0;
                };
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }
    }

    private void UpdateWidgetPreviewRefresh()
    {
        if (IsVisible && WidgetsTab.IsSelected)
        {
            RefreshWidgetPreviews();
            _widgetPreviewTimer.Start();
        }
        else _widgetPreviewTimer.Stop();
    }

    private void WidgetInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: DockWidgetOption option }) return;
        _initializing = true;
        if (option.IsInstalled)
        {
            option.IsInstalled = false;
            option.IsEnabled = false;
            option.IsSettingsExpanded = false;
        }
        else
        {
            option.IsInstalled = true;
            option.IsEnabled = true;
        }
        _initializing = false;
        UpdateWidgetOptionState();
        SaveSettings();
    }

    private void WidgetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: DockWidgetOption option }) return;
        if (!option.HasSettings) return;

        if (!option.IsInstalled)
        {
            _initializing = true;
            option.IsInstalled = true;
            option.IsEnabled = true;
            _initializing = false;
            SaveSettings();
        }

        option.IsSettingsExpanded = !option.IsSettingsExpanded;
        UpdateWidgetOptionState();

        if (option.IsSettingsExpanded)
        {
            FrameworkElement? target = option.Id switch
            {
                "Clock" => ClockOptions,
                "Weather" => WeatherOptions,
                "Focus" => FocusOptions,
                "SystemStats" => StatsOptions,
                _ => null
            };
            target?.BringIntoView();
        }
    }

    private void CollapseClockOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Clock") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void CollapseWeatherOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Weather") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void CollapseFocusOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Focus") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void CollapseStatsOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetOptions.FirstOrDefault(o => o.Id == "SystemStats") is { } option)
            option.IsSettingsExpanded = false;
        UpdateWidgetOptionState();
    }

    private void SelectAlignment(DockSettings settings)
    {
        var fullWidth = settings.Layout == DockLayout.FullWidth;
        FullWidthLayout.IsChecked = fullWidth;
        LeftPosition.IsChecked = !fullWidth && settings.Position == DockPosition.Left;
        CenterPosition.IsChecked = !fullWidth && settings.Position == DockPosition.Center;
        RightPosition.IsChecked = !fullWidth && settings.Position == DockPosition.Right;
    }

    private void LoadFeatureSelection(DockSettings settings)
    {
        RecycleBinToggle.IsChecked = settings.ShowRecycleBin;
        DensityAuto.IsChecked = settings.GlobalWidgetDensity == WidgetDensityMode.Auto;
        DensityCompact.IsChecked = settings.GlobalWidgetDensity == WidgetDensityMode.Compact;
        DensityFull.IsChecked = settings.GlobalWidgetDensity == WidgetDensityMode.Full;
        foreach (var (id, combo) in _widgetDensityBoxes)
        {
            var tag = settings.WidgetDensityOverrides is not null
                && settings.WidgetDensityOverrides.TryGetValue(id, out var mode)
                    ? mode.ToString()
                    : "Default";
            SelectDensityTag(combo, tag);
        }
        LoadDisplayChoices(settings.TargetDisplay);
        AllMonitorsToggle.IsChecked = settings.ShowOnAllMonitors;
        LoadSecondaryAppChoices(settings.SecondaryApps);
        SecondaryWidgetsToggle.IsChecked = settings.SecondaryWidgets;
        UpdateMultiMonitorControls();
        CurrentDesktopToggle.IsChecked = settings.CurrentDesktopOnly;
        UpdatesToggle.IsChecked = settings.CheckUpdates;
        BottomEdge.IsChecked = settings.Edge == DockEdge.Bottom;
        LeftEdge.IsChecked = settings.Edge == DockEdge.Left;
        RightEdge.IsChecked = settings.Edge == DockEdge.Right;
        Clock24HourRadio.IsChecked = settings.Clock24Hour;
        Clock12HourRadio.IsChecked = !settings.Clock24Hour;
        ClockSecondsToggle.IsChecked = settings.ClockShowSeconds;
        DateStyleDayShort.IsChecked = settings.DateStyle == ClockDateStyle.DayAndShortDate;
        DateStyleShort.IsChecked = settings.DateStyle == ClockDateStyle.ShortDateOnly;
        DateStyleNumeric.IsChecked = settings.DateStyle == ClockDateStyle.NumericDate;
        DateStyleHidden.IsChecked = settings.DateStyle == ClockDateStyle.Hidden;
        WeatherCityBox.Text = settings.WeatherCity;
        StatsCpuToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Cpu);
        StatsMemoryToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Memory);
        StatsBatteryToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Battery);
        StatsCpuTemperatureToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.CpuTemperature);
        StatsGpuToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.Gpu);
        StatsGpuTemperatureToggle.IsChecked = settings.StatsMeters.HasFlag(SystemMeters.GpuTemperature);
        LauncherAltSpace.IsChecked = settings.LauncherHotkey == HotkeyChoice.AltSpace;
        LauncherCtrlSpace.IsChecked = settings.LauncherHotkey == HotkeyChoice.CtrlSpace;
        LauncherCtrlAltSpace.IsChecked = settings.LauncherHotkey == HotkeyChoice.CtrlAltSpace;
        LauncherOff.IsChecked = settings.LauncherHotkey is not (HotkeyChoice.AltSpace or HotkeyChoice.CtrlSpace or HotkeyChoice.CtrlAltSpace);
        DockCtrlAltD.IsChecked = settings.DockHotkey == HotkeyChoice.CtrlAltD;
        DockWinAltD.IsChecked = settings.DockHotkey == HotkeyChoice.WinAltD;
        DockOff.IsChecked = settings.DockHotkey is not (HotkeyChoice.CtrlAltD or HotkeyChoice.WinAltD);
        StartOpensWindows.IsChecked = settings.StartAction == StartButtonAction.WindowsStart;
        StartOpensLauncher.IsChecked = settings.StartAction == StartButtonAction.Launcher;
        StartFirst.IsChecked = settings.StartPlacement == StartButtonPlacement.Start;
        StartLast.IsChecked = settings.StartPlacement == StartButtonPlacement.End;
        StartHidden.IsChecked = settings.StartPlacement == StartButtonPlacement.Hidden;
        WheelVolumeToggle.IsChecked = settings.WheelVolume;
    }

    private void AttachWidgetSettingsPanels()
    {
        WidgetPanelsPool.Children.Clear();
        _widgetDensityBoxes.Clear();
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Clock") is { } clock) clock.SettingsView = ClockOptions;
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Weather") is { } weather) weather.SettingsView = WeatherOptions;
        if (_widgetOptions.FirstOrDefault(o => o.Id == "Focus") is { } focus)
        {
            AttachDensityRow("Focus", FocusOptions);
            focus.SettingsView = FocusOptions;
        }
        if (_widgetOptions.FirstOrDefault(o => o.Id == "SystemStats") is { } stats)
        {
            AttachDensityRow("SystemStats", StatsOptions);
            stats.SettingsView = StatsOptions;
        }
        foreach (var id in new[] { "Media", "Audio", "QuickSettings", "Desktops" })
        {
            if (_widgetOptions.FirstOrDefault(o => o.Id == id) is not { } option) continue;
            var panel = new StackPanel();
            AttachDensityRow(id, panel);
            option.SettingsView = panel;
        }
    }

    private void AttachDensityRow(string widgetId, FrameworkElement container)
    {
        var panel = container is Border { Child: Panel inner } ? inner : container as Panel;
        if (panel is null || _widgetDensityBoxes.ContainsKey(widgetId)) return;

        var row = new Grid { Margin = new Thickness(0, 0, 0, panel.Children.Count > 0 ? 10 : 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = UiText.Get("WidgetDensity"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var combo = new ComboBox { MinWidth = 128 };
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("WidgetDensityFollowGlobal"), Tag = "Default" });
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("WidgetDensityAuto"), Tag = nameof(WidgetDensityMode.Auto) });
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("WidgetDensityCompact"), Tag = nameof(WidgetDensityMode.Compact) });
        combo.Items.Add(new ComboBoxItem { Content = UiText.Get("WidgetDensityFull"), Tag = nameof(WidgetDensityMode.Full) });
        combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (!_initializing) TriggerAutoSave(immediate: true);
        };
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);

        _widgetDensityBoxes[widgetId] = combo;
        panel.Children.Insert(0, row);
    }

    private static void SelectDensityTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private IReadOnlyDictionary<string, WidgetDensityMode>? ReadWidgetDensityOverrides()
    {
        Dictionary<string, WidgetDensityMode>? result = null;
        foreach (var (id, combo) in _widgetDensityBoxes)
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<WidgetDensityMode>(tag, out var mode))
            {
                result ??= new Dictionary<string, WidgetDensityMode>(StringComparer.OrdinalIgnoreCase);
                result[id] = mode;
            }
        }
        return result;
    }

    private void WeatherCityBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => PreferenceEdited(sender, e);

    private void WeatherCityBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "OpenTaskbar-settings.json",
            Filter = "OpenTaskbar (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = SettingsBackup.Export(dialog.FileName, _preferences.Current);
        if (error is not null)
            AppLog.Error($"Settings export: {error}");
        else
            MessageBox.Show(this, UiText.Get("ExportDone"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "OpenTaskbar (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var (settings, pins, error) = SettingsBackup.Read(dialog.FileName);
        if (settings is null)
        {
            AppLog.ReportError(error ?? "ErrorSettingsFileInvalid");
            return;
        }

        var added = (Application.Current.MainWindow as MainWindow)?.ImportPins(pins) ?? 0;
        if (_preferences.Save(settings with { PinOrder = settings.PinOrder ?? _preferences.Current.PinOrder }) is { } saveError)
        {
            AppLog.ReportError(saveError);
            return;
        }

        _applied = _preferences.Current;
        _initializing = true;
        LoadAllSelections(_applied);
        _initializing = false;
        UpdateWidgetOptionState();
        MessageBox.Show(this, UiText.Format("ImportDone", added), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e) => AppLog.TryOpenLogFile();

    private void LoadAllSelections(DockSettings settings)
    {
        DarkTheme.IsChecked = settings.Theme == AppTheme.Dark;
        LightTheme.IsChecked = settings.Theme == AppTheme.Light;
        SelectLanguage(settings.Language);
        SelectAlignment(settings);
        AnimationsToggle.IsChecked = settings.AnimationsEnabled;
        PreviewsToggle.IsChecked = settings.PreviewsEnabled;
        BadgesToggle.IsChecked = settings.NotificationBadgesEnabled;
        AutoHideDisabled.IsChecked = settings.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = settings.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = settings.AutoHide == AutoHideMode.Always;
        OpacitySlider.Value = settings.SurfaceOpacity * 100;
        UpdateOpacityLabel();
        MagnificationToggle.IsChecked = settings.MagnificationEnabled;
        CompactSize.IsChecked = settings.Size == DockSize.Compact;
        MediumSize.IsChecked = settings.Size == DockSize.Medium;
        LargeSize.IsChecked = settings.Size == DockSize.Large;
        DockScaleSlider.Value = Math.Round(settings.DockScale * 100);
        UpdateDockScaleLabel();
        LoadThemePacks(settings.ThemePack);
        foreach (var option in _widgetOptions)
        {
            option.IsInstalled = settings.InstalledWidgets?.Contains(option.Id, StringComparer.Ordinal) == true;
            option.IsEnabled = settings.Widgets?.Contains(option.Id, StringComparer.Ordinal) == true;
        }
        FocusMinutesSlider.Value = settings.FocusMinutes;
        BreakMinutesSlider.Value = _applied.FocusBreakMinutes;
        FocusSessionsSlider.Value = settings.FocusSessions;
        LoadFeatureSelection(settings);
    }

    private void UpdateWidgetOptionState()
    {
        bool IsExpanded(string id) => _widgetOptions.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase) && o.IsSettingsExpanded);
        ClockOptions.Visibility = IsExpanded("Clock") ? Visibility.Visible : Visibility.Collapsed;
        FocusOptions.Visibility = IsExpanded("Focus") ? Visibility.Visible : Visibility.Collapsed;
        StatsOptions.Visibility = IsExpanded("SystemStats") ? Visibility.Visible : Visibility.Collapsed;
        WeatherOptions.Visibility = IsExpanded("Weather") ? Visibility.Visible : Visibility.Collapsed;
        FocusMinutesValue.Text = UiText.Format("MinutesFormat", (int)Math.Round(FocusMinutesSlider.Value));
        BreakMinutesValue.Text = UiText.Format("MinutesFormat", (int)Math.Round(BreakMinutesSlider.Value));
        FocusSessionsValue.Text = ((int)Math.Round(FocusSessionsSlider.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void FocusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        PreferenceEdited(sender, e);
    }

    private void SaveSettings()
    {
        if (_closed) return;
        var startupChanged = _startupAvailable && StartupToggle.IsChecked != _startupEnabled;
        if (startupChanged && _startup.SetEnabled(StartupToggle.IsChecked == true) is { } startupError)
        {
            Trace.TraceWarning("Startup error: {0}", startupError);
            return;
        }
        var settings = ReadSelection();
        if (_preferences.Save(settings) is { } error)
        {
            Trace.TraceWarning("Save settings error: {0}", error);
            return;
        }
        _applied = settings;
        _startupEnabled = StartupToggle.IsChecked == true;
    }

    private bool _isClosing;
    private bool _isClosingCompleted;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApplication || Application.Current.Dispatcher.HasShutdownStarted || _isClosingCompleted) return;
        if (_preferences.Current.AnimationsEnabled && IsLoaded && !_isClosing)
        {
            e.Cancel = true;
            AnimateAndClose();
            return;
        }
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
    }

    private void AnimateAndClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        if (_autoSaveTimer.IsEnabled)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }

        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        var transform = new ScaleTransform(1.0, 1.0);
        SettingsFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        SettingsFrame.RenderTransform = transform;

        fade.Completed += (_, _) =>
        {
            _isClosingCompleted = true;
            Close();
        };

        SettingsFrame.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void PreferenceEdited(object sender, RoutedEventArgs e)
    {
        if (_initializing || _closed) return;
        if (e.OriginalSource is ToggleButton { TemplatedParent: ComboBox }) return;
        var selectedSize = LargeSize.IsChecked == true ? DockSize.Large : MediumSize.IsChecked == true ? DockSize.Medium : DockSize.Compact;
        (Application.Current.MainWindow as MainWindow)?.SetSizePreview(selectedSize, Math.Round(DockScaleSlider.Value) / 100);
        UpdateWidgetOptionState();
        UpdateMultiMonitorControls();
        TriggerAutoSave(immediate: sender is not (Slider or TextBox));
    }

    private void TriggerAutoSave(bool immediate)
    {
        if (_initializing || _closed) return;
        if (immediate)
        {
            _autoSaveTimer.Stop();
            SaveSettings();
        }
        else
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    private void UpdateOpacityLabel() => OpacityValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(OpacitySlider.Value));

    private void UpdateDockScaleLabel() => DockScaleValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(DockScaleSlider.Value));

    private void DockScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateDockScaleLabel();
        PreferenceEdited(sender, e);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateOpacityLabel();
        ThemeManager.SetSurfaceOpacity(Math.Round(e.NewValue) / 100);
        PreferenceEdited(sender, e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void CloseForShutdown()
    {
        _closingApplication = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
