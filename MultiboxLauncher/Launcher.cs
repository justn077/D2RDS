using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace MultiboxLauncher;

// Config models and process helpers for launching and logging.
public sealed class LauncherConfig
{
    public PreLaunchConfig PreLaunch { get; set; } = new();
    public string InstallPath { get; set; } = Defaults.DefaultInstallPath;
    public string Region { get; set; } = "";
    public List<AccountProfile> Accounts { get; set; } = new();
    public List<LaunchProfile> Profiles { get; set; } = new();
    public bool LockOrder { get; set; } = false;
    public BroadcastSettings Broadcast { get; set; } = new();
    public WindowLayoutSettings WindowLayout { get; set; } = new();
    public string UpdateToken { get; set; } = "";
    public bool MinimizeToTaskbar { get; set; } = false;

    public void Normalize()
    {
        PreLaunch ??= new PreLaunchConfig();
        InstallPath = string.IsNullOrWhiteSpace(InstallPath) ? Defaults.DefaultInstallPath : InstallPath;
        Region ??= "";
        Accounts ??= new List<AccountProfile>();
        Profiles ??= new List<LaunchProfile>();
        Broadcast ??= new BroadcastSettings();
        WindowLayout ??= new WindowLayoutSettings();
        WindowLayout.GridMonitorDevice ??= "";
        if (!Broadcast.DefaultsApplied || !Broadcast.Keyboard || !Broadcast.Mouse)
        {
            // Ensure keyboard/mouse default on for existing configs.
            Broadcast.Keyboard = true;
            Broadcast.Mouse = true;
            Broadcast.DefaultsApplied = true;
        }
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
    public bool ClassicMode { get; set; } = false;
}

public sealed class BroadcastSettings
{
    public bool Enabled { get; set; } = false;
    public bool BroadcastAll { get; set; } = true;
    public bool Keyboard { get; set; } = true;
    public bool Mouse { get; set; } = true;
    public string ToggleBroadcastHotkey { get; set; } = "Ctrl+Alt+B";
    public string ToggleModeHotkey { get; set; } = "Ctrl+Alt+M";
    public bool DefaultsApplied { get; set; } = false;
}

public sealed class WindowLayoutSettings
{
    public bool Enabled { get; set; } = false;
    public string GridMonitorDevice { get; set; } = "";
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
    private const int SwRestore = 9;
    private const string HandleExeName = "handle64.exe";
    private const string D2RKillaScriptName = "D2RKilla.ps1";
    private const uint MonitorDefaultToNearest = 2;
    private const int GwlStyle = -16;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsMaximizeBox = 0x00010000;
    public const int DefaultMonitorCheckIntervalMs = 750;
    public const int DefaultMoveDebounceMs = 500;
    public const int DefaultPreLaunchTimeoutMs = 20000;

    public static string DefaultHandlePath => System.IO.Path.Combine(AppContext.BaseDirectory, HandleExeName);

    public static Task RunPreLaunchAsync(string path)
    {
        return Task.Run(() => StartAndWait(path, DefaultPreLaunchTimeoutMs));
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

    public static void TryActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            var name = current.ProcessName;
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (process.Id == current.Id)
                    continue;

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    handle = TryGetMainWindowHandle(process.Id);
                if (handle == IntPtr.Zero)
                    continue;

                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return;
            }
        }
        catch
        {
            // Best-effort only; ignore failures.
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

    public static string? TryResolvePreLaunchScript(string configuredPath)
    {
        var expanded = PathTokens.Expand(configuredPath);
        if (!File.Exists(expanded))
            return null;

        if (System.IO.Path.GetExtension(expanded).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            return expanded;

        if (System.IO.Path.GetExtension(expanded).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var resolved = ShortcutResolver.Resolve(expanded);
                if (System.IO.Path.GetExtension(resolved.TargetPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                    return resolved.TargetPath;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static bool IsD2RKillaScript(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return false;
        return string.Equals(System.IO.Path.GetFileName(scriptPath), D2RKillaScriptName, StringComparison.OrdinalIgnoreCase);
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

    public static async Task<IntPtr> WaitForMainWindowHandleAsync(Process? process, int timeoutMs = 10000, int pollMs = 200)
    {
        if (process is null)
            return IntPtr.Zero;

        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (process.HasExited)
                    return IntPtr.Zero;

                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                    return handle;

                await Task.Delay(pollMs);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
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

    public static IntPtr GetMonitorHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        return MonitorFromWindow(hwnd, MonitorDefaultToNearest);
    }

    public static bool TryGetWindowRect(IntPtr hwnd, out Rect rect)
    {
        rect = new Rect();
        if (hwnd == IntPtr.Zero)
            return false;
        return GetWindowRect(hwnd, out rect);
    }

    public static bool TryGetMonitorWorkArea(IntPtr hwnd, out Rect workArea)
    {
        workArea = new Rect();
        var monitor = GetMonitorHandle(hwnd);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        workArea = info.rcWork;
        return true;
    }

    public static void FitWindowToMonitorWorkArea(IntPtr hwnd)
    {
        if (!TryGetMonitorWorkArea(hwnd, out var workArea))
            return;

        var width = workArea.Right - workArea.Left;
        var height = workArea.Bottom - workArea.Top;
        if (width <= 0 || height <= 0)
            return;

        SetWindowPos(hwnd, IntPtr.Zero, workArea.Left, workArea.Top, width, height, SwpNoZOrder | SwpNoActivate);
    }

    public static void FitWindowToPrimaryWorkArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var workArea = Screen.PrimaryScreen?.WorkingArea;
        if (workArea is null)
            return;

        var width = workArea.Value.Width;
        var height = workArea.Value.Height;
        if (width <= 0 || height <= 0)
            return;

        SetWindowPos(hwnd, IntPtr.Zero, workArea.Value.Left, workArea.Value.Top, width, height, SwpNoZOrder | SwpNoActivate);
    }

    public static void MoveWindowToRect(IntPtr hwnd, Rect rect, bool noActivate)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return;

        var flags = SwpNoZOrder | (noActivate ? SwpNoActivate : 0);
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, width, height, flags);
    }

    public static bool IsWindowProcessName(IntPtr hwnd, string processName)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(processName))
            return false;

        GetWindowThreadProcessId(hwnd, out var pid);
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

    public static void TryApplyBorderlessStyle(IntPtr hwnd, bool allowResize)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var style = GetWindowLongPtr(hwnd, GwlStyle);
        if (style == IntPtr.Zero)
            return;

        var styleValue = unchecked((uint)style.ToInt64());
        styleValue &= ~WsCaption;
        if (allowResize)
        {
            styleValue |= WsThickFrame;
            styleValue |= WsMaximizeBox;
        }
        else
        {
            styleValue &= ~WsThickFrame;
            styleValue &= ~WsMaximizeBox;
        }

        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(unchecked((int)styleValue)));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
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

    public static IntPtr GetForegroundWindowHandle()
    {
        return GetForegroundWindow();
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "";

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";

        var builder = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static int GetWindowProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
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

    private static void StartAndWait(string path, int timeoutMs)
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
            WaitForExitOrTimeout(p, timeoutMs);
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
                WaitForExitOrTimeout(ps, timeoutMs);
                return;
            }

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = resolved.TargetPath,
                Arguments = resolved.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(resolved.WorkingDirectory) ? null : resolved.WorkingDirectory,
                UseShellExecute = false
            });
            WaitForExitOrTimeout(p, timeoutMs);
            return;
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = expanded,
            UseShellExecute = true
        });
        WaitForExitOrTimeout(proc, timeoutMs);
    }

    private static void WaitForExitOrTimeout(Process? process, int timeoutMs)
    {
        if (process is null)
            return;

        if (timeoutMs <= 0)
        {
            process.WaitForExit();
            return;
        }

        if (!process.WaitForExit(timeoutMs))
        {
            Log.Info($"Pre-launch timed out after {timeoutMs}ms; continuing.");
            try
            {
                process.Kill();
            }
            catch
            {
                // Best-effort only; ignore failures.
            }
        }
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
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
