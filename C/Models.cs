// Models.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

public class AppConfig
{
    public string BuildsDir { get; set; } = ""; //[cite: 3]
    public string ModDir { get; set; } = ""; //[cite: 3]
    public string AppDataDir { get; set; } = ""; //[cite: 3]
    public bool LoadCustomTextures { get; set; } = true; //[cite: 3]
    public Dictionary<string, string> InstalledVersions { get; set; } = new(); //[cite: 3]
}

public static class ConfigManager
{
    // Saves config.json directly next to the executable
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
    public string BaseUrl { get; set; } = ""; //[cite: 3]
    public string LatestInstallerVersion { get; set; } = "1.0.0"; //[cite: 3]
    public InstallerInfo Installer { get; set; } = new(); 
    public BuildItem[] Builds { get; set; } = Array.Empty<BuildItem>(); //[cite: 3]
    public ModItem[] Mods { get; set; } = Array.Empty<ModItem>(); //[cite: 3]
    public ToolItem[] Tools { get; set; } = Array.Empty<ToolItem>(); //[cite: 3]
}

// Base class to handle UI updates for installation status
public class InstallableItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public string Name { get; set; } = "";     // <-- Added Name here so all items share it
    public string Version { get; set; } = "1.0"; // Shared Version property

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

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) ? Size : FileSizeFormatter.FormatBytes(SizeBytes);
    public override string ToString() => string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
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
    public string Build { get; set; } = "all";
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

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) ? Size : FileSizeFormatter.FormatBytes(SizeBytes);
    public override string ToString() => string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
}

public class ModGroupHeader
{
    public string Title { get; }
    public ModGroupHeader(string title) => Title = title;
}