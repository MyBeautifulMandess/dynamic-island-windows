using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Services;

internal static class NativeMethods
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmHotKey = 0x0312;
    internal const int WmDpiChanged = 0x02E0;
    internal const int WmTray = 0x8001;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmLButtonDblClk = 0x0203;
    internal const int WmRButtonUp = 0x0205;
    internal const uint MonDefaultToNearest = 2;
    internal const int SwpNoSize = 0x0001;
    internal const int SwpNoMove = 0x0002;
    internal const int SwpNoZOrder = 0x0004;
    internal const int SwpNoActivate = 0x0010;
    internal const int SwShowNormal = 1;
    internal const int WsExToolWindow = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    internal delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AclLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll")] internal static extern int SHQueryUserNotificationState(out int state);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool LockWorkStation();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, uint dpiType, out uint dpiX, out uint dpiY);
    [DllImport("psapi.dll")] internal static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Просит Windows выгрузить страницы рабочего набора процесса, которые давно
    /// не использовались. Вызывается только когда пилюля скрыта.
    /// </summary>
    internal static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(GetCurrentProcess()); } catch { }
    }

    internal static MonitorInfo GetMonitor(Point point)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(point, MonDefaultToNearest), ref info);
        return info;
    }

    internal static bool IsFullScreenActive()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground)) return false;

        var name = new StringBuilder(64);
        if (GetClassName(foreground, name, name.Capacity) > 0)
        {
            var cls = name.ToString();
            if (cls is "Shell_TrayWnd" or "Progman" or "WorkerW" or "Shell_SecondaryTrayWnd") return false;
            if (cls.StartsWith("Windows.UI.Core.CoreWindow", StringComparison.Ordinal)) return false;
        }

        if (foreground == GetShellWindow()) return false;

        if (!GetVisibleBounds(foreground, out var bounds)) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(foreground, MonDefaultToNearest);
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var target = info.Monitor;
        var width = target.Right - target.Left;
        var height = target.Bottom - target.Top;
        if (width <= 0 || height <= 0) return false;

        var coveredWidth = bounds.Right - bounds.Left;
        var coveredHeight = bounds.Bottom - bounds.Top;
        if (coveredWidth <= 0 || coveredHeight <= 0) return false;

        var fullWidth = bounds.Left <= target.Left + 2 && bounds.Right >= target.Right - 2;
        var fullHeight = bounds.Top <= target.Top + 2 && bounds.Bottom >= target.Bottom - 2;
        if (!fullWidth || !fullHeight) return false;

        if (IsZoomed(foreground)) return false;

        try
        {
            if (SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4) return true;
        }
        catch { }

        return true;
    }

    private static bool GetVisibleBounds(IntPtr window, out Rect bounds)
    {
        const int extendedFrameBounds = 9;
        if (DwmGetWindowAttribute(window, extendedFrameBounds, out bounds, Marshal.SizeOf<Rect>()) == 0) return true;
        return GetWindowRect(window, out bounds);
    }

    internal static void AssertTopmost(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// Зажата ли левая кнопка мыши. Пока её держат, идёт перетаскивание: островок
    /// в этот момент нельзя сворачивать, иначе файл некуда будет отпустить.
    /// </summary>
    internal static bool IsLeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    internal static void SendPaste()
    {
        const ushort vkControl = 0x11;
        const ushort vkV = 0x56;
        const uint keyUp = 0x0002;
        var inputs = new[]
        {
            Key(vkControl, 0), Key(vkV, 0), Key(vkV, keyUp), Key(vkControl, keyUp)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort key, uint flags) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } }
    };
}
