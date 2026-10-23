using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeLimitsTray;

internal sealed class TrayIconRenderer : IDisposable
{
    private static readonly Color Idle = Color.FromArgb(110, 110, 116);
    private (IntPtr Handle, Icon? Icon) previous;
    private (IntPtr Handle, Icon? Icon) current;

    public Icon Render(string text, Color background)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), 7);
            using var fill = new SolidBrush(background);
            g.FillPath(fill, path);

            var fontSize = text.Length >= 3 ? 13f : 17f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }

        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        // The tray still shows the current icon until the caller swaps it, so only the one before it is freed.
        Release(previous);
        previous = current;
        current = (handle, icon);
        return icon;
    }

    public Icon RenderIdle(string text) => Render(text, Idle);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Release((IntPtr Handle, Icon? Icon) entry)
    {
        entry.Icon?.Dispose();
        if (entry.Handle != IntPtr.Zero) Native.DestroyIcon(entry.Handle);
    }

    public void Dispose()
    {
        Release(previous);
        Release(current);
        previous = default;
        current = default;
    }
}
