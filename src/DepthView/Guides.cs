using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using DepthView.Updates;

namespace DepthView;

/// <summary>
/// The user guide and the tuning guide, opened on GitHub at the edition that matches this copy.
///
/// A published copy links to the guide as it stood at its own release tag (blob/v1.9.0/...),
/// so someone who has not updated in a while never reads about controls their copy does not
/// have, and an in-place update moves them to the new edition with no files to ship. (Lee,
/// 2026-10-06: links rather than PDFs in the zip, to keep the download small; the release page
/// still offers the PDFs.) A copy built from source has no tag of its own and links to main.
/// </summary>
public static class Guides
{
    public enum Kind { User, Tuning, Finishing }

    public static string Path(Kind k) => k switch
    {
        Kind.User => "README.md",
        Kind.Finishing => "docs/research/finishing-chemistry.md",
        _ => "docs/TUNING-GUIDE.md",
    };

    /// <summary>The release tag this copy's guides live at, or main for a build from source.</summary>
    public static string Ref => BuildInfo.SingleFile ? "v" + BuildInfo.Version : "main";

    public static string Url(Kind k) => $"https://github.com/{UpdateService.Repo}/blob/{Ref}/{Path(k)}";

    /// <summary>For the About box.</summary>
    public static string Status => BuildInfo.SingleFile
        ? $"Online, the {BuildInfo.Version} edition"
        : "Online, the latest edition (build from source)";

    /// <summary>
    /// Open a guide in the browser. Returns null when it opened, else a sentence giving the
    /// address, for the caller to show.
    /// </summary>
    public static async Task<string?> OpenAsync(TopLevel? top, Kind k)
    {
        if (top is null) return null;
        string url = Url(k);
        try
        {
            if (await top.Launcher.LaunchUriAsync(new Uri(url))) return null;
        }
        catch (Exception) { /* fall through to giving the address */ }
        return "Could not open a browser. The guide is at " + url;
    }
}
