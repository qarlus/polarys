using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Widgets;

internal static class WidgetViewChecks
{
    public static int Run()
    {
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            try { checks = RunOnDispatcher(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checks;
    }

    private static int RunOnDispatcher()
    {
        var checks = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            foreach (var resource in new[] { "Theme", "DockControls", "ContextMenus", "Widgets", "WidgetControls", "SettingsControls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/OpenTaskbar;component/Design/{resource}.xaml", UriKind.Relative)
                });
            ThemeManager.Apply(AppTheme.Dark);
            UiText.Current.SetLanguage(AppLanguage.Turkish);
            checks += DockLayoutChecks.Run(app);
            checks += ApplicationInteractionChecks.Run(app);
            checks += VisualParityChecks.Run(app);
            checks += FeatureAndMemoryChecks.Run(app);
            checks += SettingsAndAutoHideChecks.Run(app);
            checks += AdaptiveWidgetAndConfirmChecks.Run(app);

            var settingsAction = new Button { Content = UiText.Get("Apply") };
            var selectedTab = new TabItem { Header = UiText.Get("General"), Content = settingsAction };
            var settingsTabs = new TabControl { Items = { selectedTab }, SelectedIndex = 0 };
            Layout(settingsTabs);
            var tabPeer = UIElementAutomationPeer.CreatePeerForElement(settingsTabs);
            var selectedPeer = tabPeer?.GetChildren()?.Single();
            Check(selectedPeer?.GetChildren()?.Any(peer => peer.GetAutomationControlType() == AutomationControlType.Button) == true,
                "The selected settings page must expose its controls to assistive technology.");

            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                checks++;
            }

            var clock = new TestClock();
            var timer = new CountdownDockWidget(clock);
            var timerHost = new ContentControl { Content = timer };
            Layout(timerHost);
            var timerTile = RequiredChild<TimedWidgetTile>(timerHost);
            var action = RequiredName<Button>(timerTile, "QuickActionButton");
            var details = RequiredName<Button>(timerTile, "DetailsButton");
            var detailRequests = 0;
            timerHost.AddHandler(TimedWidgetTile.DetailsRequestedEvent,
                new RoutedEventHandler((_, _) => detailRequests++));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(detailRequests == 1 && !timer.IsRunning, "An unconfigured tile must open details instead of starting.");
            Check(AutomationProperties.GetName(action) == UiText.Get("WidgetSetDuration"), "The unconfigured action must have an accessible configure label.");
            Check(RequiredName<TextBlock>(timerTile, "ValueText").Text == UiText.Get("WidgetSetDuration"), "A blank duration must have an explicit visible prompt.");

            timer.MinutesText = "1";
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(timerHost);
            Check(timer.IsRunning && detailRequests == 1, "A configured quick action must start without opening details.");
            Check(AutomationProperties.GetName(action) == UiText.Get("TimedWidgetPause"), "The running action must have an accessible pause label.");
            clock.Advance(TimeSpan.FromSeconds(15));
            timer.Refresh();
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(timer.State == CountdownState.Paused && timer.Value == "00:00:45", "The tile must pause the same countdown model.");
            details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(detailRequests == 2, "The value area must open details independently of the quick action.");
            Check(Descendants<TextBox>(timerTile).Count() == 0, "The dock tile must contain no duration form.");
            Check(Descendants<Button>(timerTile).Count() == 2, "The dock tile must offer one detail target and one quick action.");
            Check(action.ActualWidth >= 36 && action.ActualHeight >= 36, "The quick action must keep its pointer target.");
            Check(timerHost.DesiredSize.Width < 200 && timerHost.DesiredSize.Height < 60, "The compact timer must fit within the dock shelf.");

            var panel = new TimedWidgetPanel { DataContext = timer };
            var frame = new WidgetPanelFrame { Heading = UiText.Get("WidgetCountdown"), Body = panel };
            Layout(frame);
            var duration = Descendants<TextBox>(panel).Single();
            Check(duration.IsReadOnly, "Paused duration must remain locked until reset.");
            var panelButtons = Descendants<Button>(panel).ToArray();
            panelButtons.Single(button => Equals(button.Content, UiText.Get("TimedWidgetReset")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(frame);
            Check(timer.State == CountdownState.Ready && !duration.IsReadOnly, "Panel reset must unlock the configured duration.");
            duration.Text = "0";
            duration.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Layout(frame);
            Check(timer.HasValidationError && !RequiredName<Button>(panel, "PrimaryActionButton").IsEnabled,
                "Invalid panel input must disable Start through the bound model.");
            Check(Descendants<TextBlock>(panel).Any(text => text.Text == timer.StatusText), "The validation error must be visible in the panel.");
            duration.Text = "1";
            duration.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            RequiredName<Button>(panel, "PrimaryActionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(frame);
            Check(timer.IsRunning && RequiredName<TextBlock>(timerTile, "ValueText").Text == "00:01:00",
                "Panel actions and tile display must share one timer state.");

            var stopwatch = new StopwatchDockWidget(clock);
            stopwatch.Refresh();
            var stopwatchHost = new ContentControl { Content = stopwatch };
            Layout(stopwatchHost);
            var stopwatchTile = RequiredChild<TimedWidgetTile>(stopwatchHost);
            RequiredName<Button>(stopwatchTile, "QuickActionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            clock.Advance(TimeSpan.FromHours(26));
            stopwatch.Refresh();
            timer.Refresh();
            Layout(stopwatchHost);
            Layout(timerHost);
            Check(stopwatch.Value == "26:00:00" && timer.State == CountdownState.Finished, "Both compact views must retain independent timer behavior.");
            Check(timerTile.Title == UiText.Get("CountdownFinished"), "The completed tile must display a non-color completion label.");

            UiText.Current.SetLanguage(AppLanguage.English);
            Layout(timerHost);
            Layout(stopwatchHost);
            Check(stopwatchTile.Title == "Stopwatch" && timerTile.Title == "Time is up", "Tile titles must update when language changes.");
            Check(AutomationProperties.GetName(RequiredName<Button>(stopwatchTile, "QuickActionButton")) == "Pause",
                "Quick action names must update when language changes.");
            UiText.Current.SetLanguage(AppLanguage.Turkish);

            var closeRequests = 0;
            frame.CloseRequested += (_, _) => closeRequests++;
            Descendants<Button>(frame).Single(button => AutomationProperties.GetName(button) == UiText.Get("Close"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(closeRequests == 1, "The shared panel frame must expose its close request.");
            var stopwatchPanel = new TimedWidgetPanel { DataContext = stopwatch };
            Layout(stopwatchPanel);
            Check(!Descendants<TextBox>(stopwatchPanel).Any(), "The stopwatch panel must not instantiate a countdown form.");
            frame.MaxWidth = 220;
            Layout(frame);
            Check(frame.ActualWidth <= 220 && Descendants<Button>(panel).All(button =>
            {
                var left = button.TranslatePoint(new Point(), frame).X;
                return button.ActualWidth > 0 && left >= 0 && left + button.ActualWidth <= frame.ActualWidth;
            }),
                "A narrow panel must retain reachable action controls.");
            frame.MaxWidth = double.PositiveInfinity;

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                double Luminance(Color color)
                {
                    double Linear(byte channel) => channel / 255d <= 0.04045
                        ? channel / 255d / 12.92 : Math.Pow((channel / 255d + 0.055) / 1.055, 2.4);
                    return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
                }
                double Contrast(string foreground, string background)
                {
                    var first = Luminance(((SolidColorBrush)app.Resources[foreground]).Color);
                    var second = Luminance(((SolidColorBrush)app.Resources[background]).Color);
                    return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                }
                Check(Contrast("TextPrimary", "SurfacePopup") >= 7, "Panel primary text must retain strong contrast in both themes.");
                Check(Contrast("TextSecondary", "SurfacePopup") >= 4.5, "Panel supporting text must meet normal-text contrast.");
                Check(Contrast("AccentText", "AccentBrush") >= 4.5, "Quick action glyphs must contrast with their accent surface.");
            }

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var language in new[] { AppLanguage.Turkish, AppLanguage.English })
            {
                ThemeManager.Apply(theme);
                UiText.Current.SetLanguage(language);
                var options = new DockWidgetService(clock).CreateOptions([], []);
                foreach (var option in options)
                {
                    option.Preview.Value = option.Preview is CountdownDockWidget ? string.Empty : "00:00:00";
                    var card = new WidgetCatalogItem { DataContext = option };
                    Layout(card);
                    var install = RequiredName<Button>(card, "PreviewButton");
                    var preview = Descendants<ContentControl>(install).Single(control => ReferenceEquals(control.Content, option.Preview));
                    Check(ReferenceEquals(preview.Content, option.Preview) && !preview.IsHitTestVisible
                        && System.Windows.Input.KeyboardNavigation.GetTabNavigation(preview) == System.Windows.Input.KeyboardNavigationMode.None,
                        "Catalog previews must reuse real widget templates without exposing nested interactive targets.");
                    Check(preview.LayoutTransform is ScaleTransform { ScaleX: > 1, ScaleY: > 1 },
                        "Catalog previews must scale up to fill their gallery card.");
                    Check(AutomationProperties.GetName(install) == option.Label
                        && AutomationProperties.GetItemStatus(install) == UiText.Get("WidgetAvailable"),
                        "Preview installation must expose its name and available state.");
                    Check(!Descendants<TextBlock>(card).Any(text => text.Text == option.Description),
                        "Available previews must omit descriptions.");
                    Check(!Descendants<CheckBox>(card).Any(),
                        "The card must omit separate management controls.");
                    var installations = 0;
                    card.InstallationRequested += (_, _) => installations++;
                    install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(installations == 1 && !option.IsInstalled
                        && (option.Preview is not ITimedDockWidget timedPreview || !timedPreview.IsRunning),
                        "Tapping a preview must request installation without starting its time widget.");
                    option.IsInstalled = true;
                    option.IsEnabled = true;
                    Layout(card);
                    Check(AutomationProperties.GetItemStatus(install) == UiText.Get("WidgetInstalled"),
                        "Installed previews must expose their installed state.");
                    install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(installations == 2, "Tapping an installed preview must request uninstallation.");
                    Check(Descendants<FrameworkElement>(card).Where(element => element is Button).All(element =>
                    {
                        var left = element.TranslatePoint(new Point(), card).X;
                        return left >= 0 && left + element.ActualWidth <= card.ActualWidth;
                    }), "All preview controls must fit inside their gallery item.");
                    option.UpdateText("Localized widget", "Localized description");
                    Check(option.Preview.Label == option.Label, "Preview labels must follow catalog localization.");
                }

                foreach (var width in new[] { 292d, 500d, 720d })
                {
                    var gallery = new WrapPanel { Width = width };
                    foreach (var option in options) gallery.Children.Add(new WidgetCatalogItem { DataContext = option });
                    Layout(gallery);
                    Check(gallery.Children.Cast<FrameworkElement>().All(item =>
                    {
                        var point = item.TranslatePoint(new Point(), gallery);
                        return point.X >= 0 && point.X + item.ActualWidth <= width;
                    }), "The gallery must wrap without horizontal overflow at narrow and wide settings sizes.");
                    var second = (FrameworkElement)gallery.Children[1];
                    Check(width < 488 ? second.TranslatePoint(new Point(), gallery).Y > 0
                        : second.TranslatePoint(new Point(), gallery).Y == 0,
                        "The gallery must adapt its column count to the available width.");
                }
            }
            UiText.Current.SetLanguage(AppLanguage.Turkish);

            timer.Reset();
            timer.Toggle();
            clock.Advance(TimeSpan.FromSeconds(15));
            timer.Refresh();
            stopwatch.Reset();
            stopwatch.Toggle();
            clock.Advance(TimeSpan.FromSeconds(24));
            stopwatch.Refresh();
            timer.Refresh();
            var directory = Path.Combine(Path.GetTempPath(), "OpenTaskbarWidgetComponents");
            Directory.CreateDirectory(directory);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);
                var strip = new StackPanel { Orientation = Orientation.Horizontal };
                strip.Children.Add(new ContentControl { Content = stopwatch });
                strip.Children.Add(new ContentControl { Content = timer });
                var preview = new StackPanel { Margin = new Thickness(16) };
                preview.Children.Add(strip);
                preview.Children.Add(new WidgetPanelFrame
                {
                    Heading = UiText.Get("WidgetCountdown"), Body = new TimedWidgetPanel { DataContext = timer },
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0)
                });
                var surface = new Border { Background = (Brush)app.Resources["SettingsBackground"], Child = preview };
                Layout(surface);
                Render(surface, Path.Combine(directory, $"timed-widgets-{theme.ToString().ToLowerInvariant()}.png"));
                var catalog = new WrapPanel { Width = 500, Margin = new Thickness(20) };
                var previewOptions = new DockWidgetService(clock).CreateOptions([DockWidgetService.StopwatchId], [DockWidgetService.StopwatchId]);
                SetPreviewFixtures(previewOptions);
                foreach (var option in previewOptions)
                {
                    catalog.Children.Add(new WidgetCatalogItem { DataContext = option });
                }
                var catalogSurface = new Border { Background = (Brush)app.Resources["SettingsBackground"], Child = catalog };
                Layout(catalogSurface);
                Render(catalogSurface, Path.Combine(directory, $"widget-catalog-{theme.ToString().ToLowerInvariant()}.png"));
                var settingsPath = Path.Combine(Path.GetTempPath(), $"OpenTaskbarGallery-{Guid.NewGuid():N}.json");
                var preferences = new ApplicationPreferences(new DockSettingsStore(settingsPath));
                Check(preferences.Save(preferences.Current with { Theme = theme, Language = AppLanguage.Turkish }) is null,
                    "The isolated settings fixture must save successfully.");
                var settings = new OpenTaskbar.SettingsWindow(preferences);
                try
                {
                    RequiredName<TabControl>(settings, "SettingsTabs").SelectedItem = RequiredName<TabItem>(settings, "WidgetsTab");
                    var list = RequiredName<ItemsControl>(settings, "WidgetOptionsList");
                    var selections = ((IEnumerable<DockWidgetOption>)list.ItemsSource).ToArray();
                    SetPreviewFixtures(selections);
                    var page = (FrameworkElement)settings.Content;
                    foreach (var width in new[] { 560d, 740d })
                    {
                        page.Width = width;
                        page.Height = 598;
                        Layout(page);
                        var cards = Descendants<WidgetCatalogItem>(list).ToArray();
                        Check(cards.Length == selections.Length && cards.All(card =>
                        {
                            var left = card.TranslatePoint(new Point(), list).X;
                            return left >= 0 && left + card.ActualWidth <= list.ActualWidth;
                        }), "The real settings page must keep all previews inside its viewport at default and minimum width.");
                        Render(page, Path.Combine(directory, $"widget-settings-{theme.ToString().ToLowerInvariant()}-{width:0}.png"));
                    }
                    var firstCard = Descendants<WidgetCatalogItem>(list).First();
                    var selection = (DockWidgetOption)firstCard.DataContext;
                    RequiredName<Button>(firstCard, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(selection.IsInstalled && selection.IsEnabled, "A preview tap in Settings must install and enable its widget.");
                    Check(preferences.Current.InstalledWidgets?.Contains(selection.Id) == true
                        && preferences.Current.Widgets?.Contains(selection.Id) == true,
                        "A preview tap must auto-save the installed and visible widget selections.");
                    Layout(page);
                    var currentCard = Descendants<WidgetCatalogItem>(list).Single(card => ReferenceEquals(card.DataContext, selection));
                    RequiredName<Button>(currentCard, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!selection.IsInstalled && !selection.IsEnabled, "Tapping an installed preview in Settings must uninstall and disable its widget.");
                    Check(preferences.Current.InstalledWidgets?.Contains(selection.Id) != true
                        && preferences.Current.Widgets?.Contains(selection.Id) != true,
                        "Tapping an installed preview must auto-save removal of the widget.");

                    var clockCard = Descendants<WidgetCatalogItem>(list).Single(card => ((DockWidgetOption)card.DataContext).Id == DockWidgetService.ClockId);
                    var clockOption = (DockWidgetOption)clockCard.DataContext;
                    Check(clockOption.HasSettings && clockOption.HasSettingsVisibility == Visibility.Visible,
                        "Clock widget option must report having settings and show settings expand visibility.");
                    Check(clockOption.SettingsGlyph == "\uE70D", "Initial settings glyph must be downward chevron.");
                    var clockExpandBtn = RequiredName<Button>(clockCard, "SettingsExpandButton");
                    Check(clockExpandBtn.Visibility == Visibility.Visible, "Clock card must show settings expand button.");
                    clockExpandBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(clockOption.IsSettingsExpanded && clockOption.SettingsGlyph == "\uE70E",
                        "Clicking expand button must set IsSettingsExpanded and change glyph to upward chevron.");
                    Check(RequiredName<FrameworkElement>(settings, "ClockOptions").Visibility == Visibility.Visible,
                        "Clicking settings expand button must make ClockOptions visible.");

                    var secondsToggle = RequiredName<CheckBox>(settings, "ClockSecondsToggle");
                    secondsToggle.IsChecked = true;
                    Check(preferences.Current.ClockShowSeconds,
                        "Checking ClockSecondsToggle must auto-save preferences immediately without a manual apply button.");

                    clockExpandBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!clockOption.IsSettingsExpanded && clockOption.SettingsGlyph == "\uE70D",
                        "Clicking settings expand button again must collapse settings and restore downward chevron.");
                    Check(RequiredName<FrameworkElement>(settings, "ClockOptions").Visibility == Visibility.Collapsed,
                        "Collapsing settings must set ClockOptions visibility to Collapsed.");

                    var trayCard = Descendants<WidgetCatalogItem>(list).Single(card => ((DockWidgetOption)card.DataContext).Id == DockWidgetService.TrayId);
                    var trayOption = (DockWidgetOption)trayCard.DataContext;
                    Check(!trayOption.HasSettings && RequiredName<Button>(trayCard, "SettingsExpandButton").Visibility == Visibility.Collapsed,
                        "Widgets without custom settings must hide the settings expand button.");

                    var weatherCard = Descendants<WidgetCatalogItem>(list).Single(card => ((DockWidgetOption)card.DataContext).Id == DockWidgetService.WeatherId);
                    var weatherOption = (DockWidgetOption)weatherCard.DataContext;
                    var weatherExpandBtn = RequiredName<Button>(weatherCard, "SettingsExpandButton");
                    weatherExpandBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(weatherOption.IsSettingsExpanded, "Weather card must expand when settings button is clicked.");
                    Check(weatherOption.SettingsView != null, "Weather card must host its settings view inside the card.");
                    var cityBox = RequiredName<TextBox>(settings, "WeatherCityBox");
                    Check(cityBox.Visibility == Visibility.Visible, "Weather city text box must be visible.");
                    cityBox.Text = "İzmir";
                    cityBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                    Check(preferences.Current.WeatherCity == "İzmir", "Typing a city must auto-save WeatherCity.");
                    weatherExpandBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!weatherOption.IsSettingsExpanded, "Clicking weather settings button again must collapse it.");
                }
                finally
                {
                    settings.Close();
                    if (File.Exists(settingsPath)) File.Delete(settingsPath);
                }
            }
            return checks;
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void SetPreviewFixtures(IEnumerable<DockWidgetOption> options)
    {
        foreach (var option in options)
            option.Preview.Value = option.Id switch
            {
                DockWidgetService.ClockId => "14:32",
                DockWidgetService.DateId => "Pzt, 16 Kas",
                DockWidgetService.MediaId => "▶ · 02:15",
                DockWidgetService.AudioId => "100%",
                DockWidgetService.QuickSettingsId => "Wi-Fi · BT",
                DockWidgetService.SystemStatsId => "CPU · RAM",
                DockWidgetService.FocusId => "25:00",
                DockWidgetService.WeatherId => "21°",
                DockWidgetService.DesktopsId => "1 / 2",
                DockWidgetService.TrayId => "▲",
                DockWidgetService.BatteryId => UiText.Get("SystemDataUnavailable"),
                "Storage" => "128,5 GiB",
                "DayProgress" => "61%",
                DockWidgetService.StopwatchId => "00:00:00",
                DockWidgetService.CountdownId => string.Empty,
                _ => throw new InvalidOperationException("Add a render fixture for the new widget.")
            };
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
    }

    private static T RequiredName<T>(FrameworkElement owner, string name) where T : FrameworkElement =>
        owner.FindName(name) is T element ? element : throw new InvalidOperationException($"Missing control {name}.");
    private static T RequiredChild<T>(DependencyObject owner) where T : DependencyObject =>
        Descendants<T>(owner).FirstOrDefault() ?? throw new InvalidOperationException($"Missing {typeof(T).Name}.");
    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
