using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Updates;

/// <summary>
/// Checks GitHub for a newer release and, when asked, installs it in place.
///
/// The same design as LUOM What's New's updater, carried over to a .NET single-file program.
///
/// The check reads one small JSON document from GitHub's public API - the latest release of
/// the project - and compares its tag with this build's version. Nothing about the user, their
/// files or their machine is sent beyond the request itself. The answer is cached next to the
/// preferences so the automatic check runs at most once every <see cref="CheckInterval"/>.
///
/// Installing only works for a copy unpacked from a release zip: one DepthView folder holding
/// the program (DepthView.exe, DepthView, or DepthView.app on a Mac) plus README and licence.
/// It downloads the zip for this platform, verifies it against the SHA-256 GitHub publishes for
/// the asset (or the release's SHA256SUMS.txt), unpacks it beside the running copy, runs the new
/// program once to confirm it starts and reports the expected version, and only then swaps the
/// files in - the program last, and each old item renamed aside rather than deleted, so a
/// failure part-way puts everything back. Anything unexpected stops it before a file is replaced.
///
/// Renaming aside is what makes this work on Windows, where a running .exe cannot be overwritten
/// but can be renamed. The renamed leftovers are removed the next time the program starts.
/// </summary>
public static class UpdateService
{
    public const string Repo = "LeeGillie/DepthView";
    public const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    /// <summary>The folder name every release zip unpacks to. The updater refuses anything else.</summary>
    public const string BundleFolder = "DepthView";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(20);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A release zip is ~40-70 MB; anything far beyond that is not ours.</summary>
    public const long MaxDownload = 400L * 1024 * 1024;
    private const long MaxUnpacked = 1024L * 1024 * 1024;

    private const string CacheFileName = "update-check.json";
    private const string OldSuffix = ".depthview-old";
    private const string StagingName = ".depthview-update";

    /// <summary>
    /// Developer hook: a local release JSON file (or URL) used instead of GitHub's API, whose
    /// asset URLs may be local paths. This is how the whole install path gets exercised before
    /// a release exists. Set from --update-feed; never set in normal use.
    /// </summary>
    public static string? FeedOverride { get; set; }

    /// <summary>Raised on the UI thread by whoever ran a check, so the main window can show the bar.</summary>
    public static event Action<UpdateInfo, bool>? Checked;

    public static void Announce(UpdateInfo info, bool fromUser) => Checked?.Invoke(info, fromUser);

    // ------------------------------------------------------------------ versions

    /// <summary>"v1.2.0" -> [1, 2]; null for anything that is not a plain version.</summary>
    public static int[]? ParseVersion(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        if (t.Length == 0) return null;

        var parts = new List<int>();
        foreach (var p in t.Split('.'))
        {
            if (p.Length == 0 || !p.All(char.IsAsciiDigit) || !int.TryParse(p, out int n)) return null;
            parts.Add(n);
        }
        while (parts.Count > 1 && parts[^1] == 0) parts.RemoveAt(parts.Count - 1);   // 1.2 == 1.2.0
        return parts.ToArray();
    }

    public static bool IsNewer(string? candidate, string current)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(current);
        if (a is null || b is null) return false;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    // ------------------------------------------------------------------ where this copy lives

    /// <summary>The release identifier this process should update to, e.g. "win-x64".</summary>
    public static string? CurrentRid()
    {
        string? os = OperatingSystem.IsWindows() ? "win"
                   : OperatingSystem.IsMacOS() ? "osx"
                   : OperatingSystem.IsLinux() ? "linux" : null;
        string? arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }

    public static string AssetName(string version, string rid) => $"DepthView-{version}-{rid}.zip";

    /// <summary>
    /// Where a release was unpacked. <see cref="Folder"/> holds README.txt and the program;
    /// <see cref="MainItem"/> is the top-level entry that is the program (a file, or the .app
    /// folder on a Mac); <see cref="ExecutableRelative"/> is the binary inside it.
    /// </summary>
    public sealed record InstallLayout(string Folder, string MainItem, string ExecutableRelative)
    {
        public string MainPath => Path.Combine(Folder, MainItem);
        public string ExecutablePath => Path.Combine(Folder, ExecutableRelative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Recognise the release layout from the running program's path, or null when this copy is
    /// not laid out the way a release unpacks (renamed, or a bare binary from an old release).
    /// </summary>
    public static InstallLayout? DetectLayout(string? processPath, bool? isMac = null, bool? isWindows = null)
    {
        if (string.IsNullOrEmpty(processPath)) return null;
        bool mac = isMac ?? OperatingSystem.IsMacOS();
        bool win = isWindows ?? OperatingSystem.IsWindows();

        var full = Path.GetFullPath(processPath);
        var name = Path.GetFileName(full);
        var dir = Path.GetDirectoryName(full);
        if (dir is null) return null;

        if (mac)
        {
            // .../Folder/DepthView.app/Contents/MacOS/DepthView
            var macos = new DirectoryInfo(dir);
            var contents = macos.Parent;
            var app = contents?.Parent;
            if (name != "DepthView" || macos.Name != "MacOS" || contents?.Name != "Contents"
                || app?.Name != "DepthView.app" || app.Parent is null) return null;
            return new InstallLayout(app.Parent.FullName, "DepthView.app", "DepthView.app/Contents/MacOS/DepthView");
        }

        string expected = win ? "DepthView.exe" : "DepthView";
        if (!string.Equals(name, expected, win ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return new InstallLayout(dir, expected, expected);
    }

    public static InstallLayout? CurrentLayout() => DetectLayout(Environment.ProcessPath);

    // ------------------------------------------------------------------ checking

    private static string CachePath =>
        Path.Combine(Path.GetDirectoryName(Preferences.DefaultPath) ?? ".", CacheFileName);

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DepthView/{BuildInfo.Version} (+https://github.com/{Repo})");
        return http;
    }

    /// <summary>
    /// Latest-release information, from the cache when it is recent enough. Never throws: a
    /// failed check comes back with <see cref="UpdateInfo.Error"/> set, so an offline computer
    /// only means no bar.
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        string current = BuildInfo.Version;
        UpdateInfo? info = null;

        if (!force && FeedOverride is null)
        {
            try
            {
                var cached = JsonSerializer.Deserialize<UpdateInfo>(await File.ReadAllTextAsync(CachePath, ct));
                var age = DateTime.UtcNow - (cached?.CheckedAtUtc ?? DateTime.MinValue);
                // A cache written by another version says nothing about this one.
                if (cached is not null && cached.Current == current && age >= TimeSpan.Zero && age < CheckInterval)
                    info = cached;
            }
            catch (Exception) { /* no cache, or unreadable: ask again */ }
        }

        if (info is null)
        {
            try
            {
                string json;
                if (FeedOverride is { } feed && !IsHttp(feed))
                    json = await File.ReadAllTextAsync(feed, ct);
                else
                {
                    using var http = NewClient(ApiTimeout);
                    using var req = new HttpRequestMessage(HttpMethod.Get, FeedOverride ?? LatestApi);
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var resp = await http.SendAsync(req, ct);
                    resp.EnsureSuccessStatusCode();
                    json = await resp.Content.ReadAsStringAsync(ct);
                }

                info = Summarise(json, current);
            }
            catch (Exception)
            {
                return new UpdateInfo { Current = current, Error = "Could not reach GitHub to check for updates." };
            }

            if (FeedOverride is null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    await File.WriteAllTextAsync(CachePath,
                        JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), ct);
                }
                catch (Exception) { /* a check that cannot be cached is still a check */ }
            }
        }

        info.Newer = IsNewer(info.Latest, current);
        info.InstallBlocker = InstallBlocker(info, CurrentLayout(), BuildInfo.SingleFile);
        info.CanInstall = info.InstallBlocker is null;
        return info;
    }

    /// <summary>The parts of a GitHub release the bar and the installer need.</summary>
    public static UpdateInfo Summarise(string releaseJson, string current, string? rid = null)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        string tag = Str(root, "tag_name") ?? "";
        string version = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        rid ??= CurrentRid();

        var info = new UpdateInfo
        {
            Current = current,
            Latest = version,
            NotesUrl = Str(root, "html_url") ?? ReleasesPage,
            PublishedAt = Str(root, "published_at"),
            CheckedAtUtc = DateTime.UtcNow,
            Rid = rid,
        };

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string? want = rid is null ? null : AssetName(version, rid);
            foreach (var a in assets.EnumerateArray())
            {
                string? name = Str(a, "name");
                if (name == "SHA256SUMS.txt") info.SumsUrl = Str(a, "browser_download_url");
                if (want is null || name != want) continue;
                info.AssetName = name;
                info.AssetUrl = Str(a, "browser_download_url");
                info.AssetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : null;
                info.AssetDigest = Str(a, "digest");
            }
        }

        info.Newer = IsNewer(version, current);
        return info;
    }

    /// <summary>Why "Update now" cannot work for this copy, or null when it can.</summary>
    public static string? InstallBlocker(UpdateInfo info, InstallLayout? layout, bool singleFile)
    {
        if (!singleFile)
            return "This copy was built from source - update it with git and rebuild, or download the release.";
        if (info.Rid is null)
            return "No DepthView release is built for this kind of computer.";
        if (layout is null)
            return OperatingSystem.IsMacOS()
                ? "This copy is not running from DepthView.app, so it cannot replace itself."
                : "This copy's program file has been renamed or moved out of its folder, so it cannot replace itself.";
        if (info.AssetUrl is null)
            return $"This release has no download for {info.Rid}.";
        if (!HasSha256(info.AssetDigest) && info.SumsUrl is null)
            return "This release has no checksum to verify the download against.";
        if (!CanWrite(layout.Folder))
            return $"DepthView's folder cannot be written to ({layout.Folder}).";
        return null;
    }

    private static bool HasSha256(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64;

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".depthview-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ installing

    /// <summary>
    /// Download, verify, unpack and swap in the release described by <paramref name="info"/>.
    /// Throws <see cref="UpdateException"/> with a readable reason, and nothing replaced, if any
    /// check fails. Does not restart anything; see <see cref="RestartStartInfo"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> InstallAsync(
        UpdateInfo info, InstallLayout layout, IProgress<(string Stage, double? Fraction)>? progress = null,
        CancellationToken ct = default)
    {
        if (!info.Newer) throw new UpdateException("This is already the latest version.");
        if (info.AssetUrl is null || info.Latest is null) throw new UpdateException("This release has no download for this computer.");

        progress?.Report(("Downloading", 0));
        string zipPath = Path.Combine(layout.Folder, StagingName + ".zip");
        string staging = Path.Combine(layout.Folder, StagingName);
        try
        {
            await DownloadAsync(info.AssetUrl, zipPath, info.AssetSize, progress, ct);

            progress?.Report(("Checking the download", null));
            await VerifyChecksumAsync(zipPath, info, ct);

            progress?.Report(("Unpacking", null));
            var items = Unpack(zipPath, staging, layout);

            progress?.Report(("Starting the new version to check it", null));
            string newExe = Path.Combine(staging, layout.ExecutableRelative.Replace('/', Path.DirectorySeparatorChar));
            string reported = await ReportedVersionAsync(newExe, ct);
            var got = ParseVersion(reported);
            var want = ParseVersion(info.Latest);
            if (got is null || want is null || !got.SequenceEqual(want))
                throw new UpdateException($"The new program reports version \"{reported}\", expected {info.Latest}.");

            progress?.Report(("Installing", null));
            var done = Swap(staging, layout, items);
            TryDelete(staging);
            return done;
        }
        finally
        {
            TryDelete(zipPath);
            TryDelete(staging);
        }
    }

    private static async Task DownloadAsync(string url, string target, long? size,
        IProgress<(string, double?)>? progress, CancellationToken ct)
    {
        if (size > MaxDownload) throw new UpdateException($"The download is unexpectedly large ({size:N0} bytes).");
        try
        {
            Stream src;
            HttpClient? http = null;
            HttpResponseMessage? resp = null;
            if (IsHttp(url))
            {
                http = NewClient(DownloadTimeout);
                resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                size ??= resp.Content.Headers.ContentLength;
                src = await resp.Content.ReadAsStreamAsync(ct);
            }
            else
            {
                string path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
                src = File.OpenRead(path);
                size ??= src.Length;
            }

            using (http)
            using (resp)
            await using (src)
            await using (var dst = File.Create(target))
            {
                var buf = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxDownload) throw new UpdateException("The download is unexpectedly large.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    if (size > 0) progress?.Report(("Downloading", Math.Min(1.0, total / (double)size)));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            throw new UpdateException("The download failed: " + ex.Message);
        }
    }

    private static async Task VerifyChecksumAsync(string zipPath, UpdateInfo info, CancellationToken ct)
    {
        string? want = HasSha256(info.AssetDigest) ? info.AssetDigest![7..].ToLowerInvariant() : null;

        if (want is null && info.SumsUrl is not null)
        {
            try
            {
                string sums;
                if (IsHttp(info.SumsUrl))
                {
                    using var http = NewClient(ApiTimeout);
                    sums = await http.GetStringAsync(info.SumsUrl, ct);
                }
                else sums = await File.ReadAllTextAsync(info.SumsUrl, ct);

                want = ChecksumFor(sums, info.AssetName ?? "");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                throw new UpdateException("Could not fetch the release's checksums: " + ex.Message);
            }
        }

        if (want is null) throw new UpdateException("There is no checksum for this download, so it was not used.");

        string got;
        await using (var fs = File.OpenRead(zipPath))
            got = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (got != want) throw new UpdateException("The download does not match the published checksum, so it was not used.");
    }

    /// <summary>The hex digest for <paramref name="name"/> in a sha256sum listing, or null.</summary>
    public static string? ChecksumFor(string sums, string name)
    {
        foreach (var line in sums.Split('\n'))
        {
            var t = line.Trim();
            int sp = t.IndexOf(' ');
            if (sp != 64) continue;
            var file = t[(sp + 1)..].TrimStart(' ', '*');
            if (file == name) return t[..64].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Unpack the release zip into <paramref name="staging"/>, refusing anything that is not a
    /// single DepthView/ folder of ordinary files. Returns the top-level items it holds.
    /// </summary>
    public static List<string> Unpack(string zipPath, string staging, InstallLayout layout)
    {
        TryDelete(staging);
        Directory.CreateDirectory(staging);
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        string prefix = BundleFolder + "/";
        var items = new HashSet<string>(StringComparer.Ordinal);
        long unpacked = 0;

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new UpdateException("The download is not a valid zip file."); }

        using (zip)
        {
            foreach (var e in zip.Entries)
            {
                string name = e.FullName;
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':'))
                    throw new UpdateException("The download contains an unexpected file: " + name);
                string rel = name[prefix.Length..];
                if (rel.Length == 0) continue;
                var segments = rel.TrimEnd('/').Split('/');
                if (segments.Any(s => s is "" or "." or ".."))
                    throw new UpdateException("The download contains an unexpected path: " + name);

                int mode = (int)((uint)e.ExternalAttributes >> 16);
                if ((mode & 0xF000) == 0xA000)
                    throw new UpdateException("The download contains a link, which the updater does not accept: " + name);

                string dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.Ordinal))
                    throw new UpdateException("The download contains an unexpected path: " + name);

                items.Add(segments[0]);
                if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }

                unpacked += e.Length;
                if (unpacked > MaxUnpacked) throw new UpdateException("The download unpacks to an unexpected size.");

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);

                if (!OperatingSystem.IsWindows() && (mode & 0x1FF) != 0)
                    File.SetUnixFileMode(dest, (UnixFileMode)(mode & 0x1FF));
            }
        }

        string exe = Path.Combine(staging, layout.ExecutableRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(exe))
            throw new UpdateException($"The download does not contain {layout.ExecutableRelative}.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        return items.ToList();
    }

    /// <summary>Run a program once with --version and return what it says.</summary>
    public static async Task<string> ReportedVersionAsync(string exe, CancellationToken ct)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p = Process.Start(psi) ?? throw new UpdateException("The new version could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var read = p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            string text = (await read).Trim();
            int sp = text.LastIndexOf(' ');
            return sp >= 0 ? text[(sp + 1)..] : text;
        }
        catch (OperationCanceledException)
        {
            throw new UpdateException("The new version did not answer when started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new UpdateException("The new version could not be started: " + ex.Message);
        }
        finally
        {
            if (p is not null)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Move the unpacked items into place. Each existing item is renamed aside first rather
    /// than overwritten, the program last; if any move fails, everything already moved is put
    /// back. Items in the folder that the release does not contain - the user's own - are left
    /// alone.
    /// </summary>
    public static List<string> Swap(string staging, InstallLayout layout, IEnumerable<string> items)
    {
        var order = items.OrderBy(i => string.Equals(i, layout.MainItem, StringComparison.OrdinalIgnoreCase)).ThenBy(i => i).ToList();
        var moved = new List<(string Target, string? Old)>();

        try
        {
            foreach (var item in order)
            {
                string src = Path.Combine(staging, item);
                string target = Path.Combine(layout.Folder, item);
                string? old = null;

                if (Exists(target))
                {
                    // A folder item - DepthView.app on a Mac - is replaced whole, so anything the
                    // user saved inside it has to come along. materials.json lives next to the
                    // executable, which on a Mac is inside the bundle.
                    if (Directory.Exists(target) && Directory.Exists(src)) CarryOver(target, src);

                    old = target + OldSuffix;
                    if (Exists(old)) TryDelete(old);
                    if (Exists(old)) old = target + OldSuffix + "-" + Guid.NewGuid().ToString("N")[..8];
                    Move(target, old);
                }

                try { Move(src, target); }
                catch
                {
                    if (old is not null) Move(old, target);
                    throw;
                }
                moved.Add((target, old));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                var (target, old) = moved[i];
                try
                {
                    TryDelete(target);
                    if (old is not null) Move(old, target);
                }
                catch (Exception) { /* best effort; the message below says what failed */ }
            }
            throw new UpdateException("Could not put the new files in place, so the old ones were kept: " + ex.Message);
        }

        // The old program is still running on Windows and cannot be deleted yet; the next start does it.
        foreach (var (_, old) in moved)
            if (old is not null) TryDelete(old);

        return order;
    }

    /// <summary>Copy files that exist under <paramref name="from"/> but not under <paramref name="to"/>.</summary>
    private static void CarryOver(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            string dest = Path.Combine(to, rel);
            if (File.Exists(dest) || Directory.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    /// <summary>
    /// Remove what an earlier update left behind: renamed-aside items the old process was still
    /// holding, and any half-finished staging. Safe to call on every start; a no-op otherwise.
    /// </summary>
    public static void CleanupLeftovers(InstallLayout? layout = null)
    {
        layout ??= CurrentLayout();
        if (layout is null || !Directory.Exists(layout.Folder)) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(layout.Folder))
            {
                string name = Path.GetFileName(path);
                if (name.Contains(OldSuffix, StringComparison.Ordinal) || name.StartsWith(StagingName, StringComparison.Ordinal))
                    TryDelete(path);
            }
        }
        catch (Exception) { /* nothing here is worth failing a start over */ }
    }

    /// <summary>How to start the new version with the same arguments.</summary>
    public static ProcessStartInfo RestartStartInfo(InstallLayout layout, IEnumerable<string> args)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsMacOS())
        {
            psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(layout.MainPath);
            var list = args.ToList();
            if (list.Count > 0)
            {
                psi.ArgumentList.Add("--args");
                foreach (var a in list) psi.ArgumentList.Add(a);
            }
        }
        else
        {
            psi = new ProcessStartInfo(layout.ExecutablePath) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.WorkingDirectory = Environment.CurrentDirectory;
        return psi;
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsHttp(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception) { /* in use or already gone */ }
    }
}

/// <summary>What a check found. Also the shape of the cache file.</summary>
public sealed class UpdateInfo
{
    public string Current { get; set; } = "";
    public string? Latest { get; set; }
    public string NotesUrl { get; set; } = UpdateService.ReleasesPage;
    public string? PublishedAt { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public string? Rid { get; set; }
    public string? AssetName { get; set; }
    public string? AssetUrl { get; set; }
    public long? AssetSize { get; set; }
    public string? AssetDigest { get; set; }
    public string? SumsUrl { get; set; }

    [JsonIgnore] public bool Newer { get; set; }
    [JsonIgnore] public string? Error { get; set; }
    [JsonIgnore] public bool CanInstall { get; set; }
    [JsonIgnore] public string? InstallBlocker { get; set; }
}

/// <summary>A readable reason an update was not installed. Nothing was replaced.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
}
