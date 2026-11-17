using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class WindowsTaskbarPinImporter
{
    public static (IReadOnlyList<DockApp> Apps, string? ErrorKey, int SkippedCount) Import()
    {
        var taskbarPinsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");

        if (!Directory.Exists(taskbarPinsPath))
        {
            return ([], null, 0);
        }

        List<string> shortcutPaths;
        try
        {
            shortcutPaths = Directory.EnumerateFiles(taskbarPinsPath, "*.lnk", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return ([], "ErrorTaskbarPinsRead", 0);
        }
        catch (UnauthorizedAccessException)
        {
            return ([], "ErrorTaskbarPinsAccess", 0);
        }

        if (shortcutPaths.Count == 0)
        {
            return ([], null, 0);
        }

        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (COMException)
        {
            return ([], "ErrorShortcutServiceImport", 0);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return ([], "ErrorShortcutServiceImport", 0);
        }

        if (shellType is null || shell is null)
        {
            return ([], "ErrorShortcutServiceImport", 0);
        }

        var apps = new List<DockApp>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedCount = 0;
        try
        {
            foreach (var shortcutPath in shortcutPaths)
            {
                try
                {
                    var shortcut = shellType.InvokeMember(
                        "CreateShortcut",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: shell,
                        args: [shortcutPath]);
                    if (shortcut is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        var shortcutType = shortcut.GetType();
                        var targetPath = ReadStringProperty(shortcutType, shortcut, "TargetPath");
                        var arguments = ReadStringProperty(shortcutType, shortcut, "Arguments");
                        var iconLocation = ReadStringProperty(shortcutType, shortcut, "IconLocation");
                        var executablePath = ResolveExecutablePath(targetPath, iconLocation);
                        if (executablePath is null || !seenPaths.Add(executablePath))
                        {
                            skippedCount++;
                            continue;
                        }

                        apps.Add(new DockApp(Path.GetFileNameWithoutExtension(shortcutPath), executablePath)
                        {
                            LaunchArguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
                            ShortcutPath = Path.GetFullPath(shortcutPath),
                            IconLocation = string.IsNullOrWhiteSpace(iconLocation) ? null : iconLocation
                        });
                    }
                    finally
                    {
                        if (Marshal.IsComObject(shortcut))
                        {
                            _ = Marshal.FinalReleaseComObject(shortcut);
                        }
                    }
                }
                catch (COMException)
                {
                    skippedCount++;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException)
                {
                    skippedCount++;
                }
                catch (IOException)
                {
                    skippedCount++;
                }
                catch (UnauthorizedAccessException)
                {
                    skippedCount++;
                }
            }
        }
        catch (COMException)
        {
            return (apps, "ErrorShortcutServiceStopped", skippedCount);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (apps, "ErrorShortcutServiceStopped", skippedCount);
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }

        return (apps, null, skippedCount);
    }

    public static (string? ShortcutPath, string? Error) AddPin(string appName, string executablePath, string? launchArguments = null)
    {
        var pinDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar"));

        try
        {
            Directory.CreateDirectory(pinDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "ErrorTaskbarPinFolderCreate");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = new string(appName.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = Path.GetFileNameWithoutExtension(executablePath);
        }

        var shortcutPath = Path.Combine(pinDirectory, safeName + ".lnk");

        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (COMException)
        {
            return (null, "ErrorShortcutServicePin");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (null, "ErrorShortcutServicePin");
        }

        if (shellType is null || shell is null)
        {
            return (null, "ErrorShortcutServicePin");
        }

        try
        {
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);

            if (shortcut is null)
            {
                return (null, "ErrorShortcutCreate");
            }

            try
            {
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executablePath]);
                var workingDir = Path.GetDirectoryName(executablePath);
                if (!string.IsNullOrEmpty(workingDir))
                {
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDir]);
                }
                if (!string.IsNullOrWhiteSpace(launchArguments))
                {
                    shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [launchArguments]);
                }
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return (shortcutPath, null);
            }
            finally
            {
                if (Marshal.IsComObject(shortcut))
                {
                    _ = Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            return (null, "ErrorTaskbarPin");
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public static string? RemovePin(string shortcutPath)
    {
        var pinDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar"));
        var fullPath = Path.GetFullPath(shortcutPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), pinDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "ErrorTaskbarPinPath";
        }

        try
        {
            File.Delete(fullPath);
            return null;
        }
        catch (IOException)
        {
            return "ErrorTaskbarUnpin";
        }
        catch (UnauthorizedAccessException)
        {
            return "ErrorTaskbarUnpinAccess";
        }
    }

    private static string? ResolveExecutablePath(string targetPath, string iconLocation)
    {
        var normalizedTarget = Environment.ExpandEnvironmentVariables(targetPath.Trim().Trim('"'));
        if (Path.GetExtension(normalizedTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(normalizedTarget))
        {
            return Path.GetFullPath(normalizedTarget);
        }

        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            var iconPath = iconLocation.Split(',', 2)[0].Trim().Trim('"');
            var normalizedIconPath = Environment.ExpandEnvironmentVariables(iconPath);
            if (string.Equals(Path.GetFileName(normalizedIconPath), "explorer.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(normalizedIconPath))
            {
                return Path.GetFullPath(normalizedIconPath);
            }
        }

        return null;
    }

    private static string ReadStringProperty(Type shortcutType, object shortcut, string propertyName)
    {
        return shortcutType.InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: shortcut,
            args: null) as string ?? string.Empty;
    }
}
