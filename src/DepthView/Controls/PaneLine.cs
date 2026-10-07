using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DepthView.Controls;

/// <summary>
/// The profile line, drawn over a preview pane. Sits in the same cell as the pane's image and
/// works out where a Stretch="Uniform" image lands inside it, so the line stays on the same
/// pixels of the map however the window is sized. Never takes the pointer: the pane does.
/// </summary>
public sealed class PaneLine : Control
{
    private static readonly IPen Under = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3.5);
    private static readonly IPen Over = new Pen(new SolidColorBrush(Color.Parse("#7FE0FF")), 1.6);
    private static readonly IBrush End = new SolidColorBrush(Color.Parse("#7FE0FF"));

    private double _u0, _v0, _u1, _v1;
    private bool _has;

    /// <summary>Pixel size of the image under the line, for the letterbox arithmetic.</summary>
    public Size ImageSize { get; set; }

    public PaneLine() { IsHitTestVisible = false; }

    /// <summary>Ends as fractions of the image's width and height.</summary>
    public void Set(double u0, double v0, double u1, double v1)
    {
        (_u0, _v0, _u1, _v1, _has) = (u0, v0, u1, v1, true);
        InvalidateVisual();
    }

    public void Clear() { _has = false; InvalidateVisual(); }

    /// <summary>Where the image is drawn inside this control's bounds.</summary>
    public Rect ImageRect()
    {
        var b = Bounds;
        if (ImageSize.Width <= 0 || ImageSize.Height <= 0) return new Rect(b.Size);
        double s = System.Math.Min(b.Width / ImageSize.Width, b.Height / ImageSize.Height);
        double w = ImageSize.Width * s, h = ImageSize.Height * s;
        return new Rect((b.Width - w) / 2, (b.Height - h) / 2, w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        if (!_has) return;
        var r = ImageRect();
        var a = new Point(r.X + _u0 * r.Width, r.Y + _v0 * r.Height);
        var z = new Point(r.X + _u1 * r.Width, r.Y + _v1 * r.Height);
        ctx.DrawLine(Under, a, z);
        ctx.DrawLine(Over, a, z);
        ctx.DrawEllipse(End, null, a, 3, 3);
        ctx.DrawEllipse(End, null, z, 3, 3);
    }
}
