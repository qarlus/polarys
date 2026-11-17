using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class ThemePackChecks
{
    public static int Run()
    {
        Exception? failure = null;
        var checks = 0;
        // RenderTargetBitmap, used for the fallback plate, needs an STA thread.
        var thread = new Thread(() =>
        {
            try { checks = RunChecks(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checks;
    }

    private static int RunChecks()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbarThemes-" + Path.GetRandomFileName());
        var previousRoot = ThemePacks.Root;
        try
        {
            var themeDir = Path.Combine(root, "test-theme");
            var iconDir = Path.Combine(themeDir, ThemePack.IconFolder);
            var widgetDir = Path.Combine(themeDir, ThemePack.WidgetFolder);
            Directory.CreateDirectory(Path.Combine(iconDir, "icons"));
            Directory.CreateDirectory(widgetDir);
            WritePng(Path.Combine(iconDir, "browser.png"), 48);
            WritePng(Path.Combine(iconDir, "terminal.png"), 48);
            WritePng(Path.Combine(iconDir, "trash.png"), 48);
            WritePng(Path.Combine(iconDir, "start.png"), 40);
            WritePng(Path.Combine(iconDir, "icons", "code.png"), 64);
            WritePng(Path.Combine(themeDir, "outside.png"), 48);
            File.WriteAllText(Path.Combine(themeDir, ThemePack.ManifestFileName), """
                { "name": "Test Theme", "author": "Tester", }
                """);
            File.WriteAllText(Path.Combine(iconDir, IconPack.ManifestFileName), """
                {
                  // Comments and trailing commas are allowed.
                  "icons": {
                    "firefox.exe": "browser.png",
                    "Microsoft.WindowsTerminal": "terminal.png",
                    "@recyclebin": "trash.png",
                    "@start": "start.png",
                    "evil.exe": "..\\outside.png",
                    "missing.exe": "nothing.png",
                  },
                  "fallback": { "shape": "Leaf", "sharpCorner": "TopRight", "background": "#FF336699" }
                }
                """);
            File.WriteAllText(Path.Combine(widgetDir, WidgetPack.ManifestFileName), """
                {
                  "tokens": { "WidgetAccent": "#FF00BFFF", "WidgetRadius": 16, "Unknown": "#FF000000" },
                  "dark": { "WidgetAccent": "#FF111111" }
                }
                """);

            var (theme, error, problems) = ThemePack.Load(themeDir);
            Check(error is null && problems.Count == 0 && theme is { Id: "test-theme", DisplayName: "Test Theme" }
                && theme.Icons is not null && theme.Widgets is not null, "A theme must load with its icon and widget packs.");
            var pack = theme!.Icons!;
            Check(pack.FindIconFile(@"C:\Program Files\Mozilla Firefox\FIREFOX.EXE") == Path.Combine(iconDir, "browser.png"),
                "Executable file names must match case-insensitively.");
            Check(pack.FindIconFile(@"C:\x\WindowsTerminal.exe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App") == Path.Combine(iconDir, "terminal.png"),
                "A package name must match an AppUserModelID.");
            Check(pack.FindIconFile(null, specialKey: IconPack.RecycleBinKey) == Path.Combine(iconDir, "trash.png"),
                "Special items must resolve by key.");
            Check(pack.FindIconFile(null, specialKey: IconPack.RecycleBinFullKey) is null,
                "An unmapped special item must not fall back to another special item.");
            Check(string.Equals(pack.FindIconFile(@"C:\Apps\Code.exe"), Path.Combine(iconDir, "icons", "code.png"), StringComparison.OrdinalIgnoreCase),
                "An image named after the executable in icons\\ must match without a mapping.");
            Check(pack.FindIconFile(@"C:\Apps\evil.exe") is null, "Mapped paths outside the pack folder must be ignored.");
            Check(pack.FindIconFile(@"C:\Apps\missing.exe") is null, "A mapping to a missing file must be ignored.");
            Check(pack.FindIconFile(@"C:\Apps\unknown.exe") is null, "Unknown apps must have no pack image.");
            Check(pack.Fallback is { Shape: IconPackShape.Leaf, SharpCorner: IconPackCorner.TopRight },
                "Fallback enums must parse from strings.");

            var brokenDir = Path.Combine(root, "broken-widgets");
            Directory.CreateDirectory(Path.Combine(brokenDir, ThemePack.WidgetFolder));
            File.WriteAllText(Path.Combine(brokenDir, ThemePack.ManifestFileName), "{}");
            File.WriteAllText(Path.Combine(brokenDir, ThemePack.WidgetFolder, WidgetPack.ManifestFileName), "{ not json");
            var broken = ThemePack.Load(brokenDir);
            Check(broken.Pack is { Widgets: null, Icons: null, DisplayName: "broken-widgets" } && broken.Problems.Count == 1,
                "A broken widget pack must be reported without losing the theme.");
            Directory.CreateDirectory(Path.Combine(root, "not-a-theme"));
            Check(ThemePack.Load(Path.Combine(root, "not-a-theme")).Error is not null, "A folder without theme.json must report an error.");

            WidgetChecks(theme.Widgets!, Check);

            ThemePacks.Root = root;
            var discovered = ThemePacks.Discover();
            Check(discovered.Select(item => item.Id).SequenceEqual(["broken-widgets", "test-theme"]),
                "Discovery must list only folders with a theme.json.");

            var original = CreateIcon(32);
            ThemePacks.Select(null);
            var changes = 0;
            EventHandler onChanged = (_, _) => changes++;
            ThemePacks.Changed += onChanged;
            try
            {
                Check(ReferenceEquals(ThemePacks.ApplyIcon(original, @"C:\Apps\unknown.exe"), original), "Without a theme the original icon must be used.");
                Check(ThemePacks.ApplyIcon(null, null, specialKey: IconPack.StartKey) is null, "Without a theme the Start button keeps its logo.");
                Check(ThemePacks.Select("test-theme") && changes == 1 && ThemePacks.Current?.Id == "test-theme",
                    "Selecting a theme must activate it and notify once.");
                Check(!ThemePacks.Select("TEST-THEME") && changes == 1, "Selecting the active theme again must not notify.");
                Check(ThemePacks.Select("test-theme", reload: true) && changes == 2, "A reload must notify.");

                var themed = ThemePacks.ApplyIcon(original, @"C:\Apps\firefox.exe");
                Check(themed is not null && themed.PixelWidth == ThemePacks.RenderSize,
                    "A mapped app must keep its real icon on the pack plate.");
                Check(ReferenceEquals(themed, ThemePacks.ApplyIcon(original, @"C:\Apps\firefox.exe")), "Plated icons must be cached.");
                Check(ThemePacks.ApplyIcon(null, @"C:\Apps\firefox.exe")?.PixelWidth == 48,
                    "An app without a resolved icon may fall back to the pack image.");
                Check(ThemePacks.ApplyIcon(null, null, specialKey: IconPack.StartKey)?.PixelWidth == 40, "The @start image must replace the Start logo.");

                var plated = ThemePacks.ApplyIcon(original, @"C:\Apps\unknown.exe");
                Check(plated is not null && plated.PixelWidth == ThemePacks.RenderSize, "Unmapped apps must be placed on the pack plate.");
                Check(Corners(plated!) == "TR", "A leaf plate must keep only its sharp corner square.");
                Check(ReferenceEquals(plated, ThemePacks.ApplyIcon(original, @"C:\Apps\unknown.exe")), "Plates must be cached per source icon.");
                Check(ThemePacks.ApplyIcon(null, @"C:\Apps\unknown.exe") is null, "A missing icon must stay missing when the pack has no image.");

                ThemePacks.Select("no-such-theme");
                Check(ThemePacks.Current is null && changes == 3, "An unknown theme must fall back to the built-in look.");
                Check(ReferenceEquals(ThemePacks.ApplyIcon(original, @"C:\Apps\firefox.exe"), original), "Clearing the theme must restore original icons.");
            }
            finally
            {
                ThemePacks.Changed -= onChanged;
            }

            Check(Corners(Render(new IconPackFallback(IconPackShape.Petal, IconPackCorner.TopRight))) == "TR+BL",
                "A petal plate must keep two opposite corners square.");
            Check(Corners(Render(new IconPackFallback(IconPackShape.Circle))) == "", "A circle plate must have no square corner.");

            var color = ThemePacks.AutoBackground(CreateIcon(8, Colors.Red));
            Check(color.R == 255 && color.G < 210 && color.G == color.B, "The automatic plate must be a pale tint of the icon color.");
        }
        finally
        {
            ThemePacks.Select(null);
            ThemePacks.Root = previousRoot;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        var themesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "themes");
        if (!Directory.Exists(themesDir)) themesDir = Path.Combine(Directory.GetCurrentDirectory(), "themes");
        if (Directory.Exists(themesDir))
        {
            foreach (var subDir in Directory.EnumerateDirectories(themesDir))
            {
                var (t, err, prob) = ThemePack.Load(subDir);
                Check(err is null && t is not null, $"Theme {Path.GetFileName(subDir)} must load without errors.");
                Check(prob.Count == 0, $"Theme {Path.GetFileName(subDir)} must have zero problems.");
                Check(t!.Widgets is not null, $"Theme {Path.GetFileName(subDir)} must have a valid WidgetPack.");
            }
        }

        return checks;
    }

    private static void WidgetChecks(WidgetPack pack, Action<bool, string> check)
    {
        var current = new Dictionary<string, object>
        {
            ["WidgetAccent"] = Brushes.Teal,
            ["WidgetRadius"] = new CornerRadius(12)
        };
        var problems = new List<string>();
        var dark = pack.Resolve(AppTheme.Dark, key => current.GetValueOrDefault(key), problems);
        check(dark["WidgetAccent"] is SolidColorBrush { Color: var darkAccent } && darkAccent == Color.FromRgb(0x11, 0x11, 0x11),
            "Theme-specific tokens must override shared tokens.");
        check(dark["WidgetRadius"] is CornerRadius { TopLeft: 16, BottomRight: 16 }, "A number must become a uniform corner radius.");
        check(!dark.ContainsKey("Unknown") && problems.Count == 1, "Unknown tokens must be reported and skipped.");
        var light = pack.Resolve(AppTheme.Light, key => current.GetValueOrDefault(key), []);
        check(light["WidgetAccent"] is SolidColorBrush { Color: var lightAccent } && lightAccent == Color.FromRgb(0x00, 0xBF, 0xFF),
            "Shared tokens must apply when the theme has no section of its own.");

        JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
        check(WidgetPack.Convert(Json("""{ "stops": ["#FF000000", "#FFFFFFFF", "#FF00FF00"], "direction": "horizontal" }"""), Brushes.Teal)
            is LinearGradientBrush { GradientStops.Count: 3, EndPoint.X: 1, EndPoint.Y: 0 }, "Gradient tokens must become linear gradients.");
        check(WidgetPack.Convert(Json("\"4,2\""), new Thickness()) is Thickness { Left: 4, Top: 2, Right: 4, Bottom: 2 },
            "Two numbers must become a horizontal and vertical thickness.");
        check(WidgetPack.Convert(Json("13.5"), 12.0) is 13.5, "Sizes must accept numbers.");
        check(WidgetPack.Convert(Json("\"Segoe UI Variable\""), new FontFamily("Segoe UI")) is FontFamily { Source: "Segoe UI Variable" },
            "Fonts must accept a family name.");
        check(WidgetPack.Convert(Json("\"teal\""), Brushes.Teal) is null, "Only #-colors are accepted for brushes.");
        check(WidgetPack.Convert(Json("-3"), new CornerRadius()) is null && WidgetPack.Convert(Json("\"1,2,3\""), new Thickness()) is null,
            "Negative or malformed sizes must be rejected.");
        check(WidgetPack.Convert(Json("12"), Brushes.Teal) is null, "A number must not replace a brush.");
    }

    private static BitmapSource Render(IconPackFallback fallback)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(Brushes.Black, null, ThemePacks.CreateShape(fallback, ThemePacks.RenderSize));
        }

        var bitmap = new RenderTargetBitmap(ThemePacks.RenderSize, ThemePacks.RenderSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    // The square corners of a plate, as opaque pixels right at the corner.
    private static string Corners(BitmapSource image)
    {
        var last = image.PixelWidth - 1;
        var corners = new List<string>();
        if (Alpha(image, 0, 0) > 0) corners.Add("TL");
        if (Alpha(image, last, 0) > 0) corners.Add("TR");
        if (Alpha(image, last, last) > 0) corners.Add("BR");
        if (Alpha(image, 0, last) > 0) corners.Add("BL");
        return string.Join("+", corners);
    }

    private static BitmapSource CreateIcon(int size, Color? color = null)
    {
        var fill = color ?? Colors.Orange;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = fill.B;
            pixels[i + 1] = fill.G;
            pixels[i + 2] = fill.R;
            pixels[i + 3] = 255;
        }

        var icon = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        icon.Freeze();
        return icon;
    }

    private static void WritePng(string path, int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateIcon(size)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte Alpha(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }
}
