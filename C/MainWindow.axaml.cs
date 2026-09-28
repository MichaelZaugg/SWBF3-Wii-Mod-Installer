using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SWBF_C_build.Services;

namespace SWBF_C_build;

public partial class MainWindow : Window
{
    private readonly DownloadService _downloadService = new DownloadService();
    private AppManifest? _manifest;
    private AppConfig _config; 
    private const string CurrentAppVersion = "8.0"; 

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
        BuildsPathTextBox.Text = _config.BuildsDir;
        ModPathTextBox.Text = _config.ModDir;
        AppdataPathTextBox.Text = !string.IsNullOrEmpty(_config.AppDataDir) ? _config.AppDataDir : InstallerUtils.DetectDolphinAppData();

        BuildsPathTextBox.TextChanged += (s, e) => { _config.BuildsDir = BuildsPathTextBox.Text ?? ""; ConfigManager.Save(_config); };
        ModPathTextBox.TextChanged += (s, e) => { _config.ModDir = ModPathTextBox.Text ?? ""; ConfigManager.Save(_config); };
        AppdataPathTextBox.TextChanged += (s, e) => { _config.AppDataDir = AppdataPathTextBox.Text ?? ""; ConfigManager.Save(_config); };

        string manifestUrl = "http://192.168.0.100:8881/manifest.json";
        
        try
        {
            using var client = new HttpClient();
            string json = await client.GetStringAsync(manifestUrl);
            
            _manifest = System.Text.Json.JsonSerializer.Deserialize<AppManifest>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            });

            if (_manifest != null)
            {
                await CheckForUpdates();

                EvaluateInstallStatus(_manifest.Mods);
                EvaluateInstallStatus(_manifest.Tools);
                EvaluateInstallStatus(_manifest.Builds);

                UpdateModList();
                ToolListBox.ItemsSource = _manifest.Tools;
                Wii_Builds.ItemsSource = _manifest.Builds;

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

    private async Task CheckForUpdates()
    {
        if (_manifest?.Installer != null && _manifest.Installer.Version != CurrentAppVersion)
        {
            StatusText.Text = $"Update available: v{_manifest.Installer.Version}. Downloading update...";
            string downloadUrl = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) 
                ? _manifest.Installer.DownloadUrlLinux 
                : _manifest.Installer.DownloadUrl;
            
            string exeName = AppDomain.CurrentDomain.FriendlyName;
            string newExePath = Path.Combine(AppContext.BaseDirectory, $"new_{exeName}");
            
            using var client = new HttpClient();
            var bytes = await client.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(newExePath, bytes);

            StatusText.Text = "Update downloaded. Restarting...";
            
            string scriptName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "update.bat" : "update.sh";
            string scriptPath = Path.Combine(AppContext.BaseDirectory, scriptName);
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.WriteAllText(scriptPath, $"timeout /t 2\ndel \"{exeName}\"\nren \"new_{exeName}\" \"{exeName}\"\nstart \"\" \"{exeName}\"\ndel update.bat");
            }
            else
            {
                File.WriteAllText(scriptPath, $"sleep 2\nrm \"{exeName}\"\nmv \"new_{exeName}\" \"{exeName}\"\nchmod +x \"{exeName}\"\n./\"{exeName}\" &\nrm update.sh");
                await InstallerUtils.RunBatchScriptAsync(AppContext.BaseDirectory, $"chmod +x {scriptName}"); 
            }

            Process.Start(new ProcessStartInfo { FileName = scriptPath, UseShellExecute = true, CreateNoWindow = true });
            Environment.Exit(0);
        }
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

    private void OnFilterToggleChanged(object? sender, RoutedEventArgs e)
    {
        UpdateModList();
    }

    private string GetCompatibilityTagForBuild(string buildName)
    {
        if (string.IsNullOrEmpty(buildName)) return "all";
        
        if (buildName.Contains("r2.91120a") || buildName.Contains("r91120a")) return "r911";
        if (buildName.Contains("Hybrid r904")) return "r904-Hybrid";
        if (buildName.Contains("r1.90431a") || buildName.Contains("r904")) return "r904";
        
        return "all"; 
    }

    private void UpdateModList()
    {
        if (_manifest == null || _manifest.Mods == null) return;

        bool filterCompatible = CompatibilityFilterToggle?.IsChecked ?? true;
        string targetTag = "all";
        
        if (filterCompatible && VersionComboBox?.SelectedItem is BuildItem selectedBuild)
        {
            targetTag = GetCompatibilityTagForBuild(selectedBuild.Name);
        }

        var filteredMods = _manifest.Mods.AsEnumerable();
        if (filterCompatible && targetTag != "all")
        {
            filteredMods = filteredMods.Where(m => 
                string.Equals(m.Build, "all", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(m.Build, targetTag, StringComparison.OrdinalIgnoreCase));
        }

        var listItems = new List<object>();

        var groupedMods = filteredMods
            .GroupBy(m => !string.IsNullOrWhiteSpace(m.Category) ? m.Category : "Uncategorized")
            .OrderBy(g => g.Key);

        foreach (var group in groupedMods)
        {
            listItems.Add(new BuildGroupHeader($"=== {group.Key} ==="));
            listItems.AddRange(group);
        }

        if (ModListBox != null)
        {
            ModListBox.ItemsSource = listItems;
        }
    }

    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModListBox.SelectedItems != null)
        {
            var selectedHeaders = ModListBox.SelectedItems.OfType<BuildGroupHeader>().ToList();
            if (selectedHeaders.Any())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var header in selectedHeaders)
                    {
                        ModListBox.SelectedItems.Remove(header);
                    }
                });
            }
        }

        var newlySelectedMod = e.AddedItems.OfType<ModItem>().FirstOrDefault();
        if (newlySelectedMod != null)
        {
            ModDetailsText.Text = $"{newlySelectedMod.Name}\nSize: {newlySelectedMod.DisplaySize}\n\n{newlySelectedMod.Description}";
            Console.WriteLine($"[DEBUG] Selected Mod: {newlySelectedMod.Name}");
        }
        else
        {
            var unselectedMod = e.RemovedItems.OfType<ModItem>().FirstOrDefault();
            if (unselectedMod != null)
            {
                Console.WriteLine($"[DEBUG] Deselected Mod: {unselectedMod.Name}");
                var remainingMod = ModListBox.SelectedItems?.OfType<ModItem>().FirstOrDefault();
                if (remainingMod != null)
                {
                    ModDetailsText.Text = $"{remainingMod.Name}\nSize: {remainingMod.DisplaySize}\n\n{remainingMod.Description}";
                }
                else
                {
                    ModDetailsText.Text = "Select a mod from the list to view release notes and download details.";
                }
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

    private async void OnInstallButtonClick(object? sender, RoutedEventArgs e)
    {
        SetActionButtonsEnabled(false);
        
        if (StatusText != null) StatusText.Text = "Starting installation...";

        try
        {
            // 1. Mods Tab (Play / Install Button)
            if (sender == InstallButton || sender == PlayButton)
            {
                bool installedSomething = false;

                // --- A. Handle Target Build Installation ---
                if (VersionComboBox?.SelectedItem is BuildItem targetBuild && targetBuild.InstallStatus != "Installed")
                {
                    if (StatusText != null) StatusText.Text = $"Installing required build: {targetBuild.Name}...";

                    if (string.IsNullOrWhiteSpace(BuildsPathTextBox.Text))
                    {
                        var topLevel = TopLevel.GetTopLevel(this);
                        if (topLevel == null) 
                        {
                            if (StatusText != null) StatusText.Text = "Error: Cannot open folder picker.";
                            return;
                        }

                        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = "Select Builds Directory",
                            AllowMultiple = false
                        });

                        if (folders.Count == 0) 
                        {
                            if (StatusText != null) StatusText.Text = "Installation cancelled. Builds directory required to install the target build.";
                            return; 
                        }

                        BuildsPathTextBox.Text = folders[0].Path.LocalPath;
                    }

                    string buildTargetDir = BuildsPathTextBox.Text;
                    string buildDownloadUrl = !string.IsNullOrEmpty(targetBuild.DownloadUrl) 
                        ? targetBuild.DownloadUrl : $"{_manifest?.BaseUrl}{targetBuild.Url}";

                    await InstallSingleItemAsync(targetBuild.Name, buildDownloadUrl, buildTargetDir, InstallProgressBar, StatusText);
                    MarkAsInstalledAndSave(targetBuild);
                    installedSomething = true;
                }

                // --- B. Handle Mod Installations ---
                if (ModListBox.SelectedItems != null && ModListBox.SelectedItems.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(ModPathTextBox.Text))
                    {
                        var topLevel = TopLevel.GetTopLevel(this);
                        if (topLevel == null) 
                        {
                            if (StatusText != null) StatusText.Text = "Error: Cannot open folder picker.";
                            return;
                        }
                        
                        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = "Select Mod Directory",
                            AllowMultiple = false
                        });
                        
                        if (folders.Count == 0)
                        {
                            if (StatusText != null) StatusText.Text = "Installation cancelled. Mod directory required.";
                            return;
                        }
                        
                        ModPathTextBox.Text = folders[0].Path.LocalPath;
                    }
                    
                    string modTargetDir = ModPathTextBox.Text;

                    foreach (var selected in ModListBox.SelectedItems)
                    {
                        if (selected is ModItem mod)
                        {
                            string downloadUrl = !string.IsNullOrEmpty(mod.DownloadUrl) 
                                ? mod.DownloadUrl : $"{_manifest?.BaseUrl}{mod.Url}";

                            await InstallSingleItemAsync(mod.Name, downloadUrl, modTargetDir, InstallProgressBar, StatusText);
                            MarkAsInstalledAndSave(mod);
                            installedSomething = true;
                        }
                    }
                }

                if (installedSomething)
                {
                    if (StatusText != null) StatusText.Text = "Installation process completed successfully!";
                }
                else
                {
                    if (StatusText != null) StatusText.Text = "Target build is already installed and no mods were selected.";
                }
            }
            
            // 2. Tools (Single Select)
            else if (sender == InstallToolButton || sender == RepairToolButton)
            {
                if (ToolListBox.SelectedItem is ToolItem tool)
                {
                    string targetDir = Path.Combine(AppContext.BaseDirectory, "Tools");
                        
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
                    if (string.IsNullOrWhiteSpace(BuildsPathTextBox.Text))
                    {
                        var topLevel = TopLevel.GetTopLevel(this);
                        if (topLevel == null) 
                        {
                            if (BuildsStatusText != null) BuildsStatusText.Text = "Error: Cannot open folder picker.";
                            return;
                        }

                        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = "Select Builds Directory",
                            AllowMultiple = false
                        });

                        if (folders.Count == 0) 
                        {
                            if (BuildsStatusText != null) BuildsStatusText.Text = "Installation cancelled. Builds directory required.";
                            return; 
                        }

                        BuildsPathTextBox.Text = folders[0].Path.LocalPath;
                    }
                    
                    string targetDir = BuildsPathTextBox.Text;
                        
                    string downloadUrl = !string.IsNullOrEmpty(build.DownloadUrl) 
                        ? build.DownloadUrl : $"{_manifest?.BaseUrl}{build.Url}";

                    await InstallSingleItemAsync(build.Name, downloadUrl, targetDir, BuildsProgressBar, BuildsStatusText);
                    MarkAsInstalledAndSave(build);
                    
                    if (BuildsStatusText != null) BuildsStatusText.Text = $"{build.Name} installed successfully!";
                }
                else
                {
                    if (BuildsStatusText != null) BuildsStatusText.Text = "Please select a build to install.";
                }
            }
        }
        catch (Exception ex)
        {
            if (StatusText != null) StatusText.Text = $"Error: {ex.Message}";
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
            
            // Refresh the mods list to reflect the new target build's compatibility
            UpdateModList(); 
        }
    }

    private async void OnBrowseBuildsPathClick(object? sender, RoutedEventArgs e) => await PickFolderAndSetPathAsync(BuildsPathTextBox, "Select Builds Directory");
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
        else 
        {
            comboItems.Add(new BuildItem { Name = "No Builds Installed", InstallStatus = "None" }); 
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
    
    private void OnResetConfigClick(object? sender, RoutedEventArgs e)
    {
        ConfigManager.Delete();
        _config = new AppConfig();
        
        BuildsPathTextBox.Text = string.Empty;
        ModPathTextBox.Text = string.Empty;
        AppdataPathTextBox.Text = string.Empty;
        
        Console.WriteLine("[INFO] config.json deleted and configuration reset.");
    }
}