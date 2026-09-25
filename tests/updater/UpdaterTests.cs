#pragma warning disable CA1416
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DepthView.Updates;

/// <summary>See updater-tests.csproj.</summary>
static class UpdaterTests
{
    static int fails;
    static void Ok(bool c, string m) { Console.WriteLine((c ? "PASS " : "FAIL ") + m); if (!c) fails++; }

    static readonly string Root = Path.Combine(Path.GetTempPath(), "depthview-updater-tests");

    static void Script(string path, string version)
    {
        File.WriteAllText(path, $"#!/bin/sh\necho DepthView {version}\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    // Build a release zip the way make_bundle.py does: DepthView/ root, exec bit on the program.
    static string MakeZip(string version, string exeVersion, Action<ZipArchive>? extra = null)
    {
        string zip = Path.Combine(Root, $"DepthView-{version}-linux-x64.zip");
        File.Delete(zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("DepthView/DepthView");
            e.ExternalAttributes = (0x8000 | 0b111_101_101) << 16;
            using (var w = new StreamWriter(e.Open())) w.Write($"#!/bin/sh\necho DepthView {exeVersion}\n");
            var r = z.CreateEntry("DepthView/README.txt");
            r.ExternalAttributes = (0x8000 | 0b110_100_100) << 16;
            using (var w = new StreamWriter(r.Open())) w.Write("new readme " + version);
            extra?.Invoke(z);
        }
        return zip;
    }

    static string Sha(string f) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();

    static string Feed(string version, string zip, string? digest, bool sums = false)
    {
        string sumsPath = Path.Combine(Root, "SHA256SUMS.txt");
        File.WriteAllText(sumsPath, $"{Sha(zip)}  {Path.GetFileName(zip)}\n");
        string asset = $"{{\"name\":\"{Path.GetFileName(zip)}\",\"browser_download_url\":\"{zip}\",\"size\":{new FileInfo(zip).Length}"
                     + (digest is null ? "" : $",\"digest\":\"{digest}\"") + "}";
        string sumsAsset = sums ? $",{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"{sumsPath}\"}}" : "";
        return $"{{\"tag_name\":\"v{version}\",\"html_url\":\"https://example/notes\",\"assets\":[{asset}{sumsAsset}]}}";
    }

    static UpdateService.InstallLayout FreshInstall()
    {
        string dir = Path.Combine(Root, "install", "DepthView");
        if (Directory.Exists(Path.Combine(Root, "install"))) Directory.Delete(Path.Combine(Root, "install"), true);
        Directory.CreateDirectory(dir);
        Script(Path.Combine(dir, "DepthView"), "1.3.0");
        File.WriteAllText(Path.Combine(dir, "README.txt"), "old readme");
        File.WriteAllText(Path.Combine(dir, "my-notes.txt"), "the user's own file");
        return UpdateService.DetectLayout(Path.Combine(dir, "DepthView"), isMac: false, isWindows: false)!;
    }

    static async Task<string?> TryInstall(UpdateInfo info, UpdateService.InstallLayout l)
    {
        try { await UpdateService.InstallAsync(info, l); return null; }
        catch (UpdateException ex) { return ex.Message; }
    }

    static bool Untouched(UpdateService.InstallLayout l) =>
        File.ReadAllText(Path.Combine(l.Folder, "README.txt")) == "old readme"
        && File.ReadAllText(l.ExecutablePath).Contains("1.3.0")
        && Directory.GetFileSystemEntries(l.Folder).Length == 3;

    static async Task<int> Main()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Skipped on Windows: the stand-in programs are shell scripts. CI runs this on Linux and macOS.");
            return 0;
        }

        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        Directory.CreateDirectory(Root);

        // versions
        Ok(UpdateService.IsNewer("v1.4.0", "1.3.0") && !UpdateService.IsNewer("1.3", "1.3.0")
           && !UpdateService.IsNewer("1.3.0", "1.3.0") && UpdateService.IsNewer("1.10.0", "1.9.9")
           && !UpdateService.IsNewer("1.4.0-beta", "1.3.0"), "version comparison");

        // layout
        Ok(UpdateService.DetectLayout("/a/DepthView/DepthView.app/Contents/MacOS/DepthView", true, false)
             is { Folder: "/a/DepthView", MainItem: "DepthView.app" }, "mac layout from inside the .app");
        Ok(UpdateService.DetectLayout("/a/b/DepthView-1.3.0-osx-arm64", true, false) is null, "bare mac binary is not a release layout");
        Ok(UpdateService.DetectLayout("/a/b/DepthView-1.3.0-linux-x64", false, false) is null, "renamed binary is refused");

        // summarise + blockers
        var zip = MakeZip("1.4.0", "1.4.0");
        var info = UpdateService.Summarise(Feed("1.4.0", zip, "sha256:" + Sha(zip)), "1.3.0", "linux-x64");
        Ok(info.Newer && info.AssetUrl == zip && info.AssetDigest!.StartsWith("sha256:"), "summarise finds this platform's zip");
        var l = FreshInstall();
        Ok(UpdateService.InstallBlocker(info, l, singleFile: false)!.Contains("built from source"), "source build is blocked");
        Ok(UpdateService.InstallBlocker(info, l, singleFile: true) is null, "release layout can install");
        var other = UpdateService.Summarise(Feed("1.4.0", zip, null), "1.3.0", "win-arm64");
        Ok(UpdateService.InstallBlocker(other, l, true)!.Contains("no download for win-arm64"), "no asset for platform is blocked");

        // happy path via digest
        string? err = await TryInstall(info, l);
        Ok(err is null, "install succeeds: " + err);
        Ok(File.ReadAllText(l.ExecutablePath).Contains("1.4.0") && File.ReadAllText(Path.Combine(l.Folder, "README.txt")) == "new readme 1.4.0",
           "program and README replaced");
        Ok(File.Exists(Path.Combine(l.Folder, "my-notes.txt")), "user's own file left alone");
        Ok(((int)File.GetUnixFileMode(l.ExecutablePath) & 0b001_000_000) != 0, "new program is executable");
        Ok(!Directory.GetFileSystemEntries(l.Folder).Any(p => p.Contains(".depthview")), "no leftovers after install: "
           + string.Join(",", Directory.GetFileSystemEntries(l.Folder).Select(Path.GetFileName)));

        // happy path via SHA256SUMS.txt only
        l = FreshInstall();
        var viaSums = UpdateService.Summarise(Feed("1.4.0", zip, null, sums: true), "1.3.0", "linux-x64");
        Ok(UpdateService.InstallBlocker(viaSums, l, true) is null && await TryInstall(viaSums, l) is null
           && File.ReadAllText(l.ExecutablePath).Contains("1.4.0"), "install verified by SHA256SUMS.txt");

        // tampered checksum
        l = FreshInstall();
        var bad = UpdateService.Summarise(Feed("1.4.0", zip, "sha256:" + new string('0', 64)), "1.3.0", "linux-x64");
        err = await TryInstall(bad, l);
        Ok(err?.Contains("does not match") == true && Untouched(l), "wrong checksum refused, nothing changed: " + err);

        // new program reports the wrong version
        l = FreshInstall();
        var liar = MakeZip("1.4.0", "1.3.9");
        var lie = UpdateService.Summarise(Feed("1.4.0", liar, "sha256:" + Sha(liar)), "1.3.0", "linux-x64");
        err = await TryInstall(lie, l);
        Ok(err?.Contains("reports version") == true && Untouched(l), "version mismatch refused, nothing changed: " + err);

        // path traversal
        l = FreshInstall();
        var evil = MakeZip("1.4.0", "1.4.0", z => { var e = z.CreateEntry("DepthView/../../escape.txt"); using var w = new StreamWriter(e.Open()); w.Write("x"); });
        var ev = UpdateService.Summarise(Feed("1.4.0", evil, "sha256:" + Sha(evil)), "1.3.0", "linux-x64");
        err = await TryInstall(ev, l);
        Ok(err?.Contains("unexpected path") == true && Untouched(l) && !File.Exists(Path.Combine(Root, "escape.txt")), "path traversal refused: " + err);

        // wrong root folder
        l = FreshInstall();
        var stray = MakeZip("1.4.0", "1.4.0", z => z.CreateEntry("Other/file.txt"));
        var st = UpdateService.Summarise(Feed("1.4.0", stray, "sha256:" + Sha(stray)), "1.3.0", "linux-x64");
        err = await TryInstall(st, l);
        Ok(err?.Contains("unexpected file") == true && Untouched(l), "entry outside DepthView/ refused: " + err);

        // leftovers cleaned on next start
        l = FreshInstall();
        File.WriteAllText(Path.Combine(l.Folder, "DepthView.depthview-old"), "old exe");
        Directory.CreateDirectory(Path.Combine(l.Folder, ".depthview-update"));
        UpdateService.CleanupLeftovers(l);
        Ok(Untouched(l), "leftovers removed at start");

        // a folder item (the .app on a Mac) keeps files the user saved inside it
        {
            string f = Path.Combine(Root, "mac");
            if (Directory.Exists(f)) Directory.Delete(f, true);
            string macos = Path.Combine(f, "DepthView.app", "Contents", "MacOS");
            Directory.CreateDirectory(macos);
            Script(Path.Combine(macos, "DepthView"), "1.3.0");
            File.WriteAllText(Path.Combine(macos, "materials.json"), "my materials");
            var ml = new UpdateService.InstallLayout(f, "DepthView.app", "DepthView.app/Contents/MacOS/DepthView");
            string mst = Path.Combine(Root, "mac-staging");
            Directory.CreateDirectory(Path.Combine(mst, "DepthView.app", "Contents", "MacOS"));
            Script(Path.Combine(mst, "DepthView.app", "Contents", "MacOS", "DepthView"), "1.4.0");
            UpdateService.Swap(mst, ml, new[] { "DepthView.app" });
            Ok(File.ReadAllText(ml.ExecutablePath).Contains("1.4.0")
               && File.ReadAllText(Path.Combine(macos, "materials.json")) == "my materials"
               && !Directory.Exists(Path.Combine(f, "DepthView.app.depthview-old")), "app bundle swapped, saved materials carried over");
        }

        // sums parser
        Ok(UpdateService.ChecksumFor(new string('a', 64) + "  DepthView-1.4.0-win-x64.zip\n" + new string('b', 64) + " *x.zip", "x.zip") == new string('b', 64),
           "sha256sum listing parsed, binary-mode star tolerated");

        Console.WriteLine(fails == 0 ? "All updater tests passed." : $"{fails} updater test(s) FAILED.");
        return fails;
    }
}
