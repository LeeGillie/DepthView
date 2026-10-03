using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DepthView.Imaging;
using DepthView.Processing;

namespace DepthView.Analysis;

/// <summary>
/// The analysis and tuning results as JSON, for another program to read.
///
/// This exists so a host application - MakeIt, a script, a batch tool - can run DepthView's
/// command line and act on the answer without scraping the text report. docs/INTEGRATION.md is
/// the specification; this file is its only implementation, and the two change together.
///
/// Three rules keep it usable as a contract:
/// <list type="bullet">
/// <item>Every document names its schema ("depthview.report/1", "depthview.tune/1"). Fields
/// may be added within a version; nothing is renamed, retyped or removed without bumping it.</item>
/// <item>A value that does not apply is null, never a made-up default. Absent is not zero -
/// the same lesson LightBurn's project files taught.</item>
/// <item>Output is ASCII (anything else is \u-escaped) so no console code page can mangle a
/// file name on its way through a pipe.</item>
/// </list>
///
/// Written with Utf8JsonWriter rather than a serializer, so there is no reflection for the
/// trimmer to break and the field order is exactly what the specification shows.
/// </summary>
public static class JsonReport
{
    public const string ReportSchema = "depthview.report/1";
    public const string TuneSchema = "depthview.tune/1";
    public const string SurveySchema = "depthview.survey/1";

    /// <summary>What the tuning wizard measures (--survey --json). Changes nothing.</summary>
    public static string Survey(string path, DesignSurvey s, int passes)
    {
        void Ext(Utf8JsonWriter w, string name, Extreme e)
        {
            w.WriteStartObject(name);
            w.WriteBoolean("found", e.Found);
            w.WriteNumber("low", e.Low);
            w.WriteNumber("high", e.High);
            w.WriteNumber("noise", e.Noise);
            w.WriteNumber("pixels", e.Pixels);
            Num(w, "share", e.Share);
            w.WriteNumber("suggested", e.Suggested);
            w.WriteString("source", e.Source);
            w.WriteEndObject();
        }

        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", SurveySchema);
            w.WriteString("depthview", BuildInfo.Version);
            w.WriteBoolean("ok", true);
            w.WriteString("path", path);
            w.WriteNumber("width", s.Width);
            w.WriteNumber("height", s.Height);
            w.WriteNumber("maxValue", s.MaxValue);
            w.WriteStartObject("background");
            w.WriteNumber("level", s.Background);
            Num(w, "share", s.BackgroundShare);
            Num(w, "shareInsideDesign", s.BackgroundInsideShare);
            w.WriteBoolean("looksLikeFloor", s.BackgroundLooksLikeFloor);
            w.WriteBoolean("isLow", s.BackgroundIsLow);
            w.WriteBoolean("shaded", s.SurroundShaded);
            w.WriteNumber("low", s.BackgroundLow);
            w.WriteNumber("high", s.BackgroundHigh);
            w.WriteEndObject();
            if (s.HasDesign)
            {
                w.WriteStartObject("design");
                Num(w, "centreX", s.CentreX);
                Num(w, "centreY", s.CentreY);
                Num(w, "radiusPx", s.Radius);
                Num(w, "offCentrePx", s.OffCentrePx);
                w.WriteEndObject();
            }
            else w.WriteNull("design");
            if (s.DrawnRim is { } rim)
            {
                w.WriteStartObject("drawnRim");
                Num(w, "innerPx", rim.Inner);
                Num(w, "outerPx", rim.Outer);
                Num(w, "footLevel", rim.FootLevel);
                Num(w, "topLevel", rim.TopLevel);
                w.WriteEndObject();
            }
            else w.WriteNull("drawnRim");
            // The floor and top under each reading of the design: is the background part of
            // it, and is the drawn rim being replaced. The wizard asks; these are the answers.
            w.WriteStartArray("readings");
            foreach (bool bgIsDesign in new[] { false, true })
                foreach (bool rimCovered in s.DrawnRim is null ? new[] { false } : new[] { false, true })
                {
                    w.WriteStartObject();
                    w.WriteBoolean("backgroundIsDesign", bgIsDesign);
                    w.WriteBoolean("drawnRimCovered", rimCovered);
                    Ext(w, "floor", s.Floor(bgIsDesign, rimCovered));
                    Ext(w, "top", s.Top(bgIsDesign, rimCovered));
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            Num(w, "noiseSigma", s.NoiseSigma);
            w.WriteNumber("passes", passes);
            var floor = s.Floor(s.BackgroundLooksLikeFloor);
            var top = s.Top(s.BackgroundLooksLikeFloor);
            w.WriteStartArray("flatAreas");
            foreach (var a in s.FlatAreas)
            {
                w.WriteStartObject();
                w.WriteNumber("rank", a.Rank);
                w.WriteNumber("pixels", a.Pixels);
                Num(w, "shareOfDesign", a.ShareOfDesign);
                w.WriteNumber("median", a.Median);
                w.WriteNumber("low", a.Low);
                w.WriteNumber("high", a.High);
                Num(w, "jitter", a.Jitter);
                w.WriteBoolean("mostlyJitter", a.MostlyJitter);
                w.WriteBoolean("floor", a.TouchesFloor);
                w.WriteBoolean("top", a.TouchesTop);
                w.WriteNumber("boundariesCrossed", a.BoundariesCrossed(passes, floor.Suggested, top.Suggested, s.MaxValue));
                Num(w, "centreX", a.CentreX);
                Num(w, "centreY", a.CentreY);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            Num(w, "seconds", s.Seconds);
            w.WriteEndObject();
        });
    }

    /// <summary>Pass counts every report carries unless the caller names its own.</summary>
    public static readonly int[] DefaultPassCounts = { 64, 100, 128, 200, 256, 512, 1024 };

    // The default encoder also escapes characters that are only dangerous inside HTML - so
    // "RGBA (truecolour + alpha)" came out with its plus sign as a six-character escape. This goes to a
    // pipe or a file, never into a web page, so those stay readable; non-ASCII is escaped by
    // hand in Write() instead, which keeps the whole document ASCII.
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One file's outcome in a report run: an analysis, or the reason there is none.</summary>
    public sealed record Entry(string Path, AnalysisResult? Result, string? Error);

    public static string Report(IReadOnlyList<Entry> entries, IReadOnlyList<int> passCounts, bool histogram)
    {
        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", ReportSchema);
            w.WriteString("depthview", BuildInfo.Version);
            w.WriteStartArray("files");
            foreach (var e in entries)
            {
                if (e.Result is { } r) WriteAnalysis(w, e.Path, r, passCounts, histogram);
                else
                {
                    w.WriteStartObject();
                    w.WriteString("path", FullPath(e.Path));
                    w.WriteString("name", System.IO.Path.GetFileName(e.Path));
                    w.WriteBoolean("ok", false);
                    w.WriteString("error", e.Error ?? "unknown error");
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>Everything a tune run did, with the before and after analyses it measured.</summary>
    public sealed class TuneOutcome
    {
        public required string Input { get; init; }
        public required string Output { get; init; }
        public string? Mask { get; init; }
        public string? Outline { get; init; }
        public required int InWidth { get; init; }
        public required int InHeight { get; init; }
        public required TuningOptions Options { get; init; }
        public required TuningReport Report { get; init; }
        public required int Passes { get; init; }
        public double SpotMicrons { get; init; }
        public required AnalysisResult Before { get; init; }
        public required AnalysisResult After { get; init; }
    }

    public static string Tune(TuneOutcome t)
    {
        var o = t.Options;
        var rep = t.Report;
        var passCounts = DefaultPassCounts.Append(t.Passes).Distinct().OrderBy(p => p).ToArray();

        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", TuneSchema);
            w.WriteString("depthview", BuildInfo.Version);
            w.WriteBoolean("ok", true);
            w.WriteString("input", FullPath(t.Input));
            w.WriteString("output", FullPath(t.Output));
            StringOrNull(w, "mask", t.Mask is null ? null : FullPath(t.Mask));
            StringOrNull(w, "outline", t.Outline is null ? null : FullPath(t.Outline));

            w.WriteStartObject("size");
            w.WriteNumber("inWidth", t.InWidth);
            w.WriteNumber("inHeight", t.InHeight);
            w.WriteNumber("outWidth", rep.OutWidth);
            w.WriteNumber("outHeight", rep.OutHeight);
            w.WriteEndObject();

            w.WriteStartObject("applied");
            w.WriteNumber("blackPoint", o.BlackPoint);
            w.WriteNumber("whitePoint", o.WhitePoint);
            w.WriteBoolean("stretch", o.Stretch);
            w.WriteBoolean("invert", o.Invert);
            IntOrNull(w, "slices", o.Slices > 0 ? o.Slices : null);
            w.WriteBoolean("dither", o.Dither);
            w.WriteNumber("bits", o.OutputBitDepth);
            w.WriteString("fit", o.Fit.ToString().ToLowerInvariant());
            w.WriteString("pad", o.PadWith.ToString().ToLowerInvariant());
            w.WriteBoolean("rim", o.AddRim);
            w.WriteBoolean("uniformSurround", o.UniformSurround);
            w.WriteEndObject();

            w.WriteNumber("changedPixels", rep.Changed);
            w.WriteNumber("surroundPixelsEvened", rep.SurroundEvened);
            w.WriteNumber("flattenedToBlack", rep.FlattenedToBlack);
            w.WriteNumber("liftedToWhite", rep.LiftedToWhite);

            // Added with --flat: one entry per nearly level area, in --survey's order.
            w.WriteStartObject("flat");
            w.WriteStartArray("areas");
            foreach (var a in o.FlatActions)
            {
                w.WriteStartObject();
                w.WriteString("mode", a.Mode.ToString().ToLowerInvariant());
                w.WriteNumber("low", a.Low);
                w.WriteNumber("high", a.High);
                w.WriteNumber("level", a.Level);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("pixelsChanged", rep.FlatChanged);
            w.WriteNumber("maxChange", rep.FlatMaxChange);
            w.WriteEndObject();

            if (rep.Fit is { } fit)
            {
                w.WriteStartObject("fit");
                w.WriteNumber("canvasPx", fit.Size);
                Num(w, "artAcrossMm", fit.ArtAcrossMm);
                Num(w, "pixelsPerMm", fit.PixelsPerMm);
                // Added 1.8.0 with "fit": "design". Offsets place the input's top-left corner on
                // the output and are negative where background was cropped away.
                w.WriteBoolean("recentred", fit.Recentred);
                w.WriteNumber("offsetX", fit.OffsetX);
                w.WriteNumber("offsetY", fit.OffsetY);
                w.WriteBoolean("cropped", fit.Crops(t.InWidth, t.InHeight));
                // Added 1.8.0 with --cover-rim: the design's own rim, found and put under ours.
                if (fit.CoveredRim is { } own)
                {
                    w.WriteStartObject("designRim");
                    Num(w, "innerPx", own.Inner);
                    Num(w, "outerPx", own.Outer);
                    Num(w, "widthMm", own.Width / fit.PixelsPerMm);
                    Num(w, "footLevel", own.FootLevel);
                    Num(w, "topLevel", own.TopLevel);
                    w.WriteNumber("pixelsCovered", rep.DesignRimCovered);
                    w.WriteEndObject();
                }
                else w.WriteNull("designRim");
                w.WriteEndObject();
            }
            else w.WriteNull("fit");
            if (rep.DesignOffCentreMm is double off) Num(w, "designOffCentreMm", off);
            else w.WriteNull("designOffCentreMm");

            if (o.AddRim)
            {
                w.WriteStartObject("rim");
                NumOrNull(w, "widthMm", o.RimWidthMm);
                NumOrNull(w, "rampMm", o.RimRampMm);
                Num(w, "radiusPx", o.RimRadius);
                Num(w, "rampPx", o.RimRamp);
                w.WriteNumber("pixelsPainted", rep.RimPixels);
                w.WriteNumber("contentPixelsClipped", rep.RimClipped);
                Num(w, "contentClippedFraction", rep.RimClippedFraction);
                w.WriteString("summary", rep.Summary);
                w.WriteEndObject();
            }
            else w.WriteNull("rim");

            // The written canvas, not the input: a fit changes how many pixels the blank spans.
            if (o.PixelsPerMm(rep.OutWidth, rep.OutHeight) is double ppmm && o.BlankDiameterMm is double blank)
            {
                var check = ResolutionCheck.For(1000.0 / ppmm, t.SpotMicrons);
                w.WriteStartObject("physical");
                Num(w, "blankDiameterMm", blank);
                Num(w, "pixelsPerMm", ppmm);
                NumOrNull(w, "dpi", o.Dpi);
                Num(w, "micronsPerPixel", check.MicronsPerPixel);
                Num(w, "spotMicrons", t.SpotMicrons);
                w.WriteString("resolutionNote", check.Note);
                w.WriteEndObject();
            }
            else w.WriteNull("physical");

            // Arithmetic, not a prediction: the target depth shared out evenly over the passes.
            // How deep each pass really cuts depends on the material and the settings, which is
            // what the unbuilt depth model is for. The name says "target" so nobody reads it as
            // a measured or modelled figure.
            if (o.TargetDepthMm is double depth && depth > 0)
            {
                w.WriteStartObject("target");
                Num(w, "depthMm", depth);
                w.WriteNumber("passes", t.Passes);
                Num(w, "targetMicronsPerPass", depth * 1000 / t.Passes);
                w.WriteEndObject();
            }
            else w.WriteNull("target");

            w.WriteNumber("passes", t.Passes);
            w.WritePropertyName("before");
            WriteAnalysis(w, t.Input, t.Before, passCounts, histogram: false);
            w.WritePropertyName("after");
            WriteAnalysis(w, t.Output, t.After, passCounts, histogram: false);
            w.WriteEndObject();
        });
    }

    public const string GcodeSchema = "depthview.gcode/1";

    /// <summary>What a G-code file sends the machine: power levels, sampling, heights, settings.</summary>
    public static string Gcode(DepthView.Integrations.WeCreat.Gcode.GcodeAnalysis a) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", GcodeSchema);
        w.WriteString("depthview", BuildInfo.Version);
        w.WriteBoolean("ok", true);
        w.WriteString("path", FullPath(a.Path));
        w.WriteString("name", System.IO.Path.GetFileName(a.Path));
        w.WriteNumber("fileBytes", a.Bytes);
        w.WriteBoolean("gzipped", a.Gzipped);
        w.WriteNumber("lines", a.Lines);
        StringOrNull(w, "generator", a.Generator);
        w.WriteStartArray("headerComments");
        foreach (var c in a.HeaderComments) w.WriteStringValue(c);
        w.WriteEndArray();

        w.WriteStartObject("moves");
        w.WriteNumber("g0", a.G0Moves);
        w.WriteNumber("g1", a.G1Moves);
        w.WriteNumber("burning", a.BurnMoves);
        w.WriteNumber("z", a.ZMoves);
        Num(w, "burnLengthMm", a.BurnLengthMm);
        Num(w, "travelLengthMm", a.TravelLengthMm);
        w.WriteEndObject();

        if (a.HasBurn)
        {
            w.WriteStartObject("burnArea");
            Num(w, "minX", a.MinX); Num(w, "maxX", a.MaxX);
            Num(w, "minY", a.MinY); Num(w, "maxY", a.MaxY);
            w.WriteEndObject();
        }
        else w.WriteNull("burnArea");

        // Every distinct S the burning moves use, with how many moves use it. This is the
        // depth resolution the machine actually receives.
        w.WriteNumber("powerLevelCount", a.PowerLevels.Count);
        w.WriteStartArray("powerLevels");
        foreach (var kv in a.PowerLevels)
        {
            w.WriteStartArray();
            w.WriteNumberValue(kv.Key / 1000.0);
            w.WriteNumberValue(kv.Value);
            w.WriteEndArray();
        }
        w.WriteEndArray();

        // Step between power changes along a line (binned), and one entry per scan direction.
        w.WriteStartObject("alongLine");
        if (a.StepMode is { } xs)
        {
            Num(w, "stepModeMm", xs.Mm);
            Num(w, "stepModeShare", xs.Share);
        }
        else { w.WriteNull("stepModeMm"); w.WriteNull("stepModeShare"); }
        w.WriteNumber("stepCount", a.StepCount);
        w.WriteNumber("stepBinUm", DepthView.Integrations.WeCreat.Gcode.GcodeAnalysis.StepBinUm);
        w.WriteEndObject();

        w.WriteStartArray("directions");
        foreach (var d in a.Directions)
        {
            w.WriteStartObject();
            w.WriteNumber("angleDeg", d.AngleDeg);
            w.WriteNumber("passes", d.Passes);
            w.WriteNumber("linesPerPass", d.LinesPerPass);
            NumOrNull(w, "linePitchMm", d.PitchMm);
            NumOrNull(w, "lineDensityPerCm", d.DensityPerCm);
            w.WriteNumber("burnMoves", d.BurnMoves);
            Num(w, "burnLengthMm", d.BurnLengthMm);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteNumber("layers", a.Layers);
        w.WriteStartArray("zLevels");
        foreach (var z in a.ZLevels)
        {
            w.WriteStartObject();
            Num(w, "z", z.Z);
            w.WriteNumber("layers", z.Layers);
            w.WriteNumber("burnMoves", z.BurnMoves);
            Num(w, "burnLengthMm", z.BurnLengthMm);
            IntOrNull(w, "mainAngleDeg", z.MainAngleDeg);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("zLevelsTruncated", a.ZLevelsTruncated);
        w.WriteNumber("undefinedOperandLines", a.UndefinedOperands);

        // Repeated layers and cleaning (added 1.7.0). Layer numbers count burning Z blocks in cut order.
        w.WriteStartObject("layerChecks");
        IntOrNull(w, "mainGroup", a.MainGroup);
        IntArray(w, "repeatedLayers", a.RepeatedBlocks);
        IntOrNull(w, "repeatAfterEvery", a.RepeatAfterEvery);
        IntArray(w, "otherSettingsLayers", a.OtherSettingsBlocks);
        IntOrNull(w, "otherSettingsAfterEvery", a.OtherSettingsAfterEvery);
        w.WriteNumber("emptyLayers", a.EmptyBlocks);
        w.WriteBoolean("emptyLayersAtEnd", a.EmptyBlocksAtEnd);
        w.WriteBoolean("truncated", a.BlocksTruncated);
        w.WriteStartArray("blocks");
        foreach (var b in a.Blocks)
        {
            w.WriteStartObject();
            IntOrNull(w, "layer", b.Layer);
            Num(w, "z", b.Z);
            w.WriteNumber("firstLine", b.FirstLine);
            w.WriteNumber("burnMoves", b.BurnMoves);
            Num(w, "burnLengthMm", b.BurnLengthMm);
            IntOrNull(w, "group", b.Group);
            IntOrNull(w, "angleDeg", b.AngleDeg);
            IntOrNull(w, "repeatsLayer", b.RepeatOf is int r ? a.Blocks[r - 1].Layer : null);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartArray("settingsGroups");
        foreach (var g in a.Groups)
        {
            w.WriteStartObject();
            w.WriteNumber("index", g.Index);
            Num(w, "minS", g.MinS);
            Num(w, "maxS", g.MaxS);
            w.WriteNumber("powerLevelCount", g.PowerLevels.Count);
            NumOrNull(w, "feedMmPerMin", g.FeedMmPerMin);
            NumOrNull(w, "frequencyKHz", g.FrequencyKHz);
            NumOrNull(w, "pulseWidthNs", g.PulseWidthNs);
            IntOrNull(w, "angleDeg", g.AngleDeg);
            w.WriteStartArray("rasterAnglesDeg");
            foreach (int ang in g.RasterAngles) w.WriteNumberValue(ang);
            w.WriteEndArray();
            NumOrNull(w, "linePitchMm", g.LinePitchMm);
            w.WriteNumber("linesPerPass", g.ScanLines);
            w.WriteNumber("burnMoves", g.BurnMoves);
            Num(w, "burnLengthMm", g.BurnLengthMm);
            w.WriteNumber("firstLine", g.FirstLine);

            // The same figures in the units MakeIt's settings panel shows. Decoded, not measured:
            // see "decoding" below for how far that decoding has been confirmed.
            w.WriteStartObject("makeIt");
            Num(w, "powerPercentMin", g.MinS / 10);
            Num(w, "powerPercentMax", g.MaxS / 10);
            NumOrNull(w, "speedMmPerS", g.SpeedMmPerS);
            NumOrNull(w, "frequencyKHz", g.FrequencyKHz);
            NumOrNull(w, "pulseWidthNs", g.PulseWidthNs);
            NumOrNull(w, "lineDensityPerCm", g.LineDensityPerCm);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteNumber("settingsSwitches", Math.Max(0, a.TotalRuns - 1));
        w.WriteStartArray("runs");
        foreach (var r in a.Runs)
        {
            w.WriteStartObject();
            w.WriteNumber("group", r.Group);
            w.WriteNumber("firstLine", r.FirstLine);
            NumOrNull(w, "z", r.Z);
            w.WriteNumber("burnMoves", r.BurnMoves);
            Num(w, "burnLengthMm", r.BurnLengthMm);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("runsTruncated", a.TotalRuns > a.Runs.Count);

        w.WriteStartArray("mCodes");
        foreach (var kv in a.MCodes)
        {
            w.WriteStartArray();
            w.WriteNumberValue(kv.Key);
            w.WriteNumberValue(kv.Value);
            w.WriteEndArray();
        }
        w.WriteEndArray();

        w.WriteString("decoding", DepthView.Integrations.WeCreat.Gcode.GcodeReport.EncodingNote);
        Num(w, "seconds", a.Elapsed.TotalSeconds);
        w.WriteEndObject();
    });

    /// <summary>A failure, in the same envelope, so a caller parsing stdout always gets JSON.</summary>
    public static string Error(string schema, string message) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", schema);
        w.WriteString("depthview", BuildInfo.Version);
        w.WriteBoolean("ok", false);
        w.WriteString("error", message);
        w.WriteEndObject();
    });

    // ------------------------------------------------------------------ one analysis

    private static void WriteAnalysis(Utf8JsonWriter w, string path, AnalysisResult r,
                                      IReadOnlyList<int> passCounts, bool histogram)
    {
        var m = r.Meta;
        w.WriteStartObject();
        w.WriteString("path", FullPath(path));
        w.WriteString("name", System.IO.Path.GetFileName(path));
        w.WriteBoolean("ok", true);

        w.WriteStartObject("verdict");
        w.WriteString("severity", SeverityName(r.VerdictSeverity));
        w.WriteString("imposter", ImposterName(r.Imposter));
        w.WriteString("title", r.Verdict);
        w.WriteString("detail", r.VerdictDetail);
        w.WriteEndObject();

        w.WriteStartObject("container");
        w.WriteString("format", m.Format);
        w.WriteString("colorModel", m.ColorModel);
        w.WriteNumber("declaredBitDepth", m.DeclaredBitDepth);
        w.WriteNumber("declaredChannels", m.DeclaredChannels);
        w.WriteBoolean("hasAlpha", m.HasAlpha);
        w.WriteBoolean("isPalette", m.IsPalette);
        w.WriteBoolean("bitExactDecode", m.BitDepthIsExact);
        NumOrNull(w, "dpiX", m.DpiX);
        NumOrNull(w, "dpiY", m.DpiY);
        w.WriteNumber("fileBytes", m.FileBytes);
        w.WriteEndObject();

        w.WriteStartObject("content");
        w.WriteNumber("width", r.Width);
        w.WriteNumber("height", r.Height);
        w.WriteNumber("channels", r.Channels);
        w.WriteNumber("bitDepth", r.BitDepth);
        w.WriteNumber("maxValue", r.MaxValue);
        w.WriteBoolean("isFloat", r.IsFloat);
        w.WriteNumber("uniqueGreyLevels", r.UniqueGreyLevels);
        w.WriteNumber("greyPixels", r.GreyPixels);
        w.WriteNumber("nonGreyPixels", r.NonGreyPixels);
        w.WriteBoolean("greyStoredAsColor", r.IsGrayscaleStoredAsColor);
        w.WriteEndObject();

        w.WriteStartObject("levels");
        w.WriteNumber("min", r.MinLevel);
        w.WriteNumber("max", r.MaxLevel);
        Num(w, "rangeUse", r.RangeUtilisation);
        Num(w, "occupancy", r.Occupancy);
        w.WriteNumber("effectiveBits", r.EffectiveBits);
        w.WriteNumber("step", r.LevelStep);
        w.WriteBoolean("uniformLadder", r.UniformLadder);
        w.WriteNumber("gaps", r.GapCount);
        w.WriteNumber("largestGap", r.LargestGap);
        Num(w, "mean", r.Mean);
        Num(w, "median", r.Median);
        Num(w, "stdDev", r.StdDev);
        w.WriteNumber("p1", r.P1);
        w.WriteNumber("p99", r.P99);
        w.WriteNumber("pureBlackPixels", r.PureBlackPixels);
        w.WriteNumber("pureWhitePixels", r.PureWhitePixels);
        w.WriteNumber("headroomTop", r.HeadroomTop);
        w.WriteNumber("headroomBottom", r.HeadroomBottom);
        w.WriteEndObject();

        // The same table as the text report's DEPTHS PER PASS COUNT, one row per pass count.
        w.WriteStartArray("passCounts");
        if (r.UsedLevels.Length > 1)
        {
            foreach (int n in passCounts)
            {
                var (distinct, _) = r.SlicesAt(n);
                var p = r.PassesAt(n);
                w.WriteStartObject();
                w.WriteNumber("passes", n);
                w.WriteNumber("depths", distinct);
                w.WriteNumber("uniform", p.Uniform);
                w.WriteNumber("relief", p.Relief);
                w.WriteNumber("empty", p.Empty);
                w.WriteNumber("stretched", r.SlicesAtRemapped(n));
                if (r.BandSpreadAt(n) is { } b)
                {
                    w.WriteStartObject("bandSpread");
                    w.WriteNumber("min", b.Min);
                    w.WriteNumber("max", b.Max);
                    Num(w, "ratio", b.Ratio);
                    w.WriteEndObject();
                }
                else w.WriteNull("bandSpread");
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();

        w.WriteStartArray("findings");
        foreach (var f in r.Findings.OrderBy(x => Fmt.Order(x.Severity)))
        {
            w.WriteStartObject();
            w.WriteString("severity", SeverityName(f.Severity));
            w.WriteString("title", f.Title);
            w.WriteString("detail", f.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("warnings");
        foreach (var s in m.Warnings) w.WriteStringValue(s);
        w.WriteEndArray();

        // Opt-in: a genuine 16-bit map has tens of thousands of occupied levels. Only occupied
        // levels are listed, as [level, count] pairs.
        if (histogram)
        {
            w.WriteBoolean("histogramBinned", r.HistogramIsBinned);
            w.WriteStartArray("histogram");
            var h = r.GreyHistogram;
            for (int level = 0; level < h.Length; level++)
            {
                if (h[level] == 0) continue;
                w.WriteStartArray();
                w.WriteNumberValue(level);
                w.WriteNumberValue(h[level]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ helpers

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Options)) body(w);
        string json = Encoding.UTF8.GetString(ms.ToArray());

        // Non-ASCII can only occur inside string literals, where \uXXXX is always valid, and a
        // character outside the BMP is already two UTF-16 units here, which is exactly how JSON
        // spells it. Pure ASCII means no console code page can mangle a file name in a pipe.
        var sb = new StringBuilder(json.Length);
        foreach (char c in json)
        {
            if (c < 0x80) sb.Append(c);
            else sb.Append("\\u").Append(((int)c).ToString("X4"));
        }
        return sb.ToString();
    }

    private static string FullPath(string p)
    {
        try { return System.IO.Path.GetFullPath(p); } catch { return p; }
    }

    public static string SeverityName(Severity s) => s switch
    {
        Severity.Good => "good",
        Severity.Info => "info",
        Severity.Warn => "warn",
        _ => "alert",
    };

    public static string ImposterName(ImposterKind k) => k switch
    {
        ImposterKind.None => "none",
        ImposterKind.Replicated257 => "replicated257",
        ImposterKind.HighByteOnly => "highByteOnly",
        ImposterKind.QuantisedLadder => "quantisedLadder",
        ImposterKind.SparseLevels => "sparseLevels",
        _ => k.ToString(),
    };

    // Utf8JsonWriter refuses NaN and infinity, and JSON has no spelling for them anyway.
    private static void Num(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, Math.Round(v, 6));
        else w.WriteNull(name);
    }

    private static void NumOrNull(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d) Num(w, name, d);
        else w.WriteNull(name);
    }

    private static void IntOrNull(Utf8JsonWriter w, string name, int? v)
    {
        if (v is int i) w.WriteNumber(name, i);
        else w.WriteNull(name);
    }

    private static void IntArray(Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (int v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static void StringOrNull(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name);
        else w.WriteString(name, v);
    }
}
