using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SWBF_C_build.Services;

public static class InstallerUtils
{
    public static string DetectDolphinAppData()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string winPath = Path.Combine(appData, "Dolphin Emulator");
            if (Directory.Exists(winPath)) return winPath;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string flatpakPath = Path.Combine(home, ".var", "app", "org.DolphinEmu.dolphin-emu", "data", "dolphin-emu");
            if (Directory.Exists(flatpakPath)) return flatpakPath;

            string nativePath = Path.Combine(home, ".config", "dolphin-emu");
            if (Directory.Exists(nativePath)) return nativePath;
        }
        return string.Empty;
    }

    /// <summary>
    /// Finds the game's main.dol inside an extracted Wii build.
    /// Extracted Wii discs contain several partitions (DATA, UPDATE, ...), each with its own
    /// sys/main.dol. Only DATA/sys/main.dol is the actual game; UPDATE is the system update.
    /// </summary>
    public static string FindMainDol(string buildDir)
    {
        if (!Directory.Exists(buildDir)) return string.Empty;

        // Ignore downloaded mods in Builds/<tag>/mods (e.g. the updated main.dol mod)
        string modsDir = Path.Combine(buildDir, "mods") + Path.DirectorySeparatorChar;
        var candidates = Directory.GetFiles(buildDir, "main.dol", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        static bool IsInPartition(string dolPath, string partition)
        {
            string? sysDir = Path.GetDirectoryName(dolPath);
            string? partitionDir = sysDir == null ? null : Path.GetDirectoryName(sysDir);
            return sysDir != null && partitionDir != null
                && Path.GetFileName(sysDir).Equals("sys", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(partitionDir).Equals(partition, StringComparison.OrdinalIgnoreCase);
        }

        // 1. The game partition: DATA/sys/main.dol
        string? dataDol = candidates.FirstOrDefault(p => IsInPartition(p, "DATA"));
        if (dataDol != null) return dataDol;

        // 2. Any sys/main.dol that isn't in the UPDATE partition (single-partition extractions)
        string? sysDol = candidates.FirstOrDefault(p =>
            Path.GetFileName(Path.GetDirectoryName(p) ?? "").Equals("sys", StringComparison.OrdinalIgnoreCase)
            && !IsInPartition(p, "UPDATE"));
        if (sysDol != null) return sysDol;

        // 3. Last resort: anything that isn't the UPDATE partition
        return candidates.FirstOrDefault(p => !IsInPartition(p, "UPDATE")) ?? string.Empty;
    }

    public static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite = true)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(destinationDir);

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite);
        }
    }

    public static async Task RunBatchScriptAsync(string workingDirectory, string scriptName)
    {
        string scriptPath = Path.Combine(workingDirectory, scriptName);
        if (!File.Exists(scriptPath)) return;

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "wine",
            Arguments = isWindows ? $"/c \"{scriptPath}\"" : $"cmd /c \"{scriptPath}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.WaitForExitAsync();
    }
}

public static class DolphinManager
{
    public static string GetDolphinDownloadUrl(AppManifest manifest)
    {
        bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        string targetName = isLinux ? "Custom Portable Dolphin Linux" : "Custom Portable Dolphin Windows";

        var dolphinItem = manifest.Dolphin?.FirstOrDefault(d => d.Name == targetName);

        return dolphinItem?.DownloadUrl ?? string.Empty;
    }

    private static void EnsureLinuxPermissions(string directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Directory.Exists(directory))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"-R +x \"{directory}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();
            }
            catch { /* Ignore if chmod fails */ }
        }
    }

    /// <summary>
    /// Locates the Dolphin launcher (start.sh on Linux, dolphin.exe on Windows) inside the portable folder.
    /// </summary>
    private static (string exePath, string workingDir) ResolveDolphinExe(string dolphinDir)
    {
        bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        string targetExe = isLinux ? "start.sh" : "dolphin.exe";

        string exePath = Path.GetFullPath(
            Directory.Exists(dolphinDir)
                ? Directory.GetFiles(dolphinDir, targetExe, SearchOption.AllDirectories).FirstOrDefault()
                  ?? Path.Combine(dolphinDir, targetExe)
                : Path.Combine(dolphinDir, targetExe));

        string workingDir = Path.GetDirectoryName(exePath) ?? Path.GetFullPath(dolphinDir);
        EnsureLinuxPermissions(workingDir);

        return (exePath, workingDir);
    }

    public static ProcessStartInfo GetPlayCommand(string dolphinDir, string mainDolPath)
    {
        var (exePath, workingDir) = ResolveDolphinExe(dolphinDir);

        // Run start.sh / dolphin.exe directly (no login shell) so the environment
        // matches what you get when running the command by hand.
        // Note: start.sh must begin with a shebang (e.g. #!/bin/bash) to be executed directly.
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(Path.GetFullPath(mainDolPath));

        return startInfo;
    }

    /// <summary>
    /// Returns the portable Dolphin's User/Load directory (custom textures, etc.), creating it if needed.
    /// Matches the User folder case-insensitively, since Linux paths are case-sensitive and
    /// builds differ between "User" and "user".
    /// </summary>
    public static string GetUserLoadDir(string dolphinDir)
    {
        if (!Directory.Exists(dolphinDir)) return string.Empty;

        var (exePath, exeDir) = ResolveDolphinExe(dolphinDir);

        // Prefer a User folder next to the Dolphin executable, then any User folder in the install
        string? userDir =
            Directory.GetDirectories(exeDir)
                .FirstOrDefault(d => Path.GetFileName(d).Equals("User", StringComparison.OrdinalIgnoreCase))
            ?? Directory.GetDirectories(dolphinDir, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d).Equals("User", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Length)
                .FirstOrDefault();

        userDir ??= Path.Combine(exeDir, "User");

        // Reuse an existing Load folder regardless of case, otherwise create "Load"
        string loadDir =
            (Directory.Exists(userDir)
                ? Directory.GetDirectories(userDir)
                    .FirstOrDefault(d => Path.GetFileName(d).Equals("Load", StringComparison.OrdinalIgnoreCase))
                : null)
            ?? Path.Combine(userDir, "Load");

        Directory.CreateDirectory(loadDir);
        return Path.GetFullPath(loadDir);
    }

    public static ProcessStartInfo GetSettingsCommand(string dolphinDir)
    {
        var (exePath, workingDir) = ResolveDolphinExe(dolphinDir);

        return new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false
        };
    }

    /// <summary>
    /// Starts Dolphin with stdout/stderr redirected and continuously drained.
    /// Without draining, a chatty Dolphin build can fill the inherited output pipe,
    /// block on write, and freeze the game on a black loading screen.
    /// </summary>
    /// <param name="startInfo">Command from GetPlayCommand or GetSettingsCommand.</param>
    /// <param name="logOutput">Forward Dolphin's output to Console (and the debug panel).</param>
    public static Process Launch(ProcessStartInfo startInfo, bool logOutput)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        DataReceivedEventHandler handler = (_, args) =>
        {
            if (logOutput && args.Data != null)
                Console.WriteLine($"[Dolphin] {args.Data}");
        };
        proc.OutputDataReceived += handler;
        proc.ErrorDataReceived += handler;

        Console.WriteLine($"Launching: \"{startInfo.FileName}\" {string.Join(" ", startInfo.ArgumentList.Select(a => $"\"{a}\""))}");
        Console.WriteLine($"Working dir: {startInfo.WorkingDirectory}");

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Dispose once Dolphin exits and all output has been read.
        _ = Task.Run(async () =>
        {
            try { await proc.WaitForExitAsync(); }
            finally { proc.Dispose(); }
        });

        return proc;
    }
}