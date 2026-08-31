using System;
using System.Collections.Generic;

namespace DepthView.Processing;

/// <summary>
/// A 5x7 bitmap font, covering digits and the handful of letters the calibration pattern
/// needs to label itself.
///
/// Hand-rolled for the same reason the PNG decoder and encoder are: it has to work with no
/// display, no drawing library and no font files, in a headless CLI run on any of three
/// platforms. It also only has to render about twenty characters at a size measured in
/// millimetres of engraved metal, so a real text stack would be a large dependency doing a
/// tiny job.
///
/// An engraved coupon with twenty anonymous pockets is useless a week later. This is what
/// stops that.
/// </summary>
public static class TinyFont
{
    private const int W = 5, H = 7;

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { " ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### " },
        ['1'] = new[] { "  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### " },
        ['2'] = new[] { " ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####" },
        ['3'] = new[] { "#####", "   # ", "  #  ", "   # ", "    #", "#   #", " ### " },
        ['4'] = new[] { "   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # " },
        ['5'] = new[] { "#####", "#    ", "#### ", "    #", "    #", "#   #", " ### " },
        ['6'] = new[] { "  ## ", " #   ", "#    ", "#### ", "#   #", "#   #", " ### " },
        ['7'] = new[] { "#####", "    #", "   # ", "  #  ", " #   ", " #   ", " #   " },
        ['8'] = new[] { " ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### " },
        ['9'] = new[] { " ### ", "#   #", "#   #", " ####", "    #", "   # ", " ##  " },
        ['.'] = new[] { "     ", "     ", "     ", "     ", "     ", " ##  ", " ##  " },
        ['-'] = new[] { "     ", "     ", "     ", "#####", "     ", "     ", "     " },
        [' '] = new[] { "     ", "     ", "     ", "     ", "     ", "     ", "     " },
        ['A'] = new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" },
        ['D'] = new[] { "#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### " },
        ['E'] = new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" },
        ['H'] = new[] { "#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" },
        ['M'] = new[] { "#   #", "## ##", "# # #", "#   #", "#   #", "#   #", "#   #" },
        ['O'] = new[] { " ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " },
        ['P'] = new[] { "#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    " },
        ['R'] = new[] { "#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #" },
        ['S'] = new[] { " ####", "#    ", "#    ", " ### ", "    #", "    #", "#### " },
        ['T'] = new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " },
        ['U'] = new[] { "#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " },
    };

    /// <summary>Width in pixels that <see cref="Draw"/> will occupy, including inter-glyph gaps.</summary>
    public static int MeasureWidth(string text, int scale) =>
        text.Length == 0 ? 0 : (text.Length * (W + 1) - 1) * scale;

    public static int Height(int scale) => H * scale;

    /// <summary>
    /// Stamps text into a buffer. <paramref name="value"/> is written into set pixels and
    /// nothing is written elsewhere, so labels can be laid over an existing field without
    /// erasing a rectangle around themselves.
    /// </summary>
    public static void Draw(ushort[] buffer, int bufWidth, int bufHeight,
                            string text, int x, int y, int scale, ushort value)
    {
        int cursor = x;
        foreach (char raw in text.ToUpperInvariant())
        {
            if (!Glyphs.TryGetValue(raw, out var glyph)) glyph = Glyphs[' '];

            for (int gy = 0; gy < H; gy++)
                for (int gx = 0; gx < W; gx++)
                {
                    if (glyph[gy][gx] == ' ') continue;
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = cursor + gx * scale + sx;
                            int py = y + gy * scale + sy;
                            if (px < 0 || py < 0 || px >= bufWidth || py >= bufHeight) continue;
                            buffer[(long)py * bufWidth + px] = value;
                        }
                }
            cursor += (W + 1) * scale;
        }
    }

    /// <summary>Draws text centred on a horizontal position.</summary>
    public static void DrawCentred(ushort[] buffer, int bufWidth, int bufHeight,
                                   string text, int centreX, int y, int scale, ushort value) =>
        Draw(buffer, bufWidth, bufHeight, text, centreX - MeasureWidth(text, scale) / 2, y, scale, value);
}
