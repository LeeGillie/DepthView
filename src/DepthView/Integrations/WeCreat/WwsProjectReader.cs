using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DepthView.Integrations.Common;

namespace DepthView.Integrations.WeCreat;

/// <summary>
/// Reader for WeCreat MakeIt .wws projects - a deliberate placeholder, not a working parser.
///
/// The schema is not public. A request for the supported way to identify the depth map in a
/// project, find the operation attached to it, read its parameters, and write validated changes
/// back has gone to WeCreat support; until an answer arrives, guessing at field names would
/// produce a reader that appears to work and is wrong, which is worse than one that plainly
/// does not.
///
/// So this does exactly two honest things. It identifies what kind of container the file is by
/// looking at its first bytes, and it reports what it found so that a real schema can be
/// slotted in behind the same interface without anything upstream changing. Everything a
/// caller can already do with a LightBurn project it will be able to do with a WeCreat one the
/// day the format is known.
///
/// The probe is also the fastest way to answer the first question any documentation will raise:
/// whether a .wws is a zip of parts, a compressed stream, JSON, XML, or something bespoke.
/// </summary>
public sealed class WwsProjectReader : IProjectReader
{
    public string Name => "WeCreat";

    public IReadOnlyList<string> Extensions { get; } = new[] { ".wws" };

    public bool CanWrite => false;

    public bool CanRead(string path)
        => string.Equals(Path.GetExtension(path), ".wws", StringComparison.OrdinalIgnoreCase);

    public ProjectReadResult Read(string path)
    {
        ContainerKind kind;
        long length;

        try
        {
            var info = new FileInfo(path);
            length = info.Length;
            kind = Probe(path);
        }
        catch (Exception ex)
        {
            return ProjectReadResult.Failed($"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }

        var job = new LaserJob
        {
            Format = "WeCreat",
            Path = path
        };

        job.Notes.Add($"Container looks like: {Describe(kind)} ({length:N0} bytes).");
        job.Notes.Add(
            "The .wws schema is not documented publicly and this build does not parse it. No "
            + "layers or images have been read - that is a gap in what is known about the "
            + "format, not a statement that the project contains none.");

        if (kind == ContainerKind.Wws2)
            job.Notes.Add(
                "The payload after the WWS2 magic carries no readable strings at all, so it is "
                + "compressed or encrypted. Support for this format depends on WeCreat "
                + "documenting it; this program will not attempt to defeat it.");

        return new ProjectReadResult
        {
            Job = job,
            Fidelity = ReadFidelity.ContainerOnly,
            Message =
                "WeCreat .wws projects are recognised but not yet parsed. The depth map inside "
                + "one can still be analysed by exporting it from MakeIt and opening the image "
                + "directly."
        };
    }

    public void Write(LaserJob job, string path)
        => throw new NotSupportedException(
            "Writing WeCreat projects is not implemented. Nothing should write a format it "
            + "cannot read.");

    // ------------------------------------------------------------------ probe

    public enum ContainerKind
    {
        Unknown,
        Zip,
        Gzip,
        Zlib,
        Json,
        Xml,
        Sqlite,
        PlainText,
        Empty,

        /// <summary>
        /// The real thing: a four-byte ASCII magic "WWS2" followed by an opaque payload.
        ///
        /// Observed by opening a MakeIt project rather than by being told. Everything after the
        /// magic is high-entropy with no readable strings anywhere in three megabytes - no
        /// field names, no XML, no JSON, no archive directory - which means the payload is
        /// compressed, encrypted, or both.
        /// </summary>
        Wws2
    }

    private static string Describe(ContainerKind k) => k switch
    {
        ContainerKind.Wws2 => "a WWS2 container - the magic is ASCII, the payload after it is not",
        ContainerKind.Zip => "a ZIP archive - likely several parts, one of which should be the image",
        ContainerKind.Gzip => "a gzip stream",
        ContainerKind.Zlib => "a raw zlib stream",
        ContainerKind.Json => "JSON text",
        ContainerKind.Xml => "XML text",
        ContainerKind.Sqlite => "a SQLite database",
        ContainerKind.PlainText => "plain text of some kind",
        ContainerKind.Empty => "an empty file",
        _ => "an unrecognised binary format"
    };

    /// <summary>
    /// Identify the container from its first bytes. Signature-based, so it says what the file
    /// is rather than what it is called.
    /// </summary>
    public static ContainerKind Probe(string path)
    {
        using var s = File.OpenRead(path);
        Span<byte> head = stackalloc byte[16];
        int n = s.Read(head);
        if (n <= 0) return ContainerKind.Empty;
        head = head[..n];

        if (n >= 4 && head[0] == 'W' && head[1] == 'W' && head[2] == 'S' && head[3] == '2')
            return ContainerKind.Wws2;

        if (n >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4)
            return ContainerKind.Zip;

        if (n >= 2 && head[0] == 0x1F && head[1] == 0x8B) return ContainerKind.Gzip;

        if (n >= 16 && Encoding.ASCII.GetString(head[..15]) == "SQLite format 3")
            return ContainerKind.Sqlite;

        // zlib: low nibble 8, and the two-byte header is a multiple of 31.
        if (n >= 2 && (head[0] & 0x0F) == 8 && ((head[0] << 8) | head[1]) % 31 == 0)
            return ContainerKind.Zlib;

        int i = 0;
        while (i < n && (head[i] == 0xEF || head[i] == 0xBB || head[i] == 0xBF
                      || head[i] == ' ' || head[i] == '\t' || head[i] == '\r' || head[i] == '\n'))
            i++;

        if (i < n)
        {
            if (head[i] == '{' || head[i] == '[') return ContainerKind.Json;
            if (head[i] == '<') return ContainerKind.Xml;
        }

        bool printable = true;
        for (int j = 0; j < n && printable; j++)
            printable = head[j] >= 0x09 && head[j] != 0x7F && (head[j] < 0x80 || head[j] >= 0xA0);

        return printable ? ContainerKind.PlainText : ContainerKind.Unknown;
    }

    /// <summary>
    /// What a parser will need once the schema is known, kept here so the shape of the work is
    /// visible rather than living in somebody's head:
    ///
    ///   - locate the image or depth-map object and get at its bytes
    ///   - find the operation or layer bound to it
    ///   - read speed, power, passes or layer count, and interval or DPI
    ///   - map those onto <see cref="CutLayer"/>, converting units on the way in
    ///   - keep the untouched remainder so a write can preserve everything else
    ///
    /// The last of those is the one to design for from the start. Rewriting a project wholesale
    /// to change one parameter is how a tool eats somebody's afternoon.
    /// </summary>
    internal static void SchemaNotes() { }
}
