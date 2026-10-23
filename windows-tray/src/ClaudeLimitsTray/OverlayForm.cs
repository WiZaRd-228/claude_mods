using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeLimitsTray;

internal sealed class OverlayForm : Form
{
    private const int Pad = 10;
    private const int RowHeight = 22;
    private const int BarWidth = 120;
    private const int BarHeight = 6;

    private static readonly Color Background = Color.FromArgb(32, 32, 34);
    private static readonly Color Track = Color.FromArgb(64, 64, 68);
    private static readonly Color Muted = Color.FromArgb(160, 160, 166);

    private readonly Font labelFont = new("Segoe UI Semibold", 9f);
    private readonly Font textFont = new("Segoe UI", 9f);
    private IReadOnlyList<LimitWindow> windows = Array.Empty<LimitWindow>();
    private string? message;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Opacity = 0.94;
        DoubleBuffered = true;
        Size = new Size(320, Pad * 2 + RowHeight);
        Cursor = Cursors.SizeAll;
    }

    public event Action<Point>? Dragged;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WsExToolWindow | Native.WsExTopmost | Native.WsExNoActivate;
            return cp;
        }
    }

    public void SetContent(IReadOnlyList<LimitWindow> list, string? statusMessage)
    {
        windows = list;
        message = statusMessage;
        var rows = Math.Max(1, list.Count) + (statusMessage is not null && list.Count > 0 ? 1 : 0);
        Size = new Size(LogicalToDeviceUnits(330), LogicalToDeviceUnits(Pad * 2 + RowHeight * rows));
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var corner = Native.DwmwcpRound;
        Native.DwmSetWindowAttribute(Handle, Native.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WmNcLButtonDown, Native.HtCaption, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == Native.WmExitSizeMove) Dragged?.Invoke(Location);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var pad = LogicalToDeviceUnits(Pad);
        var row = LogicalToDeviceUnits(RowHeight);
        var barWidth = LogicalToDeviceUnits(BarWidth);
        var barHeight = LogicalToDeviceUnits(BarHeight);
        var y = pad;

        if (windows.Count == 0)
        {
            TextRenderer.DrawText(g, message ?? "Claude limits: waiting for data…", textFont, new Point(pad, y + 2), Muted);
            return;
        }

        foreach (var w in windows)
        {
            var percent = w.ShownPercent;
            var color = Usage.ColorOf(percent);
            var x = pad;

            TextRenderer.DrawText(g, w.Label, labelFont, new Point(x, y + 2), Color.White);
            x += LogicalToDeviceUnits(64);

            var barTop = y + (row - barHeight) / 2;
            using (var track = new SolidBrush(Track)) g.FillRectangle(track, x, barTop, barWidth, barHeight);
            var filled = (int)Math.Round(barWidth * Math.Clamp(percent, 0, 100) / 100);
            using (var fill = new SolidBrush(color)) g.FillRectangle(fill, x, barTop, filled, barHeight);
            x += barWidth + LogicalToDeviceUnits(8);

            TextRenderer.DrawText(g, $"{percent:0.#}%", labelFont, new Point(x, y + 2), color);
            x += LogicalToDeviceUnits(46);

            var left = w.IsReset ? "reset" : w.Left;
            if (left.Length > 0) TextRenderer.DrawText(g, left, textFont, new Point(x, y + 2), Muted);
            y += row;
        }

        if (message is not null) TextRenderer.DrawText(g, message, textFont, new Point(pad, y + 2), Muted);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            labelFont.Dispose();
            textFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
