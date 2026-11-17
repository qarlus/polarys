using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class RunningAppChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var resource = CreateIcon();
        var settings = CreateIcon();
        var calculator = CreateIcon();
        var generic = CreateIcon();
        const string Desktop = @"C:\Apps\Desktop.exe";
        const string Packaged = @"C:\Program Files\WindowsApps\Vendor.App_1.0.0.0_x64__abc\App.exe";
        const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";
        var appIds = new Dictionary<nint, string?> { [1] = null, [2] = "Vendor.App_abc!App", [3] = "Settings!App", [4] = "Calculator!App" };
        var shellIcons = new Dictionary<string, BitmapSource> { ["Vendor.App_abc!App"] = settings, ["Settings!App"] = settings, ["Calculator!App"] = calculator };
        var resourceCalls = 0;
        var identityCalls = 0;
        var shellCalls = 0;
        var clock = new TestClock();
        var cache = new RunningAppIconCache(
            path => { resourceCalls++; return path == Desktop ? resource : null; },
            window => { identityCalls++; return appIds.GetValueOrDefault(window); },
            appId => { shellCalls++; return shellIcons.GetValueOrDefault(appId); },
            _ => generic,
            clock);

        Check(ReferenceEquals(cache.Get(Desktop, 1), resource) && identityCalls == 0,
            "Executable resources must win without querying the window identity.");
        cache.Get(Desktop, 1);
        Check(resourceCalls == 1, "Executable resources must be extracted once per executable.");

        Check(ReferenceEquals(cache.Get(Packaged, 2), settings), "A packaged executable without resources must use its AppsFolder icon.");
        Check(!cache.HasPendingLookups, "A resolved identity icon must not schedule retries.");

        var resourceBeforeHosts = resourceCalls;
        Check(ReferenceEquals(cache.Get(FrameHost, 3), settings) && ReferenceEquals(cache.Get(FrameHost, 4), calculator),
            "Hosted Store apps must not share an icon through their common host executable.");
        Check(resourceCalls == resourceBeforeHosts,
            "A shared host executable must never be asked for its own icon resource.");
        var shellCallsBefore = shellCalls;
        Check(ReferenceEquals(cache.Get(FrameHost, 3), settings) && shellCalls == shellCallsBefore,
            "A resolved identity icon must be reused without another Shell lookup.");

        appIds[5] = null;
        var identityBefore = identityCalls;
        Check(ReferenceEquals(cache.Get(FrameHost, 5), generic) && cache.HasPendingLookups,
            "A window without an identity yet must show the generic icon and wait for a retry.");
        cache.Get(FrameHost, 5);
        Check(identityCalls == identityBefore + 1, "Retries must wait for the retry delay.");
        clock.Advance(RunningAppIconCache.RetryDelay);
        appIds[5] = "Calculator!App";
        Check(ReferenceEquals(cache.Get(FrameHost, 5), calculator) && !cache.HasPendingLookups,
            "An identity published after the window appeared must replace the generic icon.");

        appIds[6] = null;
        for (var attempt = 0; attempt < RunningAppIconCache.AttemptLimit + 3; attempt++)
        {
            cache.Get(FrameHost, 6);
            clock.Advance(RunningAppIconCache.RetryDelay);
        }
        Check(identityCalls == identityBefore + 2 + RunningAppIconCache.AttemptLimit && !cache.HasPendingLookups,
            "Identity lookups must stop after the attempt limit.");

        var idleClock = new TestClock();
        var idle = new RunningAppIconCache(_ => null, _ => null, _ => null, _ => generic, idleClock);
        idle.Get(FrameHost, 7);
        idleClock.Advance(RunningAppIconCache.RetryDelay * RunningAppIconCache.AttemptLimit);
        Check(!idle.HasPendingLookups, "Retries must end for a window that is no longer asked for.");

        cache.Retain([new TrackedWindow(2, "App", Packaged)]);
        var resourceBefore = resourceCalls;
        identityBefore = identityCalls;
        Check(ReferenceEquals(cache.Get(Packaged, 2), settings) && resourceCalls == resourceBefore && identityCalls == identityBefore,
            "Tracked windows must keep their cached icons.");
        cache.Get(Desktop, 1);
        cache.Get(FrameHost, 6);
        Check(resourceCalls == resourceBefore + 1 && identityCalls == identityBefore + 1,
            "Entries of closed windows and executables must be dropped; hosts still skip resource lookups.");
        Check(cache.HasPendingLookups, "A reopened window must be looked up again.");

        return checks;
    }

    private static BitmapSource CreateIcon()
    {
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        icon.Freeze();
        return icon;
    }
}
