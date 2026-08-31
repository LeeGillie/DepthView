using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DepthView.Processing;

/// <summary>
/// A vector outline to import alongside a tuned map, so the laser can show you where the coin
/// is before it cuts anything.
///
/// This exists because of a limitation that cannot be worked around inside the depth map.
/// LightBurn frames a selection three ways - Bounds (a rectangle), Hull (a rubber band round
/// the shapes) and Contour (the exact perimeter) - but an <i>image</i> is a rectangle to all
/// three, whatever is drawn inside it. A round design in a square PNG frames as a square, and
/// no amount of white, transparency or cleverness in the file changes that, because the framer
/// never looks at the pixels.
///
/// A vector circle does not have that problem. Put one on a tool layer, which outputs nothing,
/// turn framing off for the image layer, and Hull or Contour framing walks the red dot round a
/// circle you can line up against the physical rim of the blank. That is the same alignment aid
/// MakeIt gives by placing artwork in a round frame, reached a different way.
///
/// Written as SVG with explicit millimetre dimensions rather than pixels, so the import lands
/// at true size without depending on anyone's DPI assumption.
/// </summary>
public static class AlignmentOutline
{
    /// <summary>
    /// Two concentric circles and a centre cross: the edge of the blank, the edge of the
    /// engraved area, and the middle. Each in its own colour, because LightBurn assigns
    /// imported vectors to layers by colour, and separating them is what lets you frame on
    /// one and ignore the rest.
    /// </summary>
    /// <param name="blankMm">Diameter of the blank - the circle you align to the rim.</param>
    /// <param name="engraveMm">Diameter of the engraved area, inside the rim. 0 to omit.</param>
    public static void Write(string path, double blankMm, double engraveMm, string? sourceName = null)
    {
        if (blankMm <= 0) throw new ArgumentOutOfRangeException(nameof(blankMm));

        var c = CultureInfo.InvariantCulture;
        double size = blankMm;
        double mid = size / 2;
        double cross = Math.Max(0.5, blankMm * 0.03);

        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        sb.AppendLine($"<!-- Alignment outline for {sourceName ?? "a tuned depth map"}, " +
                      $"written by DepthView {BuildInfo.Version}.");
        sb.AppendLine();
        sb.AppendLine("     Import this alongside the depth map. Both are sized in millimetres, and the");
        sb.AppendLine("     map's own physical size is stored in the PNG, so they land on top of each other.");
        sb.AppendLine();
        sb.AppendLine("     To use it as an alignment aid:");
        sb.AppendLine("       1. Select these circles and put them on a tool layer (T1). Tool layers are");
        sb.AppendLine("          never sent to the laser, so nothing here can be engraved by accident.");
        sb.AppendLine("       2. In the Cuts / Layers window, turn OFF Frame for the image layer. An image");
        sb.AppendLine("          frames as its rectangle no matter what is drawn in it, so leaving it on");
        sb.AppendLine("          gives you a square regardless of these circles.");
        sb.AppendLine("       3. Set framing to Hull or Contour rather than Bounds - Bounds is a rectangle");
        sb.AppendLine("          by definition.");
        sb.AppendLine("       4. Frame. The pointer traces the outer circle; line it up with the rim of the");
        sb.AppendLine("          blank, and the map is registered to the coin.");
        sb.AppendLine("-->");

        sb.AppendLine($@"<svg xmlns=""http://www.w3.org/2000/svg"" version=""1.1""");
        sb.AppendLine($@"     width=""{F(size)}mm"" height=""{F(size)}mm""");
        sb.AppendLine($@"     viewBox=""0 0 {F(size)} {F(size)}"">");

        sb.AppendLine($"  <!-- edge of the blank: align this to the rim -->");
        sb.AppendLine($@"  <circle cx=""{F(mid)}"" cy=""{F(mid)}"" r=""{F(blankMm / 2)}"" " +
                      @"fill=""none"" stroke=""#000000"" stroke-width=""0.1"" />");

        if (engraveMm > 0 && engraveMm < blankMm)
        {
            sb.AppendLine($"  <!-- edge of the engraved area: everything outside this is left untouched -->");
            sb.AppendLine($@"  <circle cx=""{F(mid)}"" cy=""{F(mid)}"" r=""{F(engraveMm / 2)}"" " +
                          @"fill=""none"" stroke=""#FF0000"" stroke-width=""0.1"" />");
        }

        sb.AppendLine($"  <!-- centre -->");
        sb.AppendLine($@"  <path d=""M {F(mid - cross)} {F(mid)} L {F(mid + cross)} {F(mid)} " +
                      $@"M {F(mid)} {F(mid - cross)} L {F(mid)} {F(mid + cross)}"" " +
                      @"fill=""none"" stroke=""#0000FF"" stroke-width=""0.1"" />");

        sb.AppendLine("</svg>");

        File.WriteAllText(path, sb.ToString());

        string F(double v) => v.ToString("0.####", c);
    }
}
