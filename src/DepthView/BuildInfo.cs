using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DepthView;

/// <summary>
/// Everything the About box reports about this build, resolved once at first use.
/// The version and build date come from assembly attributes stamped by the csproj,
/// so there is exactly one place to change a version number: DepthView.csproj.
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly Self = typeof(BuildInfo).Assembly;

    /// <summary>"1.0.0" - the &lt;Version&gt; from the csproj, without any build metadata suffix.</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>UTC date the assembly was compiled, as "2026-08-29", or "unknown" if unstamped.</summary>
    public static string BuildDate { get; } = Metadata("BuildDate") ?? "unknown";

    /// <summary>".NET 8.0.x" - the runtime actually executing, not the one it was built against.</summary>
    public static string Runtime { get; } = RuntimeInformation.FrameworkDescription;

    /// <summary>The host OS as the runtime describes it, plus the process architecture.</summary>
    public static string Host { get; } =
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    /// <summary>Avalonia's NuGet package version, stamped in by the csproj.</summary>
    public static string UiToolkit { get; } = Named("Avalonia UI", Metadata("AvaloniaVersion"));

    /// <summary>ImageSharp's NuGet package version, stamped in by the csproj.</summary>
    public static string ImageLibrary { get; } = Named("SixLabors.ImageSharp", Metadata("ImageSharpVersion"));

    /// <summary>
    /// Platforms this build is published for. These are exactly the seven runtime
    /// identifiers in publish.ps1 and publish.sh, and nothing else: an About box that
    /// names a target the publish scripts do not build is a promise the project cannot
    /// keep. If you add or drop a RID there, change it here in the same commit.
    /// Tuple is (name, detail).
    /// </summary>
    public static readonly (string Name, string Detail)[] Platforms =
    {
        ("Windows 10, 11 and Server",   "x64, x86 and Arm64 - win-x64, win-x86, win-arm64"),
        ("macOS 12 Monterey or newer",  "Intel and Apple silicon - osx-x64, osx-arm64"),
        ("Linux, glibc distributions",  "Ubuntu, Fedora, Debian, Mint - linux-x64, linux-arm64"),
    };

    /// <summary>
    /// True when running from the self-contained single-file bundle, which is the form end
    /// users get from a release. Inside a bundle Assembly.Location is documented to be empty,
    /// which is the only check that does not depend on what happens to sit next to the exe.
    /// Useful in a bug report: it separates "ran the release" from "built it myself".
    /// </summary>
    public static bool SingleFile { get; } = Self.Location.Length == 0;

    /// <summary>How this copy was obtained, in the words a bug report needs.</summary>
    public static string Packaging => SingleFile ? "self-contained single file" : "framework build from source";

    /// <summary>One line suitable for pasting into an issue report.</summary>
    public static string Signature =>
        $"DepthView {Version} ({BuildDate}) - {Runtime} on {Host} - {Packaging}";

    private static string ResolveVersion()
    {
        // InformationalVersion carries the source-revision suffix on SDK builds ("1.0.0+abc123");
        // trim it, because a user reading an About box wants the number they can compare.
        var informational = Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var v = Self.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static string? Metadata(string key) =>
        Self.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    private static string Named(string display, string? version) =>
        string.IsNullOrWhiteSpace(version) ? display : $"{display} {version}";
}
