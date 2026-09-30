using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepthView.Updates;

/// <summary>
/// This build's own release notes, and whether it has just replaced an older version.
///
/// The notes are the "What's new" section of .github/RELEASE_TEMPLATE.md, embedded in the
/// program at build time. That file is what the release page leads with, and the release
/// process updates it before the version is tagged, so the build and its release page say the
/// same thing - and the notes read offline, with no second request to GitHub.
///
/// "Just updated" is decided by the version that starts, not by the one that installed it: the
/// last version to run is remembered in preferences, and a start by a newer one offers the
/// notes once. That covers every way of arriving at a new version - the in-place updater of any
/// earlier release (which knows nothing of this), --update, or a zip unpacked by hand - and a
/// first-ever run offers nothing, because there is nothing to compare with.
/// </summary>
public static class ReleaseNotes
{
    private const string ResourceName = "DepthView.ReleaseNotes.md";
    public const string TagPagePrefix = "https://github.com/" + UpdateService.Repo + "/releases/tag/v";

    /// <summary>The release page for this version.</summary>
    public static string PageUrl => TagPagePrefix + BuildInfo.Version;

    // ------------------------------------------------------------------ the notes

    private static string? _markdown;

    /// <summary>The embedded release template, or "" when the build did not carry it.</summary>
    public static string Markdown => _markdown ??= Load();

    private static string Load()
    {
        try
        {
            using var s = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(ResourceName);
            if (s is null) return "";
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// The "What's new" section: from its heading up to the next heading of the same level.
    /// What follows it in the template (updating, which zip to download, code signing) is for
    /// someone who has not installed this version yet.
    /// </summary>
    public static string WhatsNew(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.StartsWith("## What's new", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return "";
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ") || l.TrimEnd() == "---");
        if (end < 0) end = lines.Length;
        return string.Join("\n", lines[start..end]).Trim();
    }

    /// <summary>The version the notes describe - "1.7.0" from "## What's new in 1.7.0 - ..." - or null.</summary>
    public static string? NotesVersion(string markdown)
    {
        var m = Regex.Match(markdown, @"^## What's new in v?(\d+(?:\.\d+)*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The notes for this build, when they describe this build.</summary>
    public static string? ForThisBuild()
    {
        var md = Markdown;
        var v = NotesVersion(md);
        if (v is null || UpdateService.IsNewer(v, BuildInfo.Version) || UpdateService.IsNewer(BuildInfo.Version, v))
            return null;
        var section = WhatsNew(md);
        return section.Length == 0 ? null : section;
    }

    // ------------------------------------------------------------------ a small Markdown reader

    public enum BlockKind { Heading, Paragraph, Bullet }
    public sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false);
    public sealed record Block(BlockKind Kind, IReadOnlyList<Span> Spans);

    /// <summary>
    /// Just enough Markdown for the release template: headings, paragraphs, "-" bullets with
    /// indented continuation lines, **bold**, *italic*, `code` and [links](url), whose text is
    /// kept and address dropped. Anything else comes through as plain text, never lost.
    /// </summary>
    public static List<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        var para = new StringBuilder();
        BlockKind kind = BlockKind.Paragraph;

        void Flush()
        {
            if (para.Length > 0) blocks.Add(new Block(kind, Inline(para.ToString())));
            para.Clear();
            kind = BlockKind.Paragraph;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith('#'))
            {
                Flush();
                blocks.Add(new Block(BlockKind.Heading, Inline(line.TrimStart('#').Trim())));
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                Flush();
                kind = BlockKind.Bullet;
                para.Append(line[2..].Trim());
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(line.Trim());
        }
        Flush();
        return blocks;
    }

    private static readonly Regex InlineToken = new(
        @"\*\*(?<b>.+?)\*\*|(?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*])|`(?<c>[^`]+)`|\[(?<l>[^\]]+)\]\([^)]*\)",
        RegexOptions.Compiled);

    public static List<Span> Inline(string text)
    {
        var spans = new List<Span>();
        int at = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > at) spans.Add(new Span(text[at..m.Index]));
            if (m.Groups["b"].Success) spans.AddRange(Inline(m.Groups["b"].Value).Select(s => s with { Bold = true }));
            else if (m.Groups["i"].Success) spans.Add(new Span(m.Groups["i"].Value, Italic: true));
            else if (m.Groups["c"].Success) spans.Add(new Span(m.Groups["c"].Value, Code: true));
            else spans.AddRange(Inline(m.Groups["l"].Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length) spans.Add(new Span(text[at..]));
        return spans;
    }

    /// <summary>The notes as plain text, for a terminal.</summary>
    public static string PlainText(string markdown)
    {
        var sb = new StringBuilder();
        BlockKind? prev = null;
        foreach (var b in Parse(markdown))
        {
            string t = string.Concat(b.Spans.Select(s => s.Text));
            if (prev == BlockKind.Bullet && b.Kind != BlockKind.Bullet) sb.AppendLine();
            prev = b.Kind;
            if (b.Kind == BlockKind.Heading) { sb.AppendLine(t); sb.AppendLine(); continue; }
            if (b.Kind == BlockKind.Bullet)
            {
                // Hanging: the dash sits two spaces in, and the text wraps under the text.
                string w = DepthView.Analysis.Fmt.Wrap("- " + t, 74, "    ");
                sb.AppendLine("  " + w[4..]);
            }
            else
                sb.AppendLine(DepthView.Analysis.Fmt.Wrap(t, 76, "  "));
            if (b.Kind != BlockKind.Bullet) sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    // ------------------------------------------------------------------ just updated?

    /// <summary>Set by <see cref="Probe"/>: this start is the first by a newer version.</summary>
    public static bool JustUpdated { get; private set; }

    /// <summary>The version that ran before, when it is known.</summary>
    public static string? UpdatedFrom { get; private set; }

    /// <summary>
    /// Decide, before anything this session can write a preference, whether this is the first
    /// start of a newer version. A missing record with preferences or an update-check cache
    /// already on disk means an earlier release ran here - one from before this was recorded.
    /// </summary>
    public static void Probe()
    {
        try
        {
            string current = BuildInfo.Version;
            string? last = Preferences.Current.LastRunVersion;
            if (last is null)
                JustUpdated = File.Exists(Preferences.DefaultPath) || File.Exists(UpdateService.CachePath);
            else
                JustUpdated = UpdateService.IsNewer(current, last);
            UpdatedFrom = last;
        }
        catch (Exception) { JustUpdated = false; }
    }

    /// <summary>Remember this version as the last to run, so the notes are offered once.</summary>
    public static void MarkSeen()
    {
        var p = Preferences.Current;
        if (p.LastRunVersion == BuildInfo.Version) return;
        p.LastRunVersion = BuildInfo.Version;
        p.Save();
    }
}
