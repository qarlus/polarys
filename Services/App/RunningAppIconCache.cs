using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>
/// Icons for running windows. Executable resources come first. Packaged apps and apps hosted by
/// ApplicationFrameHost often have none, so the window's AppUserModelID selects the Start menu image
/// (<c>shell:AppsFolder\{AUMID}</c>) before the generic file icon is used.
/// </summary>
public sealed class RunningAppIconCache
{
    public const int AttemptLimit = 4;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    // Hosted frames run every Store app under one shared executable, whose own icon belongs to none
    // of them. Their identity icons come from the window's AppUserModelID instead.
    private static readonly HashSet<string> SharedHostExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe",
        "wwahost.exe"
    };

    private readonly Func<string, BitmapSource?> _resourceIcon;
    private readonly Func<nint, string?> _appId;
    private readonly Func<string, BitmapSource?> _appIcon;
    private readonly Func<string, BitmapSource?> _fileIcon;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, BitmapSource?> _resourceIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource?> _fileIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource> _appIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, Lookup> _windows = [];

    public RunningAppIconCache()
        : this(
            Guard<string, BitmapSource>(path => ApplicationIconProvider.GetForResource(path, 0)),
            Guard<nint, string>(window => NativeJumpListSession.ReadWindowAppId(window) ?? NativeWindowTracker.GetPackagedAppId(window)),
            Guard<string, BitmapSource>(appId => ApplicationIconProvider.GetForShellItem(@"shell:AppsFolder\" + appId, ApplicationIconProvider.PreferredIconPixels, iconOnly: true)),
            Guard<string, BitmapSource>(path => ApplicationIconProvider.GetForFile(path)),
            TimeProvider.System)
    {
    }

    public RunningAppIconCache(
        Func<string, BitmapSource?> resourceIcon,
        Func<nint, string?> appId,
        Func<string, BitmapSource?> appIcon,
        Func<string, BitmapSource?> fileIcon,
        TimeProvider clock)
    {
        _resourceIcon = resourceIcon;
        _appId = appId;
        _appIcon = appIcon;
        _fileIcon = fileIcon;
        _clock = clock;
    }

    /// <summary>True while a window without an identity icon still has lookups left; call <see cref="Get"/> again after <see cref="RetryDelay"/>.</summary>
    public bool HasPendingLookups
    {
        get
        {
            var now = _clock.GetTimestamp();
            return _windows.Values.Any(lookup => IsPending(lookup, now));
        }
    }

    public BitmapSource? Get(string executablePath, nint window)
    {
        BitmapSource? icon = null;
        if (!SharedHostExecutables.Contains(Path.GetFileName(executablePath))
            && !_resourceIcons.TryGetValue(executablePath, out icon))
        {
            _resourceIcons[executablePath] = icon = _resourceIcon(executablePath);
        }

        // Shell images are keyed by identity, never by executable: ApplicationFrameHost.exe hosts many apps.
        icon ??= GetAppIcon(window);
        if (icon is null && !_fileIcons.TryGetValue(executablePath, out icon))
        {
            _fileIcons[executablePath] = icon = _fileIcon(executablePath);
        }

        // The icon pack is applied last, so switching packs needs no cache reset here.
        var appId = _windows.TryGetValue(window, out var lookup) ? lookup.AppId : null;
        return ThemePacks.ApplyIcon(icon, executablePath, appId);
    }

    /// <summary>Drops entries for windows and executables that are no longer tracked, so the caches stay bounded.</summary>
    public void Retain(IReadOnlyList<TrackedWindow> windows)
    {
        var handles = windows.Select(window => window.Handle).ToHashSet();
        foreach (var handle in _windows.Keys.Where(handle => !handles.Contains(handle)).ToList())
        {
            _windows.Remove(handle);
        }

        var appIds = _windows.Values.Select(lookup => lookup.AppId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var appId in _appIcons.Keys.Where(appId => !appIds.Contains(appId)).ToList())
        {
            _appIcons.Remove(appId);
        }

        var paths = windows.Select(window => window.ExecutablePath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _resourceIcons.Keys.Where(path => !paths.Contains(path)).ToList())
        {
            _resourceIcons.Remove(path);
            _fileIcons.Remove(path);
        }
    }

    private BitmapSource? GetAppIcon(nint window)
    {
        if (window == 0) return null;
        var now = _clock.GetTimestamp();
        var known = _windows.TryGetValue(window, out var lookup);
        if (known && (!IsPending(lookup, now) || now < lookup.NextAttempt))
        {
            return lookup.Icon;
        }

        // A window can be listed before it publishes its AppUserModelID, so a miss is retried a few times.
        // The deadline also ends retries for windows that are no longer asked for, such as a background frame.
        var appId = lookup.AppId ?? _appId(window);
        BitmapSource? icon = null;
        if (appId is not null && !_appIcons.TryGetValue(appId, out icon))
        {
            icon = _appIcon(appId);
            if (icon is not null) _appIcons[appId] = icon;
        }

        var delay = (long)(RetryDelay.TotalSeconds * _clock.TimestampFrequency);
        var deadline = known ? lookup.Deadline : now + delay * AttemptLimit;
        _windows[window] = new Lookup(appId, icon, lookup.Attempts + 1, now + delay, deadline);
        return icon;
    }

    private static bool IsPending(Lookup lookup, long now) =>
        lookup.Icon is null && lookup.Attempts < AttemptLimit && now < lookup.Deadline;

    // The default sources call into the Shell on the UI thread; a failure there must only cost the icon.
    private static Func<T, TResult?> Guard<T, TResult>(Func<T, TResult?> source) where TResult : class => value =>
    {
        try
        {
            return source(value);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or System.IO.IOException)
        {
            AppLog.Warning($"Application icon lookup failed: {ex.Message}");
            return null;
        }
    };

    private readonly record struct Lookup(string? AppId, BitmapSource? Icon, int Attempts, long NextAttempt, long Deadline);
}
