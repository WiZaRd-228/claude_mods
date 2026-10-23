using System.Diagnostics;

namespace ClaudeLimitsTray;

internal sealed class ClaudeWindowTracker : IDisposable
{
    private readonly Native.WinEventProc callback;
    private readonly List<IntPtr> hooks = new();
    private readonly Dictionary<uint, bool> claudeProcesses = new();
    private readonly uint ownProcessId = (uint)Environment.ProcessId;

    public ClaudeWindowTracker()
    {
        callback = OnWinEvent;
        hooks.Add(Native.SetWinEventHook(Native.EventSystemForeground, Native.EventSystemForeground, IntPtr.Zero, callback, 0, 0, Native.WineventOutOfContext));
        hooks.Add(Native.SetWinEventHook(Native.EventSystemMinimizeStart, Native.EventSystemMinimizeEnd, IntPtr.Zero, callback, 0, 0, Native.WineventOutOfContext));
        hooks.Add(Native.SetWinEventHook(Native.EventObjectLocationChange, Native.EventObjectLocationChange, IntPtr.Zero, callback, 0, 0, Native.WineventOutOfContext));
        Refresh(Native.GetForegroundWindow());
    }

    public event Action? Changed;

    public IntPtr ClaudeWindow { get; private set; }

    public bool IsClaudeActive { get; private set; }

    public Rectangle? ClaudeBounds
    {
        get
        {
            if (ClaudeWindow == IntPtr.Zero || !Native.IsWindowVisible(ClaudeWindow) || Native.IsIconic(ClaudeWindow)) return null;
            if (!Native.GetWindowRect(ClaudeWindow, out var r)) return null;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == Native.EventObjectLocationChange)
        {
            if (hwnd == ClaudeWindow && idObject == Native.ObjidWindow) Changed?.Invoke();
            return;
        }

        Refresh(eventType == Native.EventSystemForeground ? hwnd : Native.GetForegroundWindow());
    }

    private void Refresh(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return;
        var root = Native.GetAncestor(foreground, Native.GaRoot);
        if (root == IntPtr.Zero) root = foreground;
        Native.GetWindowThreadProcessId(root, out var processId);

        if (processId == ownProcessId) return;

        if (IsClaude(processId))
        {
            ClaudeWindow = root;
            IsClaudeActive = true;
        }
        else
        {
            IsClaudeActive = false;
        }

        Changed?.Invoke();
    }

    private bool IsClaude(uint processId)
    {
        if (claudeProcesses.TryGetValue(processId, out var known)) return known;
        bool isClaude;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            isClaude = process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            isClaude = false;
        }

        if (claudeProcesses.Count > 512) claudeProcesses.Clear();
        claudeProcesses[processId] = isClaude;
        return isClaude;
    }

    public void Dispose()
    {
        foreach (var hook in hooks) Native.UnhookWinEvent(hook);
        hooks.Clear();
    }
}
