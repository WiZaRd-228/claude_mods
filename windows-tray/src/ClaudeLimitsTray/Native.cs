using System.Runtime.InteropServices;

namespace ClaudeLimitsTray;

internal static class Native
{
    public const uint EventSystemForeground = 0x0003;
    public const uint EventSystemMinimizeStart = 0x0016;
    public const uint EventSystemMinimizeEnd = 0x0017;
    public const uint EventObjectLocationChange = 0x800B;
    public const uint WineventOutOfContext = 0x0000;
    public const int ObjidWindow = 0;
    public const int WmNcLButtonDown = 0x00A1;
    public const int WmExitSizeMove = 0x0232;
    public const int HtCaption = 2;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExTopmost = 0x00000008;
    public const int WsExNoActivate = 0x08000000;
    public const uint GaRoot = 2;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
