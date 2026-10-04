using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SWBF_C_build.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SWBF_C_build;

public partial class MainWindow : Window
{
    private AppManifest _manifest;
    private DownloadService _downloadService;

    // Central Dolphin and Tools directories, sitting alongside the executable
    private static readonly string GlobalDolphinDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dolphin");
    private static readonly string GlobalToolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");

    // Active build filter on the Play/Mods tab. null = show every mod.
    private string? _activeBuildFilter;

    public MainWindow()
    {
        InitializeComponent();

        // Mirror Console output (installer logs, Dolphin output, config warnings) into the Debug/Console panel
        Console.SetOut(new ConsoleRedirector(Console.Out, AppendToDebugConsole));
        Console.SetError(new ConsoleRedirector(Console.Error, AppendToDebugConsole));

        _downloadService = new DownloadService();
        _manifest = new AppManifest();

        // Resolve the general mods folder (next to Builds) and show saved paths in the Settings tab
        var startupConfig = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(startupConfig.ModDir))
        {
            startupConfig.ModDir = ConfigManager.GetGeneralModsDir(startupConfig);
            ConfigManager.Save(startupConfig);
        }
        BuildsPathTextBox.Text = startupConfig.BuildsDir;
        ModPathTextBox.Text = startupConfig.ModDir;
        AppdataPathTextBox.Text = startupConfig.AppDataDir;
        
        // Load manifest from server and populate UI
        _ = InitializeAppAsync();
    }

    // ───────────────────────── Debug console ─────────────────────────

    // Keep the log bounded; Dolphin can print a lot
    private const int MaxDebugConsoleChars = 200_000;

    /// <summary>Appends text to the Debug/Console panel and scrolls to the end. Called on the UI thread.</summary>
    private void AppendToDebugConsole(string text)
    {
        string current = (DebugConsoleTextBox.Text ?? "") + text;
        if (current.Length > MaxDebugConsoleChars)
            current = current[^MaxDebugConsoleChars..];

        DebugConsoleTextBox.Text = current;
        DebugConsoleTextBox.CaretIndex = current.Length;
    }

    // ───────────────────────── Install status ─────────────────────────

    private static string ItemKey(string id, string name) => !string.IsNullOrEmpty(id) ? id : name;

    /// <summary>
    /// Universal mods are tracked once ("<id>"); build-specific mods are tracked per build ("<tag>/<id>").
    /// </summary>
    private static string ModConfigKey(ModItem mod, string? buildTag) =>
        mod.IsUniversal || string.IsNullOrEmpty(buildTag)
            ? ItemKey(mod.Id, mod.Name)
            : $"{buildTag}/{ItemKey(mod.Id, mod.Name)}";

    private string? CurrentTargetTag => (VersionComboBox.SelectedItem as BuildItem)?.Tag;

    private void UpdateInstallationStatuses()
    {
        var config = ConfigManager.Load();

        foreach (var build in _manifest.Builds)
        {
            build.InstallStatus =
                config.InstalledBuilds.TryGetValue(build.Tag, out var paths) && File.Exists(paths.MainDolPath)
                    ? "Installed"
                    : "Not Installed";
        }

        UpdateModStatuses(config);

        foreach (var tool in _manifest.Tools)
        {
            tool.InstallStatus = config.InstalledVersions.ContainsKey(ItemKey(tool.Id, tool.Name))
                ? "Installed"
                : "Not Installed";
        }
    }

    /// <summary>
    /// Mod status is relative to the current target build: build-specific mods show whether
    /// they're installed into that build, or that they don't apply to it.
    /// </summary>
    private void UpdateModStatuses(AppConfig config)
    {
        string? targetTag = CurrentTargetTag;

        foreach (var mod in _manifest.Mods)
        {
            if (mod.IsUniversal)
            {
                // Texture-only mods are tracked once; ones that change game files are tracked per build
                string id = ItemKey(mod.Id, mod.Name);
                bool installed = config.InstalledVersions.ContainsKey(id)
                                 || (targetTag != null && config.InstalledVersions.ContainsKey($"{targetTag}/{id}"));
                mod.InstallStatus = installed ? "Installed" : "Not Installed";
            }
            else if (targetTag == null || !mod.IsCompatibleWith(targetTag))
            {
                mod.InstallStatus = targetTag == null ? "Not Installed" : $"Not for {targetTag}";
            }
            else
            {
                mod.InstallStatus = config.InstalledVersions.ContainsKey(ModConfigKey(mod, targetTag))
                    ? "Installed" : "Not Installed";
            }
        }
    }

    // ───────────────────────── Startup ─────────────────────────

    private async Task InitializeAppAsync()
    {
        try
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = "Fetching manifest from server...");

            // Fetch manifest from the server
            using var client = new HttpClient();
            string manifestUrl = "http://192.168.0.100:8881/manifest.json";
            
            string json = await client.GetStringAsync(manifestUrl);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _manifest = JsonSerializer.Deserialize<AppManifest>(json, options) ?? new AppManifest();

            // Append the BaseUrl to all item URLs
            _manifest.BuildFullUrls();

            Dispatcher.UIThread.Post(() =>
            {
                WizardBuildComboBox.ItemsSource = _manifest.Builds;
                Wii_Builds.ItemsSource = _manifest.Builds;
                ToolListBox.ItemsSource = _manifest.Tools;

                // Group the Build ComboBox to satisfy the XAML DataTemplate
                var buildList = new List<object> { new BuildGroupHeader("Available Builds") };
                buildList.AddRange(_manifest.Builds);
                VersionComboBox.ItemsSource = buildList;

                // Filter buttons must exist before a target build is selected
                CreateBuildFilterButtons();

                // Check file system and config to set item status
                UpdateInstallationStatuses();

                if (_manifest.Builds.Any())
                {
                    WizardBuildComboBox.SelectedIndex = 0;

                    // Default the target to the first installed build, otherwise the first build
                    var config = ConfigManager.Load();
                    var firstInstalled = _manifest.Builds.FirstOrDefault(b => config.InstalledBuilds.ContainsKey(b.Tag));
                    VersionComboBox.SelectedItem = firstInstalled ?? _manifest.Builds[0];
                }

                // Bind SelectionChanged events to update the details/description text blocks
                ModListBox.SelectionChanged += (s, e) =>
                {
                    var selected = ModListBox.SelectedItems?.Cast<object>().OfType<ModItem>().FirstOrDefault();
                    ModDetailsText.Text = selected?.Description ?? "Select a mod from the list to view release notes and download details.";
                };
                
                ToolListBox.SelectionChanged += (s, e) =>
                {
                    var selected = ToolListBox.SelectedItem as ToolItem;
                    ToolDetailsText.Text = selected?.Description ?? "Select a tool from the list to view release notes and download details.";
                };
                
                Wii_Builds.SelectionChanged += (s, e) =>
                {
                    var selected = Wii_Builds.SelectedItem as BuildItem;
                    Wii_Builds_DetailsText.Text = selected?.Description ?? "Select a build from the list to view release notes and download details.";
                };

                RefreshModList();
                StatusText.Text = "Ready";
            });
        }
        catch (HttpRequestException ex)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Network Error: Could not reach manifest server. ({ex.Message})");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Failed to parse manifest: {ex.Message}");
        }
    }

    // ───────────────────────── Build filter ─────────────────────────

    private void CreateBuildFilterButtons()
    {
        BuildFilterPanel.Children.Clear();
        AddFilterButton("All", null, "Show every mod");

        foreach (var build in _manifest.Builds.GroupBy(b => b.Tag).Select(g => g.First()))
            AddFilterButton(build.Tag, build.Tag, build.Name);

        UpdateFilterButtonStates();
    }

    private void AddFilterButton(string label, string? buildTag, string tooltip)
    {
        var button = new ToggleButton
        {
            Content = label,
            Tag = buildTag,
            FontSize = 12,
            Padding = new Thickness(10, 3),
            Margin = new Thickness(0, 0, 6, 4)
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => OnBuildFilterClick(buildTag);
        BuildFilterPanel.Children.Add(button);
    }

    private void OnBuildFilterClick(string? buildTag)
    {
        if (buildTag == null)
        {
            // "All": show every mod, keep the current target build
            _activeBuildFilter = null;
            UpdateFilterButtonStates();
            RefreshModList();
            return;
        }

        // A build button also makes that build the target, so installs and Play line up with the list.
        // Selecting it fires OnVersionChanged, which applies the filter and refreshes the list.
        var build = _manifest.Builds.FirstOrDefault(b => b.Tag == buildTag);
        if (build != null && !ReferenceEquals(VersionComboBox.SelectedItem, build))
        {
            VersionComboBox.SelectedItem = build;
        }
        else
        {
            _activeBuildFilter = buildTag;
            UpdateFilterButtonStates();
            RefreshModList();
        }
    }

    private void UpdateFilterButtonStates()
    {
        foreach (var button in BuildFilterPanel.Children.OfType<ToggleButton>())
            button.IsChecked = (button.Tag as string) == _activeBuildFilter;
    }

    private void RefreshModList()
    {
        if (_manifest?.Mods == null) return;

        UpdateModStatuses(ConfigManager.Load());

        // Build filter: that build's mods plus mods compatible with all builds
        var filteredMods = _activeBuildFilter == null
            ? _manifest.Mods.AsEnumerable()
            : _manifest.Mods.Where(m => m.IsCompatibleWith(_activeBuildFilter));

        // Interleave group headers with the mod items so the XAML DataTemplates render them cleanly
        var groupedMods = new List<object>();
        foreach (var group in filteredMods.GroupBy(m => m.Category))
        {
            groupedMods.Add(new BuildGroupHeader(group.Key));
            groupedMods.AddRange(group);
        }

        ModListBox.ItemsSource = groupedMods;
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Prevent users from selecting the group header itself
        if (VersionComboBox.SelectedItem is BuildGroupHeader)
        {
            if (e.RemovedItems.Count > 0)
                VersionComboBox.SelectedItem = e.RemovedItems[0];
            else if (_manifest?.Builds.Any() == true)
                VersionComboBox.SelectedIndex = 1;
            return;
        }

        // Selecting a build to play filters the list to mods compatible with it
        if (VersionComboBox.SelectedItem is BuildItem build)
            _activeBuildFilter = build.Tag;

        UpdateFilterButtonStates();
        RefreshModList();
    }

    // ───────────────────────── Wizard ─────────────────────────

    private void UpdateWizardProgress(double progress, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            WizardProgressBar.Value = progress;
            WizardStatusText.Text = status;
        });
    }

    private async void OnWizardStartClick(object sender, RoutedEventArgs e)
    {
        var config = ConfigManager.Load();
        var selectedBuild = WizardBuildComboBox.SelectedItem as BuildItem;
        if (selectedBuild == null) return;

        bool isManual = ManualSetupRadio.IsChecked == true; 
        bool setupControls = SetupControlsCheckBox.IsChecked == true; 

        WizardProgressBar.IsVisible = true;
        StartWizardButton.IsEnabled = false;

        try
        {
            string buildDir = Path.Combine(config.BuildsDir, selectedBuild.Tag);
            string buildModsDir = Path.Combine(buildDir, "mods"); 
            Directory.CreateDirectory(buildModsDir);
            Directory.CreateDirectory(ConfigManager.GetGeneralModsDir(config));

            WizardStatusText.Text = $"Downloading {selectedBuild.Name}...";
            await _downloadService.DownloadAndExtractAsync(selectedBuild.DownloadUrl, buildDir, UpdateWizardProgress);
            
            string dolPath = InstallerUtils.FindMainDol(buildDir);
            
            config.InstalledBuilds[selectedBuild.Tag] = new BuildPaths 
            {
                MainDolPath = dolPath,
                ModDirPath = buildModsDir
            };
            selectedBuild.InstallStatus = "Installed";
            ConfigManager.Save(config);

            WizardStatusText.Text = "Downloading Dolphin Emulator...";
            string dolphinUrl = DolphinManager.GetDolphinDownloadUrl(_manifest);
            await _downloadService.DownloadAndExtractAsync(dolphinUrl, GlobalDolphinDir, UpdateWizardProgress);

            // Point the appdata directory at the portable Dolphin's User/Load folder
            string userLoadDir = DolphinManager.GetUserLoadDir(GlobalDolphinDir);
            if (!string.IsNullOrEmpty(userLoadDir))
            {
                config.AppDataDir = userLoadDir;
                ConfigManager.Save(config);
                AppdataPathTextBox.Text = userLoadDir;
            }

            // Desktop shortcuts chosen in step 4
            var shortcutNotes = new List<string>();
            if (LauncherShortcutCheckBox.IsChecked == true)
                shortcutNotes.Add(await CreateLauncherShortcutAsync());
            if (BuildShortcutCheckBox.IsChecked == true)
            {
                string? gameNote = await CreateGameShortcutAsync(selectedBuild, dolPath, askFirst: false);
                if (gameNote != null) shortcutNotes.Add(gameNote);
            }

            if (isManual || setupControls)
            {
                WizardStatusText.Text = isManual ? "Opening Dolphin for manual configuration..." : "Please configure your controls in Dolphin.";
                DolphinManager.Launch(DolphinManager.GetSettingsCommand(GlobalDolphinDir), DebugCheckBox.IsChecked == true);
            }

            RefreshModList();
            WizardStatusText.Text = shortcutNotes.Any()
                ? $"Setup Complete! {string.Join(" ", shortcutNotes)}"
                : "Setup Complete!";
        }
        catch (Exception ex)
        {
            WizardStatusText.Text = $"Setup failed: {ex.Message}";
        }
        finally
        {
            StartWizardButton.IsEnabled = true;
        }
    }

    // ───────────────────────── Play ─────────────────────────

    private void OnPlayButtonClick(object sender, RoutedEventArgs e)
    {
        var selectedBuild = VersionComboBox.SelectedItem as BuildItem; 
        var config = ConfigManager.Load(); 
        
        if (selectedBuild != null && config.InstalledBuilds.TryGetValue(selectedBuild.Tag, out var paths))
        {
            // Re-resolve so builds installed before the DATA/UPDATE fix get corrected automatically
            string resolvedDol = InstallerUtils.FindMainDol(Path.Combine(config.BuildsDir, selectedBuild.Tag));
            if (!string.IsNullOrEmpty(resolvedDol) && resolvedDol != paths.MainDolPath)
            {
                paths.MainDolPath = resolvedDol;
                ConfigManager.Save(config);
            }

            if (File.Exists(paths.MainDolPath))
            {
                try
                {
                    var playCommand = DolphinManager.GetPlayCommand(GlobalDolphinDir, paths.MainDolPath);

                    // Launch with output drained so Dolphin can't block on a full stdout pipe.
                    // Output is only forwarded to the debug console when Debug/Console Output is enabled.
                    DolphinManager.Launch(playCommand, DebugCheckBox.IsChecked == true);
                    StatusText.Text = $"Launching {selectedBuild.Name}...";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Failed to launch Dolphin: {ex.Message}";
                }
            }
            else
            {
                StatusText.Text = "main.dol not found for selected build.";
            }
        }
        else
        {
            StatusText.Text = "Selected build is not installed yet.";
        }
    }

    // ───────────────────────── Desktop shortcuts ─────────────────────────

    /// <summary>
    /// Creates a desktop shortcut that starts this build directly in Dolphin.
    /// askFirst: show a Yes/No dialog first (Builds tab); the wizard uses its checkbox instead.
    /// Returns a short note for the status line, or null if nothing was done or the user said no.
    /// </summary>
    private async Task<string?> CreateGameShortcutAsync(BuildItem build, string dolPath, bool askFirst)
    {
        if (string.IsNullOrEmpty(dolPath) || !File.Exists(dolPath))
            return askFirst ? null : "Couldn't create the game shortcut: the build's main.dol wasn't found.";

        var playCommand = DolphinManager.GetPlayCommand(GlobalDolphinDir, dolPath);
        if (!File.Exists(playCommand.FileName))
            return "Install Dolphin with the setup wizard to create a game shortcut.";

        if (askFirst)
        {
            bool create = await ConfirmDialog.AskAsync(this,
                "Desktop shortcut",
                $"Create a desktop shortcut that starts {build.Name} directly in Dolphin?",
                "Create shortcut", "No thanks");
            if (!create) return null;
        }

        try
        {
            string path = await Task.Run(() => ShortcutService.CreateGameShortcut(build.Name, playCommand));
            Console.WriteLine($"[OK] Created game shortcut: {path}");
            return "Game shortcut added to your desktop.";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Couldn't create the game shortcut: {ex.Message}");
            return $"Couldn't create the game shortcut: {ex.Message}";
        }
    }

    private async Task<string> CreateLauncherShortcutAsync()
    {
        try
        {
            string path = await Task.Run(() => ShortcutService.CreateLauncherShortcut());
            Console.WriteLine($"[OK] Created launcher shortcut: {path}");
            return "Launcher shortcut added to your desktop.";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Couldn't create the launcher shortcut: {ex.Message}");
            return $"Couldn't create the launcher shortcut: {ex.Message}";
        }
    }

    // ───────────────────────── Installs ─────────────────────────

    private async void OnInstallButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender == InstallButton) await InstallSelectedModsAsync();
        else if (sender == Install_Build_Button) await InstallSelectedBuildAsync();
        else if (sender == InstallToolButton) await InstallSelectedToolAsync();
    }

    /// <summary>
    /// Installs the selected mods into the target build using ModInstaller:
    /// game files go into that build's own game folder, textures into Dolphin's appdata Load folder.
    /// Downloads are kept in the general Mods folder (all-build mods) or Builds/&lt;tag&gt;/mods.
    /// </summary>
    private async Task InstallSelectedModsAsync()
    {
        var config = ConfigManager.Load();

        var selectedMods = ModListBox.SelectedItems?.Cast<object>().OfType<ModItem>().ToList();
        if (selectedMods == null || !selectedMods.Any())
        {
            StatusText.Text = "No mods selected for installation.";
            return;
        }

        var targetBuild = VersionComboBox.SelectedItem as BuildItem;
        BuildPaths? targetPaths = null;
        if (targetBuild != null)
            config.InstalledBuilds.TryGetValue(targetBuild.Tag, out targetPaths);

        // Build-specific mods need a compatible, installed target build
        var skipped = new List<string>();
        var toInstall = new List<ModItem>();
        foreach (var mod in selectedMods)
        {
            if (!mod.IsUniversal)
            {
                if (targetBuild == null || !mod.IsCompatibleWith(targetBuild.Tag))
                {
                    skipped.Add($"{mod.Name} (needs {mod.CompatibilityLabel})");
                    continue;
                }
                if (targetPaths == null)
                {
                    skipped.Add($"{mod.Name} (install {targetBuild.Tag} first)");
                    continue;
                }
            }
            toInstall.Add(mod);
        }

        if (!toInstall.Any())
        {
            StatusText.Text = $"Nothing to install. Skipped: {string.Join("; ", skipped)}";
            return;
        }

        // Texture mods go to Dolphin's User/Load folder; fill it in if the wizard hasn't yet
        if (string.IsNullOrWhiteSpace(config.AppDataDir) && Directory.Exists(GlobalDolphinDir))
        {
            config.AppDataDir = DolphinManager.GetUserLoadDir(GlobalDolphinDir);
            ConfigManager.Save(config);
            AppdataPathTextBox.Text = config.AppDataDir;
        }

        string? installedTag = targetPaths != null ? targetBuild?.Tag : null;
        var request = new ModInstallRequest
        {
            Mods = toInstall,
            TargetBuildTag = installedTag,
            BuildDir = installedTag != null ? Path.Combine(config.BuildsDir, installedTag) : null,
            BuildModsDir = targetPaths?.ModDirPath,
            GeneralModsDir = ConfigManager.GetGeneralModsDir(config),
            AppDataLoadDir = config.AppDataDir,
            ToolsDir = GlobalToolsDir,
            Manifest = _manifest,
            Config = config
        };

        var installer = new ModInstaller(_downloadService, (percent, message) =>
            Dispatcher.UIThread.Post(() =>
            {
                InstallProgressBar.Value = percent;
                StatusText.Text = message;
            }));

        InstallButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        try
        {
            var result = await installer.InstallAsync(request);

            foreach (var mod in result.Installed)
            {
                string id = ItemKey(mod.Id, mod.Name);
                string key = result.BuildScoped.Contains(mod) && installedTag != null ? $"{installedTag}/{id}" : id;
                config.InstalledVersions[key] = mod.Version;
            }
            ConfigManager.Save(config);
            UpdateInstallationStatuses(); // also refreshes tools the installer downloaded

            string summary = result.Success
                ? $"Installed {result.Installed.Count} mod(s)."
                : $"Finished with {result.Errors.Count} error(s): {result.Errors[0]}";
            if (result.Warnings.Any()) summary += $" {result.Warnings.Count} warning(s).";
            if (skipped.Any()) summary += $" Skipped: {string.Join("; ", skipped)}.";
            if (!result.Success || result.Warnings.Any()) summary += " See Debug/Console Output for details.";

            // Posted so it lands after any queued progress updates
            Dispatcher.UIThread.Post(() => StatusText.Text = summary);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = $"Mod installation failed: {ex.Message}");
        }
        finally
        {
            InstallButton.IsEnabled = true;
            PlayButton.IsEnabled = true;
        }
    }

    private async Task InstallSelectedBuildAsync()
    {
        var config = ConfigManager.Load();
        var selectedBuild = Wii_Builds.SelectedItem as BuildItem;
        if (selectedBuild == null) return;

        Install_Build_Button.IsEnabled = false;
        try
        {
            string buildDir = Path.Combine(config.BuildsDir, selectedBuild.Tag);
            string buildModsDir = Path.Combine(buildDir, "mods");
            Directory.CreateDirectory(buildModsDir);
            Directory.CreateDirectory(ConfigManager.GetGeneralModsDir(config));

            BuildsStatusText.Text = $"Installing {selectedBuild.Name}...";
            await _downloadService.DownloadAndExtractAsync(selectedBuild.DownloadUrl, buildDir, (val, msg) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    BuildsProgressBar.Value = val;
                    BuildsStatusText.Text = msg;
                });
            });

            string dolPath = InstallerUtils.FindMainDol(buildDir);
            config.InstalledBuilds[selectedBuild.Tag] = new BuildPaths
            {
                MainDolPath = dolPath,
                ModDirPath = buildModsDir
            };

            selectedBuild.InstallStatus = "Installed";
            ConfigManager.Save(config);
            RefreshModList();
            BuildsStatusText.Text = "Build installed successfully!";

            string? shortcutNote = await CreateGameShortcutAsync(selectedBuild, dolPath, askFirst: true);
            if (shortcutNote != null)
                BuildsStatusText.Text = $"Build installed successfully! {shortcutNote}";
        }
        catch (Exception ex)
        {
            BuildsStatusText.Text = $"Build installation failed: {ex.Message}";
        }
        finally
        {
            Install_Build_Button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Tools are installed to Tools/<ToolName> in the same directory as the program.
    /// </summary>
    private async Task InstallSelectedToolAsync()
    {
        var config = ConfigManager.Load();
        var selectedTool = ToolListBox.SelectedItem as ToolItem;
        if (selectedTool == null) return;

        InstallToolButton.IsEnabled = false;
        try
        {
            string toolDir = Path.Combine(GlobalToolsDir, selectedTool.Name);

            ToolStatusText.Text = $"Installing {selectedTool.Name}...";
            await _downloadService.DownloadAndExtractAsync(selectedTool.DownloadUrl, toolDir, (val, msg) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ToolProgressBar.Value = val;
                    ToolStatusText.Text = msg;
                });
            });

            selectedTool.InstallStatus = "Installed";
            config.InstalledVersions[ItemKey(selectedTool.Id, selectedTool.Name)] = selectedTool.Version;
            ConfigManager.Save(config);

            ToolStatusText.Text = $"Tool installed to {toolDir}";
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"Tool installation failed: {ex.Message}";
        }
        finally
        {
            InstallToolButton.IsEnabled = true;
        }
    }

    // ───────────────────────── Settings ─────────────────────────

    private void OnBrowseBuildsPathClick(object sender, RoutedEventArgs e) { }
    private void OnBrowseModPathClick(object sender, RoutedEventArgs e) { }
    private void OnBrowseAppdataPathClick(object sender, RoutedEventArgs e) { }
    private void OnResetConfigClick(object sender, RoutedEventArgs e) { }
}