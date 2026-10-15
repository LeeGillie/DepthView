using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace DepthView.Views;

/// <summary>
/// Keeps a window no bigger than the screen it opens on. The ablation windows ask for a
/// generous size; on a laptop or a scaled display that would run off the bottom, and a
/// ScrollViewer inside a window that is taller than the screen never gets to scroll.
/// </summary>
public static class WindowFit
{
    public static void FitToScreen(this Window w)
    {
        w.Opened += (_, _) => Fit(w);
    }

    private static void Fit(Window w)
    {
        Screen? screen = null;
        try { screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary; } catch { }
        if (screen is null) return;
        double k = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        double maxW = area.Width / k - 16, maxH = area.Height / k - 16;
        if (maxW < 300 || maxH < 300) return;
        if (w.MinWidth > maxW) w.MinWidth = maxW;
        if (w.MinHeight > maxH) w.MinHeight = maxH;
        double width = Math.Min(double.IsNaN(w.Width) ? w.Bounds.Width : w.Width, maxW);
        double height = Math.Min(double.IsNaN(w.Height) ? w.Bounds.Height : w.Height, maxH);
        w.Width = width; w.Height = height;
        // Keep the whole window inside the working area.
        int px = (int)Math.Round(width * k), py = (int)Math.Round(height * k);
        int x = Math.Clamp(w.Position.X, area.X, Math.Max(area.X, area.Right - px));
        int y = Math.Clamp(w.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - py));
        if (x != w.Position.X || y != w.Position.Y) w.Position = new PixelPoint(x, y);
    }
}
