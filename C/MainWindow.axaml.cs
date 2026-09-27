// MainWindow.axaml.cs
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SWBF_C_build.Services;

namespace SWBF_C_build;

public partial class MainWindow : Window
{
    private readonly DownloadService _downloadService = new DownloadService();
    private AppManifest? _manifest;

    private bool IsDebugEnabled => DebugCheckBox?.IsChecked ?? true;

    public MainWindow()
    {
        InitializeComponent();
        SetupConsoleRedirection();
        InitializeData();
    }

    private void SetupConsoleRedirection()
    {
        var redirectedOut = new ConsoleRedirector(Console.Out, LogToDebugConsole);
        var redirectedError = new ConsoleRedirector(Console.Error, LogToDebugConsole);

        Console.SetOut(redirectedOut);
        Console.SetError(redirectedError);
    }

    private void LogToDebugConsole(string message)
    {
        if (IsDebugEnabled && DebugConsoleTextBox != null)
        {
            DebugConsoleTextBox.Text += message;
            DebugConsoleTextBox.CaretIndex = DebugConsoleTextBox.Text.Length;
        }
    }

    private async void InitializeData()
    {
        AppdataPathTextBox.Text = InstallerUtils.DetectDolphinAppData();

        // Fetch live manifest from Nginx server
        string manifestUrl = "http://192.168.0.100:8881/manifest.json";
        
        try
        {
            using var client = new System.Net.Http.HttpClient();
            string json = await client.GetStringAsync(manifestUrl);
            _manifest = System.Text.Json.JsonSerializer.Deserialize<AppManifest>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (_manifest != null)
            {
                ModListBox.ItemsSource = _manifest.Mods;
                ToolListBox.ItemsSource = _manifest.Tools;
                Wii_Builds.ItemsSource = _manifest.Builds;

                // Populate the dropdown with manifest builds
                VersionComboBox.ItemsSource = _manifest.Builds;
                if (_manifest.Builds.Length > 0)
                {
                    VersionComboBox.SelectedIndex = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to load manifest from server: {ex.Message}");
        }

        ModListBox.SelectionChanged += OnModSelectionChanged;
        ToolListBox.SelectionChanged += OnToolSelectionChanged;
        Wii_Builds.SelectionChanged += OnBuildSelectionChanged;

        Console.WriteLine("[INFO] Initialization complete.");
    }

    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModListBox.SelectedItem is ModItem selectedMod)
        {
            ModDetailsText.Text = $"{selectedMod.Name}\nSize: {selectedMod.DisplaySize}\n\n{selectedMod.Description}";
            Console.WriteLine($"[DEBUG] Selected Mod: {selectedMod.Name}");
        }
    }

    private void OnToolSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ToolListBox.SelectedItem is ToolItem selectedTool)
        {
            ToolDetailsText.Text = $"{selectedTool.Name}\nSize: {selectedTool.DisplaySize}\n\n{selectedTool.Description}";
            Console.WriteLine($"[DEBUG] Selected Tool: {selectedTool.Name}");
        }
    }

    private void OnBuildSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Wii_Builds.SelectedItem is BuildItem selectedBuild)
        {
            Wii_Builds_DetailsText.Text = $"{selectedBuild.Name}\nSize: {selectedBuild.DisplaySize}\n\n{selectedBuild.Description}";
            Console.WriteLine($"[DEBUG] Selected Build: {selectedBuild.Name}");
        }
    }

    private async void OnInstallButtonClick(object sender, RoutedEventArgs e)
    {
        SetActionButtonsEnabled(false);
        StatusText.Text = "Starting installation...";

        try
        {
            string? downloadUrl = null;
            string? itemName = null;
            string targetDir = string.Empty;

            // 1. Determine item and set target directory based on Settings paths
            if ((sender == InstallButton || sender == RepairButton) && ModListBox.SelectedItem is ModItem mod)
            {
                itemName = mod.Name;
                downloadUrl = !string.IsNullOrEmpty(mod.DownloadUrl) 
                    ? mod.DownloadUrl 
                    : $"{_manifest?.BaseUrl}{mod.Url}";

                // Use Mod Directory setting -> fallback to Game Directory setting -> fallback to Downloads folder
                targetDir = !string.IsNullOrWhiteSpace(ModPathTextBox.Text) ? ModPathTextBox.Text
                    : !string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? GamePathTextBox.Text
                    : Path.Combine(AppContext.BaseDirectory, "Downloads");
            }
            else if ((sender == InstallToolButton || sender == RepairToolButton) && ToolListBox.SelectedItem is ToolItem tool)
            {
                itemName = tool.Name;
                downloadUrl = !string.IsNullOrEmpty(tool.DownloadUrl) 
                    ? tool.DownloadUrl 
                    : $"{_manifest?.BaseUrl}{tool.Url}";

                // Use AppData Directory setting -> fallback to Downloads folder
                targetDir = !string.IsNullOrWhiteSpace(AppdataPathTextBox.Text) ? AppdataPathTextBox.Text
                    : Path.Combine(AppContext.BaseDirectory, "Downloads");
            }
            else if ((sender == Install_Build_Button || sender == Repair_Build_Button) && Wii_Builds.SelectedItem is BuildItem build)
            {
                itemName = build.Name;
                downloadUrl = !string.IsNullOrEmpty(build.DownloadUrl) 
                    ? build.DownloadUrl 
                    : $"{_manifest?.BaseUrl}{build.Url}";

                // Use Game Directory setting -> fallback to Downloads folder
                targetDir = !string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? GamePathTextBox.Text
                    : Path.Combine(AppContext.BaseDirectory, "Downloads");
            }

            if (!string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(itemName))
            {
                // 2. Ensure target directory exists on disk before downloading/extracting
                Directory.CreateDirectory(targetDir);

                Console.WriteLine($"[DEBUG] Target installation directory: {targetDir}");
                Console.WriteLine($"[DEBUG] Starting download for: {itemName} ({downloadUrl})");

                StatusText.Text = $"Downloading {itemName}...";
                if (ToolStatusText != null) ToolStatusText.Text = $"Downloading {itemName}...";

                await _downloadService.DownloadAndExtractAsync(downloadUrl, targetDir, (progress, statusMessage) =>
                {
                    // Update progress bars
                    InstallProgressBar.Value = progress;
                    if (ToolProgressBar != null) ToolProgressBar.Value = progress;

                    // Update status text labels
                    StatusText.Text = statusMessage;
                    if (ToolStatusText != null) ToolStatusText.Text = statusMessage;
                });

                Console.WriteLine($"[INFO] Successfully installed {itemName}");
                StatusText.Text = $"{itemName} installed successfully!";
                if (ToolStatusText != null) ToolStatusText.Text = $"{itemName} installed successfully!";
            }
            else
            {
                StatusText.Text = "Please select an item to install.";
                Console.WriteLine("[WARN] Install clicked with no item selected.");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            Console.Error.WriteLine($"[ERROR] Installation failed: {ex.Message}");
        }
        finally
        {
            SetActionButtonsEnabled(true);
        }
    }

    private void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Design.IsDesignMode || StatusText == null) return;

        if (VersionComboBox?.SelectedItem is BuildItem selectedBuild)
        {
            StatusText.Text = $"Switched build target to {selectedBuild.Name}";
            Console.WriteLine($"[INFO] Target build changed to: {selectedBuild.Name}");
        }
    }

    private async void OnBrowseGamePathClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAndSetPathAsync(GamePathTextBox, "Select Game Directory");

    private async void OnBrowseModPathClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAndSetPathAsync(ModPathTextBox, "Select Mod Directory");

    private async void OnBrowseAppdataPathClick(object? sender, RoutedEventArgs e) =>
        await PickFolderAndSetPathAsync(AppdataPathTextBox, "Select AppData Directory");

    private async Task PickFolderAndSetPathAsync(TextBox targetTextBox, string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            targetTextBox.Text = folders[0].Path.LocalPath;
            Console.WriteLine($"[DEBUG] Directory path set for '{title}': {targetTextBox.Text}");
        }
    }

    private void SetActionButtonsEnabled(bool isEnabled)
    {
        if (InstallButton != null) InstallButton.IsEnabled = isEnabled;
        if (RepairButton != null) RepairButton.IsEnabled = isEnabled;
        if (InstallToolButton != null) InstallToolButton.IsEnabled = isEnabled;
        if (RepairToolButton != null) RepairToolButton.IsEnabled = isEnabled;
        if (Install_Build_Button != null) Install_Build_Button.IsEnabled = isEnabled;
        if (Repair_Build_Button != null) Repair_Build_Button.IsEnabled = isEnabled;
        if (UpdateButton != null) UpdateButton.IsEnabled = isEnabled;
    }
}