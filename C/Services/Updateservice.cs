// Services/UpdateService.cs
// Self-update for the manager.
//
// How an update works:
//   1. The manifest's "installer" entry has the latest version and download URLs (Windows / Linux).
//   2. The new manager is downloaded next to the running one (a plain executable, or a .zip/.rar/.7z holding it).
//   3. The running executable is renamed to "<name>.swbf3old" (allowed even while it runs, on Windows and Linux),
//      and the new one is moved into its place under the SAME file name, so desktop shortcuts keep working.
//   4. The new manager is started and this one exits.
//   5. On startup, every manager deletes leftover "*.swbf3old" files (retrying while the old process exits).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SWBF_C_build.Services;

public record UpdateInfo(string LatestVersion, string DownloadUrl);

public static class UpdateService
{
    /// <summary>This manager's version. Bump it for every release, and set the manifest's "installer.version" to match.</summary>
    public const string CurrentVersion = "8.0";

    private const string OldSuffix = ".swbf3old";
    private const string DownloadSuffix = ".swbf3new";
    private const string CleanupListName = ".swbf3_update_cleanup"; // old files in subfolders, to delete on next start

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz" };

    private static string AppDir => AppContext.BaseDirectory;

    // ───────────── Startup cleanup ─────────────

    /// <summary>True if this start finished an update (an old version was left behind to delete).</summary>
    public static bool JustUpdated { get; private set; }

    /// <summary>
    /// Deletes files left by a previous update (the old manager, replaced files, unfinished downloads).
    /// Runs in the background, retrying for up to 30 seconds while the old manager is still closing.
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        // Only the manager's own folder is searched (not Builds/, which is huge); old files the update put
        // aside in subfolders are listed in the cleanup file
        List<string> leftovers;
        string cleanupList = Path.Combine(AppDir, CleanupListName);
        try
        {
            leftovers = Directory.EnumerateFiles(AppDir, "*" + OldSuffix, SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(AppDir, "*" + DownloadSuffix, SearchOption.TopDirectoryOnly))
                .Concat(File.Exists(cleanupList)
                    ? File.ReadAllLines(cleanupList).Where(l => l.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Empty<string>())
                .Distinct()
                .ToList();
            TryDelete(cleanupList);
        }
        catch { return; }

        if (leftovers.Count == 0) return;
        JustUpdated = leftovers.Any(f => f.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase));

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 30 && leftovers.Count > 0; attempt++)
            {
                foreach (string file in leftovers.ToList())
                {
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                        leftovers.Remove(file);
                    }
                    catch { /* still in use by the closing old version; retry */ }
                }
                if (leftovers.Count > 0) await Task.Delay(1000);
            }

            foreach (string file in leftovers)
                Console.WriteLine($"[WARN] Couldn't delete {file} left over from the update. You can delete it yourself.");
        });
    }

    // ───────────── Checking ─────────────

    /// <summary>The newer version from the manifest, or null if this manager is up to date.</summary>
    public static UpdateInfo? CheckForUpdate(AppManifest manifest)
    {
        var info = manifest.Installer;
        if (info == null || string.IsNullOrWhiteSpace(info.Version) || !IsNewer(info.Version, CurrentVersion))
            return null;

        string url = OperatingSystem.IsWindows() ? info.DownloadUrl : info.DownloadUrlLinux;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"Version {info.Version} is available, but the manifest has no download for this system.");

        return new UpdateInfo(info.Version.Trim(), ResolveUrl(manifest, url));
    }

    public static bool IsNewer(string latest, string current)
    {
        var a = ParseVersion(latest);
        var b = ParseVersion(current);
        if (a != null && b != null) return a > b;
        return !string.Equals(latest.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : null;
    }

    private static string ResolveUrl(AppManifest manifest, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
        return manifest.BaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    // ───────────── Updating ─────────────

    /// <summary>Path of the running manager's executable, or an explanation of why it can't update itself.</summary>
    public static string GetRunningExecutable()
    {
        string path = Environment.ProcessPath
                      ?? throw new InvalidOperationException("couldn't find the manager's executable.");

        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the manager is running through 'dotnet' (e.g. from an IDE). Updating only works for the published program.");

        return path;
    }

    /// <summary>
    /// Downloads the update, swaps it in for the running executable and starts it.
    /// The caller should close this window right after this returns.
    /// </summary>
    /// <param name="onProgress">0–100 and a status line. Called from a background thread.</param>
    public static async Task DownloadAndApplyAsync(UpdateInfo update, Action<double, string> onProgress, CancellationToken ct = default)
    {
        string exe = GetRunningExecutable();
        string exeDir = Path.GetDirectoryName(exe)!;
        EnsureWritable(exeDir);

        string download = Path.Combine(exeDir, Path.GetFileName(exe) + DownloadSuffix);
        string? extractDir = null;

        try
        {
            await DownloadFileAsync(update.DownloadUrl, download, onProgress, ct);

            string newExe = download;
            var extraFiles = new List<(string Source, string Relative)>();

            if (IsArchive(update.DownloadUrl))
            {
                onProgress(92, "Extracting the update...");
                extractDir = Path.Combine(Path.GetTempPath(), "swbf3_update_" + Guid.NewGuid().ToString("N"));
                Extract(download, extractDir);

                newExe = FindNewExecutable(extractDir, Path.GetFileName(exe))
                         ?? throw new FileNotFoundException("the update archive doesn't contain the manager's executable.");

                // Everything else in the archive (e.g. native libraries) is replaced too, keeping its folder layout
                string root = Path.GetDirectoryName(newExe)!;
                extraFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f => f != newExe)
                    .Select(f => (f, Path.GetRelativePath(root, f)))
                    .ToList();
            }

            onProgress(96, "Installing the update...");

            foreach (var (source, relative) in extraFiles)
            {
                // Keep the user's settings
                if (relative.Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
                ReplaceFile(Path.Combine(exeDir, relative), source);
            }

            // Swap the executable last, so a failure above leaves the current manager working
            ReplaceFile(exe, newExe);
            MakeExecutable(exe);

            onProgress(100, $"Updated to version {update.LatestVersion}. Restarting...");

            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = exeDir,
                UseShellExecute = false
            });
        }
        finally
        {
            TryDelete(download);
            if (extractDir != null)
            {
                try { Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }
    }

    private static async Task DownloadFileAsync(string url, string dest, Action<double, string> onProgress, CancellationToken ct)
    {
        onProgress(0, "Downloading the update...");
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long read = 0;
        var timer = Stopwatch.StartNew();
        long lastReport = 0;
        int n;

        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;

            if (timer.ElapsedMilliseconds - lastReport > 250)
            {
                lastReport = timer.ElapsedMilliseconds;
                string done = FileSizeFormatter.FormatBytes(read);
                if (total > 0)
                {
                    int percent = (int)(read * 100 / total);
                    onProgress(read * 90.0 / total, $"Downloading the update... {percent}% ({done} of {FileSizeFormatter.FormatBytes(total)})");
                }
                else
                {
                    onProgress(45, $"Downloading the update... {done}");
                }
            }
        }

        if (total > 0 && read != total)
            throw new IOException($"the download was cut off ({read} of {total} bytes).");
    }

    /// <summary>Moves <paramref name="target"/> aside (deleted on next start) and puts <paramref name="source"/> in its place.</summary>
    private static void ReplaceFile(string target, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            string old = target + OldSuffix;
            TryDelete(old);
            // Renaming works on files in use (including the running executable); overwriting them doesn't
            File.Move(target, old);
            if (!Path.GetDirectoryName(target)!.TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(AppDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                File.AppendAllLines(Path.Combine(AppDir, CleanupListName), new[] { old });
        }

        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            // Different drive (e.g. extracted to the temp folder): copy instead
            File.Copy(source, target, overwrite: true);
        }
    }

    private static string? FindNewExecutable(string dir, string currentName)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();

        // Same name as the running manager first
        string? sameName = files.FirstOrDefault(f => Path.GetFileName(f).Equals(currentName, StringComparison.OrdinalIgnoreCase));
        if (sameName != null) return sameName;

        // Otherwise the largest program file (the single-file manager is far bigger than anything else)
        var candidates = OperatingSystem.IsWindows()
            ? files.Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            : files.Where(f => Path.GetExtension(f) == "" || Path.GetExtension(f).Equals(".x86_64", StringComparison.OrdinalIgnoreCase));

        return candidates.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    private static bool IsArchive(string url)
    {
        string path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static void Extract(string archivePath, string dest)
    {
        Directory.CreateDirectory(dest);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            entry.WriteToDirectory(dest, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                                       | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Couldn't mark the new manager as executable: {ex.Message}");
        }
    }

    private static void EnsureWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".update_write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"the manager's folder ({dir}) isn't writable, so it can't update itself ({ex.Message}). Move the manager to a folder you own, or download the new version by hand.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}