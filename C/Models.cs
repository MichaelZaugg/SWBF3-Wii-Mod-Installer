// Models.cs
using System;

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
    public string GameDir { get; set; } = "";
    public string ModDir { get; set; } = "";
    public string AppDataDir { get; set; } = "";
    public bool LoadCustomTextures { get; set; } = true;
}

public class AppManifest
{
    public string BaseUrl { get; set; } = "";
    public string LatestInstallerVersion { get; set; } = "1.0.0";
    public BuildItem[] Builds { get; set; } = Array.Empty<BuildItem>();
    public ModItem[] Mods { get; set; } = Array.Empty<ModItem>();
    public ToolItem[] Tools { get; set; } = Array.Empty<ToolItem>();
}

public class BuildItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0;

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) 
        ? Size 
        : FileSizeFormatter.FormatBytes(SizeBytes);

    public override string ToString() => 
        string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
}

public class ModItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string TargetSubpath { get; set; } = "";
    public bool IsSelected { get; set; } = false;
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0;

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) 
        ? Size 
        : FileSizeFormatter.FormatBytes(SizeBytes);

    public override string ToString() => 
        string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
}

public class ToolItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Url { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; } = 0;

    public string DisplaySize => !string.IsNullOrWhiteSpace(Size) 
        ? Size 
        : FileSizeFormatter.FormatBytes(SizeBytes);

    public override string ToString() => 
        string.IsNullOrEmpty(DisplaySize) ? Name : $"{Name} ({DisplaySize})";
}