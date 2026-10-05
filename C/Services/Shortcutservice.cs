// Services/ShortcutService.cs
// Creates desktop shortcuts with the program's icon:
//   Windows → .lnk (via PowerShell + WScript.Shell)
//   Linux   → .desktop entry (marked executable/trusted so it can be launched)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SWBF_C_build.Services;

public static class ShortcutService
{
    public const string LauncherName = "SWBF III Wii Manager";
    private const string IconAsset = "avares://SWBF_C_build/Assets/SWBF3Icon.ico";

    /// <summary>Shortcut that opens this launcher. Returns the shortcut's path.</summary>
    public static string CreateLauncherShortcut()
    {
        var (target, args) = GetLauncherCommand();
        return CreateShortcut(
            LauncherName,
            target,
            args,
            AppContext.BaseDirectory,
            "Install, mod and play Star Wars Battlefront III (Wii)");
    }

    /// <summary>
    /// Shortcut that starts a build directly in Dolphin, using the same command as the Play button
    /// (from DolphinManager.GetPlayCommand). Returns the shortcut's path.
    /// </summary>
    public static string CreateGameShortcut(string buildName, ProcessStartInfo playCommand) =>
        CreateShortcut(
            $"SWBF3 - {buildName}",
            playCommand.FileName,
            playCommand.ArgumentList.ToList(),
            playCommand.WorkingDirectory,
            $"Play Star Wars Battlefront III ({buildName}) in Dolphin");

    // ───────────── Launcher command ─────────────

    private static (string Target, List<string> Args) GetLauncherCommand()
    {
        string processPath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("couldn't find the launcher's executable");

        // Started as "dotnet SWBF_C_build.dll" (e.g. from an IDE): the shortcut runs it the same way.
        // (Built from AppContext.BaseDirectory rather than Assembly.Location, which is empty in single-file builds.)
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            string dll = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!string.IsNullOrEmpty(assemblyName) && File.Exists(dll))
                return (processPath, new List<string> { dll });
        }

        return (processPath, new List<string>());
    }

    // ───────────── Shortcut creation ─────────────

    private static string CreateShortcut(string name, string target, IList<string> args, string workingDir, string comment)
    {
        string desktop = GetDesktopDir();
        string fileName = SafeFileName(name);
        string icon = EnsureIconFile();

        if (OperatingSystem.IsWindows())
        {
            string lnk = Path.Combine(desktop, fileName + ".lnk");
            string arguments = string.Join(" ", args.Select(QuoteWindowsArg));
            CreateWindowsShortcut(lnk, target, arguments, workingDir, icon, comment);
            return lnk;
        }

        if (OperatingSystem.IsLinux())
        {
            string entry = Path.Combine(desktop, fileName + ".desktop");
            CreateLinuxDesktopEntry(entry, name, target, args, workingDir, icon, comment);
            return entry;
        }

        throw new PlatformNotSupportedException("desktop shortcuts are only supported on Windows and Linux");
    }

    private static void CreateWindowsShortcut(string lnkPath, string target, string arguments,
        string workingDir, string iconPath, string description)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'"; // PowerShell single-quoted string

        string script =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Q(lnkPath)}); " +
            $"$s.TargetPath = {Q(target)}; " +
            $"$s.Arguments = {Q(arguments)}; " +
            $"$s.WorkingDirectory = {Q(workingDir)}; " +
            $"$s.IconLocation = {Q(iconPath + ",0")}; " +
            $"$s.Description = {Q(description)}; " +
            "$s.Save()";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("couldn't start PowerShell");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(lnkPath))
            throw new InvalidOperationException($"PowerShell couldn't create the shortcut: {error.Trim()}");
    }

    private static void CreateLinuxDesktopEntry(string path, string name, string target, IList<string> args,
        string workingDir, string iconPath, string comment)
    {
        string exec = string.Join(" ", new[] { target }.Concat(args).Select(DesktopExecArg));

        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Version=1.0");
        sb.AppendLine("Type=Application");
        sb.AppendLine($"Name={DesktopValue(name)}");
        sb.AppendLine($"Comment={DesktopValue(comment)}");
        sb.AppendLine($"Exec={exec}");
        sb.AppendLine($"Path={DesktopValue(workingDir)}");
        sb.AppendLine($"Icon={DesktopValue(iconPath)}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("Categories=Game;");
        File.WriteAllText(path, sb.ToString());

        // Desktop environments only launch executable (and, on GNOME, "trusted") .desktop files
        RunQuietly("chmod", "+x", path);
        RunQuietly("gio", "set", path, "metadata::trusted", "true");
    }

    // ───────────── Icon ─────────────

    /// <summary>
    /// Writes the program's icon next to the program (or to local app data if that folder isn't writable).
    /// Windows uses the .ico; Linux gets a PNG converted from it, since not every desktop shows .ico files.
    /// </summary>
    private static string EnsureIconFile()
    {
        string dir = IconDir();
        string ico = Path.Combine(dir, "SWBF3Icon.ico");

        if (!File.Exists(ico))
        {
            using var asset = AssetLoader.Open(new Uri(IconAsset));
            using var file = File.Create(ico);
            asset.CopyTo(file);
        }

        if (OperatingSystem.IsWindows()) return ico;

        string png = Path.Combine(dir, "SWBF3Icon.png");
        if (!File.Exists(png))
        {
            try
            {
                using var asset = AssetLoader.Open(new Uri(IconAsset));
                using var bitmap = new Bitmap(asset);
                bitmap.Save(png);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Couldn't convert the icon to PNG ({ex.Message}); using the .ico instead.");
                return ico;
            }
        }
        return png;
    }

    private static string IconDir()
    {
        string baseDir = AppContext.BaseDirectory;
        try
        {
            string probe = Path.Combine(baseDir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return baseDir;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWBF3Manager");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    // ───────────── Helpers ─────────────

    private static string GetDesktopDir()
    {
        // On Linux this honours XDG_DESKTOP_DIR (e.g. a localized "Schreibtisch" folder)
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");

        Directory.CreateDirectory(desktop);
        return desktop;
    }

    private static string SafeFileName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        string safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(safe) ? "SWBF3" : safe;
    }

    private static string QuoteWindowsArg(string arg) =>
        arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>Quotes one Exec argument per the Desktop Entry spec.</summary>
    private static string DesktopExecArg(string arg)
    {
        const string reserved = " \t\n\"'\\><~|&;$*?#()`";
        string quoted;

        if (arg.Length > 0 && arg.IndexOfAny(reserved.ToCharArray()) < 0)
        {
            quoted = arg;
        }
        else
        {
            var sb = new StringBuilder("\"");
            foreach (char c in arg)
            {
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '`' => "\\`",
                    '$' => "\\$",
                    '\\' => "\\\\\\\\", // string-level escape + quoting escape
                    _ => c.ToString()
                });
            }
            sb.Append('"');
            quoted = sb.ToString();
        }

        return quoted.Replace("%", "%%"); // % starts field codes in Exec
    }

    private static string DesktopValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", " ");

    private static void RunQuietly(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch
        {
            // Optional step (e.g. gio isn't installed outside GNOME)
        }
    }
}