using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthView.Integrations.Common;

namespace DepthView.Integrations.LightBurn;

/// <summary>
/// Reader for LightBurn .lbrn and .lbrn2 projects.
///
/// Both are plain UTF-8 XML - verified by opening real files rather than assumed, because the
/// "2" in the extension reads like a container change and is not one.
///
/// Two shapes of markup matter, and they are inconsistent with each other in ways a parser has
/// to respect rather than tidy:
///
///   Layers are elements whose name STARTS WITH CutSetting - "CutSetting", "CutSetting_Img",
///   "CutSetting_Cut" - carrying a lowercase "type" attribute. Their parameters are CHILD
///   elements, each with a single capital-V "Value" attribute.
///
///   Shapes are "Shape" elements with a capital-T "Type" attribute, and their parameters are
///   ATTRIBUTES. The image itself lives in a "Data" attribute as base64.
///
/// The casing difference between the layer's "type" and the shape's "Type" is real. So is the
/// mixture of "True"/"False" on the root and 0/1/-1 inside UIPrefs.
///
/// The rule that shapes everything else: LightBurn omits any parameter that sits at its
/// default. A layer with no numPasses element is not a layer with zero passes. Every field
/// here is therefore read as optional and left null when absent, and nothing downstream is
/// allowed to assume a missing value means zero.
/// </summary>
public sealed class LbrnProjectReader : IProjectReader
{
    public string Name => "LightBurn";

    public IReadOnlyList<string> Extensions { get; } = new[] { ".lbrn", ".lbrn2" };

    public bool CanWrite => false;

    /// <summary>
    /// Looks for the root element rather than trusting the extension, and reads only the head
    /// of the file to do it. A project can be several megabytes of embedded base64 and there is
    /// no reason to load that to answer "is this a LightBurn file".
    /// </summary>
    public bool CanRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);

            var head = new char[600];
            int n = reader.Read(head, 0, head.Length);
            return n > 0 && new string(head, 0, n).Contains("<LightBurnProject",
                                                            StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public ProjectReadResult Read(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            return ProjectReadResult.Failed($"Could not parse as XML: {ex.Message}");
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "LightBurnProject")
            return ProjectReadResult.Failed(
                $"Root element is <{root?.Name.LocalName ?? "nothing"}>, expected <LightBurnProject>.");

        var job = new LaserJob
        {
            Format = "LightBurn",
            Path = path,
            AppVersion = (string?)root.Attribute("AppVersion"),
            DeviceName = (string?)root.Attribute("DeviceName"),
            Source = doc
        };

        foreach (var el in root.Elements().Where(e => e.Name.LocalName.StartsWith(
                     "CutSetting", StringComparison.Ordinal)))
            job.Layers.Add(ReadLayer(el));

        foreach (var el in root.Elements("Shape"))
        {
            string? type = (string?)el.Attribute("Type");
            if (!string.Equals(type, "Bitmap", StringComparison.OrdinalIgnoreCase)) continue;
            job.Images.Add(ReadBitmap(el, job));
        }

        foreach (var l in job.Layers.Where(l => UnfamiliarDither(l.DitherMode)))
            job.Notes.Add(
                $"Layer {l.Index} uses image mode \"{l.DitherMode}\", which this build does not "
                + "recognise. If that is 3D Sliced under another name, say so - the reader is "
                + "currently matching on the words \"slice\" and \"3d\" rather than on a known "
                + "value.");

        var fidelity = ReadFidelity.Full;
        string? message = null;

        if (job.Layers.Count == 0)
        {
            fidelity = ReadFidelity.Partial;
            message = "No layers found. The project may hold only geometry with no cut settings.";
        }
        else if (job.Images.Count == 0)
        {
            fidelity = ReadFidelity.Partial;
            message = "No bitmap shapes found - nothing in this project for a depth map to be.";
        }

        return new ProjectReadResult { Job = job, Fidelity = fidelity, Message = message };
    }

    public void Write(LaserJob job, string path)
        => throw new NotSupportedException(
            "Writing LightBurn projects is not implemented yet. Reading came first deliberately: "
            + "a wrong write costs somebody their project file, and a wrong read costs a message.");

    // ------------------------------------------------------------------ layers

    private static CutLayer ReadLayer(XElement el)
    {
        var layer = new CutLayer
        {
            VendorKind = (string?)el.Attribute("type")
        };

        // Parameters are child elements carrying a Value attribute. Collected verbatim first so
        // that a field this build has never heard of still survives into Vendor, then the ones
        // with a home are lifted out of that same dictionary.
        foreach (var child in el.Elements())
        {
            var value = (string?)child.Attribute("Value");
            if (value is not null) layer.Vendor[child.Name.LocalName] = value;
        }

        layer.Index = Int(layer, "index") ?? 0;
        layer.Name = Str(layer, "name");
        layer.Priority = Int(layer, "priority");

        layer.SpeedMmPerSec = Num(layer, "speed");
        layer.MaxPowerPercent = Num(layer, "maxPower");
        layer.MinPowerPercent = Num(layer, "minPower");
        layer.MaxPower2Percent = Num(layer, "maxPower2");
        layer.MinPower2Percent = Num(layer, "minPower2");

        layer.Passes = Int(layer, "numPasses");
        layer.LineIntervalMm = Num(layer, "interval");
        layer.Dpi = Num(layer, "dpi");

        // Frequency and pulse width are left null on purpose, and their raw values stay in
        // Vendor. The sample this reader was written against is a diode image job with no fibre
        // layer in it, so the unit LightBurn writes "frequency" in - Hz or kHz - has not been
        // confirmed against a real file. Dividing by a thousand on the strength of a memory is
        // exactly the kind of quiet error this program exists to catch in other people's work.
        // Fill these in from a fibre project, not from an assumption.
        layer.PulseWidthNs = null;
        layer.FrequencyKhz = null;

        layer.ZOffsetMm = Num(layer, "zOffset");
        layer.ZStepPerPassMm = Num(layer, "perPassZ");

        // Two spellings for the same idea, and which one appears depends on the device profile:
        // "enableAirAssist" on some, "runBlower" on the galvo profiles in the sample projects.
        // Both are read; whichever the file used wins, and neither is invented when absent.
        layer.AirAssist = Bool(layer, "enableAirAssist") ?? Bool(layer, "runBlower");
        layer.Output = Bool(layer, "doOutput");
        layer.DitherMode = Str(layer, "ditherMode");

        // A tool layer is one LightBurn marks as not for output. Nothing else in the file says
        // "this is a tool layer" directly, so this is inference and is labelled as such rather
        // than presented as something the file stated.
        layer.Kind = layer.Output == false
            ? LayerKind.Tool
            : KindFrom(layer.VendorKind);

        // 3D Sliced is an image mode rather than a layer type, so a sliced layer arrives here
        // looking like any other Image. LightBurn's documentation establishes two things about
        // it: the mode lives in the image-mode dropdown, and its "Number of Passes" is the
        // count the depth map is split into - which is the slice count, not a repeat count.
        //
        // What is NOT established is the string LightBurn writes into ditherMode for it. Every
        // sample to hand was saved against a GRBL profile, where the mode is not offered at all
        // because it is galvo-only. So this matches on the shape of the word rather than on a
        // known value, and anything unrecognised is reported rather than assumed.
        if (layer.Kind == LayerKind.Image && LooksSliced(layer.DitherMode))
        {
            layer.Kind = LayerKind.Slice3D;
            layer.SliceCount = layer.Passes;
        }

        return layer;
    }

    /// <summary>
    /// LightBurn's layer type string to the common vocabulary.
    ///
    /// Anything unrecognised stays <see cref="LayerKind.Unknown"/> with the original spelling
    /// kept, because a wrong guess here would make a fill layer analyse as a 3D slice and
    /// quote every depth figure against the wrong number.
    /// </summary>
    private static LayerKind KindFrom(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "image" => LayerKind.Image,
        "cut" or "line" => LayerKind.Line,
        "scan" or "fill" => LayerKind.Fill,
        "scan+cut" or "fillandline" => LayerKind.FillAndLine,
        "offset" or "offsetfill" => LayerKind.Offset,
        "tool" => LayerKind.Tool,
        _ => LayerKind.Unknown
    };

    /// <summary>Image modes seen in real files. Used only to decide whether an unfamiliar mode
    /// is worth reporting, never to reject one.</summary>
    private static readonly string[] KnownDitherModes =
    {
        "threshold", "ordered", "atkinson", "jarvis", "stucki", "newsprint", "halftone",
        "grayscale", "greyscale", "dither"
    };

    private static bool LooksSliced(string? mode)
    {
        var m = (mode ?? "").ToLowerInvariant();
        return m.Contains("slice", StringComparison.Ordinal)
            || m.Contains("3d", StringComparison.Ordinal);
    }

    /// <summary>True for a mode string this build has never seen, so the caller can say so
    /// rather than treat an unfamiliar image mode as an ordinary one.</summary>
    private static bool UnfamiliarDither(string? mode)
        => mode is { Length: > 0 }
        && !LooksSliced(mode)
        && !KnownDitherModes.Contains(mode.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    // ------------------------------------------------------------------ bitmaps

    private static ImagePlacement ReadBitmap(XElement el, LaserJob job)
    {
        var img = new ImagePlacement
        {
            LayerIndex = ParseInt((string?)el.Attribute("CutIndex")) ?? 0,
            SourcePath = (string?)el.Attribute("File"),

            // Always, for every LightBurn project. Its bed has Y increasing upward, so an
            // embedded bitmap is stored with its rows bottom-up and comes out of a decoder
            // upside down. Checked against two projects whose XForm disagreed on the Y sign:
            // both stored the data flipped and byte-identical to the source otherwise, so this
            // is the container's convention rather than anything about how a piece is placed.
            StoredBottomUp = true
        };

        foreach (var a in el.Attributes())
        {
            // Everything except the payload. Keeping several megabytes of base64 in a
            // diagnostics dictionary would make every ToString a memory event.
            if (a.Name.LocalName is "Data") continue;
            img.Vendor[a.Name.LocalName] = a.Value;
        }

        string? data = (string?)el.Attribute("Data");
        if (!string.IsNullOrEmpty(data))
        {
            try { img.Data = Convert.FromBase64String(data); }
            catch (FormatException) { job.Notes.Add("A Shape's Data attribute was not valid base64."); }
        }

        double? w = ParseNum((string?)el.Attribute("W"));
        double? h = ParseNum((string?)el.Attribute("H"));

        // The XForm child is six numbers - a b c d e f - mapping source units to bed
        // millimetres. Scientific notation shows up in real files, so this parses invariantly
        // and accepts exponents.
        var xf = el.Element("XForm");
        if (xf is not null)
        {
            var parts = xf.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6)
            {
                var m = new double[6];
                bool ok = true;
                for (int i = 0; i < 6 && ok; i++)
                    ok = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture,
                                         out m[i]);

                if (ok)
                {
                    img.Transform = m;
                    img.CentreXMm = m[4];
                    img.CentreYMm = m[5];

                    // Scale is the length of each basis vector, which survives rotation - the
                    // sample file's transform is a rotation, so reading m[0] and m[3] alone
                    // would have reported a size of essentially zero.
                    double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1]);
                    double sy = Math.Sqrt(m[2] * m[2] + m[3] * m[3]);

                    if (w is { } ww) img.WidthMm = Math.Abs(ww * sx);
                    if (h is { } hh) img.HeightMm = Math.Abs(hh * sy);
                }
                else
                {
                    job.Notes.Add("A Shape's XForm could not be parsed as six numbers.");
                }
            }
        }

        // W and H are the source's own units. Treated as pixels only where they are whole
        // numbers, because the sample this was written against carries a fractional W, and a
        // fractional pixel count is a sign the assumption does not hold.
        if (w is { } wv && Math.Abs(wv - Math.Round(wv)) < 1e-6) img.PixelWidth = (int)Math.Round(wv);
        if (h is { } hv && Math.Abs(hv - Math.Round(hv)) < 1e-6) img.PixelHeight = (int)Math.Round(hv);

        return img;
    }

    // ------------------------------------------------------------------ helpers
    //
    // All invariant-culture. A project written on a machine with a comma decimal separator has
    // to read identically on one without, and Parse-with-current-culture is the classic way for
    // "0.075" to silently become 75.

    private static string? Str(CutLayer l, string key)
        => l.Vendor.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static double? Num(CutLayer l, string key) => ParseNum(Str(l, key));

    private static int? Int(CutLayer l, string key) => ParseInt(Str(l, key));

    private static bool? Bool(CutLayer l, string key)
    {
        var s = Str(l, key);
        if (s is null) return null;
        if (bool.TryParse(s, out bool b)) return b;
        return ParseInt(s) is { } n ? n != 0 : null;
    }

    private static double? ParseNum(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;

    private static int? ParseInt(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : ParseNum(s) is { } d ? (int)Math.Round(d) : null;
}
