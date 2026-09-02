using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DepthView.Integrations.Common;

/// <summary>How completely a reader managed to understand a file.</summary>
public enum ReadFidelity
{
    /// <summary>Nothing was understood. The job, if any, is not usable.</summary>
    None,

    /// <summary>The container was identified but its contents are not yet documented. Layers
    /// and images may be missing entirely, and the caller must not treat absence as evidence.
    /// This is where WeCreat sits until the format is known.</summary>
    ContainerOnly,

    /// <summary>Some layers or images were read, but the reader knows it skipped things.</summary>
    Partial,

    /// <summary>Everything the reader looks for was found and understood.</summary>
    Full
}

public sealed class ProjectReadResult
{
    public LaserJob? Job;
    public ReadFidelity Fidelity = ReadFidelity.None;

    /// <summary>Why it failed, or what was skipped. Never empty when fidelity is below
    /// <see cref="ReadFidelity.Full"/>.</summary>
    public string? Message;

    public bool Ok => Job is not null && Fidelity > ReadFidelity.None;

    public static ProjectReadResult Failed(string why)
        => new() { Fidelity = ReadFidelity.None, Message = why };
}

/// <summary>
/// One laser project format.
///
/// Deliberately small. A reader identifies files it can handle and turns one into a
/// <see cref="LaserJob"/>; writing back is optional, because a format can be worth reading long
/// before anybody is confident enough to write it. <see cref="WeCreat.WwsProjectReader"/> is
/// exactly that case today.
/// </summary>
public interface IProjectReader
{
    /// <summary>Short name for messages: "LightBurn", "WeCreat".</summary>
    string Name { get; }

    /// <summary>Extensions handled, lowercase, with the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Whether this reader recognises the file, decided by looking inside it rather than by
    /// trusting the extension. A depth map renamed to .lbrn2 should be refused, and a project
    /// somebody saved with the wrong suffix should still be read.
    /// </summary>
    bool CanRead(string path);

    ProjectReadResult Read(string path);

    /// <summary>Whether <see cref="Write"/> is implemented at all.</summary>
    bool CanWrite { get; }

    /// <summary>
    /// Write the job back, changing only what the caller changed and preserving everything
    /// else. Throws <see cref="NotSupportedException"/> when <see cref="CanWrite"/> is false,
    /// rather than quietly producing a lossy file.
    /// </summary>
    void Write(LaserJob job, string path);
}

/// <summary>
/// The readers this build knows about, and the dispatcher over them.
/// </summary>
public static class ProjectReaders
{
    public static IReadOnlyList<IProjectReader> All { get; } = new IProjectReader[]
    {
        new LightBurn.LbrnProjectReader(),
        new WeCreat.WwsProjectReader()
    };

    /// <summary>Every extension any reader handles, for a file picker.</summary>
    public static IReadOnlyList<string> AllExtensions { get; } =
        All.SelectMany(r => r.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string SupportedPatterns =>
        string.Join(';', AllExtensions.Select(e => "*" + e));

    /// <summary>True when the path looks like a project rather than an image, by extension
    /// alone. Cheap enough for a drag-and-drop handler to call on every file.</summary>
    public static bool LooksLikeProject(string path)
        => AllExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The reader that recognises this file, or null. Content first, extension only as a
    /// tie-break, so a mislabelled file still opens and a mislabelled image still does not.
    /// </summary>
    public static IProjectReader? Find(string path)
        => All.FirstOrDefault(r => r.CanRead(path));

    public static ProjectReadResult Read(string path)
    {
        if (!File.Exists(path)) return ProjectReadResult.Failed($"No such file: {path}");

        var reader = Find(path);
        return reader is null
            ? ProjectReadResult.Failed(
                $"Not a project format this build understands ({System.IO.Path.GetExtension(path)}). "
                + $"Known: {string.Join(", ", AllExtensions)}")
            : reader.Read(path);
    }
}
