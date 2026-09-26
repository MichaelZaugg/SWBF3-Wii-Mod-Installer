// Services/InstallerUtils.cs
using System;
using System.Diagnostics;
using System.IO;
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