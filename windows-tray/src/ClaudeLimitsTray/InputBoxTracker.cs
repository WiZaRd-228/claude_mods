using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ClaudeLimitsTray;

internal sealed class InputBoxTracker : IDisposable
{
    private const int PollMs = 400;
    private const double MaxHeightShare = 0.6;

    private readonly SynchronizationContext ui;
    private readonly CancellationTokenSource stop = new();
    private IntPtr claudeWindow;
    private volatile bool isActive;
    private volatile bool isEnabled = true;
    private volatile Margins? margins;

    public InputBoxTracker()
    {
        ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _ = Task.Run(LoopAsync);
    }

    public event Action? Changed;

    public bool IsEnabled
    {
        get => isEnabled;
        set => isEnabled = value;
    }

    public void Update(IntPtr window, bool active)
    {
        Interlocked.Exchange(ref claudeWindow, window);
        isActive = active;
    }

    public Rectangle? BoundsIn(Rectangle window)
    {
        if (margins is not { } m) return null;
        var left = window.Left + m.Left;
        var right = window.Right - m.Right;
        var bottom = window.Bottom - m.Bottom;
        if (right - left < 100 || bottom - m.Height < window.Top) return null;
        return Rectangle.FromLTRB(left, bottom - m.Height, right, bottom);
    }

    private async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (isEnabled && isActive) Probe();
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
            {
            }

            try
            {
                await Task.Delay(PollMs, stop.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private void Probe()
    {
        var window = Interlocked.CompareExchange(ref claudeWindow, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero || !Native.GetWindowRect(window, out var w)) return;
        Native.GetWindowThreadProcessId(window, out var claudeProcess);

        var focused = AutomationElement.FocusedElement;
        if (focused is null) return;
        var info = focused.Current;
        if (info.ProcessId != (int)claudeProcess || !info.IsKeyboardFocusable) return;

        var isText = info.ControlType == ControlType.Edit
            || info.ControlType == ControlType.Document
            || focused.TryGetCurrentPattern(TextPattern.Pattern, out _);
        if (!isText) return;

        var r = info.BoundingRectangle;
        if (r.IsEmpty || double.IsInfinity(r.Width)) return;
        var windowHeight = w.Bottom - w.Top;
        // A focused page body also reports as a document; only a box in the lower part of the window is the message box.
        if (r.Height > windowHeight * MaxHeightShare || r.Bottom < w.Top + windowHeight / 2.0) return;

        var next = new Margins(
            (int)Math.Round(r.Left) - w.Left,
            w.Right - (int)Math.Round(r.Right),
            w.Bottom - (int)Math.Round(r.Bottom),
            (int)Math.Round(r.Height));
        if (next == margins) return;
        margins = next;
        ui.Post(_ => Changed?.Invoke(), null);
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }

    private sealed record Margins(int Left, int Right, int Bottom, int Height);
}
