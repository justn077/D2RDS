using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MultiboxLauncher;

// Captures global input and mirrors it to selected D2R windows.
public sealed class BroadcastManager : IDisposable
{
    private const int HotkeyToggleBroadcast = 0xB001;
    private const int HotkeyToggleMode = 0xB002;
    private const int HotkeyToggleWindow = 0xB003;

    private readonly Func<BroadcastSettings> _settingsProvider;
    private readonly Func<IReadOnlyList<IntPtr>> _targetsProvider;
    private readonly Func<bool> _isForegroundTarget;
    private HwndSource? _source;

    private static readonly LowLevelKeyboardProc KeyboardProcDelegate = KeyboardHookProc;
    private static readonly LowLevelMouseProc MouseProcDelegate = MouseHookProc;
    private static IntPtr _keyboardHook = IntPtr.Zero;
    private static IntPtr _mouseHook = IntPtr.Zero;
    private static BroadcastManager? _instance;
    private bool _hooksEnabled;

    public event Action? ToggleBroadcastRequested;
    public event Action? ToggleModeRequested;
    public event Action? ToggleWindowRequested;

    public BroadcastManager(Func<BroadcastSettings> settingsProvider, Func<IReadOnlyList<IntPtr>> targetsProvider, Func<bool> isForegroundTarget)
    {
        _settingsProvider = settingsProvider;
        _targetsProvider = targetsProvider;
        _isForegroundTarget = isForegroundTarget;
    }

    public void Initialize(Window window)
    {
        _instance = this;
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        UpdateHotkeys();
        UpdateBroadcastState(_settingsProvider());
    }

    public void UpdateHotkeys()
    {
        if (_source is null)
            return;

        UnregisterHotKey(_source.Handle, HotkeyToggleBroadcast);
        UnregisterHotKey(_source.Handle, HotkeyToggleMode);
        UnregisterHotKey(_source.Handle, HotkeyToggleWindow);

        var settings = _settingsProvider();
        RegisterHotkey(_source.Handle, HotkeyToggleBroadcast, settings.ToggleBroadcastHotkey);
        RegisterHotkey(_source.Handle, HotkeyToggleMode, settings.ToggleModeHotkey);
        RegisterHotkey(_source.Handle, HotkeyToggleWindow, settings.ToggleWindowHotkey);
    }

    public void UpdateBroadcastState(BroadcastSettings settings)
    {
        var shouldEnable = settings.Enabled && (settings.Keyboard || settings.Mouse);
        if (shouldEnable && !_hooksEnabled)
        {
            EnsureHooks();
            _hooksEnabled = true;
        }
        else if (!shouldEnable && _hooksEnabled)
        {
            RemoveHooks();
            _hooksEnabled = false;
        }
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyToggleBroadcast);
            UnregisterHotKey(_source.Handle, HotkeyToggleMode);
            _source.RemoveHook(WndProc);
        }

        _source = null;
        RemoveHooks();
        _hooksEnabled = false;
        _instance = null;
    }

    private static void EnsureHooks()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, KeyboardProcDelegate, GetModuleHandle(curModule.ModuleName), 0);
        }

        if (_mouseHook == IntPtr.Zero)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, MouseProcDelegate, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private static void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyToggleBroadcast)
            {
                ToggleBroadcastRequested?.Invoke();
                handled = true;
            }
            else if (id == HotkeyToggleMode)
            {
                ToggleModeRequested?.Invoke();
                handled = true;
            }
            else if (id == HotkeyToggleWindow)
            {
                ToggleWindowRequested?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    // Low-level keyboard hook: mirrors key messages to target windows when enabled.
    private static IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _instance is not null)
        {
            var settings = _instance._settingsProvider();
            if (settings.Enabled && settings.Keyboard && _instance._isForegroundTarget())
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vkCode = info.vkCode;
                var scanCode = info.scanCode;
                var flags = info.flags;
                var time = info.time;
                var extraInfo = info.dwExtraInfo;

                var message = wParam.ToInt32();
                if (message == WM_KEYDOWN || message == WM_KEYUP || message == WM_SYSKEYDOWN || message == WM_SYSKEYUP)
                {
                    if (ShouldSuppressHotkey(settings, vkCode))
                        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

                    var targets = _instance._targetsProvider();
                    var foreground = GetForegroundWindow();
                    foreach (var target in targets)
                    {
                        if (target == IntPtr.Zero || target == foreground)
                            continue;

                        var lParamValue = BuildKeyLParam(scanCode, flags, message == WM_KEYUP || message == WM_SYSKEYUP);
                        PostMessage(target, (uint)message, (IntPtr)vkCode, lParamValue);
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // Low-level mouse hook: mirrors mouse messages to target windows when enabled.
    private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _instance is not null)
        {
            var settings = _instance._settingsProvider();
            if (settings.Enabled && settings.Mouse && _instance._isForegroundTarget())
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var message = (uint)wParam.ToInt32();
                if (IsMouseMessage(message))
                {
                    var targets = _instance._targetsProvider();
                    var foreground = GetForegroundWindow();
                    foreach (var target in targets)
                    {
                        if (target == IntPtr.Zero || target == foreground)
                            continue;

                        var pt = info.pt;
                        var clientPoint = pt;
                        if (ScreenToClient(target, ref clientPoint))
                        {
                            var lParamValue = (IntPtr)((clientPoint.Y << 16) | (clientPoint.X & 0xFFFF));
                            var wParamValue = GetMouseWParam(message, info.mouseData);
                            PostMessage(target, message, wParamValue, lParamValue);
                        }
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // Hotkey registration uses a simple string format like "Ctrl+Alt+B".
    private static bool RegisterHotkey(IntPtr handle, int id, string hotkey)
    {
        if (!TryParseHotkey(hotkey, out var modifiers, out var key))
            return false;

        return RegisterHotKey(handle, id, modifiers, key);
    }

    private static bool TryParseHotkey(string hotkey, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_CONTROL;
                continue;
            }
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_ALT;
                continue;
            }
            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_SHIFT;
                continue;
            }
            if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_WIN;
                continue;
            }

            if (!Enum.TryParse<Key>(part, true, out var keyEnum))
                return false;

            key = (uint)KeyInterop.VirtualKeyFromKey(keyEnum);
        }

        return key != 0;
    }

    // Build a WM_KEY* lParam based on scan code and flags.
    private static IntPtr BuildKeyLParam(uint scanCode, uint flags, bool keyUp)
    {
        var lParam = 0;
        lParam |= 1; // repeat count
        lParam |= (int)(scanCode << 16);
        if ((flags & 0x01) != 0)
            lParam |= 1 << 24; // extended
        if (keyUp)
            lParam |= 1 << 31;
        return (IntPtr)lParam;
    }

    private static bool IsMouseMessage(uint message)
    {
        return message == WM_MOUSEMOVE ||
               message == WM_LBUTTONDOWN ||
               message == WM_LBUTTONUP ||
               message == WM_RBUTTONDOWN ||
               message == WM_RBUTTONUP ||
               message == WM_MBUTTONDOWN ||
               message == WM_MBUTTONUP ||
               message == WM_MOUSEWHEEL ||
               message == WM_MOUSEHWHEEL;
    }

    private static IntPtr GetMouseWParam(uint message, uint mouseData)
    {
        var keyState = GetMouseKeyState();
        if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL)
        {
            var delta = (short)(mouseData >> 16);
            return (IntPtr)((delta << 16) | (keyState & 0xFFFF));
        }
        return (IntPtr)keyState;
    }

    private static int GetMouseKeyState()
    {
        var state = 0;
        if (IsKeyDown(VK_LBUTTON)) state |= MK_LBUTTON;
        if (IsKeyDown(VK_RBUTTON)) state |= MK_RBUTTON;
        if (IsKeyDown(VK_MBUTTON)) state |= MK_MBUTTON;
        if (IsKeyDown(VK_XBUTTON1)) state |= MK_XBUTTON1;
        if (IsKeyDown(VK_XBUTTON2)) state |= MK_XBUTTON2;
        if (IsKeyDown(VK_SHIFT)) state |= MK_SHIFT;
        if (IsKeyDown(VK_CONTROL)) state |= MK_CONTROL;
        return state;
    }

    private static bool ShouldSuppressHotkey(BroadcastSettings settings, uint vkCode)
    {
        if (IsHotkeyChord(settings.ToggleBroadcastHotkey, vkCode))
            return true;
        if (IsHotkeyChord(settings.ToggleModeHotkey, vkCode))
            return true;
        if (IsHotkeyChord(settings.ToggleWindowHotkey, vkCode))
            return true;
        return false;
    }

    private static bool IsHotkeyChord(string hotkey, uint vkCode)
    {
        if (!TryParseHotkey(hotkey, out var modifiers, out var key))
            return false;
        if (key == 0 || vkCode != key)
            return false;
        return AreHotkeyModifiersDown(modifiers);
    }

    private static bool AreHotkeyModifiersDown(uint modifiers)
    {
        if ((modifiers & MOD_CONTROL) != 0 && !IsKeyDown(VK_CONTROL))
            return false;
        if ((modifiers & MOD_ALT) != 0 && !IsKeyDown(VK_MENU))
            return false;
        if ((modifiers & MOD_SHIFT) != 0 && !IsKeyDown(VK_SHIFT))
            return false;
        if ((modifiers & MOD_WIN) != 0 && !(IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)))
            return false;
        return true;
    }

    private static bool IsKeyDown(int vk)
    {
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    // Fallback: resolve windows by exact title (used when process handle isn't available).
    public static IReadOnlyList<IntPtr> FindWindowsByTitleExact(string title)
    {
        var results = new List<IntPtr>();
        if (string.IsNullOrWhiteSpace(title))
            return results;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var text = GetWindowText(hwnd);
            if (string.Equals(text, title, StringComparison.OrdinalIgnoreCase))
                results.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static string GetWindowText(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private const int WM_HOTKEY = 0x0312;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int VK_XBUTTON1 = 0x05;
    private const int VK_XBUTTON2 = 0x06;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int MK_LBUTTON = 0x0001;
    private const int MK_RBUTTON = 0x0002;
    private const int MK_SHIFT = 0x0004;
    private const int MK_CONTROL = 0x0008;
    private const int MK_MBUTTON = 0x0010;
    private const int MK_XBUTTON1 = 0x0020;
    private const int MK_XBUTTON2 = 0x0040;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
