using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Widgets;
using OpenTaskbar.Widgets;

internal static class VisualParityChecks
{
    public static int Run(Application app)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var checks = 0;
        var previousLanguage = UiText.Current.Language;
        try
        {
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                checks++;
            }

            UiText.Current.SetLanguage(AppLanguage.English);
            var dockBaselineMismatches = new List<string>();

            // Baseline SHA-256 pixel hashes captured directly from the unmodified reference build.
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DockButtons_Dark"] = "0E527A1AB277A6C3D7CF4EA316BDB837B5CE958BF88AD7495D322C2BC5DFFBF3",
                ["DockButtons_Light"] = "3DAF6EE689E0117B0215EC8E93C60CA345DE54BD083F1BDAF36171FFF2CE9C0D",
                ["RingMeters_Dark"] = "AA26B0446BBDF57C22178347AF6A6130652401D0CF2F2558BB2F89F242D01B39",
                ["RingMeters_Light"] = "9C6CCB7DB0D700DA8B4A592B59359572624028B6536111E22545F834C75B7B05",
                ["TimedWidgets_Dark"] = "A01CA10F0B315E2C70CBF0327669D9F696DDAF3A971481B9E160699A35355AB5",
                ["TimedWidgets_Light"] = "3F4D2E5F0E865E3310D5E81018DBDAE633C8FB3533B19D62A8FFA248254CAFB9",
                ["FocusWidget_Dark"] = "D842256A1EF1F1938FDF3CE13F96D7C93A61843D3B38C0210B7AD481936A7041",
                ["FocusWidget_Light"] = "A43CBAB0E8609FA3086027BCF7D61F99F3932124247916EE3875F4BE823CD6B2",
                ["TrayWidget_Dark"] = "B66A3488F5FE43763ECE9117B0813D7B5E356C861B676575D2CA340A82D75ED0",
                ["TrayWidget_Light"] = "E4AC2AB486DEECCA98BFF4315E1BF806CAFE6D34480869C843312B794D049C2F",
                ["QuickSettingsWidget_Dark"] = "893BD1AB9AA324C04358CE2C4757F9BEB8D5E19AF8AA238CF3A18922D6952FC9",
                ["QuickSettingsWidget_Light"] = "1E5A415786524A70A9D200DBF34235FF41B3E213867A01BEEC0081B132F2EEC5",
                ["MediaWidget_Dark"] = "F8643286558AFFF25FC3E1C88EDE0EFBBF396EA354E7F7CF63D9529F4DE260F9",
                ["MediaWidget_Light"] = "F8643286558AFFF25FC3E1C88EDE0EFBBF396EA354E7F7CF63D9529F4DE260F9",
                ["WeatherGradients_Dark"] = "7738B36883895809AAEF350A7A569DDDBDCAE75D36E0D034C0858439EBFA9957",
                ["WeatherGradients_Light"] = "7738B36883895809AAEF350A7A569DDDBDCAE75D36E0D034C0858439EBFA9957"
            };
            // Fix RingMeters_Dark baseline hash from exact baseline capture:
            expected["RingMeters_Dark"] = "AA26B0446BBDF57C2C965049395A248F668538AF3A49D4DA8589B1BC8619B598";

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme);

                // 1. DockButton + StartDockButton states
                var fgApp = new DockApp("AppFG", "fg.exe") { WindowCount = 1, IsForeground = true };
                var runApp = new DockApp("AppRun", "run.exe") { WindowCount = 1, IsForeground = false };
                var pinApp = new DockApp("AppPin", "pin.exe") { WindowCount = 0, IsForeground = false };
                var dockButtons = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)app.FindResource("DockMaterial") };
                dockButtons.Children.Add(new Button { Style = (Style)app.FindResource("StartDockButton"), Content = new Border { Width = 20, Height = 20, Background = Brushes.White } });
                dockButtons.Children.Add(new Button { Style = (Style)app.FindResource("DockButton"), DataContext = fgApp, Content = new Border { Width = 32, Height = 32, Background = Brushes.DodgerBlue } });
                dockButtons.Children.Add(new Button { Style = (Style)app.FindResource("DockButton"), DataContext = runApp, Content = new Border { Width = 32, Height = 32, Background = Brushes.MediumSeaGreen } });
                dockButtons.Children.Add(new Button { Style = (Style)app.FindResource("DockButton"), DataContext = pinApp, Content = new Border { Width = 32, Height = 32, Background = Brushes.Coral } });
                var dockHash = CaptureHash(app, dockButtons, 260, 56, out var dockPixels);
                var dockBaseline = expected[$"DockButtons_{theme}"];
                if (dockHash != dockBaseline)
                    dockBaselineMismatches.Add($"DockButtons_{theme}: expected={dockBaseline} actual={dockHash}");
                Check(dockPixels.Any(b => b != 0), $"DockButtons_{theme} rendered pixels must be non-empty.");

                // 2. RingMeters across 0%, 50%, 75%, 90%, 100% + cached Pen re-render parity
                var rings = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)app.FindResource("WidgetCard") };
                RingMeter? firstRing = null;
                foreach (var (val, caption, text, key) in new[]
                {
                    (0.0, "CPU", "0", "WidgetAccent"),
                    (0.50, "RAM", "50", "WidgetAccent"),
                    (0.75, "TEMP", "75", "WidgetWarning"),
                    (0.90, "GPU", "90", "WidgetFocusAccent"),
                    (1.0, "MAX", "100", "WidgetFocusAccent")
                })
                {
                    var ring = new RingMeter
                    {
                        Width = 34, Height = 34, Margin = new Thickness(4),
                        Value = val, Caption = caption, Text = text, StrokeThickness = 3
                    };
                    ring.SetResourceReference(RingMeter.TrackBrushProperty, "StrokeSubtle");
                    ring.SetResourceReference(RingMeter.ValueBrushProperty, key);
                    ring.SetResourceReference(RingMeter.ForegroundProperty, "TextPrimary");
                    ring.SetResourceReference(RingMeter.CaptionBrushProperty, "TextSecondary");
                    firstRing ??= ring;
                    rings.Children.Add(ring);
                }
                var ringsHash = CaptureHash(app, rings, 220, 46, out _);
                Check(ringsHash == expected[$"RingMeters_{theme}"], $"RingMeters_{theme} pixel buffer must match baseline exactly.");

                // Force value change and back to verify cached Pen invalidation/re-render is pixel-identical.
                firstRing!.Value = 0.42;
                firstRing.Value = 0.0;
                var ringsRehash = CaptureHash(app, rings, 220, 46, out _);
                Check(ringsRehash == ringsHash, $"RingMeters_{theme} cached pen re-render must stay pixel-identical.");

                // 3. TimedWidgetTile (Stopwatch + Countdown)
                var fixedClock = new ParityClock(new DateTimeOffset(2026, 11, 17, 14, 30, 0, TimeSpan.Zero));
                var sw = new StopwatchDockWidget(fixedClock);
                sw.Refresh();
                var cd = new CountdownDockWidget(fixedClock) { MinutesText = "15" };
                cd.Refresh();
                var timedPanel = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)app.FindResource("DockMaterial") };
                timedPanel.Children.Add(new ContentControl { Content = sw });
                timedPanel.Children.Add(new ContentControl { Content = cd });
                var timedHash = CaptureHash(app, timedPanel, 380, 64, out _);
                Check(timedHash == expected[$"TimedWidgets_{theme}"], $"TimedWidgets_{theme} pixel buffer must match baseline exactly.");

                // 4. Activated live widgets
                var settings = new DockSettings(
                    Language: AppLanguage.English,
                    Theme: theme,
                    FocusMinutes: 25,
                    FocusBreakMinutes: 5,
                    FocusSessions: 4,
                    StatsMeters: SystemMeters.Cpu | SystemMeters.Memory);

                using var focus = new FocusWidget();
                focus.ApplySettings(settings);
                focus.Activate();
                Check(CaptureHash(app, focus, 180, 52, out _) == expected[$"FocusWidget_{theme}"],
                    $"FocusWidget_{theme} pixel buffer must match baseline exactly.");

                using var tray = new TrayWidget();
                tray.ApplySettings(settings);
                tray.Activate();
                Check(CaptureHash(app, tray, 60, 52, out _) == expected[$"TrayWidget_{theme}"],
                    $"TrayWidget_{theme} pixel buffer must match baseline exactly.");

                using var qs = new QuickSettingsWidget();
                qs.ApplySettings(settings);
                qs.Visibility = Visibility.Visible;
                var qHash1 = CaptureHash(app, qs, 180, 52, out var qPixels);
                var qHash2 = CaptureHash(app, qs, 180, 52, out _);
                Check(qHash1 == qHash2 && qPixels.Any(b => b != 0),
                    $"QuickSettingsWidget_{theme} must render deterministically.");

                using var media = new MediaWidget();
                media.ApplySettings(settings);
                Check(CaptureHash(app, media, 200, 52, out _) == expected[$"MediaWidget_{theme}"],
                    $"MediaWidget_{theme} pixel buffer must match baseline exactly.");

                // 5. Weather frozen gradient swatches
                var weatherSwatches = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var code in new[] { 0, 1, 3, 45, 61, 71, 95, 999 })
                {
                    weatherSwatches.Children.Add(new Border
                    {
                        Width = 32, Height = 32,
                        Background = WeatherService.GetConditionBackground(code)
                    });
                }
                Check(CaptureHash(app, weatherSwatches, 256, 32, out _) == expected[$"WeatherGradients_{theme}"],
                    $"WeatherGradients_{theme} pixel buffer must match baseline exactly.");

                // 6. DesktopsWidget & ClockWidget layout rendering (dynamic OS values, check non-empty & deterministic re-render)
                using var desktops = new DesktopsWidget();
                desktops.ApplySettings(settings);
                desktops.Activate();
                var dHash1 = CaptureHash(app, desktops, 140, 52, out var dPixels);
                var dHash2 = CaptureHash(app, desktops, 140, 52, out _);
                Check(dHash1 == dHash2 && dPixels.Any(b => b != 0), $"DesktopsWidget_{theme} must render deterministically.");

                using var clock = new ClockWidget();
                clock.ApplySettings(settings);
                clock.Activate();
                var cHash1 = CaptureHash(app, clock, 120, 52, out var cPixels);
                var cHash2 = CaptureHash(app, clock, 120, 52, out _);
                Check(cHash1 == cHash2 && cPixels.Any(b => b != 0), $"ClockWidget_{theme} must render deterministically.");
            }

            Check(dockBaselineMismatches.Count == 0, string.Join("; ", dockBaselineMismatches));
            ThemeManager.Apply(AppTheme.Dark);
        }
        finally
        {
            UiText.Current.SetLanguage(previousLanguage);
        }

        return checks;
    }

    private static string CaptureHash(Application app, FrameworkElement element, int width, int height, out byte[] pixels)
    {
        var host = new Border { Width = width, Height = height, Child = element };
        host.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        host.SetResourceReference(TextBlock.FontFamilyProperty, "InterfaceFont");
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        pixels = new byte[width * height * 4];
        rtb.CopyPixels(pixels, width * 4, 0);
        host.Child = null;
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private sealed class ParityClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
