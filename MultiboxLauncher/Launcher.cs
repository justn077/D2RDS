using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiboxLauncher;

public sealed class LauncherConfig
{
    public PreLaunchConfig? PreLaunch { get; set; }
    public List<LaunchProfile> Profiles { get; set; } = new();
}

public sealed class PreLaunchConfig
{
    public bool Enabled { get; set; } = true;
    public string? Path { get; set; }
}

public sealed class LaunchProfile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class ConfigLoader
{
    public static string DefaultConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    public static LauncherConfig Load()
    {
        var configPath = DefaultConfigPath;
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Missing config file: {configPath}");

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<LauncherConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config is null)
            throw new InvalidOperationException("Failed to parse config.json");

        return config;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        lock (Gate)
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(System.IO.Path.Combine(dir, "launcher.log"), line);
        }
    }
}

public static class PathTokens
{
    public static string Expand(string path)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return path.Replace("%DESKTOP%", desktop, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ResolvedTarget(string TargetPath, string Arguments, string WorkingDirectory);

public static class ShortcutResolver
{
    public static ResolvedTarget Resolve(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);

        string targetPath = shortcut.TargetPath as string ?? "";
        string arguments = shortcut.Arguments as string ?? "";
        string workingDirectory = shortcut.WorkingDirectory as string ?? "";

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new InvalidOperationException($"Shortcut target missing: {lnkPath}");

        return new ResolvedTarget(targetPath, arguments, workingDirectory);
    }
}

public static class ProcessLauncher
{
    public static Task RunPreLaunchAsync(string path)
    {
        return Task.Run(() => StartAndWait(path));
    }

    public static void Launch(string path) => StartNoWait(path);

    private static void StartNoWait(string path)
    {
        var expanded = PathTokens.Expand(path);
        if (!File.Exists(expanded))
            throw new FileNotFoundException($"Path not found: {expanded}");

        if (System.IO.Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ShortcutResolver.Resolve(expanded);
            Process.Start(new ProcessStartInfo
            {
                FileName = resolved.TargetPath,
                Arguments = resolved.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(resolved.WorkingDirectory) ? null : resolved.WorkingDirectory,
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = expanded,
            UseShellExecute = true
        });
    }

    private static void StartAndWait(string path)
    {
        var expanded = PathTokens.Expand(path);
        if (!File.Exists(expanded))
            throw new FileNotFoundException($"Path not found: {expanded}");

        if (System.IO.Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ShortcutResolver.Resolve(expanded);
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = resolved.TargetPath,
                Arguments = resolved.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(resolved.WorkingDirectory) ? null : resolved.WorkingDirectory,
                UseShellExecute = false
            });
            p?.WaitForExit();
            return;
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = expanded,
            UseShellExecute = true
        });
        proc?.WaitForExit();
    }
}
