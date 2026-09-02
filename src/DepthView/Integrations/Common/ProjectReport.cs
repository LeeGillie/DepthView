using System;
using System.Linq;
using System.Text;

namespace DepthView.Integrations.Common;

/// <summary>
/// A project written out as text, in the same spirit as the depth-map report: everything it
/// says is something the file said, and everything the file did not say reads as "not stated"
/// rather than as a number.
/// </summary>
public static class ProjectReport
{
    public static string Write(ProjectReadResult result)
    {
        var sb = new StringBuilder();

        if (result.Job is null)
        {
            sb.AppendLine("PROJECT");
            sb.AppendLine("  " + (result.Message ?? "Could not be read."));
            return sb.ToString();
        }

        var job = result.Job;

        sb.AppendLine("PROJECT");
        sb.AppendLine($"  Format            {job.Format}");
        sb.AppendLine($"  Fidelity          {Describe(result.Fidelity)}");
        if (job.AppVersion is { Length: > 0 }) sb.AppendLine($"  Written by        {job.AppVersion}");
        if (job.DeviceName is { Length: > 0 }) sb.AppendLine($"  Device            {job.DeviceName}");
        sb.AppendLine($"  Layers            {job.Layers.Count}");
        sb.AppendLine($"  Images            {job.Images.Count}");
        sb.AppendLine();

        if (job.Layers.Count > 0)
        {
            sb.AppendLine("LAYERS");
            sb.AppendLine("  idx  kind         name    speed      power   passes    interval");
            foreach (var l in job.Layers.OrderBy(l => l.Index))
            {
                sb.AppendLine(
                    $"  {l.Index,3}  {l.Kind,-11}  {Trim(l.Name, 6),-6}"
                    + $"  {Mm(l.SpeedMmPerSec, "mm/s"),10}"
                    + $"  {Pct(l.MaxPowerPercent),7}"
                    + $"  {Opt(l.EffectivePasses),7}"
                    + $"  {Mm(l.LineIntervalMm, "mm"),11}");
            }

            // The vendor fields with no home in the common model are worth showing rather than
            // hiding: they are how you find out what the format carries that this build does
            // not yet understand.
            var unmapped = job.Layers
                .SelectMany(l => l.Vendor.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Except(Mapped, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (unmapped.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  Layer fields read but not interpreted:");
                sb.AppendLine("    " + string.Join(", ", unmapped));
            }
            sb.AppendLine();
        }

        if (job.Images.Count > 0)
        {
            sb.AppendLine("IMAGES");
            foreach (var img in job.Images)
            {
                var layer = job.LayerFor(img);
                sb.AppendLine($"  on layer {img.LayerIndex}"
                            + (layer?.Name is { Length: > 0 } n ? $" ({n})" : "")
                            + (layer is not null ? $", {layer.Kind}" : ""));

                if (img.WidthMm is { } w && img.HeightMm is { } h)
                    sb.AppendLine($"    placed size     {w:0.##} x {h:0.##} mm");
                if (img.PixelWidth is { } pw && img.PixelHeight is { } ph)
                    sb.AppendLine($"    source          {pw:N0} x {ph:N0} px");
                if (img.MmPerPixel is { } mpp)
                    sb.AppendLine($"    resolution      {1 / mpp:0.#} px/mm   {mpp * 1000:0.#} um/pixel");
                if (img.CentreXMm is { } cx && img.CentreYMm is { } cy)
                    sb.AppendLine($"    centred at      {cx:0.##}, {cy:0.##} mm");
                sb.AppendLine($"    embedded data   {(img.Data is null ? "none" : $"{img.Data.Length:N0} bytes")}");
                if (img.SourcePath is { Length: > 0 } p)
                    sb.AppendLine($"    original path   {p}");
                sb.AppendLine();
            }
        }

        if (result.Message is { Length: > 0 })
        {
            sb.AppendLine("NOTE");
            sb.AppendLine("  " + result.Message);
            sb.AppendLine();
        }

        if (job.Notes.Count > 0)
        {
            sb.AppendLine("READER NOTES");
            foreach (var n in job.Notes) sb.AppendLine("  " + n);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Vendor keys the common model already accounts for, so the report can list what
    /// is left over rather than everything.</summary>
    private static readonly string[] Mapped =
    {
        "index", "name", "priority", "speed", "maxPower", "minPower", "maxPower2", "minPower2",
        "numPasses", "interval", "dpi", "zOffset", "perPassZ", "enableAirAssist", "runBlower",
        "doOutput", "ditherMode"
    };

    private static string Describe(ReadFidelity f) => f switch
    {
        ReadFidelity.Full => "everything this build looks for was found",
        ReadFidelity.Partial => "read, with gaps",
        ReadFidelity.ContainerOnly => "container identified, contents not parsed",
        _ => "not read"
    };

    private static string Trim(string? s, int n)
        => s is null ? "-" : s.Length <= n ? s : s[..n];

    private static string Opt(int? v) => v?.ToString("N0") ?? "-";

    private static string Pct(double? v) => v is { } d ? $"{d:0.##}%" : "-";

    private static string Mm(double? v, string unit) => v is { } d ? $"{d:0.###} {unit}" : "-";
}
