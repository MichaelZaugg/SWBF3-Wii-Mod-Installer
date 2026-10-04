using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SWBF_C_build;

public static class FileSizeFormatter
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double size = bytes;

        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:0.##} {suffixes[i]}";
    }
}

public class BuildPaths
{
    public string MainDolPath { get; set; } = "";
    public string ModDirPath { get; set; } = "";
}

public class AppConfig
{
    public string BuildsDir { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Builds");

    // General mods folder (mods compatible with all builds). Defaults to a "Mods" folder next to BuildsDir.
    public string ModDir { get; set; } = "";
    public string AppDataDir { get; set; } = "";
    public bool LoadCustomTextures { get; set; } = true;
    
    public Dictionary<string, BuildPaths> InstalledBuilds { get; set; } = new(); 

    // Keys: universal mods/tools use their id; build-specific mods use "<buildTag>/<id>".
    public Dictionary<string, string> InstalledVersions { get; set; } = new();
}

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            catch { Console.Error.WriteLine("[WARN] Failed to read config.json. Creating new one."); }
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ERROR] Failed to save config: {ex.Message}"); }
    }

    public static void Delete()
    {
        if (File.Exists(ConfigPath))
        {
            File.Delete(ConfigPath);
        }
    }

    /// <summary>
    /// The general mods folder: config.ModDir if set, otherwise "Mods" in the same directory as the Builds folder.
    /// </summary>
    public static string GetGeneralModsDir(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ModDir)) return config.ModDir;

        string buildsDir = Path.GetFullPath(config.BuildsDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetDirectoryName(buildsDir) ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(parent, "Mods");
    }
}

public class InstallerInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("download_url_linux")]
    public string DownloadUrlLinux { get; set; } = "";
}

public class AppManifest
{
    public string BaseUrl { get; set; } = "";
    public string LatestInstallerVersion { get; set; } = "1.0.0";
    public InstallerInfo Installer { get; set; } = new();
    public BuildItem[] Builds { get; set; } = Array.Empty<BuildItem>();
    public DolphinItem[] Dolphin { get; set; } = Array.Empty<DolphinItem>();
    public ModItem[] Mods { get; set; } = Array.Empty<ModItem>();
    public ToolItem[] Tools { get; set; } = Array.Empty<ToolItem>();

    public void BuildFullUrls()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) return;
        
        string baseUri = BaseUrl.TrimEnd('/') + "/";

        foreach (var build in Builds)
            if (!string.IsNullOrEmpty(build.Url))
                build.DownloadUrl = baseUri + build.Url.TrimStart('/');

        foreach (var mod in Mods)
            if (!string.IsNullOrEmpty(mod.Url))
                mod.DownloadUrl = baseUri + mod.Url.TrimStart('/');

        foreach (var tool in Tools)
            if (!string.IsNullOrEmpty(tool.Url))
                tool.DownloadUrl = baseUri + tool.Url.TrimStart('/');
                
        foreach (var d in Dolphin)
            if (!string.IsNullOrEmpty(d.Url))
                d.DownloadUrl = baseUri + d.Url.TrimStart('/');
    }
}

public class InstallableItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private string _installStatus = "Not Installed";
    public string InstallStatus
    {
        get => _installStatus;
        set
        {
            if (_installStatus != value)
            {
                _installStatus = value;
                OnPropertyChanged();
            }
        }
    }
}

public class BuildGroupHeader
{
    public string Title { get; }
    public BuildGroupHeader(string title) => Title = title;
}

public class BuildItem : InstallableItem
{
    public string Id { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0;
    public string Tag { get; set; } = "";

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) ? Size : FileSizeFormatter.FormatBytes(SizeBytes);
    public override string ToString() => string.IsNullOrEmpty(DisplaySize) ? $"{Name} [{InstallStatus}]" : $"{Name} ({DisplaySize}) [{InstallStatus}]";
}

public class DolphinItem : InstallableItem
{
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = ""; 
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0; 
}

public class ModItem : InstallableItem
{
    public string Id { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string TargetSubpath { get; set; } = "";
    public bool IsSelected { get; set; } = false;
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0;

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) ? Size : FileSizeFormatter.FormatBytes(SizeBytes);
    public override string ToString() => string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
    public string Category { get; set; } = "Uncategorized";

    // "all", a single build tag ("r911"), or a comma-separated list of tags ("r911,r911-M").
    public string Build { get; set; } = "all";

    [JsonIgnore]
    public string[] BuildTags =>
        (Build ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [JsonIgnore]
    public bool IsUniversal =>
        BuildTags.Length == 0 || BuildTags.Any(t => t.Equals("all", StringComparison.OrdinalIgnoreCase));

    public bool IsCompatibleWith(string? buildTag) =>
        IsUniversal || (buildTag != null && BuildTags.Contains(buildTag, StringComparer.OrdinalIgnoreCase));

    [JsonIgnore]
    public string CompatibilityLabel => IsUniversal ? "All builds" : string.Join(", ", BuildTags);
}

public class ToolItem : InstallableItem
{
    public string Id { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0;

    // Build the tool is made for (e.g. "r911"), or empty for any build
    public string Build { get; set; } = "";

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) ? Size : FileSizeFormatter.FormatBytes(SizeBytes);
    public override string ToString() => string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
}

public class ModGroupHeader
{
    public string Title { get; }
    public ModGroupHeader(string title) => Title = title;
}