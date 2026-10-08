using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DepthView.Processing;

/// <summary>
/// A .dvp profile ("DepthView parameters", TODO 10.1): the settings of a job, saved beside the
/// map so a later map can start from them. Schema <c>depthview.params/1</c>.
///
/// Two kinds of setting are kept apart. <b>Portable</b> ones belong to the job - workpiece,
/// laser, pass count, rim, finishing, notes - and carry over to any map. <b>Map-specific</b>
/// ones - black and white points, flat-area changes - are true of one map only. The profile
/// records which map it was made for (a hash of its grey samples), so applied to a different
/// map the portable part arrives and the map-specific part is offered as "from another map",
/// never applied blindly. Writing one never touches the map.
/// </summary>
public sealed class DvpProfile
{
    public const string Schema = "depthview.params/1";
    public const string Extension = ".dvp";

    // ---- the map it was made for
    public string? MapName;
    public int MapWidth, MapHeight, MapBitDepth, MapMaxValue;
    public string? MapHash;

    // ---- portable: the workpiece
    public string Shape = "round";
    public double DiameterMm = Blank.DefaultDiameterMm;
    public double ThicknessMm = Blank.DefaultThicknessMm;
    /// <summary>Null when the target depth follows the thickness.</summary>
    public double? TargetDepthMm;
    public double DepthPercent = Blank.DefaultDepthPercent;
    public string? Material;

    // ---- portable: the laser
    public string? LaserType;
    public string? Lens;
    public double SpotMicrons = 7;
    public int Passes = 256;

    // ---- portable: tuning that is about the job rather than the picture
    public bool Rim;
    public double RimWidthMm = 1.0, RimRampMm;
    public FitPolicy Fit = FitPolicy.None;
    public PadFill Pad = PadFill.Background;
    public bool CoverDesignRim;
    public bool UniformSurround;
    public bool Stretch = true;
    public bool Invert;
    public bool Slice;
    public bool Dither;
    public int OutputBitDepth = 16;
    public bool WriteDpi;
    public bool Outline;
    public bool Mask;

    /// <summary>The finishing recipe as key=value pairs, as --render --finish takes it.</summary>
    public string? Finish;
    public string? Notes;

    // ---- map-specific
    public int? BlackPoint, WhitePoint;
    public List<FlatAction> FlatActions = new();

    /// <summary>The DepthView version that wrote the file, when read from one.</summary>
    public string? SavedBy;

    /// <summary>The .dvp that belongs beside a map: same folder, same base name.</summary>
    public static string PathBeside(string mapPath) => Path.ChangeExtension(mapPath, Extension);

    /// <summary>
    /// A fingerprint of the map's grey samples (and size), so the same picture is recognised
    /// whatever it is called, and a re-save of it in another container still matches.
    /// </summary>
    public static string GreyHash(ushort[] grey, int w, int h)
    {
        using var sha = SHA256.Create();
        var head = BitConverter.GetBytes(((long)w << 32) | (uint)h);
        sha.TransformBlock(head, 0, head.Length, null, 0);
        var buf = new byte[65536];
        int k = 0;
        foreach (var v in grey)
        {
            buf[k++] = (byte)v;
            buf[k++] = (byte)(v >> 8);
            if (k == buf.Length) { sha.TransformBlock(buf, 0, k, null, 0); k = 0; }
        }
        sha.TransformFinalBlock(buf, 0, k);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public bool SameMap(string? hash) => hash is not null && MapHash is not null
        && string.Equals(hash, MapHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The settings onto a <see cref="TuningOptions"/>. The map-specific part only when asked,
    /// which callers do only for the same map.
    /// </summary>
    public void ApplyTo(TuningOptions o, bool mapSpecific, int maxValue)
    {
        o.Stretch = Stretch;
        o.Invert = Invert;
        o.UniformSurround = UniformSurround;
        o.Slices = Slice ? Passes : 0;
        o.Dither = Dither;
        o.OutputBitDepth = OutputBitDepth == 8 ? 8 : 16;
        o.BlankDiameterMm = DiameterMm;
        o.TargetDepthMm = TargetDepthMm ?? Math.Clamp(ThicknessMm * DepthPercent / 100.0, Blank.MinDepthMm, Blank.MaxDepthMm);
        if (Rim)
        {
            o.RimWidthMm = RimWidthMm;
            o.RimRampMm = RimRampMm;
            o.Fit = Fit;
            o.PadWith = Pad;
            o.CoverDesignRim = CoverDesignRim && Fit == FitPolicy.Design;
        }
        if (mapSpecific)
        {
            if (BlackPoint is int b) o.BlackPoint = Math.Clamp(b, 0, maxValue);
            if (WhitePoint is int wp) o.WhitePoint = Math.Clamp(wp, 0, maxValue);
            o.FlatActions = new List<FlatAction>(FlatActions);
        }
    }

    // ------------------------------------------------------------------ writing

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("depthview", BuildInfo.Version);
            w.WriteString("saved", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            w.WriteStartObject("map");
            Str(w, "name", MapName);
            w.WriteNumber("width", MapWidth);
            w.WriteNumber("height", MapHeight);
            w.WriteNumber("bitDepth", MapBitDepth);
            w.WriteNumber("maxValue", MapMaxValue);
            Str(w, "greyHash", MapHash);
            w.WriteEndObject();

            w.WriteStartObject("portable");
            w.WriteStartObject("workpiece");
            w.WriteString("shape", Shape);
            w.WriteNumber("diameterMm", DiameterMm);
            w.WriteNumber("thicknessMm", ThicknessMm);
            if (TargetDepthMm is double td) w.WriteNumber("targetDepthMm", td); else w.WriteNull("targetDepthMm");
            w.WriteNumber("depthPercent", DepthPercent);
            Str(w, "material", Material);
            w.WriteEndObject();
            w.WriteStartObject("laser");
            Str(w, "type", LaserType);
            Str(w, "lens", Lens);
            w.WriteNumber("spotMicrons", SpotMicrons);
            w.WriteNumber("passes", Passes);
            w.WriteEndObject();
            w.WriteStartObject("rim");
            w.WriteBoolean("enabled", Rim);
            w.WriteNumber("widthMm", RimWidthMm);
            w.WriteNumber("rampMm", RimRampMm);
            w.WriteString("fit", FitName(Fit));
            w.WriteString("pad", Pad == PadFill.Untouched ? "untouched" : "background");
            w.WriteBoolean("coverDesignRim", CoverDesignRim);
            w.WriteEndObject();
            w.WriteBoolean("uniformSurround", UniformSurround);
            w.WriteBoolean("stretch", Stretch);
            w.WriteBoolean("invert", Invert);
            w.WriteBoolean("quantise", Slice);
            w.WriteBoolean("ditherSlices", Dither);
            w.WriteStartObject("output");
            w.WriteNumber("bitDepth", OutputBitDepth);
            w.WriteBoolean("writeDpi", WriteDpi);
            w.WriteBoolean("outline", Outline);
            w.WriteBoolean("rimMask", Mask);
            w.WriteEndObject();
            Str(w, "finish", Finish);
            Str(w, "notes", Notes);
            w.WriteEndObject();

            w.WriteStartObject("mapSpecific");
            if (BlackPoint is int b) w.WriteNumber("blackPoint", b); else w.WriteNull("blackPoint");
            if (WhitePoint is int wp) w.WriteNumber("whitePoint", wp); else w.WriteNull("whitePoint");
            w.WriteStartArray("flatAreas");
            foreach (var a in FlatActions)
            {
                w.WriteStartObject();
                w.WriteString("mode", a.Mode.ToString().ToLowerInvariant());
                w.WriteNumber("low", a.Low);
                w.WriteNumber("high", a.High);
                w.WriteNumber("level", a.Level);
                w.WriteNumber("maskWidth", a.MaskW);
                w.WriteNumber("maskHeight", a.MaskH);
                w.WriteString("mask", PackMask(a.Mask));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Write beside the map, or wherever asked. Refuses to write over an image.</summary>
    public void Save(string path)
    {
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A profile is written as {Extension}, not over {Path.GetFileName(path)}.");
        File.WriteAllText(path, ToJson() + Environment.NewLine, new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ reading

    public static DvpProfile Load(string path, out List<string> problems)
        => Parse(File.ReadAllText(path), out problems);

    public static DvpProfile Parse(string json, out List<string> problems)
    {
        problems = new List<string>();
        var p = new DvpProfile();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        string schema = S(root, "schema") ?? "";
        if (schema != Schema)
        {
            if (schema.StartsWith("depthview.params/", StringComparison.Ordinal))
                problems.Add($"Written as {schema}; this build reads {Schema}. Read what it could.");
            else
                throw new InvalidDataException($"Not a DepthView profile (schema '{schema}').");
        }
        p.SavedBy = S(root, "depthview");

        if (root.TryGetProperty("map", out var m))
        {
            p.MapName = S(m, "name");
            p.MapWidth = I(m, "width") ?? 0;
            p.MapHeight = I(m, "height") ?? 0;
            p.MapBitDepth = I(m, "bitDepth") ?? 0;
            p.MapMaxValue = I(m, "maxValue") ?? 0;
            p.MapHash = S(m, "greyHash");
        }
        if (root.TryGetProperty("portable", out var po))
        {
            if (po.TryGetProperty("workpiece", out var wk))
            {
                p.Shape = S(wk, "shape") ?? "round";
                if (p.Shape != "round") problems.Add($"Workpiece shape '{p.Shape}' is kept, but this build draws round blanks only.");
                p.DiameterMm = Math.Clamp(D(wk, "diameterMm") ?? p.DiameterMm, Blank.MinDiameterMm, Blank.MaxDiameterMm);
                p.ThicknessMm = Math.Clamp(D(wk, "thicknessMm") ?? p.ThicknessMm, Blank.MinThicknessMm, Blank.MaxThicknessMm);
                p.TargetDepthMm = D(wk, "targetDepthMm") is double t ? Math.Clamp(t, Blank.MinDepthMm, Blank.MaxDepthMm) : null;
                p.DepthPercent = Math.Clamp(D(wk, "depthPercent") ?? p.DepthPercent, 1, 100);
                p.Material = S(wk, "material");
            }
            if (po.TryGetProperty("laser", out var la))
            {
                p.LaserType = S(la, "type");
                p.Lens = S(la, "lens");
                p.SpotMicrons = Math.Clamp(D(la, "spotMicrons") ?? p.SpotMicrons, 1, 500);
                p.Passes = Math.Clamp(I(la, "passes") ?? p.Passes, 2, 65535);
            }
            if (po.TryGetProperty("rim", out var ri))
            {
                p.Rim = B(ri, "enabled") ?? false;
                p.RimWidthMm = Math.Clamp(D(ri, "widthMm") ?? p.RimWidthMm, 0, 50);
                p.RimRampMm = Math.Clamp(D(ri, "rampMm") ?? 0, 0, 20);
                p.Fit = ParseFit(S(ri, "fit"), problems);
                p.Pad = S(ri, "pad") == "untouched" ? PadFill.Untouched : PadFill.Background;
                p.CoverDesignRim = B(ri, "coverDesignRim") ?? false;
            }
            p.UniformSurround = B(po, "uniformSurround") ?? false;
            p.Stretch = B(po, "stretch") ?? true;
            p.Invert = B(po, "invert") ?? false;
            p.Slice = B(po, "quantise") ?? false;
            p.Dither = B(po, "ditherSlices") ?? false;
            if (po.TryGetProperty("output", out var ou))
            {
                p.OutputBitDepth = I(ou, "bitDepth") == 8 ? 8 : 16;
                p.WriteDpi = B(ou, "writeDpi") ?? false;
                p.Outline = B(ou, "outline") ?? false;
                p.Mask = B(ou, "rimMask") ?? false;
            }
            p.Finish = S(po, "finish");
            p.Notes = S(po, "notes");
        }
        if (root.TryGetProperty("mapSpecific", out var ms))
        {
            p.BlackPoint = I(ms, "blackPoint");
            p.WhitePoint = I(ms, "whitePoint");
            if (ms.TryGetProperty("flatAreas", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var e in fa.EnumerateArray())
                {
                    try
                    {
                        int mw = I(e, "maskWidth") ?? 0, mh = I(e, "maskHeight") ?? 0;
                        var mask = UnpackMask(S(e, "mask") ?? "", mw * mh);
                        var mode = Enum.TryParse<FlatMode>(S(e, "mode"), true, out var md) ? md : FlatMode.Leave;
                        p.FlatActions.Add(new FlatAction(mask, mw, mh, I(e, "low") ?? 0, I(e, "high") ?? 0, I(e, "level") ?? 0, mode));
                    }
                    catch (Exception ex) { problems.Add("A flat-area entry could not be read: " + ex.Message); }
                }
        }
        return p;
    }

    // ------------------------------------------------------------------ helpers

    public static string FitName(FitPolicy f) => f switch
    {
        FitPolicy.Content => "artwork",
        FitPolicy.Canvas => "canvas",
        FitPolicy.Design => "design",
        _ => "none",
    };

    private static FitPolicy ParseFit(string? s, List<string> problems) => s switch
    {
        null or "none" => FitPolicy.None,
        "artwork" or "content" => FitPolicy.Content,
        "canvas" => FitPolicy.Canvas,
        "design" => FitPolicy.Design,
        _ => Unknown(s, problems),
    };

    private static FitPolicy Unknown(string s, List<string> problems)
    {
        problems.Add($"Fit '{s}' is not one this build knows; the design is not fitted.");
        return FitPolicy.None;
    }

    private static string PackMask(bool[] mask)
    {
        var bytes = new byte[(mask.Length + 7) / 8];
        for (int i = 0; i < mask.Length; i++) if (mask[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
        return Convert.ToBase64String(bytes);
    }

    private static bool[] UnpackMask(string b64, int n)
    {
        var bytes = Convert.FromBase64String(b64);
        if (bytes.Length * 8 < n) throw new InvalidDataException("mask shorter than its size");
        var m = new bool[n];
        for (int i = 0; i < n; i++) m[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
        return m;
    }

    private static void Str(Utf8JsonWriter w, string name, string? v)
    {
        if (string.IsNullOrEmpty(v)) w.WriteNull(name); else w.WriteString(name, v);
    }

    private static string? S(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? D(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static int? I(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? B(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}
