using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary><c>theme.json</c> at the root of a theme folder.</summary>
public sealed record ThemePackManifest(string? Name = null, string? Author = null, string? Description = null);

/// <summary>
/// A theme folder: <c>theme.json</c>, an optional icon pack in <c>icons\</c> and an optional widget pack in <c>widgets\</c>.
/// </summary>
public sealed class ThemePack
{
    public const string ManifestFileName = "theme.json";
    public const string IconFolder = "icons";
    public const string WidgetFolder = "widgets";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private ThemePack(string id, string directory, ThemePackManifest manifest, IconPack? icons, WidgetPack? widgets)
    {
        Id = id;
        Directory = directory;
        Manifest = manifest;
        Icons = icons;
        Widgets = widgets;
    }

    /// <summary>The folder name, which is what settings store.</summary>
    public string Id { get; }
    public string Directory { get; }
    public ThemePackManifest Manifest { get; }
    public IconPack? Icons { get; }
    public WidgetPack? Widgets { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Id : Manifest.Name!;

    /// <summary>Loads a theme; a broken icon or widget pack is reported in <c>Problems</c> and left out.</summary>
    public static (ThemePack? Pack, string? Error, IReadOnlyList<string> Problems) Load(string directory)
    {
        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var manifestPath = Path.Combine(directory, ManifestFileName);
        ThemePackManifest? manifest;
        try
        {
            if (!File.Exists(manifestPath)) return (null, $"{manifestPath} not found.", []);
            manifest = JsonSerializer.Deserialize<ThemePackManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null) return (null, $"{manifestPath} is empty.", []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (null, $"{manifestPath}: {ex.Message}", []);
        }

        var problems = new List<string>();
        IconPack? icons = null;
        WidgetPack? widgets = null;
        var iconDirectory = Path.Combine(directory, IconFolder);
        if (File.Exists(Path.Combine(iconDirectory, IconPack.ManifestFileName)))
        {
            (icons, var iconError) = IconPack.Load(iconDirectory);
            if (iconError is not null) problems.Add(iconError);
        }

        var widgetDirectory = Path.Combine(directory, WidgetFolder);
        if (File.Exists(Path.Combine(widgetDirectory, WidgetPack.ManifestFileName)))
        {
            (widgets, var widgetError) = WidgetPack.Load(widgetDirectory);
            if (widgetError is not null) problems.Add(widgetError);
        }

        return (new ThemePack(id, directory, manifest, icons, widgets), null, problems);
    }
}

/// <summary>
/// The active theme pack. Themes are folders under <c>%LOCALAPPDATA%\OpenTaskbar\Themes</c>; none ship with the app.
/// </summary>
public static class ThemePacks
{
    public const int RenderSize = 128;
    private const string BuiltInResourcePrefix = "OpenTaskbar.BuiltInThemes/";
    private const int MaxCachedImages = 24;
    private static readonly Dictionary<string, BitmapSource?> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> ImageOrder = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, BitmapSource> Plated = new();
    private static readonly Dictionary<string, (ResourceDictionary Owner, object Value)> MetricOriginals = new(StringComparer.Ordinal);
    private static int _preferredIconPixels;

    /// <summary>Optional DPI-scaled max decode pixel size for icon pack PNGs (0 = natural image size).</summary>
    public static int PreferredIconPixels
    {
        get => _preferredIconPixels;
        set
        {
            if (_preferredIconPixels == value) return;
            _preferredIconPixels = value;
            Images.Clear();
            ImageOrder.Clear();
        }
    }

    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar", "Themes");

    public static ThemePack? Current { get; private set; }

    /// <summary>Copies bundled theme packs into the user theme folder without replacing user files.</summary>
    public static string? InstallBuiltInThemes()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Root);
            var assembly = typeof(ThemePacks).Assembly;
            var resources = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(BuiltInResourcePrefix, StringComparison.Ordinal))
                .Select(name => (Name: name, RelativePath: name[BuiltInResourcePrefix.Length..]
                    .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)))
                .ToArray();
            if (resources.Length == 0) throw new InvalidDataException("No built-in theme resources were found.");

            foreach (var theme in resources.GroupBy(resource => resource.RelativePath.Split(Path.DirectorySeparatorChar)[0], StringComparer.OrdinalIgnoreCase))
            {
                foreach (var resource in theme)
                {
                    var destination = Path.GetFullPath(Path.Combine(Root, resource.RelativePath));
                    var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Warning($"Built-in theme resource path was rejected: {resource.RelativePath}");
                        continue;
                    }

                    if (File.Exists(destination)) continue;
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = assembly.GetManifestResourceStream(resource.Name);
                    if (input is null) throw new InvalidDataException($"Built-in theme resource '{resource.Name}' is missing.");
                    using var output = File.Create(destination);
                    input.CopyTo(output);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warning($"Built-in theme packs could not be installed: {ex.Message}");
            return "ErrorThemeInstall";
        }
    }

    /// <summary>Raised after <see cref="Select"/> changes the theme, so views can reload their icons.</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyList<ThemePack> Discover()
    {
        try
        {
            if (!System.IO.Directory.Exists(Root)) return [];
            return System.IO.Directory.EnumerateDirectories(Root)
                .Select(ThemePack.Load)
                .Where(result => result.Pack is not null)
                .Select(result => result.Pack!)
                .OrderBy(pack => pack.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Theme packs could not be listed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Activates the theme in folder <paramref name="id"/>; null or an unknown folder restores the built-in look.
    /// Selecting the active theme again does nothing unless <paramref name="reload"/> rereads it from disk.
    /// Returns true when the active theme changed.
    /// </summary>
    public static bool Select(string? id, bool reload = false)
    {
        var requested = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        if (!reload && string.Equals(Current?.Id, requested, StringComparison.OrdinalIgnoreCase)
            && (Current is not null || requested is null)) return false;
        ThemePack? pack = null;
        if (requested is not null)
        {
            var (loaded, error, problems) = ThemePack.Load(Path.Combine(Root, requested));
            if (error is not null) AppLog.Warning($"Theme pack '{requested}' could not be loaded: {error}");
            foreach (var problem in problems) AppLog.Warning($"Theme pack '{requested}': {problem}");
            pack = loaded;
        }

        var changed = reload || !string.Equals(Current?.Id, pack?.Id, StringComparison.OrdinalIgnoreCase);
        Current = pack;
        if (!changed) return false;
        Images.Clear();
        ImageOrder.Clear();
        Plated.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Applies the active widget pack for <paramref name="theme"/>. Brushes and colors change at once; sizes, radii and
    /// fonts are read when a view is first built, so they take effect after a restart.
    /// </summary>
    public static IReadOnlyList<string> ApplyWidgets(AppTheme theme)
    {
        var problems = new List<string>();
        var resources = Application.Current?.Resources;
        if (resources is null) return problems;
        RestoreMetrics();
        ThemeManager.SetOverrides(new Dictionary<string, object>());
        var pack = Current?.Widgets;
        if (pack is null) return problems;

        var values = pack.Resolve(theme, key => resources[key], problems);
        var live = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (value is Brush or Color)
            {
                live[key] = value;
                continue;
            }

            var owner = FindOwner(resources, key);
            if (owner is null) continue;
            MetricOriginals.TryAdd(key, (owner, owner[key]));
            owner[key] = value;
        }

        ThemeManager.SetOverrides(live);
        foreach (var problem in problems) AppLog.Warning($"Widget pack: {problem}");
        return problems;
    }

    private static void RestoreMetrics()
    {
        foreach (var (key, (owner, value)) in MetricOriginals) owner[key] = value;
        MetricOriginals.Clear();
    }

    private static ResourceDictionary? FindOwner(ResourceDictionary dictionary, string key)
    {
        for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var merged = dictionary.MergedDictionaries[i];
            if (ThemeManager.IsManagedDictionary(merged)) continue;
            if (FindOwner(merged, key) is { } owner) return owner;
        }

        return dictionary.Contains(key) && !ThemeManager.IsManagedDictionary(dictionary) ? dictionary : null;
    }

    /// <summary>
    /// The original icon on the theme's plate. The theme's own image only applies to special items
    /// (<c>@start</c>, <c>@recyclebin</c>) or to an app whose real icon could not be resolved, so an
    /// application is never replaced by the pack's generic category artwork.
    /// </summary>
    public static BitmapSource? ApplyIcon(BitmapSource? original, string? executablePath, string? appId = null, string? specialKey = null, int targetPixels = 0)
    {
        var pack = Current?.Icons;
        if (pack is null) return original;
        if (specialKey is not null || original is null)
        {
            var file = pack.FindIconFile(executablePath, appId, specialKey);
            if (file is not null && LoadImage(file, targetPixels) is { } themed) return themed;
        }

        // Special items keep their built-in look unless the pack draws them.
        if (original is null || specialKey == IconPack.StartKey || pack.Fallback.Shape == IconPackShape.None) return original;
        if (Plated.TryGetValue(original, out var plated)) return plated;
        try
        {
            plated = RenderPlate(original, pack.Fallback);
            Plated.AddOrUpdate(original, plated);
            return plated;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            AppLog.Warning($"Icon plate failed: {ex.Message}");
            return original;
        }
    }

    private static BitmapSource? LoadImage(string file, int targetPixels = 0)
    {
        var decodeTarget = targetPixels > 0 ? targetPixels : _preferredIconPixels;
        var cacheKey = decodeTarget > 0 ? $"{file}|{decodeTarget}" : file;
        if (Images.TryGetValue(cacheKey, out var cached)) return cached;
        BitmapSource? image = null;
        try
        {
            if (decodeTarget > 0)
            {
                using var stream = File.OpenRead(file);
                var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = header.Frames[0];
                var decodeWide = frame.PixelWidth > decodeTarget || frame.PixelHeight > decodeTarget;
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWide)
                {
                    if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = decodeTarget;
                    else bitmap.DecodePixelHeight = decodeTarget;
                }
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            else
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(file, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warning($"Icon pack image '{file}' could not be loaded: {ex.Message}");
        }

        if (image is not null)
        {
            while (Images.Count >= MaxCachedImages && ImageOrder.TryDequeue(out var oldest))
            {
                Images.Remove(oldest);
            }
            Images[cacheKey] = image;
            ImageOrder.Enqueue(cacheKey);
        }
        return image;
    }

    internal static Geometry CreateShape(IconPackFallback fallback, double size)
    {
        var rect = new Rect(0, 0, size, size);
        var radius = Math.Clamp(fallback.CornerRadius, 0, 0.5) * size;
        switch (fallback.Shape)
        {
            case IconPackShape.Circle:
                return new EllipseGeometry(rect);
            case IconPackShape.Rounded:
                return new RectangleGeometry(rect, radius, radius);
            case IconPackShape.Petal:
                return CreateCorners(radius, size, fallback.SharpCorner, Opposite(fallback.SharpCorner));
            default:
                return CreateCorners(radius, size, fallback.SharpCorner);
        }
    }

    private static IconPackCorner Opposite(IconPackCorner corner) => corner switch
    {
        IconPackCorner.TopLeft => IconPackCorner.BottomRight,
        IconPackCorner.TopRight => IconPackCorner.BottomLeft,
        IconPackCorner.BottomLeft => IconPackCorner.TopRight,
        _ => IconPackCorner.TopLeft
    };

    // Rounded corners except the square ones, clockwise from the top left.
    private static Geometry CreateCorners(double radius, double size, params IconPackCorner[] square)
    {
        double R(IconPackCorner corner) => square.Contains(corner) ? 0 : radius;
        var tl = R(IconPackCorner.TopLeft);
        var tr = R(IconPackCorner.TopRight);
        var br = R(IconPackCorner.BottomRight);
        var bl = R(IconPackCorner.BottomLeft);
        var figure = new PathFigure { StartPoint = new Point(tl, 0), IsClosed = true, IsFilled = true };
        void Line(double x, double y) => figure.Segments.Add(new LineSegment(new Point(x, y), true));
        void Arc(double x, double y, double r)
        {
            if (r > 0) figure.Segments.Add(new ArcSegment(new Point(x, y), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        }

        Line(size - tr, 0);
        Arc(size, tr, tr);
        Line(size, size - br);
        Arc(size - br, size, br);
        Line(bl, size);
        Arc(0, size - bl, bl);
        Line(0, tl);
        Arc(tl, 0, tl);
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static BitmapSource RenderPlate(BitmapSource icon, IconPackFallback fallback)
    {
        const double size = RenderSize;
        var shape = CreateShape(fallback, size);
        var plate = ParseColor(fallback.Background) ?? AutoBackground(icon);
        // A soft top-to-bottom shade gives the flat plate the depth of hand-drawn launcher icons.
        var fill = new LinearGradientBrush(Lighten(plate, 0.18), Darken(plate, 0.08), 90);
        fill.Freeze();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(fill, null, shape);
            var scale = Math.Clamp(fallback.IconScale, 0.2, 1.0);
            var inner = size * scale;
            var offset = (size - inner) / 2;
            context.PushClip(shape);
            context.DrawImage(icon, new Rect(offset, offset, inner, inner));
            context.Pop();
        }

        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        var bitmap = new RenderTargetBitmap(RenderSize, RenderSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(color.A,
        (byte)(color.R + (255 - color.R) * amount), (byte)(color.G + (255 - color.G) * amount), (byte)(color.B + (255 - color.B) * amount));

    private static Color Darken(Color color, double amount) => Color.FromArgb(color.A,
        (byte)(color.R * (1 - amount)), (byte)(color.G * (1 - amount)), (byte)(color.B * (1 - amount)));

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A pale tint of the icon's average opaque color, light enough for the icon to stay readable on it.</summary>
    internal static Color AutoBackground(BitmapSource icon)
    {
        var source = icon.Format == PixelFormats.Bgra32 || icon.Format == PixelFormats.Pbgra32
            ? icon
            : new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        var premultiplied = source.Format == PixelFormats.Pbgra32;
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha < 128) continue;
            // Premultiplied channels are divided back so translucent edges do not darken the tint.
            var a = premultiplied ? alpha / 255.0 : 1;
            b += pixels[i] / a;
            g += pixels[i + 1] / a;
            r += pixels[i + 2] / a;
            weight++;
        }

        if (weight == 0) return Colors.White;
        static byte Tint(double channel) => (byte)Math.Clamp(Math.Round(channel * 0.22 + 255 * 0.78), 0, 255);
        return Color.FromRgb(Tint(r / weight), Tint(g / weight), Tint(b / weight));
    }
}
