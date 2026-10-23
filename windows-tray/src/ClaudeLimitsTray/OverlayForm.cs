using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeLimitsTray;

internal sealed class OverlayForm : Form
{
    public const int DefaultWidth = 560;
    private const int MinWidth = 300;
    private const int RowHeight = 28;
    private const int Pad = 12;
    private const int Gap = 24;
    private const int Spacing = 8;
    private const int MinBar = 24;
    private const int MaxBar = 160;
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
        Size = new Size(DefaultWidth, RowHeight);
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
        Invalidate();
    }

    public void SetWidth(int deviceWidth)
    {
        var size = new Size(Math.Max(LogicalToDeviceUnits(MinWidth), deviceWidth), LogicalToDeviceUnits(RowHeight));
        if (size == Size) return;
        Size = size;
        Invalidate();
    }

    public int DefaultDeviceWidth => LogicalToDeviceUnits(DefaultWidth);

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
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        var pad = LogicalToDeviceUnits(Pad);
        if (windows.Count == 0)
        {
            TextRenderer.DrawText(g, message ?? "Claude limits: waiting for data…", textFont,
                new Rectangle(pad, 0, Width - pad * 2, Height), Muted, flags | TextFormatFlags.EndEllipsis);
            return;
        }

        var segments = windows.Select(Segment.From).ToList();
        var spacing = LogicalToDeviceUnits(Spacing);
        var gap = LogicalToDeviceUnits(Gap);
        var slot = (Width - pad * 2 - gap * (segments.Count - 1)) / segments.Count;
        var textWidth = segments.Max(s => Measure(g, s) + spacing * 3);
        var barWidth = Math.Clamp(slot - textWidth, LogicalToDeviceUnits(MinBar), LogicalToDeviceUnits(MaxBar));

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var width = Measure(g, s) + spacing * 3 + barWidth;
            var slotLeft = pad + i * (slot + gap);
            // Two windows hug opposite edges; any further ones sit in their own slot.
            var x = segments.Count == 2 && i == 1 ? Width - pad - width : slotLeft;
            DrawSegment(g, s, x, barWidth, spacing, flags);
        }
    }

    private int Measure(Graphics g, Segment s)
    {
        var size = TextRenderer.MeasureText(g, s.Label, labelFont).Width
            + TextRenderer.MeasureText(g, s.Percent, labelFont).Width;
        return s.Left.Length > 0 ? size + TextRenderer.MeasureText(g, s.Left, textFont).Width : size;
    }

    private void DrawSegment(Graphics g, Segment s, int x, int barWidth, int spacing, TextFormatFlags flags)
    {
        var color = Usage.ColorOf(s.Value);
        x = DrawText(g, s.Label, labelFont, x, Color.White, flags) + spacing;

        var barHeight = LogicalToDeviceUnits(BarHeight);
        var barTop = (Height - barHeight) / 2;
        using (var track = new SolidBrush(Track)) g.FillRectangle(track, x, barTop, barWidth, barHeight);
        var filled = (int)Math.Round(barWidth * Math.Clamp(s.Value, 0, 100) / 100);
        using (var fill = new SolidBrush(color)) g.FillRectangle(fill, x, barTop, filled, barHeight);
        x += barWidth + spacing;

        x = DrawText(g, s.Percent, labelFont, x, color, flags) + spacing;
        if (s.Left.Length > 0) DrawText(g, s.Left, textFont, x, Muted, flags);
    }

    private int DrawText(Graphics g, string text, Font font, int x, Color color, TextFormatFlags flags)
    {
        var width = TextRenderer.MeasureText(g, text, font).Width;
        TextRenderer.DrawText(g, text, font, new Rectangle(x, 0, width + 2, Height), color, flags);
        return x + width;
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

    private sealed record Segment(string Label, double Value, string Percent, string Left)
    {
        public static Segment From(LimitWindow w) => new(
            w.Label,
            w.ShownPercent,
            $"{w.ShownPercent:0.#}%",
            w.IsReset ? "reset" : w.Left);
    }
}
