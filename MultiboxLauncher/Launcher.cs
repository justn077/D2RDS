using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiboxLauncher;

public sealed class LauncherConfig
{
    public PreLaunchConfig PreLaunch { get; set; } = new();
    public string InstallPath { get; set; } = Defaults.DefaultInstallPath;
    public string Region { get; set; } = "";
    public List<AccountProfile> Accounts { get; set; } = new();
    public List<LaunchProfile> Profiles { get; set; } = new();
    public bool LockOrder { get; set; } = false;
    public BroadcastSettings Broadcast { get; set; } = new();
    public string UpdateToken { get; set; } = "";

    public void Normalize()
    {
        PreLaunch ??= new PreLaunchConfig();
        InstallPath = string.IsNullOrWhiteSpace(InstallPath) ? Defaults.DefaultInstallPath : InstallPath;
        Region ??= "";
        Accounts ??= new List<AccountProfile>();
        Profiles ??= new List<LaunchProfile>();
        Broadcast ??= new BroadcastSettings();
        UpdateToken ??= "";
    }
}

public sealed class PreLaunchConfig
{
    public bool Enabled { get; set; } = true;
    public string? Path { get; set; }
}

public sealed class AccountProfile
{
    public string Id { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Email { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public bool BroadcastEnabled { get; set; } = true;
}

public sealed class BroadcastSettings
{
    public bool Enabled { get; set; } = false;
    public bool BroadcastAll { get; set; } = true;
    public bool Keyboard { get; set; } = true;
    public bool Mouse { get; set; } = true;
    public string ToggleBroadcastHotkey { get; set; } = "Ctrl+Alt+B";
    public string ToggleModeHotkey { get; set; } = "Ctrl+Alt+M";
}

public sealed class LaunchProfile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class ConfigLoader
{
    public static string DefaultConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    public static LauncherConfig LoadOrCreate()
    {
        var configPath = DefaultConfigPath;
        if (!File.Exists(configPath))
        {
            var created = new LauncherConfig();
            created.Normalize();
            Save(created);
            return created;
        }

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<LauncherConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config is null)
            throw new InvalidOperationException("Failed to parse config.json");

        config.Normalize();
        return config;
    }

    public static void Save(LauncherConfig config)
    {
        config.Normalize();
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(DefaultConfigPath, json);
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
        var appDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return path
            .Replace("%DESKTOP%", desktop, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDIR%", appDir, StringComparison.OrdinalIgnoreCase);
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

    public static void LaunchShortcutOrFile(string path) => StartNoWait(path);

    public static Process? LaunchExecutable(string exePath, string arguments, string workingDirectory)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Executable not found: {exePath}");

        return Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            UseShellExecute = true
        });
    }

    public static bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryValidatePreLaunchPath(string path, out string error)
    {
        error = "";
        var expanded = PathTokens.Expand(path);
        if (!File.Exists(expanded))
        {
            error = $"Path not found: {expanded}";
            return false;
        }

        if (System.IO.Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var resolved = ShortcutResolver.Resolve(expanded);
                if (!File.Exists(resolved.TargetPath))
                {
                    error = $"Shortcut target not found: {resolved.TargetPath}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        return true;
    }

    public static async Task TrySetWindowTitleAsync(Process? process, string title, int timeoutMs = 10000, int pollMs = 200)
    {
        if (process is null || string.IsNullOrWhiteSpace(title))
            return;

        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (process.HasExited)
                    return;

                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    SetWindowText(handle, title);
                    return;
                }

                await Task.Delay(pollMs);
            }
        }
        catch
        {
            // Best-effort only; ignore failures.
        }
    }

    public static IReadOnlyList<IntPtr> GetMainWindowHandlesByProcessName(string processName)
    {
        var results = new List<IntPtr>();
        if (string.IsNullOrWhiteSpace(processName))
            return results;

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    handle = TryGetMainWindowHandle(process.Id);
                if (handle != IntPtr.Zero)
                    results.Add(handle);
            }
        }
        catch
        {
            return results;
        }

        return results;
    }

    public static IntPtr TryGetMainWindowHandle(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, lParam) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId && IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsForegroundProcess(string processName)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == 0)
            return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            return string.Equals(proc.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void StartNoWait(string path)
    {
        var expanded = PathTokens.Expand(path);
        if (!File.Exists(expanded))
            throw new FileNotFoundException($"Path not found: {expanded}");

        if (System.IO.Path.GetExtension(expanded).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArg(expanded)}",
                UseShellExecute = true
            });
            return;
        }

        if (System.IO.Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ShortcutResolver.Resolve(expanded);
            if (System.IO.Path.GetExtension(resolved.TargetPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                var psArgs = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArg(resolved.TargetPath)}";
                if (!string.IsNullOrWhiteSpace(resolved.Arguments))
                    psArgs += " " + resolved.Arguments;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = psArgs,
                    UseShellExecute = true
                });
                return;
            }

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

        if (System.IO.Path.GetExtension(expanded).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArg(expanded)}",
                UseShellExecute = true
            });
            p?.WaitForExit();
            return;
        }

        if (System.IO.Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ShortcutResolver.Resolve(expanded);
            if (System.IO.Path.GetExtension(resolved.TargetPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                var psArgs = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArg(resolved.TargetPath)}";
                if (!string.IsNullOrWhiteSpace(resolved.Arguments))
                    psArgs += " " + resolved.Arguments;

                using var ps = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = psArgs,
                    UseShellExecute = true
                });
                ps?.WaitForExit();
                return;
            }

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

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}

public static class Defaults
{
    public const string DefaultInstallPath = @"C:\Program Files (x86)\Diablo II Resurrected";
}

public static class RegionOptions
{
    public static readonly IReadOnlyList<RegionOption> All = new List<RegionOption>
    {
        new("Americas", "us.actual.battle.net"),
        new("Europe", "eu.actual.battle.net"),
        new("Asia", "kr.actual.battle.net")
    };

    public static RegionOption? FindByName(string name)
    {
        foreach (var option in All)
        {
            if (string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase))
                return option;
        }
        return null;
    }
}

public sealed class RegionOption
{
    public string Name { get; }
    public string Address { get; }

    public RegionOption(string name, string address)
    {
        Name = name;
        Address = address;
    }

    public override string ToString() => Name;
}
