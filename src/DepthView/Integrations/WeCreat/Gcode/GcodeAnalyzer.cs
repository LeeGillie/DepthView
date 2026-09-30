using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DepthView.Integrations.WeCreat.Gcode;

/// <summary>
/// What a G-code file actually tells the machine to do: the power levels it really uses,
/// how finely it samples along and across its scan lines, the heights it cuts at, and the
/// settings each part of the job runs with.
///
/// This is the ground truth for everything upstream. A project file says what the user set;
/// the G-code is what the machine was sent. Reading MakeIt's staged job is a documented user
/// action, so nothing here needs the vendor's cooperation.
///
/// Built to stream: MakeIt relief jobs run to hundreds of megabytes and tens of millions of
/// lines. Nothing is held per line; collections grow only with the number of distinct values
/// (power levels, scan lines, heights, directions).
///
/// Scan lines are found in any direction. MakeIt relief jobs have been seen rastering at 45
/// degrees and changing direction between layers, so "horizontal lines, spaced in Y" would
/// be wrong for exactly the files that matter most. Each burning move is filed under its
/// direction (to the nearest degree) and its perpendicular offset; lines are distinct offsets
/// within a direction, and their spacing is the line pitch.
///
/// What is decoded and how sure it is:
/// <list type="bullet">
/// <item>Power: <c>S</c> on a <c>G1</c> move, 0-1000. A move burns when S &gt; 0.</item>
/// <item>Frequency <c>M38F</c> (kHz), pulse width <c>M39P</c> (ns), speed <c>G1 F</c> (mm/min)
/// and power as S/10 percent: confirmed for MakeIt's Color Test by a controlled run in the
/// MOPAChroma Atlas project, not yet for relief jobs. The report says so.</item>
/// <item>Line pitch and the step between power changes along a line are measured from the
/// moves themselves, not decoded from any setting.</item>
/// </list>
/// </summary>
public static class GcodeAnalyzer
{
    public static GcodeAnalysis Analyze(string path)
    {
        var sw = Stopwatch.StartNew();
        var a = new GcodeAnalysis { Path = path, Bytes = new FileInfo(path).Length, Gzipped = IsGzip(path) };
        var st = new State();

        using (var g = GcodeStream.Open(path))
        {
            while (g.ReadLine())
            {
                a.Lines++;
                var line = g.Current;
                var code = GcodeWords.StripComment(line);
                var comment = GcodeWords.CommentOf(line);

                if (comment.Length > 0)
                {
                    a.CommentLines++;
                    if (a.HeaderComments.Count < GcodeAnalysis.MaxHeaderComments)
                        a.HeaderComments.Add(Encoding.UTF8.GetString(comment).Trim());
                }
                if (IsBlank(code)) { if (comment.Length == 0) a.BlankLines++; continue; }

                if (code.IndexOf("undefined"u8) >= 0) a.UndefinedOperands++;
                Line(a, st, code, g.LineNumber);
            }
        }

        a.Finish();
        a.Elapsed = sw.Elapsed;
        return a;
    }

    private sealed class State
    {
        public double X, Y, Z;
        public bool HaveX, HaveY, HaveZ;
        public bool HaveXY => HaveX && HaveY;
        public int Motion = -1;          // 0 or 1 once a G0/G1 has been seen
        public bool Relative;
        public double S;
        public double? FeedG0, FeedG1;
        public double? Frequency, PulseWidth;

        // The previous G1 that moved, for the step between power changes along a line.
        public int LastAngle = int.MinValue;
        public double LastS = double.NaN;
    }

    private static void Line(GcodeAnalysis a, State st, ReadOnlySpan<byte> code, long lineNo)
    {
        int? g = null;
        double? x = null, y = null, z = null, s = null, f = null;
        int? m = null;

        foreach (var w in new GcodeWords(code))
        {
            // Everything after an M word is that M's parameter, not motion: MakeIt's preamble
            // has M107X-105Y-105 (not a move) and M4S1000 (a power scale, not a power), and
            // M38F45 / M39P250 carry frequency and pulse width.
            if (w.Letter == 'M')
            {
                m = (int)w.Value;
                a.MCodes[m.Value] = a.MCodes.GetValueOrDefault(m.Value) + 1;
                continue;
            }
            if (w.Letter == 'G') { g = (int)w.Value; m = null; continue; }
            if (m is not null)
            {
                if (m == 38 && w.Letter == 'F') st.Frequency = w.Value;
                else if (m == 39 && w.Letter == 'P') st.PulseWidth = w.Value;
                continue;
            }

            switch (w.Letter)
            {
                case 'X': x = w.Value; break;
                case 'Y': y = w.Value; break;
                case 'Z': z = w.Value; break;
                case 'S': s = w.Value; break;
                case 'F': f = w.Value; break;
            }
        }

        if (g is 90) st.Relative = false;
        else if (g is 91) st.Relative = true;
        else if (g is 0 or 1) st.Motion = g.Value;

        if (s is double sv) st.S = sv;
        if (f is double fv)
        {
            // MakeIt keeps separate feeds for rapids and cuts (G0F15000, then G1F48000), so an F
            // is filed under the motion mode of the line it is on.
            int mode = g is 0 or 1 ? g.Value : st.Motion;
            if (mode == 0) st.FeedG0 = fv; else st.FeedG1 = fv;
        }

        bool moves = x is not null || y is not null || z is not null;
        if (!moves || st.Motion < 0 || (g is not null && g is not (0 or 1)))
        {
            if (moves) ApplyPosition(st, x, y, z);
            return;
        }

        double ox = st.X, oy = st.Y;
        bool hadXY = st.HaveXY;
        ApplyPosition(st, x, y, z);

        if (z is not null)
        {
            a.ZMoves++;
            a.NoteZ(st.Z);
        }

        if (st.Motion == 0) { a.G0Moves++; st.LastAngle = int.MinValue; } else a.G1Moves++;
        if (!hadXY) return;

        double dx = st.X - ox, dy = st.Y - oy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return;

        if (st.Motion == 0) { a.TravelLengthMm += len; return; }

        // Direction folded into 0..179 degrees, so a line and its return pass count as one
        // direction, as they do in a bidirectional raster.
        int angle = (int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI);
        angle = ((angle % 180) + 180) % 180;

        // The step between power changes along a line: the length of a G1 that continues in the
        // direction of the previous one with a different power. In a raster that modulates power
        // per sample this is a whole number of samples, and its commonest value is the sample
        // pitch along the line.
        if (angle == st.LastAngle && st.S != st.LastS)
            a.NoteStep(len);
        st.LastAngle = angle;
        st.LastS = st.S;

        if (st.S > 0)
            a.Burn(st.X, st.Y, ox, oy, len, angle, st.S, st.FeedG1, st.Frequency, st.PulseWidth,
                   st.HaveZ ? st.Z : null, lineNo);
        else
            a.TravelLengthMm += len;
    }

    private static void ApplyPosition(State st, double? x, double? y, double? z)
    {
        if (st.Relative)
        {
            if (x is double rx) st.X += rx;
            if (y is double ry) st.Y += ry;
            if (z is double rz) st.Z += rz;
        }
        else
        {
            if (x is double ax) st.X = ax;
            if (y is double ay) st.Y = ay;
            if (z is double az) st.Z = az;
        }
        // A length means nothing until both axes have been given at least once.
        if (x is not null) st.HaveX = true;
        if (y is not null) st.HaveY = true;
        if (z is not null) st.HaveZ = true;
    }

    private static bool IsBlank(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s) if (b is not ((byte)' ' or (byte)'\t')) return false;
        return true;
    }

    private static bool IsGzip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
        }
        catch { return false; }
    }
}

/// <summary>The result of <see cref="GcodeAnalyzer.Analyze"/>.</summary>
public sealed class GcodeAnalysis
{
    public const int MaxHeaderComments = 12;
    public const int MaxRuns = 2000;
    public const int MaxZLevels = 2000;

    /// <summary>Along-line steps are binned to this, in micrometres. Diagonal moves between
    /// coordinates printed to three decimals vary by a micrometre or so either way.</summary>
    public const int StepBinUm = 5;

    public string Path = "";
    public long Bytes;
    public bool Gzipped;
    public long Lines, CommentLines, BlankLines;
    public List<string> HeaderComments = new();

    /// <summary>"wecreat 3.0.6" from MakeIt's first comment line, when present.</summary>
    public string? Generator => HeaderComments.Count > 0 && HeaderComments[0].StartsWith("wecreat", StringComparison.OrdinalIgnoreCase)
        ? HeaderComments[0] : null;

    public long G0Moves, G1Moves, BurnMoves, ZMoves;
    public double BurnLengthMm, TravelLengthMm;
    public double MinX = double.PositiveInfinity, MaxX = double.NegativeInfinity;
    public double MinY = double.PositiveInfinity, MaxY = double.NegativeInfinity;
    public bool HasBurn => BurnMoves > 0;

    /// <summary>Lines carrying the non-numeric operand "undefined", e.g. MakeIt's "Zundefined".</summary>
    public long UndefinedOperands;

    public SortedDictionary<int, long> MCodes = new();

    /// <summary>Burn moves per distinct power value (S, in thousandths).</summary>
    public SortedDictionary<long, long> PowerLevels = new();

    /// <summary>Step between power changes along a line, binned to <see cref="StepBinUm"/>, and how often.</summary>
    public SortedDictionary<long, long> Steps = new();

    /// <summary>Burning per scan direction, busiest first after <see cref="Finish"/>.</summary>
    public List<Direction> Directions = new();

    /// <summary>Burn length per cutting height, in order of first use.</summary>
    public List<ZLevel> ZLevels = new();
    public bool ZLevelsTruncated;

    public List<SettingsGroup> Groups = new();
    public List<Run> Runs = new();
    public int TotalRuns;

    public TimeSpan Elapsed;

    /// <summary>
    /// Burning in one scan direction. Line spacing is measured per pass - a stretch of burning
    /// at one height, one set of settings and one direction - because MakeIt shifts the lines of
    /// successive diagonal passes, and the union of many passes' lines is far denser than any
    /// one of them.
    /// </summary>
    public sealed class Direction
    {
        public int AngleDeg;
        public long BurnMoves;
        public double BurnLengthMm;
        internal readonly List<(double Pitch, int Lines)> PassPitches = new();

        /// <summary>Passes in this direction with enough lines to be a raster.</summary>
        public int Passes => PassPitches.Count;

        /// <summary>Typical line spacing within one pass, mm (median over passes, by line count).</summary>
        public double? PitchMm;

        /// <summary>Typical lines in one pass.</summary>
        public int LinesPerPass;

        public double? DensityPerCm => PitchMm is double p && p > 0 ? 10.0 / p : null;

        internal void Summarise()
        {
            if (PassPitches.Count == 0) return;
            var byPitch = PassPitches.OrderBy(p => p.Pitch).ToArray();
            long half = byPitch.Sum(p => (long)p.Lines) / 2, seen = 0;
            foreach (var p in byPitch)
            {
                seen += p.Lines;
                if (seen >= half) { PitchMm = p.Pitch; break; }
            }
            var lines = PassPitches.Select(p => p.Lines).OrderBy(v => v).ToArray();
            LinesPerPass = lines[lines.Length / 2];
        }
    }

    public sealed class ZLevel
    {
        public double Z;
        public long BurnMoves;
        public double BurnLengthMm;
        internal readonly Dictionary<int, double> ByAngle = new();

        /// <summary>The scan direction carrying most of this height's burning.</summary>
        public int? MainAngleDeg => ByAngle.Count == 0 ? null : ByAngle.MaxBy(kv => kv.Value).Key;
    }

    public sealed class SettingsGroup
    {
        public int Index;
        public double? FrequencyKHz, PulseWidthNs, FeedMmPerMin;
        public long BurnMoves;
        public double BurnLengthMm;
        public long FirstLine;
        public double MinS = double.PositiveInfinity, MaxS;
        public readonly HashSet<long> PowerLevels = new();
        internal readonly Dictionary<int, Direction> ByAngle = new();

        /// <summary>Scan direction, line count and pitch of this group's raster, when it is one.</summary>
        public int? AngleDeg;
        public List<int> RasterAngles = new();
        public int ScanLines;
        public double? LinePitchMm;

        public double? SpeedMmPerS => FeedMmPerMin / 60.0;
        public double? LineDensityPerCm => LinePitchMm is double p && p > 0 ? 10.0 / p : null;
    }

    public readonly record struct Run(int Group, long FirstLine, double? Z, long BurnMoves, double BurnLengthMm);

    // ------------------------------------------------------------------ accumulation

    private readonly Dictionary<int, Direction> _dirIndex = new();
    private readonly Dictionary<(long, long, long), SettingsGroup> _groupIndex = new();
    private readonly Dictionary<long, ZLevel> _zIndex = new();
    private SettingsGroup? _runGroup;
    private long _runFirstLine, _runMoves;
    private double _runLength;
    private double? _runZ;

    internal void NoteZ(double z)
    {
        long key = Key(z);
        if (_zIndex.ContainsKey(key)) return;
        if (_zIndex.Count >= MaxZLevels) { ZLevelsTruncated = true; return; }
        var level = new ZLevel { Z = z };
        _zIndex[key] = level;
        ZLevels.Add(level);
    }

    internal void NoteStep(double len)
    {
        long bin = (long)Math.Round(len * 1000 / StepBinUm) * StepBinUm;
        if (bin <= 0) return;
        Steps[bin] = Steps.GetValueOrDefault(bin) + 1;
    }

    internal void Burn(double x, double y, double ox, double oy, double len, int angle,
                       double s, double? feed, double? freq, double? pw, double? z, long lineNo)
    {
        BurnMoves++;
        BurnLengthMm += len;
        MinX = Math.Min(MinX, Math.Min(x, ox)); MaxX = Math.Max(MaxX, Math.Max(x, ox));
        MinY = Math.Min(MinY, Math.Min(y, oy)); MaxY = Math.Max(MaxY, Math.Max(y, oy));

        long sk = Key(s);
        PowerLevels[sk] = PowerLevels.GetValueOrDefault(sk) + 1;

        // Perpendicular offset of the line this move lies on, for its direction. Averaging both
        // ends halves the rounding in the printed coordinates.
        double th = angle * Math.PI / 180;
        double offset = (-(x + ox) / 2 * Math.Sin(th) + (y + oy) / 2 * Math.Cos(th));
        long ok = (long)Math.Round(offset * 10000);

        var dir = DirectionFor(_dirIndex, Directions, angle);
        dir.BurnMoves++;
        dir.BurnLengthMm += len;

        var gkey = (KeyOrNull(freq), KeyOrNull(pw), KeyOrNull(feed));
        if (!_groupIndex.TryGetValue(gkey, out var grp))
        {
            grp = new SettingsGroup
            {
                Index = Groups.Count + 1, FrequencyKHz = freq, PulseWidthNs = pw, FeedMmPerMin = feed, FirstLine = lineNo,
            };
            _groupIndex[gkey] = grp;
            Groups.Add(grp);
        }
        grp.BurnMoves++;
        grp.BurnLengthMm += len;
        grp.MinS = Math.Min(grp.MinS, s);
        grp.MaxS = Math.Max(grp.MaxS, s);
        grp.PowerLevels.Add(sk);
        var gdir = DirectionFor(grp.ByAngle, null, angle);
        gdir.BurnMoves++;
        gdir.BurnLengthMm += len;

        // A pass ends when the height, the settings or the direction changes. Its lines are
        // measured then, and the offsets thrown away, so memory follows one pass, not the job.
        var passKey = (z is double pz ? Key(pz) : long.MinValue, grp.Index, angle);
        if (passKey != _passKey) { ClosePass(); _passKey = passKey; _passDir = dir; _passGroupDir = gdir; }
        _passOffsets.Add(ok);

        if (z is double zz)
        {
            NoteZ(zz);
            if (_zIndex.TryGetValue(Key(zz), out var level))
            {
                level.BurnMoves++;
                level.BurnLengthMm += len;
                level.ByAngle[angle] = level.ByAngle.GetValueOrDefault(angle) + len;
            }
        }

        if (!ReferenceEquals(grp, _runGroup)) { CloseRun(); _runGroup = grp; _runFirstLine = lineNo; _runZ = z; }
        _runMoves++;
        _runLength += len;
    }

    private static Direction DirectionFor(Dictionary<int, Direction> index, List<Direction>? list, int angle)
    {
        if (!index.TryGetValue(angle, out var d))
        {
            d = new Direction { AngleDeg = angle };
            index[angle] = d;
            list?.Add(d);
        }
        return d;
    }

    private (long, int, int) _passKey = (long.MaxValue, 0, 0);
    private readonly HashSet<long> _passOffsets = new();
    private Direction? _passDir, _passGroupDir;

    private void ClosePass()
    {
        if (_passOffsets.Count > 0 && _passDir is not null)
        {
            var (lines, pitch) = Measure(_passOffsets, _passDir.AngleDeg);
            // Fewer than 20 lines is a stroke or a fragment, not a raster worth a spacing.
            if (lines >= 20 && pitch is double p)
            {
                _passDir.PassPitches.Add((p, lines));
                _passGroupDir?.PassPitches.Add((p, lines));
            }
        }
        _passOffsets.Clear();
    }

    private void CloseRun()
    {
        if (_runGroup is null) return;
        TotalRuns++;
        if (Runs.Count < MaxRuns)
            Runs.Add(new Run(_runGroup.Index, _runFirstLine, _runZ, _runMoves, _runLength));
        _runMoves = 0;
        _runLength = 0;
    }

    internal void Finish()
    {
        CloseRun();
        ClosePass();

        foreach (var d in Directions) d.Summarise();
        Directions.Sort((p, q) => q.BurnLengthMm.CompareTo(p.BurnLengthMm));

        foreach (var grp in Groups)
        {
            // A group is a raster when directions with measurable passes carry most of its
            // burning - all of them together, because MakeIt relief jobs cross-hatch, turning the
            // raster between passes. Vector strokes (MakeIt draws a test card's labels that way)
            // spread over many directions and get no line density, rather than a number that
            // means nothing.
            foreach (var d in grp.ByAngle.Values) d.Summarise();
            var raster = grp.ByAngle.Values.Where(d => d.Passes > 0 && d.PitchMm is not null)
                                           .OrderByDescending(d => d.BurnLengthMm).ToList();
            if (raster.Count == 0 || raster.Sum(d => d.BurnLengthMm) * 2 < grp.BurnLengthMm) continue;
            var main = raster[0];
            grp.AngleDeg = main.AngleDeg;
            grp.RasterAngles = raster.Select(d => d.AngleDeg).OrderBy(v => v).ToList();
            grp.ScanLines = main.LinesPerPass;
            grp.LinePitchMm = main.PitchMm;
        }

        // Heights that never carried a burn are travel heights, not cutting heights.
        ZLevels.RemoveAll(l => l.BurnMoves == 0);
    }

    /// <summary>
    /// Count the distinct lines in one direction and their typical spacing.
    ///
    /// Offsets that differ by less than the rounding in the printed coordinates are one line
    /// (only matters off the axes, where a line's points print a fraction of a micrometre
    /// apart). The spacing is the median gap, refined by averaging the gaps no larger than
    /// twice it: the jump between two separate areas does not count, and a pitch that prints
    /// as alternating 33 and 34 um still comes out as 33.3.
    /// </summary>
    private static (int Lines, double? PitchMm) Measure(HashSet<long> offsets, int angleDeg)
    {
        var sorted = offsets.OrderBy(v => v).ToArray();
        bool axis = angleDeg % 90 == 0;
        long tol = axis ? 0 : 15;   // 0.1 um units: axis lines print exactly, diagonals to within 1.5 um

        var centres = new List<double>();
        long start = sorted[0]; double sum = 0; int n = 0;
        foreach (var v in sorted)
        {
            if (n > 0 && v - start > tol)
            {
                centres.Add(sum / n);
                start = v; sum = 0; n = 0;
            }
            sum += v; n++;
        }
        centres.Add(sum / n);
        if (centres.Count < 3) return (centres.Count, null);

        var gaps = new double[centres.Count - 1];
        for (int i = 1; i < centres.Count; i++) gaps[i - 1] = centres[i] - centres[i - 1];
        var ordered = gaps.OrderBy(v => v).ToArray();
        double median = ordered[ordered.Length / 2];
        if (median <= 0) return (centres.Count, null);
        return (centres.Count, gaps.Where(v => v <= 2 * median).Average() / 10000.0);
    }

    /// <summary>Commonest step between power changes along a line, mm, and its share of all steps.</summary>
    public (double Mm, double Share)? StepMode
    {
        get
        {
            if (Steps.Count == 0) return null;
            long total = Steps.Values.Sum();
            var top = Steps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
            return (top.Key / 1000.0, (double)top.Value / total);
        }
    }

    public long StepCount => Steps.Values.Sum();

    // Values are keyed at a thousandth - a micrometre for positions - which is as fine as
    // MakeIt prints and coarse enough to fold float noise.
    internal static long Key(double v) => (long)Math.Round(v * 1000);
    private static long KeyOrNull(double? v) => v is double d ? Key(d) : long.MinValue;
}
