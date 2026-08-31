using System;
using System.Linq;
using System.Text;

namespace DepthView.Analysis;

public static class Fmt
{
    public static long RawBytes(AnalysisResult r)
        => (long)r.Width * r.Height * r.Channels * (r.IsFloat ? 32 : r.BitDepth) / 8;

    public static string Bytes(long b)
        => b < 1024 ? $"{b} B"
         : b < 1024 * 1024 ? $"{b / 1024.0:F1} KB ({b:N0} bytes)"
         : $"{b / 1048576.0:F2} MB ({b:N0} bytes)";

    public static string Pct(long part, long whole)
        => whole <= 0 ? "0%" : $"{100.0 * part / whole:F3}%";

    public static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n] + "...";

    public static int Order(Severity s) => s switch
    {
        Severity.Alert => 0, Severity.Warn => 1, Severity.Info => 2, _ => 3
    };

    public static string Wrap(string text, int width, string indent = "  ")
    {
        var sb = new StringBuilder();
        var line = new StringBuilder(indent);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width + indent.Length)
            {
                sb.AppendLine(line.ToString());
                line.Clear().Append(indent);
            }
            if (line.Length > indent.Length) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > indent.Length) sb.Append(line);
        return sb.ToString();
    }
}

/// <summary>Plain-text rendering of an analysis, shared by the GUI and the CLI report mode.</summary>
public static class ReportWriter
{
    public static string Build(AnalysisResult r)
    {
        var m = r.Meta;
        var sb = new StringBuilder();

        sb.AppendLine("DepthView analysis report");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 74));
        sb.AppendLine();
        sb.AppendLine($"VERDICT: {r.Verdict}");
        sb.AppendLine(Fmt.Wrap(r.VerdictDetail, 74));
        sb.AppendLine();

        sb.AppendLine("FILE");
        sb.AppendLine($"  Name              {m.FileName}");
        if (!string.IsNullOrEmpty(m.FilePath)) sb.AppendLine($"  Path              {m.FilePath}");
        sb.AppendLine($"  Size on disk      {Fmt.Bytes(m.FileBytes)}");
        sb.AppendLine($"  Raw sample size   {Fmt.Bytes(Fmt.RawBytes(r))}");
        sb.AppendLine($"  Loaded via        {m.SourceNote}");
        sb.AppendLine();

        sb.AppendLine("CONTAINER (what the file declares)");
        sb.AppendLine($"  Format            {m.Format}");
        sb.AppendLine($"  Colour model      {m.ColorModel}");
        sb.AppendLine($"  Declared depth    {m.DeclaredBitDepth} bits/sample, {m.DeclaredChannels} channel(s)");
        sb.AppendLine($"  Bit exact decode  {(m.BitDepthIsExact ? "yes" : "NO - samples were normalised")}");
        sb.AppendLine($"  Alpha             {(m.HasAlpha ? "present" : "none")}");
        if (m.IsPalette) sb.AppendLine($"  Palette entries   {m.PaletteSize}");
        sb.AppendLine($"  Compression       {m.CompressionMethod}");
        sb.AppendLine($"  Interlacing       {m.InterlaceMethod}");
        if (m.Gamma is { } g) sb.AppendLine($"  Gamma             {g:F5}");
        if (m.SignificantBits is { Length: > 0 }) sb.AppendLine($"  sBIT              {string.Join(", ", m.SignificantBits)}");
        sb.AppendLine($"  ICC profile       {(m.HasIccProfile ? m.IccProfileName ?? "embedded" : "none")}");
        sb.AppendLine();

        sb.AppendLine("CONTENT (what the pixels contain)");
        sb.AppendLine($"  Dimensions        {r.DimensionText}");
        sb.AppendLine($"  Channels          {r.Channels}");
        sb.AppendLine(r.IsFloat
            ? $"  Sample range      {r.FloatMin:G6} .. {r.FloatMax:G6} (float32)"
            : $"  Sample range      0 .. {r.MaxValue:N0}");
        sb.AppendLine($"  Unique greys      {r.UniqueGreyLevels:N0}");
        sb.AppendLine($"  Grey pixels       {r.GreyPixels:N0} ({Fmt.Pct(r.GreyPixels, r.PixelCount)})");
        sb.AppendLine($"  Non-grey pixels   {r.NonGreyPixels:N0} ({Fmt.Pct(r.NonGreyPixels, r.PixelCount)})");
        sb.AppendLine($"  Non-grey colours  {(r.NonGreyColorsCapped ? "over " : "")}{r.UniqueNonGreyColors:N0}");
        if (r.UniqueColorsTotal > 0)
            sb.AppendLine($"  Unique colours    {(r.TotalColorsCapped ? "over " : "")}{r.UniqueColorsTotal:N0}");
        if (r.HistR is not null)
            sb.AppendLine($"  Unique per chan   R {r.UniqueR:N0}  G {r.UniqueG:N0}  B {r.UniqueB:N0}");
        sb.AppendLine();

        sb.AppendLine("LEVEL STRUCTURE");
        sb.AppendLine($"  Occupied range    {r.MinLevel:N0} .. {r.MaxLevel:N0}");
        sb.AppendLine($"  Range use         {r.RangeUtilisation * 100:F2}%");
        sb.AppendLine($"  Level occupancy   {r.Occupancy * 100:F4}%");
        sb.AppendLine($"  Effective bits    {r.EffectiveBits}");
        sb.AppendLine($"  Level step (GCD)  {r.LevelStep:N0}");
        sb.AppendLine($"  Uniform ladder    {(r.UniformLadder ? "YES" : "no")}");
        sb.AppendLine($"  Gaps              {r.GapCount:N0}, largest {r.LargestGap:N0}");
        sb.AppendLine($"  Mean / median     {r.Mean:F2} / {r.Median:F0}");
        sb.AppendLine($"  Std deviation     {r.StdDev:F2}");
        sb.AppendLine($"  P1 / P99          {r.P1:N0} / {r.P99:N0}");
        if (r.HasAlphaChannel)
            sb.AppendLine($"  Alpha             {(r.AlphaConstant ? $"constant {r.AlphaMin:N0}" : $"{r.AlphaMin:N0} .. {r.AlphaMax:N0}")}");
        sb.AppendLine();

        // What the level structure above is actually worth once a slicer has had it.
        if (r.UsableSlices > 0)
        {
            sb.AppendLine("SLICING (through a 256-level slicer, e.g. LightBurn 3D Slice)");
            sb.AppendLine($"  Usable slices     {r.UsableSlices:N0}");
            sb.AppendLine($"  Suggested passes  {r.UsableSlices:N0}   (more than this duplicates layers)");
            if (r.SlicesLostToHeadroom > 0)
                sb.AppendLine($"  Recoverable       {r.SlicesLostToHeadroom:N0} more by remapping to full range " +
                              $"({r.UsableSlicesRemapped:N0} total)");
            else
                sb.AppendLine("  Recoverable       none - the range is already well used");

            sb.AppendLine("  At a given pass count:");
            foreach (int passes in new[] { 64, 128, 256, 384 })
            {
                var (distinct, wasted) = r.SlicesAt(passes);
                sb.AppendLine($"    {passes,4} passes      {distinct,4:N0} distinct" +
                              (wasted > 0 ? $", {wasted:N0} repeating an existing slice" : ""));
            }
            sb.AppendLine();
        }

        sb.AppendLine("ENDPOINTS AND CLIPPING");
        sb.AppendLine(r.PureWhitePixels > 0
            ? $"  Pure white        {r.PureWhitePixels:N0} px ({Fmt.Pct(r.PureWhitePixels, r.GreyPixels)}) on level {r.MaxValue:N0}"
            : $"  Pure white        none - lightest present is {r.LightestLevel:N0} " +
              $"({r.LightestCount:N0} px, {Fmt.Pct(r.LightestCount, r.GreyPixels)})");
        sb.AppendLine(r.PureBlackPixels > 0
            ? $"  Pure black        {r.PureBlackPixels:N0} px ({Fmt.Pct(r.PureBlackPixels, r.GreyPixels)}) on level 0"
            : $"  Pure black        none - darkest present is {r.DarkestLevel:N0} " +
              $"({r.DarkestCount:N0} px, {Fmt.Pct(r.DarkestCount, r.GreyPixels)})");
        sb.AppendLine($"  Lightest level    {r.LightestLevel:N0}  ({r.LightestCount:N0} px)");
        sb.AppendLine($"  Darkest level     {r.DarkestLevel:N0}  ({r.DarkestCount:N0} px)");
        sb.AppendLine($"  Unused headroom   {r.HeadroomTop:N0} above, {r.HeadroomBottom:N0} below");
        sb.AppendLine();

        if (r.TopLevels.Count > 0)
        {
            sb.AppendLine("MOST COMMON LEVELS");
            foreach (var (level, count) in r.TopLevels)
                sb.AppendLine($"  {level,8:N0}  {count,14:N0} px  {Fmt.Pct(count, r.GreyPixels),9}");
            sb.AppendLine();
        }

        if (r.Findings.Count > 0)
        {
            sb.AppendLine("FINDINGS");
            foreach (var f in r.Findings.OrderBy(x => Fmt.Order(x.Severity)))
            {
                sb.AppendLine($"  [{f.Severity.ToString().ToUpperInvariant()}] {f.Title}");
                sb.AppendLine(Fmt.Wrap(f.Detail, 70, "      "));
            }
            sb.AppendLine();
        }

        if (m.Text.Count > 0)
        {
            sb.AppendLine("EMBEDDED METADATA");
            foreach (var kv in m.Text.Take(40))
                sb.AppendLine($"  {kv.Key}: {Fmt.Truncate(kv.Value, 200)}");
            sb.AppendLine();
        }

        sb.AppendLine($"Analysis took {r.Elapsed.TotalMilliseconds:N0} ms.");
        return sb.ToString();
    }

    /// <summary>One-line summary, for scanning a folder full of candidates.</summary>
    public static string Summary(AnalysisResult r)
    {
        string flag = r.VerdictSeverity switch
        {
            Severity.Alert => "FAIL",
            Severity.Warn => "WARN",
            Severity.Good => "OK  ",
            _ => "INFO"
        };
        return $"{flag}  {r.Width,5}x{r.Height,-5} {r.BitDepth,2}bit  " +
               $"{r.UniqueGreyLevels,6:N0} levels  step {r.LevelStep,-5:N0} " +
               $"{r.NonGreyPixels,10:N0} non-grey  {r.Meta.FileName}";
    }
}
