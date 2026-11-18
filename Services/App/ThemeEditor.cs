using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>Which section of a widget pack a theme editor row writes to.</summary>
public enum ThemeEditScope
{
    Shared,
    Dark,
    Light
}

/// <summary>
/// One editable design token: a resource dictionary entry whose type the widget pack format can express.
/// <see cref="DarkBase"/> and <see cref="LightBase"/> are the untouched palette values, so the editor can
/// show a per-theme starting point even while the other theme is active.
/// </summary>
public sealed class ThemeToken
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required object DarkBase { get; init; }
    public required object LightBase { get; init; }

    public string Label
    {
        get
        {
            var text = new StringBuilder(Key.Length + 4);
            foreach (var c in Key)
            {
                if (char.IsUpper(c) && text.Length > 0) text.Append(' ');
                text.Append(c);
            }
            return text.ToString();
        }
    }

    public object BaseFor(ThemeEditScope scope, AppTheme active) => scope switch
    {
        ThemeEditScope.Dark => DarkBase,
        ThemeEditScope.Light => LightBase,
        _ => active == AppTheme.Light ? LightBase : DarkBase
    };
}

/// <summary>
/// Reads and writes widget pack token maps for the Settings theme editor. The live look is always produced
/// through the normal pipeline: edits land in <c>widgets\pack.json</c>, then the theme reloads.
/// </summary>
public static class ThemeEditor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    // Palette key → group; checked in order, first prefix wins.
    private static readonly (string Prefix, string Group)[] Groups =
    [
        ("Dock", "Dock"), ("Start", "Dock"), ("Indicator", "Dock"), ("WindowsLogo", "Dock"), ("Icon", "Dock"),
        ("Widget", "Widgets"), ("Preview", "Widgets"), ("Notification", "Widgets"),
        ("Text", "Text"), ("Interface", "Text"), ("Caption", "Text"),
        ("Menu", "Chrome"), ("Settings", "Chrome"), ("Surface", "Chrome"), ("Stroke", "Chrome"),
        ("Accent", "Chrome"), ("Danger", "Chrome"), ("Control", "Chrome")
    ];

    public static readonly string[] GroupOrder = ["Dock", "Widgets", "Text", "Chrome", "Layout"];

    // The dock-size profile writes these to the root dictionary at runtime, so they belong to the
    // Dock size setting rather than the theme.
    private static readonly HashSet<string> RuntimeOwnedKeys =
        ["DockCellSize", "DockCellHeight", "DockIconSize", "StartLogoSize"];

    /// <summary>All editable tokens, grouped and ordered for display.</summary>
    public static IReadOnlyList<ThemeToken> Catalog()
    {
        var dark = LoadPalette(AppTheme.Dark);
        var light = LoadPalette(AppTheme.Light);
        var paletteKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in dark.Keys) if (key is string s) paletteKeys.Add(s);
        foreach (var key in light.Keys) if (key is string s) paletteKeys.Add(s);

        var tokens = new List<ThemeToken>();
        foreach (var key in paletteKeys)
        {
            if (dark[key] is { } d && light[key] is { } l && IsEditable(d) && IsEditable(l))
                tokens.Add(new ThemeToken { Key = key, Group = Group(key), DarkBase = d, LightBase = l });
        }

        foreach (var (key, value) in SharedResources())
        {
            if (paletteKeys.Contains(key) || RuntimeOwnedKeys.Contains(key) || !IsEditable(value)) continue;
            tokens.Add(new ThemeToken { Key = key, Group = Group(key), DarkBase = value, LightBase = value });
        }

        return tokens
            .OrderBy(t => Array.IndexOf(GroupOrder, t.Group))
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Token values as plain JSON strings for display ("#FF5EEAD4", "18", "l,t,r,b", "Segoe UI").</summary>
    public static string Display(object value) => value switch
    {
        SolidColorBrush solid => solid.Color.ToString(),
        Color color => color.ToString(),
        double d => d.ToString(CultureInfo.InvariantCulture),
        CornerRadius r => r.TopLeft == r.TopRight && r.TopLeft == r.BottomRight && r.TopLeft == r.BottomLeft
            ? r.TopLeft.ToString(CultureInfo.InvariantCulture)
            : string.Join(',', new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }.Select(n => n.ToString(CultureInfo.InvariantCulture))),
        Thickness t => t.Left == t.Top && t.Left == t.Right && t.Left == t.Bottom
            ? t.Left.ToString(CultureInfo.InvariantCulture)
            : string.Join(',', new[] { t.Left, t.Top, t.Right, t.Bottom }.Select(n => n.ToString(CultureInfo.InvariantCulture))),
        FontFamily font => font.Source,
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>The token map of <paramref name="scope"/> as a mutable copy of the pack manifest.</summary>
    public static Dictionary<string, JsonElement> Section(WidgetPackManifest manifest, ThemeEditScope scope)
    {
        var source = scope switch
        {
            ThemeEditScope.Dark => manifest.Dark,
            ThemeEditScope.Light => manifest.Light,
            _ => manifest.Tokens
        };
        return new Dictionary<string, JsonElement>(source ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
    }

    /// <summary>The manifest with one replaced section, ready to be written back.</summary>
    public static WidgetPackManifest WithSection(WidgetPackManifest manifest, ThemeEditScope scope, IReadOnlyDictionary<string, JsonElement> section) => scope switch
    {
        ThemeEditScope.Dark => manifest with { Dark = section },
        ThemeEditScope.Light => manifest with { Light = section },
        _ => manifest with { Tokens = section }
    };

    /// <summary>
    /// Converts a typed value back to pack.json form. Returns null for values the format cannot express.
    /// Gradients keep their stop colors and direction; a radial gradient becomes a linear one.
    /// </summary>
    public static JsonElement? Serialize(object value)
    {
        try
        {
            return value switch
            {
                SolidColorBrush solid => JsonSerializer.SerializeToElement(solid.Color.ToString()),
                Color color => JsonSerializer.SerializeToElement(color.ToString()),
                GradientBrush gradient => SerializeGradient(gradient),
                CornerRadius r => SerializeNumbers(r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft),
                Thickness t => SerializeNumbers(t.Left, t.Top, t.Right, t.Bottom),
                double d when double.IsFinite(d) && d >= 0 => JsonSerializer.SerializeToElement(d),
                FontFamily font when !string.IsNullOrWhiteSpace(font.Source) => JsonSerializer.SerializeToElement(font.Source),
                _ => null
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Loads the pack's widget manifest, or a blank one when the theme has no widget pack yet.</summary>
    public static WidgetPackManifest LoadManifest(ThemePack pack) =>
        pack.Widgets?.Manifest ?? new WidgetPackManifest(pack.Manifest.Name, null, null, null);

    /// <summary>Persists the manifest to <c>widgets\pack.json</c>. Returns null on success or an error key.</summary>
    public static string? SaveManifest(ThemePack pack, WidgetPackManifest manifest)
    {
        try
        {
            var directory = Path.Combine(pack.Directory, ThemePack.WidgetFolder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, WidgetPack.ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            AppLog.Warning($"Theme editor could not save '{pack.Id}': {ex.Message}");
            return "ErrorThemeInstall";
        }
    }

    private static JsonElement SerializeGradient(GradientBrush gradient)
    {
        var stops = gradient.GradientStops.Select(stop => stop.Color.ToString()).ToArray();
        var direction = gradient is LinearGradientBrush linear
            ? linear.EndPoint is { X: 1, Y: 0 } ? "horizontal" : linear.EndPoint is { X: 1, Y: 1 } ? "diagonal" : "vertical"
            : "vertical";
        return JsonSerializer.SerializeToElement(new { stops, direction });
    }

    private static JsonElement SerializeNumbers(double a, double b, double c, double d) =>
        a == b && a == c && a == d
            ? JsonSerializer.SerializeToElement(a)
            : JsonSerializer.SerializeToElement(string.Join(',', new[] { a, b, c, d }.Select(n => n.ToString(CultureInfo.InvariantCulture))));

    private static bool IsEditable(object value) =>
        value is Brush or Color or CornerRadius or Thickness or double or FontFamily;

    private static string Group(string key)
    {
        foreach (var (prefix, group) in Groups)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return group;
        }
        return "Layout";
    }

    private static ResourceDictionary LoadPalette(AppTheme theme) => new()
    {
        Source = new Uri($"/OpenTaskbar;component/Design/Palettes/{theme}.xaml", UriKind.Relative)
    };

    private static IEnumerable<(string Key, object Value)> SharedResources()
    {
        var resources = Application.Current?.Resources;
        if (resources is null) yield break;
        // A key can live in several dictionaries (runtime values shadow the merged files);
        // last write wins, matching resource lookup.
        var effective = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in Flatten(resources))
        {
            if (key is string s) effective[s] = value;
        }
        foreach (var pair in effective) yield return (pair.Key, pair.Value);
    }

    private static IEnumerable<(object Key, object Value)> Flatten(ResourceDictionary dictionary)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (ThemeManager.IsManagedDictionary(merged)) continue;
            foreach (var entry in Flatten(merged)) yield return entry;
        }
        foreach (var key in dictionary.Keys)
        {
            if (dictionary[key] is { } value) yield return (key, value);
        }
    }
}
