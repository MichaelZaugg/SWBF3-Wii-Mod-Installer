// MainWindow.axaml.cs
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SWBF_C_build.Services;

namespace SWBF_C_build;

public partial class MainWindow : Window
{
    private readonly DownloadService _downloadService = new DownloadService();
    private AppManifest? _manifest;
    private AppConfig _config; // The persistent configuration state

    private bool IsDebugEnabled => DebugCheckBox?.IsChecked ?? true;

    public MainWindow()
    {
        InitializeComponent();
        SetupConsoleRedirection();
        
        // Load the persistent config on startup
        _config = ConfigManager.Load();
        
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
        // Populate text boxes from config. Fallback to default if empty.
        GamePathTextBox.Text = _config.GameDir;
        ModPathTextBox.Text = _config.ModDir;
        AppdataPathTextBox.Text = !string.IsNullOrEmpty(_config.AppDataDir) ? _config.AppDataDir : InstallerUtils.DetectDolphinAppData();

        // Listen for user typing to save directory paths persistently
        GamePathTextBox.TextChanged += (s, e) => { _config.GameDir = GamePathTextBox.Text ?? ""; ConfigManager.Save(_config); };
        ModPathTextBox.TextChanged += (s, e) => { _config.ModDir = ModPathTextBox.Text ?? ""; ConfigManager.Save(_config); };
        AppdataPathTextBox.TextChanged += (s, e) => { _config.AppDataDir = AppdataPathTextBox.Text ?? ""; ConfigManager.Save(_config); };

        // Fetch live manifest from Nginx server
        string manifestUrl = "http://192.168.0.100:8881/manifest.json";
        
        try
        {
            using var client = new System.Net.Http.HttpClient();
            string json = await client.GetStringAsync(manifestUrl);
            
            // Note: Configure the deserializer to allow numbers to read as strings in case your JSON uses "version": 1.0 instead of "version": "1.0"
            _manifest = System.Text.Json.JsonSerializer.Deserialize<AppManifest>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            });

            if (_manifest != null)
            {
                // Compare live versions vs installed config to determine update statuses
                EvaluateInstallStatus(_manifest.Mods);
                EvaluateInstallStatus(_manifest.Tools);
                EvaluateInstallStatus(_manifest.Builds);

                ModListBox.ItemsSource = _manifest.Mods;
                ToolListBox.ItemsSource = _manifest.Tools;
                Wii_Builds.ItemsSource = _manifest.Builds;

                // Listen for install status changes to dynamically move builds between dropdown groups
                foreach (var build in _manifest.Builds)
                {
                    build.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(BuildItem.InstallStatus))
                        {
                            Dispatcher.UIThread.Post(UpdateBuildDropdown);
                        }
                    };
                }

                UpdateBuildDropdown();
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

    private void EvaluateInstallStatus(IEnumerable<InstallableItem> items)
    {
        foreach (var item in items)
        {
            if (_config.InstalledVersions.TryGetValue(item.Name, out string? installedVersion))
            {
                if (installedVersion != item.Version)
                {
                    item.InstallStatus = "Update";
                }
                else
                {
                    item.InstallStatus = "Installed";
                }
            }
            else
            {
                item.InstallStatus = "Not Installed";
            }
        }
    }

    private void MarkAsInstalledAndSave(InstallableItem item)
    {
        item.InstallStatus = "Installed";
        _config.InstalledVersions[item.Name] = item.Version;
        ConfigManager.Save(_config);
    }

    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModItem newlySelectedMod)
        {
            ModDetailsText.Text = $"{newlySelectedMod.Name}\nSize: {newlySelectedMod.DisplaySize}\n\n{newlySelectedMod.Description}";
            Console.WriteLine($"[DEBUG] Selected Mod: {newlySelectedMod.Name}");
        }
        else if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is ModItem unselectedMod)
        {
            Console.WriteLine($"[DEBUG] Deselected Mod: {unselectedMod.Name}");
            if (ModListBox.SelectedItems != null && ModListBox.SelectedItems.Count > 0 && ModListBox.SelectedItems[0] is ModItem remainingMod)
            {
                ModDetailsText.Text = $"{remainingMod.Name}\nSize: {remainingMod.DisplaySize}\n\n{remainingMod.Description}";
            }
            else
            {
                ModDetailsText.Text = "Select a mod from the list to view release notes and download details.";
            }
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
            // 1. Mods (Multi-Select Support)
            if (sender == InstallButton || sender == PlayButton)
            {
                if (ModListBox.SelectedItems == null || ModListBox.SelectedItems.Count == 0)
                {
                    StatusText.Text = "Please select at least one mod to install.";
                    return;
                }

                foreach (var selected in ModListBox.SelectedItems)
                {
                    if (selected is ModItem mod)
                    {
                        string targetDir = !string.IsNullOrWhiteSpace(ModPathTextBox.Text) ? ModPathTextBox.Text
                            : !string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? GamePathTextBox.Text
                            : Path.Combine(AppContext.BaseDirectory, "Downloads");
                            
                        string downloadUrl = !string.IsNullOrEmpty(mod.DownloadUrl) 
                            ? mod.DownloadUrl : $"{_manifest?.BaseUrl}{mod.Url}";

                        await InstallSingleItemAsync(mod.Name, downloadUrl, targetDir, InstallProgressBar, StatusText);
                        MarkAsInstalledAndSave(mod);
                    }
                }
                StatusText.Text = "All selected mods installed successfully!";
            }
            
            // 2. Tools (Single Select)
            else if (sender == InstallToolButton || sender == RepairToolButton)
            {
                if (ToolListBox.SelectedItem is ToolItem tool)
                {
                    string targetDir = !string.IsNullOrWhiteSpace(AppdataPathTextBox.Text) ? AppdataPathTextBox.Text
                        : Path.Combine(AppContext.BaseDirectory, "Downloads");
                        
                    string downloadUrl = !string.IsNullOrEmpty(tool.DownloadUrl) 
                        ? tool.DownloadUrl : $"{_manifest?.BaseUrl}{tool.Url}";

                    await InstallSingleItemAsync(tool.Name, downloadUrl, targetDir, ToolProgressBar, ToolStatusText);
                    MarkAsInstalledAndSave(tool);
                    
                    if (ToolStatusText != null) ToolStatusText.Text = $"{tool.Name} installed successfully!";
                }
                else
                {
                    if (ToolStatusText != null) ToolStatusText.Text = "Please select a tool to install.";
                }
            }
            
            // 3. Builds (Single Select)
            else if (sender == Install_Build_Button || sender == Repair_Build_Button)
            {
                if (Wii_Builds.SelectedItem is BuildItem build)
                {
                    string targetDir = !string.IsNullOrWhiteSpace(GamePathTextBox.Text) ? GamePathTextBox.Text
                        : Path.Combine(AppContext.BaseDirectory, "Downloads");
                        
                    string downloadUrl = !string.IsNullOrEmpty(build.DownloadUrl) 
                        ? build.DownloadUrl : $"{_manifest?.BaseUrl}{build.Url}";

                    await InstallSingleItemAsync(build.Name, downloadUrl, targetDir, null, StatusText);
                    MarkAsInstalledAndSave(build);
                    
                    StatusText.Text = $"{build.Name} installed successfully!";
                }
                else
                {
                    StatusText.Text = "Please select a build to install.";
                }
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

    private async Task InstallSingleItemAsync(string itemName, string downloadUrl, string targetDir, ProgressBar? progressBar, TextBlock? statusControl)
    {
        Directory.CreateDirectory(targetDir);
        Console.WriteLine($"[DEBUG] Target installation directory: {targetDir}");
        Console.WriteLine($"[DEBUG] Starting download for: {itemName} ({downloadUrl})");

        if (statusControl != null) statusControl.Text = $"Downloading {itemName}...";
        if (progressBar != null) progressBar.Value = 0;

        await _downloadService.DownloadAndExtractAsync(downloadUrl, targetDir, (progress, statusMessage) =>
        {
            if (progressBar != null) progressBar.Value = progress;
            if (statusControl != null) statusControl.Text = statusMessage;
        });

        Console.WriteLine($"[INFO] Successfully installed {itemName}");
    }

    private void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Design.IsDesignMode || StatusText == null) return;

        if (VersionComboBox?.SelectedItem is BuildGroupHeader)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var validItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
                if (validItem is BuildItem)
                {
                    VersionComboBox.SelectedItem = validItem;
                }
                else if (VersionComboBox.ItemsSource is List<object> items)
                {
                    VersionComboBox.SelectedItem = items.FirstOrDefault(x => x is BuildItem);
                }
            });
            return;
        }

        if (VersionComboBox?.SelectedItem is BuildItem selectedBuild)
        {
            StatusText.Text = $"Switched build target to {selectedBuild.Name}";
            Console.WriteLine($"[INFO] Target build changed to: {selectedBuild.Name}");
        }
    }

    private async void OnBrowseGamePathClick(object? sender, RoutedEventArgs e) => await PickFolderAndSetPathAsync(GamePathTextBox, "Select Game Directory");
    private async void OnBrowseModPathClick(object? sender, RoutedEventArgs e) => await PickFolderAndSetPathAsync(ModPathTextBox, "Select Mod Directory");
    private async void OnBrowseAppdataPathClick(object? sender, RoutedEventArgs e) => await PickFolderAndSetPathAsync(AppdataPathTextBox, "Select AppData Directory");

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
        if (PlayButton != null) PlayButton.IsEnabled = isEnabled;
        if (InstallToolButton != null) InstallToolButton.IsEnabled = isEnabled;
        if (RepairToolButton != null) RepairToolButton.IsEnabled = isEnabled;
        if (Install_Build_Button != null) Install_Build_Button.IsEnabled = isEnabled;
        if (Repair_Build_Button != null) Repair_Build_Button.IsEnabled = isEnabled;
        if (UpdateButton != null) UpdateButton.IsEnabled = isEnabled;
    }

    private void UpdateBuildDropdown()
    {
        if (_manifest == null || _manifest.Builds == null) return;

        var comboItems = new List<object>();
        var installed = new List<BuildItem>();
        var notInstalled = new List<BuildItem>();

        foreach (var build in _manifest.Builds)
        {
            if (build.InstallStatus == "Installed" || build.InstallStatus == "Update") installed.Add(build);
            else notInstalled.Add(build);
        }

        if (installed.Count > 0)
        {
            comboItems.Add(new BuildGroupHeader("=== Installed ==="));
            comboItems.AddRange(installed);
        }

        if (notInstalled.Count > 0)
        {
            comboItems.Add(new BuildGroupHeader("=== Available to Download ==="));
            comboItems.AddRange(notInstalled);
        }

        var prevSelection = VersionComboBox.SelectedItem as BuildItem;
        VersionComboBox.ItemsSource = comboItems;

        if (prevSelection != null && comboItems.Contains(prevSelection))
        {
            VersionComboBox.SelectedItem = prevSelection;
        }
        else
        {
            VersionComboBox.SelectedItem = comboItems.FirstOrDefault(x => x is BuildItem);
        }
    }
}