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

    private const string ManifestUrl = "https://starwars.thinggoeserror.net/manifest.json";

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

        // Updates: delete what an update left behind, show this version, restore the auto-check setting
        UpdateService.CleanUpAfterUpdate();
        VersionText.Text = $"Version: {UpdateService.CurrentVersion}";
        AutoUpdateCheckBox.IsChecked = startupConfig.CheckForUpdatesOnLaunch;
        if (UpdateService.JustUpdated)
            UpdateStatusText.Text = $"Updated to version {UpdateService.CurrentVersion}.";
        
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

        RefreshRepairBuilds(config);
    }

    /// <summary>Fills the Tools tab's "Repair build" list with installed builds the repair files are made for.</summary>
    private void RefreshRepairBuilds(AppConfig? config = null)
    {
        config ??= ConfigManager.Load();

        var builds = _manifest.Builds
            .Where(b => config.InstalledBuilds.ContainsKey(b.Tag) && ModInstaller.RepairFilesApplyTo(_manifest, b.Tag))
            .ToList();

        var previous = RepairBuildComboBox.SelectedItem as BuildItem;
        RepairBuildComboBox.ItemsSource = builds;
        RepairBuildComboBox.SelectedItem = previous != null && builds.Contains(previous) ? previous : builds.FirstOrDefault();
        RepairButton.IsEnabled = builds.Any();
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
            string json = await client.GetStringAsync(ManifestUrl);
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
                StatusText.Text = UpdateService.JustUpdated ? $"Updated to version {UpdateService.CurrentVersion}. Ready" : "Ready";

                if (AutoUpdateCheckBox.IsChecked == true)
                    _ = CheckForUpdatesAsync(userRequested: false);
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
            RefreshRepairBuilds(config);

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

        // Mods that already changed this build's game files (reinstalled if the build has to be recompiled)
        var installedGameMods = installedTag == null
            ? new List<ModItem>()
            : _manifest.Mods
                .Where(m => config.InstalledVersions.ContainsKey($"{installedTag}/{ItemKey(m.Id, m.Name)}")
                            && (ModInstaller.GetRecipe(m)?.TouchesGame ?? true))
                .ToList();

        var request = new ModInstallRequest
        {
            Mods = toInstall,
            InstalledGameMods = installedGameMods,
            TargetBuildTag = installedTag,
            BuildDir = installedTag != null ? Path.Combine(config.BuildsDir, installedTag) : null,
            BuildModsDir = targetPaths?.ModDirPath,
            GeneralModsDir = ConfigManager.GetGeneralModsDir(config),
            AppDataLoadDir = config.AppDataDir,
            ToolsDir = GlobalToolsDir,
            DolphinDir = GlobalDolphinDir,
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
                // Recorded per build when the mod is for specific builds (how the mod list looks it up)
                // or when it changed this build's game files; otherwise once for all builds.
                string id = ItemKey(mod.Id, mod.Name);
                bool perBuild = !mod.IsUniversal || result.BuildScoped.Contains(mod);
                string key = perBuild && installedTag != null ? $"{installedTag}/{id}" : id;
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
            RefreshRepairBuilds(config);
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

    // ───────────────────────── Restore Dolphin controls ─────────────────────────

    /// <summary>
    /// Play/Mods → Restore Controls: puts back the Dolphin control settings KBM Controls changed,
    /// from the backups made when it was installed.
    /// </summary>
    private async void OnRestoreControlsClick(object sender, RoutedEventArgs e)
    {
        var config = ConfigManager.Load();
        if (string.IsNullOrWhiteSpace(config.AppDataDir))
        {
            StatusText.Text = "Dolphin's appdata folder isn't set, so there's nothing to restore. Run the setup wizard first.";
            return;
        }

        bool confirmed = await ConfirmDialog.AskAsync(this,
            "Restore Dolphin controls",
            "Put Dolphin's controls back to how they were before KBM Controls was installed?\n\n" +
            "This restores Wii Remote 1's mappings, the Connect USB Keyboard setting and the Reset hotkey. " +
            "Esc stays set to stop the game. " +
            "Close Dolphin first, or it may save over the change when it exits.",
            "Restore", "Cancel");
        if (!confirmed) return;

        RestoreControlsButton.IsEnabled = false;
        try
        {
            var restored = await Task.Run(() => ModInstaller.RestoreDolphinControls(config.AppDataDir));
            StatusText.Text = (restored.Any()
                ? $"Restored Dolphin's original {string.Join(", ", restored)}."
                : "No backups of Dolphin's control settings were found (KBM Controls makes them when it's installed).")
                + " Esc is set to stop the game.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't restore Dolphin's controls: {ex.Message}";
        }
        finally
        {
            RestoreControlsButton.IsEnabled = true;
        }
    }

    // ───────────────────────── Repair ─────────────────────────

    /// <summary>
    /// Tools tab → Repair: restores the selected build's stock files from the r911 Repair Files
    /// (data, embed_wi_v4, main.dol), overwriting every mod's changes to them.
    /// </summary>
    private async void OnRepairClick(object sender, RoutedEventArgs e)
    {
        if (RepairBuildComboBox.SelectedItem is not BuildItem build) return;

        var config = ConfigManager.Load();
        if (!config.InstalledBuilds.TryGetValue(build.Tag, out var paths))
        {
            ToolStatusText.Text = $"{build.Name} isn't installed.";
            return;
        }

        bool confirmed = await ConfirmDialog.AskAsync(this,
            "Repair build",
            $"Restore the stock files of {build.Name}?\n\n" +
            "This overwrites the build's data folder, embed_wi_v4 and main.dol with the r911 Repair Files, " +
            "undoing every installed mod's changes to the game files. Texture mods in Dolphin aren't affected.",
            "Repair", "Cancel");
        if (!confirmed) return;

        var installer = new ModInstaller(_downloadService, (percent, message) =>
            Dispatcher.UIThread.Post(() =>
            {
                ToolProgressBar.Value = percent;
                ToolStatusText.Text = message;
            }));

        var request = new ModInstallRequest
        {
            TargetBuildTag = build.Tag,
            BuildDir = Path.Combine(config.BuildsDir, build.Tag),
            BuildModsDir = paths.ModDirPath,
            GeneralModsDir = ConfigManager.GetGeneralModsDir(config),
            AppDataLoadDir = config.AppDataDir,
            ToolsDir = GlobalToolsDir,
            DolphinDir = GlobalDolphinDir,
            Manifest = _manifest,
            Config = config
        };

        RepairButton.IsEnabled = false;
        InstallToolButton.IsEnabled = false;
        try
        {
            var result = await installer.RepairGameAsync(request);

            string summary;
            if (result.Success)
            {
                // The build's game-file mods were overwritten, so they're no longer installed.
                // Texture-only mods (e.g. Custom Skyboxes) live in Dolphin's folder and stay installed.
                string prefix = build.Tag + "/";
                var removed = config.InstalledVersions.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Where(k =>
                    {
                        string id = k[prefix.Length..];
                        var mod = _manifest.Mods.FirstOrDefault(m => string.Equals(ItemKey(m.Id, m.Name), id, StringComparison.OrdinalIgnoreCase));
                        var recipe = mod == null ? null : ModInstaller.GetRecipe(mod);
                        bool textureOnly = recipe != null && !recipe.TouchesGame;
                        return !textureOnly;
                    })
                    .ToList();
                foreach (string key in removed)
                    config.InstalledVersions.Remove(key);
                ConfigManager.Save(config);

                summary = removed.Any()
                    ? $"Repaired {build.Name}. {removed.Count} mod(s) are now marked as not installed for it."
                    : $"Repaired {build.Name}.";
                Console.WriteLine($"[OK] {summary}");
            }
            else
            {
                summary = $"Repair failed: {result.Errors[0]} See Debug/Console Output for details.";
            }

            UpdateInstallationStatuses(); // also refreshes the mod list's statuses and the tool list
            RefreshModList();

            // Posted so it lands after any queued progress updates
            Dispatcher.UIThread.Post(() => ToolStatusText.Text = summary);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ToolStatusText.Text = $"Repair failed: {ex.Message}");
        }
        finally
        {
            InstallToolButton.IsEnabled = true;
            RefreshRepairBuilds();
        }
    }

    // ───────────────────────── Settings ─────────────────────────

    private void OnBrowseBuildsPathClick(object sender, RoutedEventArgs e) { }
    private void OnBrowseModPathClick(object sender, RoutedEventArgs e) { }
    private void OnBrowseAppdataPathClick(object sender, RoutedEventArgs e) { }
    private void OnResetConfigClick(object sender, RoutedEventArgs e) { }

    // ───────────────────────── Open Dolphin ─────────────────────────

    /// <summary>Opens Dolphin on its own (no game), e.g. to change graphics settings or controls.</summary>
    private void OnOpenDolphinClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var command = DolphinManager.GetSettingsCommand(GlobalDolphinDir);
            if (!File.Exists(command.FileName))
            {
                StatusText.Text = "Dolphin isn't installed yet. Run the setup in the Install SWBF3 tab first.";
                return;
            }

            DolphinManager.Launch(command, DebugCheckBox.IsChecked == true);
            StatusText.Text = "Opening Dolphin...";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to open Dolphin: {ex.Message}";
        }
    }

    // ───────────────────────── Manager updates ─────────────────────────

    private void OnAutoUpdateCheckChanged(object? sender, RoutedEventArgs e)
    {
        var config = ConfigManager.Load();
        bool enabled = AutoUpdateCheckBox.IsChecked == true;
        if (config.CheckForUpdatesOnLaunch == enabled) return;
        config.CheckForUpdatesOnLaunch = enabled;
        ConfigManager.Save(config);
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(userRequested: true);

    /// <summary>
    /// Compares this version with the manifest's "installer" entry and offers the update.
    /// On launch (<paramref name="userRequested"/> false) it stays quiet unless there is an update.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool userRequested)
    {
        if (!UpdateButton.IsEnabled) return;
        UpdateButton.IsEnabled = false;

        try
        {
            if (userRequested)
            {
                UpdateStatusText.Text = "Checking for updates...";
                await RefreshManifestAsync();
            }

            UpdateInfo? update;
            try
            {
                update = UpdateService.CheckForUpdate(_manifest);
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = ex.Message;
                return;
            }

            if (update == null)
            {
                if (userRequested)
                    UpdateStatusText.Text = $"You have the latest version ({UpdateService.CurrentVersion}).";
                return;
            }

            UpdateStatusText.Text = $"Version {update.LatestVersion} is available (you have {UpdateService.CurrentVersion}).";

            if (IsBusy())
            {
                UpdateStatusText.Text += " Finish the current install, then click Check for Updates.";
                return;
            }

            bool install = await ConfirmDialog.AskAsync(this, "Update available",
                $"Version {update.LatestVersion} of {ShortcutService.LauncherName} is available (you have {UpdateService.CurrentVersion}).\n\n" +
                "Download it now? The manager will restart. Your builds, mods and settings are kept.",
                "Update", "Later");
            if (!install) return;

            await InstallUpdateAsync(update);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo update)
    {
        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.Value = 0;

        try
        {
            await Task.Run(() => UpdateService.DownloadAndApplyAsync(update, (percent, message) =>
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgressBar.Value = percent;
                    UpdateStatusText.Text = message;
                    StatusText.Text = message;
                })));

            // The new version has started; close this one so its file can be deleted
            await Task.Delay(500);
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Update failed: {ex}");
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            UpdateProgressBar.IsVisible = false;
        }
    }

    /// <summary>Downloads the manifest again so a manual check sees a version published since launch.</summary>
    private async Task RefreshManifestAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string json = await client.GetStringAsync(ManifestUrl);
            var fresh = JsonSerializer.Deserialize<AppManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (fresh != null) _manifest.Installer = fresh.Installer;
            if (!string.IsNullOrWhiteSpace(fresh?.BaseUrl)) _manifest.BaseUrl = fresh.BaseUrl;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Couldn't refresh the manifest ({ex.Message}); using the one from launch.");
        }
    }

    /// <summary>True while something is being installed (those buttons are disabled for the duration).</summary>
    private bool IsBusy() =>
        !InstallButton.IsEnabled || !Install_Build_Button.IsEnabled || !InstallToolButton.IsEnabled
        || !RestoreControlsButton.IsEnabled || !StartWizardButton.IsEnabled;
}