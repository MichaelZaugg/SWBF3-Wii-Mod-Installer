// Services/ModInstaller.cs
// C# port of the Python installer.py mod installation logic.
//
// Flow for "Install Mod(s)":
//   1. Pre-checks (incompatible mod pairs, required paths)            ← check_install_conditions
//   2. Download + extract each mod into its staging folder:
//        mods for all builds  → <General Mods>/<zip name>
//        build-specific mods  → <Builds>/<tag>/mods/<zip name>
//   3. .res / .war file-name conflict check across the selected mods    ← check_file_name_conflicts
//   4. Copy each mod into place (Lighting Fix last):
//        game files → the target build's own game folder (the folder containing DATA)
//        textures   → Dolphin's appdata Load folder (Load/Textures/RABAZZ, Load/DynamicInputTextures)
//   5. If needed: download the Embedded ResCompiler tool, copy it into DATA/files,
//      restore stock embed_wi_v4 (r911 repair files), and run the compile batch files.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SWBF_C_build.Services;

/// <summary>Where a mod's files are copied to.</summary>
public enum ModTarget
{
    GameRoot,     // the build's game folder (the folder that contains DATA)
    Data,         // <game>/DATA
    DataFiles,    // <game>/DATA/files
    DataSys,      // <game>/DATA/sys
    AppDataLoad,  // Dolphin User/Load
    Textures,     // Dolphin User/Load/Textures/RABAZZ
    DolphinUser   // Dolphin User
}

/// <summary>Which resource compile a mod needs after its files are copied.</summary>
public enum CompileMode
{
    None,
    TemplatesAndRes, // compile_templates_and_res.bat
    ResForced        // compile_all_res_forced.bat
}

/// <summary>
/// Copies one folder from the extracted download into place.
/// Sources are tried in order: a path relative to the download (matched case-insensitively,
/// also found if nested deeper), or "" for the download's content root.
/// </summary>
public sealed record ModCopyStep(string[] Sources, ModTarget Target, string TargetSubpath = "", bool Optional = false);

public sealed record ModRecipe(
    ModCopyStep[] Steps,
    CompileMode Compile = CompileMode.None,
    int Order = 50,
    bool ReplacesMainDol = false,
    bool Custom = false,     // installed by a hand-written function (see "Custom installers" in ModInstaller)
    bool NeedsGame = false)  // for Custom mods: set true if the function writes game files (checked before downloading)
{
    /// <summary>True when the mod writes into a build's game files (so it's tracked per build).</summary>
    public bool TouchesGame => NeedsGame || ReplacesMainDol || Steps.Any(s =>
        s.Target is ModTarget.GameRoot or ModTarget.Data or ModTarget.DataFiles or ModTarget.DataSys);

    public bool TouchesAppData => Steps.Any(s =>
        s.Target is ModTarget.AppDataLoad or ModTarget.Textures or ModTarget.DolphinUser);
}

public sealed class ModInstallRequest
{
    public IReadOnlyList<ModItem> Mods { get; init; } = Array.Empty<ModItem>();

    /// <summary>Tag of the installed target build, or null if none is installed.</summary>
    public string? TargetBuildTag { get; init; }

    /// <summary>Builds/&lt;tag&gt; for the installed target build, or null.</summary>
    public string? BuildDir { get; init; }

    /// <summary>Staging folder for build-specific mods (Builds/&lt;tag&gt;/mods).</summary>
    public string? BuildModsDir { get; init; }

    /// <summary>Staging folder for mods compatible with all builds.</summary>
    public string GeneralModsDir { get; init; } = "";

    /// <summary>Dolphin's User/Load folder (config.AppDataDir).</summary>
    public string AppDataLoadDir { get; init; } = "";

    /// <summary>Tools folder next to the program.</summary>
    public string ToolsDir { get; init; } = "";

    public AppManifest Manifest { get; init; } = new();
    public AppConfig Config { get; init; } = new();
}

public sealed class ModInstallResult
{
    public List<ModItem> Installed { get; } = new();

    /// <summary>Installed mods that changed the target build's game files (tracked per build).</summary>
    public HashSet<ModItem> BuildScoped { get; } = new();

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Success => Errors.Count == 0;
}

public class ModInstaller
{
    // ───────────── Manifest file names ─────────────

    public const string TexturePack4k = "4k_texture_packs.zip";
    public const string Characters4k = "4k_Characters_Model_Fix.zip";
    public const string ClassUniqueIcons = "ClassUniqueIcons.zip";
    public const string DynamicInputTextures = "DynamicInputTextures.zip";
    public const string HdUserInterface = "HD_user_interface.zip";
    public const string MusicAndCloneVo = "Music_and_Clone_VO.zip";
    public const string MutedBlankSounds = "Muted_Blank_Sounds.zip";
    public const string RestoredR7Vehicles = "Restored_r7_vehicles.zip";
    public const string FaithfulHpBars = "_faithful_hpbars_v2.zip";
    public const string LightingFix = "lighting_fix.zip";
    public const string CustomSkyboxes = "r2-91120a_custom_skyboxes.zip";
    public const string R904Minimaps = "r904_fixed_minimaps.zip";
    public const string UpdatedMainDol = "r91120a_updated_main-dol.zip";
    public const string ClothFix = "r911_cloth_fix.zip";
    public const string RestoredMeleeClasses = "r9_RestoredMeleeClasses.zip";
    public const string UnlockedFrontend = "unlocked_PC_Xbox_Frontend.zip";

    // Mods that weren't in installer.py (installed by the placeholder functions below)
    public const string FixedHvVAndHuntAudio = "FixedHvVandHuntAudio.zip";
    public const string KbmControls = "KBM_controls.zip";
    public const string MustafarWaterFix = "mustafar_water_fix.zip";
    public const string R904DantooineCrashFix = "r904_dantooine_crash_fix.zip";

    public const string ResCompilerTool = "EmbeddedResCompiler_with_templates.zip";
    public const string RepairFilesTool = "r911_repair_files.zip";

    private const string GameId = "RABAZZ";
    private const string StageMarker = ".swbf3_mod_version";

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Folder names that mean "this is real content, don't unwrap past it" when looking
    /// for the content root of a download.
    /// </summary>
    private static readonly HashSet<string> StructuralNames = new(Ci)
    {
        "DATA", "files", "sys", "data", "assets", "bf", "menus",
        "Load", "Textures", "DynamicInputTextures", GameId,
        "Config", "GameSettings", "characters", "minimaps"
    };

    // ───────────── Install recipes (ported from installer.py, keyed by manifest file name) ─────────────
    //
    // The first source in each list is the folder name the Python installer used; later entries
    // are fallbacks for the new archives, whose layout may differ. "" = the download's content root.

    public static readonly IReadOnlyDictionary<string, ModRecipe> Recipes = new Dictionary<string, ModRecipe>(Ci)
    {
        // install_4k_texture_pack
        [TexturePack4k] = new(new[]
        {
            new ModCopyStep(new[] { "4kTexturePacks", "" }, ModTarget.Textures)
        }, Order: 10),

        // install_4k_characters_model_fix
        [Characters4k] = new(new[]
        {
            new ModCopyStep(new[] { "characters" }, ModTarget.Textures, "characters"),
            new ModCopyStep(new[] { "ingame_models_x1_x2_new" }, ModTarget.DataFiles)
        }),

        // install_unique_icons
        [ClassUniqueIcons] = new(new[]
        {
            new ModCopyStep(new[] { "ClassUniqueIcons", "" }, ModTarget.Data)
        }, CompileMode.TemplatesAndRes),

        // install_dynamic_input_textures
        [DynamicInputTextures] = new(new[]
        {
            new ModCopyStep(new[] { "DynamicInputTextures/DynamicInputTextures", "DynamicInputTextures", "" },
                ModTarget.AppDataLoad, "DynamicInputTextures"),
            new ModCopyStep(new[] { "RABAZZ_dynamic/RABAZZ" },
                ModTarget.AppDataLoad, "DynamicInputTextures/RABAZZ", Optional: true)
        }),

        // install_hd_interface
        [HdUserInterface] = new(new[]
        {
            new ModCopyStep(new[] { "HD_interface", "" }, ModTarget.Textures)
        }),

        // install_music_clonetrooper_vo
        [MusicAndCloneVo] = new(new[]
        {
            new ModCopyStep(new[] { "Music_and_Clone_VO/Music_and_Clone_VO", "Music_and_Clone_VO", "" }, ModTarget.GameRoot)
        }, CompileMode.TemplatesAndRes),

        // install_muted_blank_audio
        [MutedBlankSounds] = new(new[]
        {
            new ModCopyStep(new[] { "SWBF3_Wii_Muted_Blank_Sounds", "" }, ModTarget.GameRoot)
        }),

        // install_restored_r7_vehicles
        [RestoredR7Vehicles] = new(new[]
        {
            new ModCopyStep(new[] { "restored_r7_vehicles", "" }, ModTarget.Data)
        }, CompileMode.TemplatesAndRes),

        // install_faithful_health_bars
        [FaithfulHpBars] = new(new[]
        {
            new ModCopyStep(new[] { "_faithful_hpbars_v2", "" }, ModTarget.Textures, "_faithful_hpbars_v2")
        }),

        // install_lighting_fix — installed last, then compile_all_res_forced
        [LightingFix] = new(new[]
        {
            new ModCopyStep(new[] { "SWBF3_Wii_Light_Fixes/data", "data" }, ModTarget.DataFiles, "data")
        }, CompileMode.ResForced, Order: 100),

        // ── Not in the Python installer: hand-written functions (see "Custom installers") ──

        // After the 4K pack (Order 10), so it overwrites it, as its description requires
        [CustomSkyboxes] = new(Array.Empty<ModCopyStep>(), Order: 20, Custom: true),
        [FixedHvVAndHuntAudio] = new(Array.Empty<ModCopyStep>(), Custom: true),
        [KbmControls] = new(Array.Empty<ModCopyStep>(), Custom: true),
        [MustafarWaterFix] = new(Array.Empty<ModCopyStep>(), Custom: true),
        [R904DantooineCrashFix] = new(Array.Empty<ModCopyStep>(), Custom: true),

        // install_minimap_fix
        [R904Minimaps] = new(new[]
        {
            new ModCopyStep(new[] { "minimaps/minimaps", "minimaps", "" }, ModTarget.Textures, "minimaps")
        }),

        // install_updated_debug_menu
        [UpdatedMainDol] = new(Array.Empty<ModCopyStep>(), ReplacesMainDol: true),

        // install_cloth_fix
        [ClothFix] = new(new[]
        {
            new ModCopyStep(new[] { "Battlefront_III_Cloth_Fix/Battlefront_III_Cloth_Fix", "Battlefront_III_Cloth_Fix", "" },
                ModTarget.DataFiles)
        }, CompileMode.TemplatesAndRes),

        // install_restored_melee_classes
        [RestoredMeleeClasses] = new(new[]
        {
            new ModCopyStep(new[] { "RestoredMeleeClasses", "" }, ModTarget.Data)
        }, CompileMode.TemplatesAndRes),

        // install_pc_xbox_features
        [UnlockedFrontend] = new(new[]
        {
            new ModCopyStep(new[] { "frontend_preview", "" }, ModTarget.DataFiles, "data/bf/menus")
        }, CompileMode.TemplatesAndRes),
    };

    public static ModRecipe? GetRecipe(ModItem mod) =>
        !string.IsNullOrEmpty(mod.Filename) && Recipes.TryGetValue(mod.Filename, out var recipe) ? recipe : null;

    // ───────────── State ─────────────

    private readonly DownloadService _downloads;
    private readonly Action<double, string>? _progress;
    private readonly object _lock = new();

    private ModInstallRequest _req = new();
    private ModInstallResult _result = new();
    private string? _gameDir;

    /// <summary>Hand-written installers for recipes marked Custom, keyed by manifest file name.</summary>
    private readonly Dictionary<string, Func<ModItem, string, string, CompileMode>> _customInstallers;

    /// <param name="onProgress">Overall progress (0–100) and a status line. May be called from any thread.</param>
    public ModInstaller(DownloadService downloads, Action<double, string>? onProgress = null)
    {
        _downloads = downloads;
        _progress = onProgress;

        _customInstallers = new Dictionary<string, Func<ModItem, string, string, CompileMode>>(Ci)
        {
            [CustomSkyboxes] = InstallCustomSkyboxes,
            [FixedHvVAndHuntAudio] = InstallFixedHvVAndHuntAudio,
            [KbmControls] = InstallKbmControls,
            [MustafarWaterFix] = InstallMustafarWaterFix,
            [R904DantooineCrashFix] = InstallR904DantooineCrashFix,
        };
    }

    private void Begin(ModInstallRequest req)
    {
        _req = req;
        _result = new ModInstallResult();
        _gameDir = string.IsNullOrEmpty(req.BuildDir) ? null : ResolveGameDir(req.BuildDir);
    }

    // ───────────── Main entry point (install_selected_mods) ─────────────

    public async Task<ModInstallResult> InstallAsync(ModInstallRequest req)
    {
        Begin(req);
        var mods = req.Mods.Distinct().ToList();

        if (mods.Count == 0)
        {
            Error("No mods have been selected.");
            return _result;
        }

        if (_gameDir != null) Info($"Target game folder: {_gameDir}");

        // 1. Pre-checks
        CheckInstallConditions(mods);
        if (!_result.Success)
        {
            Error("Installation aborted due to errors.");
            Report(0, "Installation aborted");
            return _result;
        }

        // 2. Download + extract into the staging folders
        var staged = new List<(ModItem Mod, string Stage)>();
        for (int i = 0; i < mods.Count; i++)
        {
            try
            {
                staged.Add((mods[i], await StageModAsync(mods[i], i, mods.Count)));
            }
            catch (Exception ex)
            {
                Error($"Download failed for {mods[i].Name}: {ex.Message}");
            }
        }
        if (!_result.Success)
        {
            Error("Installation aborted due to errors.");
            return _result;
        }

        // 3. File-name conflicts between the selected mods
        CheckFileNameConflicts(staged);
        if (!_result.Success)
        {
            Error("Installation aborted: the selected mods have conflicting files.");
            Report(80, "Installation aborted (file conflicts)");
            return _result;
        }
        Ok("No conflicts detected. Proceeding with installation...");

        // 4. Copy into place. Lighting Fix is installed last; texture packs before skyboxes.
        var ordered = staged
            .Select((s, index) => (s.Mod, s.Stage, Index: index))
            .OrderBy(s => GetRecipe(s.Mod)?.Order ?? 50)
            .ThenBy(s => s.Index)
            .ToList();

        var compileModes = new List<CompileMode>();
        await Task.Run(() =>
        {
            for (int k = 0; k < ordered.Count; k++)
            {
                var (mod, stage, _) = ordered[k];
                Report(80 + 10.0 * k / ordered.Count, $"Installing {mod.Name}...");
                Info($"Installing: {mod.Name}");
                try
                {
                    compileModes.Add(ApplyMod(mod, stage));
                    lock (_lock) _result.Installed.Add(mod);
                    Ok($"{mod.Name} installed successfully.");
                }
                catch (Exception ex)
                {
                    Error($"Error installing {mod.Name}: {ex.Message}");
                }
            }
        });

        // 5. Compile resources if any installed mod needs it
        bool templates = compileModes.Contains(CompileMode.TemplatesAndRes);
        bool forced = compileModes.Contains(CompileMode.ResForced);
        if (templates || forced)
        {
            await CompileAsync(templates, forced);
        }

        Report(100, _result.Success ? "Mod installation complete!" : "Mod installation finished with errors");
        Log(_result.Success ? "OK" : "WARN", "----- Installation Complete -----");
        return _result;
    }

    // ───────────── Pre-checks (check_install_conditions) ─────────────

    private void CheckInstallConditions(List<ModItem> mods)
    {
        bool Has(string filename) => mods.Any(m => Ci.Equals(m.Filename, filename));

        foreach (var mod in mods)
        {
            var recipe = GetRecipe(mod);
            bool needsGame = recipe?.TouchesGame == true;
            bool needsAppData = recipe?.TouchesAppData == true
                                || ((recipe == null || recipe.Custom) && IsTextureMod(mod));

            if (needsGame && _gameDir == null)
            {
                Error(string.IsNullOrEmpty(_req.BuildDir)
                    ? $"{mod.Name} changes game files, so it needs an installed target build."
                    : $"{mod.Name} changes game files, but no DATA folder was found in {_req.BuildDir}.");
            }

            if (needsAppData && string.IsNullOrWhiteSpace(_req.AppDataLoadDir))
            {
                Error($"{mod.Name} installs into Dolphin's appdata folder, but none is set. " +
                      "Run the setup wizard (or set the Appdata Directory in Settings) first.");
            }
        }

        if (Has(MusicAndCloneVo) && Has(RestoredR7Vehicles))
            Error("'Music and Clone VO' is incompatible with 'Restored r7 Vehicles'. Please install one or the other.");

        if (Has(ClassUniqueIcons) && Has(RestoredMeleeClasses))
            Error("'Class Unique Icons' is incompatible with 'r9 Restored Melee Classes' (which already includes the icon fix). " +
                  "Please install one or the other.");

        if (Has(MusicAndCloneVo) && Has(LightingFix))
            Warn("'Music and Clone VO' and 'Lighting Fix' both change lighting files. Lighting Fix is installed last, so its versions are kept.");
    }

    // ───────────── Download + extract ─────────────

    private async Task<string> StageModAsync(ModItem mod, int index, int count)
    {
        string baseDir = mod.IsUniversal
            ? _req.GeneralModsDir
            : _req.BuildModsDir ?? throw new InvalidOperationException("install the target build first");

        if (string.IsNullOrWhiteSpace(baseDir))
            throw new InvalidOperationException("no mods folder is configured");
        if (string.IsNullOrWhiteSpace(mod.DownloadUrl))
            throw new InvalidOperationException("the manifest has no download URL for it");

        string stage = Path.Combine(baseDir, StageName(mod));
        string marker = Path.Combine(stage, StageMarker);
        string stamp = $"{mod.Version}|{mod.DownloadUrl}";

        double from = 80.0 * index / count;
        double to = 80.0 * (index + 1) / count;

        // Reuse an earlier download of the same version (saves re-downloading e.g. the 5 GB texture pack)
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp)
        {
            Info($"Using already-downloaded files for {mod.Name}: {stage}");
            Report(to, $"{mod.Name}: already downloaded");
            return stage;
        }

        if (Directory.Exists(stage))
            await Task.Run(() => Directory.Delete(stage, recursive: true));

        Info($"Downloading {mod.Name} to {stage}");
        Report(from, $"Downloading {mod.Name}...");
        await _downloads.DownloadAndExtractAsync(mod.DownloadUrl, stage,
            (p, msg) => Report(from + (to - from) * p / 100.0, $"{mod.Name}: {msg}"));

        await File.WriteAllTextAsync(marker, stamp);
        return stage;
    }

    private static string StageName(ModItem mod)
    {
        string name = Path.GetFileNameWithoutExtension(mod.Filename);
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(mod.Url);
        if (string.IsNullOrWhiteSpace(name)) name = !string.IsNullOrEmpty(mod.Id) ? mod.Id : mod.Name;

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ───────────── Conflict check (check_file_name_conflicts) ─────────────

    private void CheckFileNameConflicts(List<(ModItem Mod, string Stage)> staged)
    {
        var owners = new Dictionary<string, HashSet<string>>(Ci);
        var displayNames = new Dictionary<string, string>(Ci);

        foreach (var (mod, stage) in staged)
        {
            if (GetRecipe(mod)?.ReplacesMainDol == true) continue; // main.dol only, nothing to compare

            string key = string.IsNullOrEmpty(mod.Filename) ? mod.Name : mod.Filename;
            displayNames[key] = mod.Name;

            foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!Ci.Equals(ext, ".res") && !Ci.Equals(ext, ".war")) continue;

                string name = Path.GetFileName(file);
                if (!owners.TryGetValue(name, out var set))
                    owners[name] = set = new HashSet<string>(Ci);
                set.Add(key);
            }
        }

        var hudModSet = new[] { RestoredR7Vehicles, RestoredMeleeClasses, ClassUniqueIcons };
        var lightingMusicSet = new[] { LightingFix, MusicAndCloneVo };

        foreach (var (file, set) in owners)
        {
            if (set.Count < 2) continue;

            string modList = string.Join(", ", set.Select(k => displayNames.GetValueOrDefault(k, k)));
            bool dismissed =
                Ci.Equals(file, "hudmgr.res")
                || set.IsSupersetOf(hudModSet)
                || (Ci.Equals(file, "invisible_hand.res") && set.IsSupersetOf(lightingMusicSet));

            if (dismissed)
                Info($"File {file} is present in mods: {modList} (dismissed)");
            else
                Error($"File conflict: {file} is present in mods: {modList}");
        }
    }

    // ───────────── Applying a mod ─────────────

    private CompileMode ApplyMod(ModItem mod, string stage)
    {
        var recipe = GetRecipe(mod);
        string content = ContentRoot(stage);

        if (recipe == null)
            return ApplyAutoDetected(mod, stage, content);

        if (recipe.Custom)
        {
            if (!_customInstallers.TryGetValue(mod.Filename, out var install))
                throw new InvalidOperationException($"no install function is registered for {mod.Filename}");

            var mode = install(mod, stage, content);
            if (recipe.NeedsGame)
                lock (_lock) _result.BuildScoped.Add(mod);
            return mode;
        }

        if (recipe.ReplacesMainDol)
        {
            ReplaceMainDol(stage);
            lock (_lock) _result.BuildScoped.Add(mod);
            return CompileMode.None;
        }

        foreach (var step in recipe.Steps)
        {
            string? src = ResolveSource(stage, content, step.Sources);
            if (src == null)
            {
                if (step.Optional)
                {
                    Info($"Optional folder '{step.Sources[0]}' not in the download; skipping.");
                    continue;
                }
                throw new DirectoryNotFoundException(
                    $"couldn't find '{step.Sources[0]}' in the downloaded files ({stage})");
            }

            CopyStep(src, step.Target, step.TargetSubpath);
        }

        if (recipe.TouchesGame)
            lock (_lock) _result.BuildScoped.Add(mod);

        return recipe.Compile;
    }

    // ───────────── Custom installers (mods that weren't in installer.py) ─────────────
    //
    // Each function gets:
    //   mod     – the manifest entry
    //   stage   – the folder the zip was extracted to (Mods/<zip name> or Builds/<tag>/mods/<zip name>)
    //   content – the stage with any single wrapper folder unwrapped
    // and returns the resource compile the mod needs (CompileMode.None if it changes no .res files).
    //
    // Building blocks:
    //   string src = RequireSource(stage, content, "FolderInZip", "OtherName", "");
    //                                    → finds a folder in the download ("" = content root) or throws
    //   CopyStep(src, ModTarget.DataFiles, "data/bf/xyz");   → copy a folder to a place in the game or appdata
    //   PlaceIntoGame(src, ModTarget.DataFiles);             → copy into the game at the level the layout implies
    //   PlaceTextures(src);                                   → copy into Load/Textures/RABAZZ
    //   CopyFile(src, ModTarget.DataSys, "main.dol");        → copy a single file (src = file path)
    //   MarkBuildScoped(mod);                                  → track as installed per build (wrote game files)
    //
    // If a function writes game files, also set NeedsGame: true on its entry in Recipes,
    // so a missing target build is caught before anything is downloaded.
    //
    // Until a real implementation is written, the placeholders fall back to ApplyAutoDetected,
    // which places files based on the download's folder layout.

    /// <summary>
    /// r2-91120a Custom Skyboxes (r2-91120a_custom_skyboxes.zip) — texture mod, r911.
    /// Replaces skies in Bespin, Cato Neimoidia, Coruscant, Dathomir, Endor, Hoth and Tatooine.
    /// Must overwrite the 4K Texture Pack, so its recipe has Order 20 (the 4K pack is 10).
    /// </summary>
    private CompileMode InstallCustomSkyboxes(ModItem mod, string stage, string content)
    {
        // TODO: confirm the zip layout. Currently copies everything into Load/Textures/RABAZZ.
        PlaceTextures(content);
        return CompileMode.None;
    }

    /// <summary>
    /// Fixed HvV and Hunt Audio (FixedHvVandHuntAudio.zip) — r911.
    /// Re-enables music for Heroes vs Villains and Hunt.
    /// </summary>
    private CompileMode InstallFixedHvVAndHuntAudio(ModItem mod, string stage, string content)
    {
        // TODO: replace with the real steps once the zip layout is known, e.g.:
        //   CopyStep(RequireSource(stage, content, "FixedHvVandHuntAudio", ""), ModTarget.DataFiles, "");
        //   MarkBuildScoped(mod);
        //   return CompileMode.TemplatesAndRes;   // if it changes .res files that need compiling
        return ApplyAutoDetected(mod, stage, content);
    }

    /// <summary>
    /// KBM Controls (KBM_controls.zip) — all builds, including the hybrid builds.
    /// Enables full keyboard controls and fixes the crosshair.
    /// </summary>
    private CompileMode InstallKbmControls(ModItem mod, string stage, string content)
    {
        // TODO: replace with the real steps once the zip layout is known.
        // Likely parts:
        //   - a Dolphin controller profile → CopyStep(RequireSource(stage, content, "Config"), ModTarget.DolphinUser, "Config");
        //   - game files for the crosshair → PlaceIntoGame(RequireSource(stage, content, "DATA"), ModTarget.Data);
        //                                    MarkBuildScoped(mod);
        return ApplyAutoDetected(mod, stage, content);
    }

    /// <summary>
    /// Mustafar Water Fix (mustafar_water_fix.zip) — r911 (untested in r904).
    /// Fixes the Mustafar water effect by removing it.
    /// </summary>
    private CompileMode InstallMustafarWaterFix(ModItem mod, string stage, string content)
    {
        // TODO: replace with the real steps once the zip layout is known, e.g.:
        //   CopyStep(RequireSource(stage, content, "data"), ModTarget.DataFiles, "data");
        //   MarkBuildScoped(mod);
        //   return CompileMode.TemplatesAndRes;   // if it changes .res files that need compiling
        return ApplyAutoDetected(mod, stage, content);
    }

    /// <summary>
    /// r904 Dantooine Crash Fix (r904_dantooine_crash_fix.zip) — Hybrid r904 (tag r904-H).
    /// </summary>
    private CompileMode InstallR904DantooineCrashFix(ModItem mod, string stage, string content)
    {
        // TODO: replace with the real steps once the zip layout is known, e.g.:
        //   PlaceIntoGame(RequireSource(stage, content, ""), ModTarget.DataFiles);
        //   MarkBuildScoped(mod);
        //   return CompileMode.TemplatesAndRes;   // if it changes .res files that need compiling
        return ApplyAutoDetected(mod, stage, content);
    }

    // Helpers for the custom installers

    private static string RequireSource(string stage, string content, params string[] sources) =>
        ResolveSource(stage, content, sources)
        ?? throw new DirectoryNotFoundException(
            $"couldn't find '{sources.FirstOrDefault()}' in the downloaded files ({stage})");

    private void CopyFile(string srcFile, ModTarget target, string subpath)
    {
        string dest = TargetPath(target, subpath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(srcFile, dest, overwrite: true);
        Ok($"File '{Path.GetFileName(srcFile)}' copied to {dest}");
    }

    private void MarkBuildScoped(ModItem mod)
    {
        lock (_lock) _result.BuildScoped.Add(mod);
    }

    /// <summary>
    /// Fallback for mods without a real install function: place files based on what the download contains.
    /// </summary>
    private CompileMode ApplyAutoDetected(ModItem mod, string stage, string content)
    {
        Info($"{mod.Name}: placing files based on the download's folder layout.");

        if (IsTextureMod(mod))
        {
            PlaceTextures(content);
            return CompileMode.None;
        }

        bool placed = false;

        // Dolphin user folders (profiles, per-game settings, Load)
        var userFolders = new HashSet<string>(Ci) { "Config", "GameSettings", "Load" };
        foreach (string dir in Directory.GetDirectories(content))
        {
            string name = Path.GetFileName(dir);
            if (!userFolders.Contains(name)) continue;
            CopyLogged(dir, ResolveDestPath(RequireDolphinUserDir(), name));
            placed = true;
        }

        // Game files
        var compile = CompileMode.None;
        bool hasGameLayout = new[] { "DATA", "files", "sys", "assets", "data" }.Any(n => HasChild(content, n));
        if (hasGameLayout)
        {
            PlaceIntoGame(content, ModTarget.GameRoot, userFolders);
            lock (_lock) _result.BuildScoped.Add(mod);
            placed = true;

            bool hasRes = Directory.EnumerateFiles(content, "*.res", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive
            }).Any();
            if (hasRes) compile = CompileMode.TemplatesAndRes;
        }

        if (!placed)
            throw new InvalidOperationException(
                $"couldn't work out where its files go. They're in {stage}; please copy them in manually");

        return compile;
    }

    private void CopyStep(string src, ModTarget target, string subpath)
    {
        // If the source already contains the first folder of the sub-path (e.g. it has "data/..."
        // and we were going to put it in ".../data"), copy it one level up so it isn't nested twice.
        if (!string.IsNullOrEmpty(subpath))
        {
            string first = subpath.Split('/', '\\')[0];
            if (HasChild(src, first)) subpath = "";
        }

        if (string.IsNullOrEmpty(subpath))
        {
            if (target is ModTarget.GameRoot or ModTarget.Data or ModTarget.DataFiles)
            {
                PlaceIntoGame(src, target);
                return;
            }
            if (target == ModTarget.Textures)
            {
                PlaceTextures(src);
                return;
            }
        }

        CopyLogged(src, TargetPath(target, subpath));
    }

    /// <summary>
    /// Copies into the game at the level the source's layout implies
    /// (DATA/... → game root, files/sys → DATA, assets/data → DATA/files), else at <paramref name="fallback"/>.
    /// </summary>
    private void PlaceIntoGame(string src, ModTarget fallback, ISet<string>? skipTopLevel = null)
    {
        string dest;
        if (HasChild(src, "DATA"))
            dest = RequireGameDir();
        else if (HasChild(src, "files") || HasChild(src, "sys"))
            dest = TargetPath(ModTarget.Data, "");
        else if (HasChild(src, "assets") || HasChild(src, "data"))
            dest = TargetPath(ModTarget.DataFiles, "");
        else
            dest = TargetPath(fallback, "");

        CopyLogged(src, dest, skipTopLevel);
    }

    /// <summary>Copies textures into Load/Textures/RABAZZ, adjusting if the download already includes those folders.</summary>
    private void PlaceTextures(string src)
    {
        string dest;
        if (HasChild(src, "Load"))
            dest = RequireDolphinUserDir();
        else if (HasChild(src, "Textures"))
            dest = RequireLoadDir();
        else if (HasChild(src, GameId))
            dest = ResolveDestPath(RequireLoadDir(), "Textures");
        else
            dest = TargetPath(ModTarget.Textures, "");

        CopyLogged(src, dest);
    }

    private void ReplaceMainDol(string stage)
    {
        string? dol = Directory.EnumerateFiles(stage, "main.dol", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive
            })
            .OrderBy(p => p.Length)
            .FirstOrDefault();

        if (dol == null)
            throw new FileNotFoundException($"main.dol not found in the downloaded files ({stage})");

        string sysDir = TargetPath(ModTarget.DataSys, "");
        Directory.CreateDirectory(sysDir);
        string dest = MatchExisting(sysDir, "main.dol", isDirectory: false);

        // Keep the original once, so it can be restored
        string backup = dest + ".original";
        if (File.Exists(dest) && !File.Exists(backup))
        {
            File.Copy(dest, backup);
            Info($"Backed up the original main.dol to {backup}");
        }

        File.Copy(dol, dest, overwrite: true);
        Ok($"File 'main.dol' copied to {sysDir}");
    }

    // ───────────── Resource compiling (compile_templates_res) ─────────────

    private async Task CompileAsync(bool templates, bool forced)
    {
        if (_gameDir == null)
        {
            Error("Resource compilation needs an installed target build.");
            return;
        }

        string filesDir = TargetPath(ModTarget.DataFiles, "");
        Report(90, "Preparing the resource compiler...");
        if (!await EnsureResCompilerAsync(filesDir)) return;

        if (templates)
        {
            await RepairGameAsync(all: false);

            Info("Compilation required. Starting compilation process...");
            Report(94, "Compiling templates and resources...");
            await RunCompileBatchAsync(filesDir, "compile_templates_and_res.bat");
        }

        if (forced)
        {
            Report(97, "Compiling resources (forced) for Lighting Fix...");
            await RunCompileBatchAsync(filesDir, "compile_all_res_forced.bat");
        }
    }

    /// <summary>
    /// Makes sure the compile batch files are in DATA/files, downloading the
    /// Embedded ResCompiler tool and copying it in if they're missing.
    /// </summary>
    private async Task<bool> EnsureResCompilerAsync(string filesDir)
    {
        if (FindFile(filesDir, "compile_templates_and_res.bat") != null) return true;

        Warn("compile_templates_and_res.bat not found in DATA/files. Copying the Embedded ResCompiler...");
        string? toolDir = await EnsureToolAsync(ResCompilerTool, 90, 93);
        if (toolDir == null) return false;

        string? bat = Directory.EnumerateFiles(toolDir, "compile_templates_and_res.bat", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive
            })
            .OrderBy(p => p.Length)
            .FirstOrDefault();

        if (bat == null)
        {
            Error($"compile_templates_and_res.bat wasn't found in the Embedded ResCompiler download ({toolDir}).");
            return false;
        }

        await Task.Run(() => CopyLogged(Path.GetDirectoryName(bat)!, filesDir));

        if (FindFile(filesDir, "compile_templates_and_res.bat") != null) return true;

        Error("compile_templates_and_res.bat still not found after copying. Please check manually.");
        return false;
    }

    private async Task<bool> RunCompileBatchAsync(string filesDir, string batchName)
    {
        string? bat = FindFile(filesDir, batchName);
        if (bat == null)
        {
            Error($"{batchName} not found in {filesDir}.");
            return false;
        }

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "wine",
            WorkingDirectory = Path.GetDirectoryName(bat)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closed right away so any "pause" in the batch file can't hang
            CreateNoWindow = true
        };
        if (!isWindows) psi.ArgumentList.Add("cmd");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(Path.GetFileName(bat));

        Info($"Starting compilation: {batchName}{(isWindows ? "" : " (via Wine)")}");

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) Log("OUT", a.Data); };
        proc.ErrorDataReceived += (_, a) => { if (!string.IsNullOrWhiteSpace(a.Data)) Log("OUT", a.Data); };

        try
        {
            proc.Start();
        }
        catch (Win32Exception)
        {
            Error(isWindows
                ? "Couldn't start cmd.exe to run the resource compiler."
                : "Wine is required to run the resource compiler on Linux. Install Wine and try again.");
            return false;
        }

        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();

        if (proc.ExitCode == 0)
        {
            Ok($"Resource compilation completed successfully ({batchName}).");
            return true;
        }

        Error($"Error during resource compilation: {batchName} exited with code {proc.ExitCode}.");
        return false;
    }

    // ───────────── Repair (repair_game) ─────────────

    /// <summary>
    /// Restores stock files from the r911 Repair Files tool, downloading it if needed.
    /// Default: embed_wi_v4 only (run before compiling). all=true also restores assets/bf/data and main.dol.
    /// Only runs for builds the repair files are made for (r911, r911-M, ...).
    /// </summary>
    public async Task<ModInstallResult> RepairGameAsync(ModInstallRequest req, bool all)
    {
        Begin(req);
        if (_gameDir == null)
        {
            Error("Repair needs an installed target build.");
            return _result;
        }
        await RepairGameAsync(all);
        return _result;
    }

    private async Task RepairGameAsync(bool all)
    {
        var tool = FindTool(RepairFilesTool);
        if (tool == null || _req.TargetBuildTag == null)
        {
            Warn("Repair files aren't available; skipping the embed_wi_v4 restore.");
            return;
        }

        if (!string.IsNullOrEmpty(tool.Build)
            && !Ci.Equals(BaseRevision(tool.Build), BaseRevision(_req.TargetBuildTag)))
        {
            Info($"Skipping repair files: they're for {tool.Build} builds, not {_req.TargetBuildTag}.");
            return;
        }

        string? toolDir = await EnsureToolAsync(RepairFilesTool, 91, 93);
        if (toolDir == null) return;

        string filesDir = TargetPath(ModTarget.DataFiles, "");

        await Task.Run(() =>
        {
            string? embed = FindDirectory(toolDir, "embed_wi_v4");
            if (embed != null)
                CopyLogged(embed, ResolveDestPath(filesDir, "assets/bf/embed_wi_v4"));
            else
                Warn("embed_wi_v4 not found in the repair files.");

            if (!all) return;

            string? data = FindDirectory(toolDir, "data");
            if (data != null)
                CopyLogged(data, ResolveDestPath(filesDir, "assets/bf/data"));

            string? dol = Directory.EnumerateFiles(toolDir, "main.dol", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive
            }).FirstOrDefault();
            if (dol != null)
            {
                string sysDir = TargetPath(ModTarget.DataSys, "");
                File.Copy(dol, MatchExisting(sysDir, "main.dol", isDirectory: false), overwrite: true);
                Ok($"File 'main.dol' copied to {sysDir}");
            }
        });
    }

    private static string BaseRevision(string tag) => tag.Split('-')[0];

    // ───────────── Uninstall textures (uninstall_textures) ─────────────

    /// <summary>Removes everything in Load/Textures and Load/DynamicInputTextures.</summary>
    public static void UninstallTextures(string loadDir)
    {
        foreach (string sub in new[] { "Textures", "DynamicInputTextures" })
        {
            string dir = MatchExisting(loadDir, sub, isDirectory: true);
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"[INFO] Path {dir} does not exist.");
                continue;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(dir).ToList())
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to delete {entry}: {ex.Message}");
                }
            }
            Console.WriteLine($"[OK] All files and folders in {dir} have been deleted.");
        }
    }

    // ───────────── Tools ─────────────

    private ToolItem? FindTool(string filename) =>
        _req.Manifest.Tools.FirstOrDefault(t => Ci.Equals(t.Filename, filename));

    /// <summary>Downloads a tool from the manifest into Tools/&lt;Name&gt; if it isn't there yet.</summary>
    private async Task<string?> EnsureToolAsync(string filename, double progressFrom, double progressTo)
    {
        var tool = FindTool(filename);
        if (tool == null)
        {
            Error($"{filename} is not listed in the manifest's tools.");
            return null;
        }

        string dir = Path.Combine(_req.ToolsDir, tool.Name);
        bool present = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

        if (!present)
        {
            Info($"Downloading tool: {tool.Name}");
            try
            {
                await _downloads.DownloadAndExtractAsync(tool.DownloadUrl, dir,
                    (p, msg) => Report(progressFrom + (progressTo - progressFrom) * p / 100.0, $"{tool.Name}: {msg}"));
            }
            catch (Exception ex)
            {
                Error($"Failed to download {tool.Name}: {ex.Message}");
                return null;
            }
        }

        string key = !string.IsNullOrEmpty(tool.Id) ? tool.Id : tool.Name;
        _req.Config.InstalledVersions[key] = tool.Version;
        ConfigManager.Save(_req.Config);
        return dir;
    }

    // ───────────── Paths ─────────────

    /// <summary>
    /// The folder that contains DATA for an installed build (e.g. Builds/r911-M/Battlefront III r2.91120a Unpacked).
    /// </summary>
    public static string? ResolveGameDir(string buildDir)
    {
        if (!Directory.Exists(buildDir)) return null;

        string dol = InstallerUtils.FindMainDol(buildDir);
        if (!string.IsNullOrEmpty(dol))
        {
            string? data = Path.GetDirectoryName(Path.GetDirectoryName(dol) ?? "");
            if (data != null && Ci.Equals(Path.GetFileName(data), "DATA"))
                return Path.GetDirectoryName(data);
        }

        // Fallback: the shallowest DATA folder, ignoring downloaded mods in Builds/<tag>/mods
        string modsDir = Path.Combine(buildDir, "mods") + Path.DirectorySeparatorChar;
        string? dataDir = Directory.EnumerateDirectories(buildDir, "*", SearchOption.AllDirectories)
            .Where(d => Ci.Equals(Path.GetFileName(d), "DATA"))
            .Where(d => !d.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Length)
            .FirstOrDefault();

        return dataDir == null ? null : Path.GetDirectoryName(dataDir);
    }

    private string RequireGameDir() =>
        _gameDir ?? throw new InvalidOperationException("it changes game files, so it needs an installed target build");

    private string RequireLoadDir() =>
        !string.IsNullOrWhiteSpace(_req.AppDataLoadDir)
            ? _req.AppDataLoadDir
            : throw new InvalidOperationException("no Dolphin appdata (User/Load) folder is set");

    private string RequireDolphinUserDir() =>
        Path.GetDirectoryName(RequireLoadDir().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        ?? throw new InvalidOperationException("couldn't find Dolphin's User folder");

    private string TargetPath(ModTarget target, string subpath)
    {
        string basePath = target switch
        {
            ModTarget.GameRoot => RequireGameDir(),
            ModTarget.Data => ResolveDestPath(RequireGameDir(), "DATA"),
            ModTarget.DataFiles => ResolveDestPath(RequireGameDir(), "DATA/files"),
            ModTarget.DataSys => ResolveDestPath(RequireGameDir(), "DATA/sys"),
            ModTarget.AppDataLoad => RequireLoadDir(),
            ModTarget.Textures => ResolveDestPath(RequireLoadDir(), $"Textures/{GameId}"),
            ModTarget.DolphinUser => RequireDolphinUserDir(),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        return string.IsNullOrEmpty(subpath) ? basePath : ResolveDestPath(basePath, subpath);
    }

    /// <summary>
    /// Builds a destination path, reusing existing folders whatever their case
    /// (so "data" lands in an existing "Data" on case-sensitive Linux file systems).
    /// </summary>
    private static string ResolveDestPath(string basePath, string relative)
    {
        string current = basePath;
        foreach (string segment in relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            current = MatchExisting(current, segment, isDirectory: true);
        return current;
    }

    private static string MatchExisting(string parent, string name, bool isDirectory)
    {
        string exact = Path.Combine(parent, name);
        if (isDirectory ? Directory.Exists(exact) : File.Exists(exact)) return exact;
        if (!Directory.Exists(parent)) return exact;

        var entries = isDirectory ? Directory.EnumerateDirectories(parent) : Directory.EnumerateFiles(parent);
        return entries.FirstOrDefault(e => Ci.Equals(Path.GetFileName(e), name)) ?? exact;
    }

    // ───────────── Finding things inside a download ─────────────

    /// <summary>
    /// Unwraps single wrapper folders (e.g. "lighting_fix/SomeFolder/...") down to the real content,
    /// stopping at structural folders like DATA, data or Textures.
    /// </summary>
    private static string ContentRoot(string stage)
    {
        string dir = stage;
        while (true)
        {
            bool hasFiles = Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f) != StageMarker);
            var dirs = Directory.GetDirectories(dir)
                .Where(d => !Ci.Equals(Path.GetFileName(d), "__MACOSX"))
                .ToArray();

            if (hasFiles || dirs.Length != 1) return dir;
            if (StructuralNames.Contains(Path.GetFileName(dirs[0]))) return dir;
            dir = dirs[0];
        }
    }

    private static string? ResolveSource(string stage, string content, string[] sources)
    {
        foreach (string source in sources)
        {
            if (source == "") return content;

            string? found = ResolveRelative(stage, source)
                            ?? ResolveRelative(content, source)
                            ?? FindBySuffix(stage, source);
            if (found != null) return found;
        }
        return null;
    }

    private static string? ResolveRelative(string root, string relative)
    {
        string? current = root;
        foreach (string segment in relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = FindChildDir(current, segment);
            if (current == null) return null;
        }
        return current;
    }

    /// <summary>Finds the shallowest folder whose path ends with <paramref name="relative"/> (case-insensitive).</summary>
    private static string? FindBySuffix(string root, string relative)
    {
        string[] segments = relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => (Dir: d, Parts: Path.GetRelativePath(root, d).Split(separators)))
            .Where(x => x.Parts.Length >= segments.Length
                        && x.Parts.Skip(x.Parts.Length - segments.Length).SequenceEqual(segments, Ci))
            .OrderBy(x => x.Parts.Length)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static string? FindDirectory(string root, string name) => FindBySuffix(root, name);

    private static string? FindChildDir(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (Directory.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir).FirstOrDefault(d => Ci.Equals(Path.GetFileName(d), name));
    }

    private static bool HasChild(string dir, string name) => FindChildDir(dir, name) != null;

    private static string? FindFile(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => Ci.Equals(Path.GetFileName(f), name));
    }

    private static bool IsTextureMod(ModItem mod) =>
        mod.Category?.Contains("Texture", StringComparison.OrdinalIgnoreCase) == true;

    // ───────────── Copying ─────────────

    private void CopyLogged(string src, string dest, ISet<string>? skipTopLevel = null)
    {
        int count = CopyMerge(src, dest, skipTopLevel);
        Ok($"Copied {count} file(s) from {src} to {dest}");
    }

    /// <summary>
    /// Recursively copies src into dest, overwriting files and merging into existing
    /// folders regardless of case. Skips the staging marker file.
    /// </summary>
    private static int CopyMerge(string src, string dest, ISet<string>? skipTopLevel = null)
    {
        int count = 0;
        Directory.CreateDirectory(dest);

        // Index what's already in dest once (texture folders can hold thousands of files)
        var existingFiles = IndexByName(Directory.EnumerateFiles(dest));
        var existingDirs = IndexByName(Directory.EnumerateDirectories(dest));

        foreach (string file in Directory.EnumerateFiles(src))
        {
            string name = Path.GetFileName(file);
            if (name == StageMarker || skipTopLevel?.Contains(name) == true) continue;

            File.Copy(file, PickTarget(dest, name, existingFiles, File.Exists), overwrite: true);
            count++;
        }

        foreach (string dir in Directory.EnumerateDirectories(src))
        {
            string name = Path.GetFileName(dir);
            if (skipTopLevel?.Contains(name) == true || Ci.Equals(name, "__MACOSX")) continue;

            count += CopyMerge(dir, PickTarget(dest, name, existingDirs, Directory.Exists));
        }

        return count;
    }

    private static Dictionary<string, string> IndexByName(IEnumerable<string> paths)
    {
        var index = new Dictionary<string, string>(Ci);
        foreach (string path in paths)
            index.TryAdd(Path.GetFileName(path), path);
        return index;
    }

    /// <summary>Exact name if it exists, else an existing entry with the same name in another case, else the exact name.</summary>
    private static string PickTarget(string dest, string name, Dictionary<string, string> existing, Func<string, bool> exists)
    {
        string exact = Path.Combine(dest, name);
        if (exists(exact)) return exact;
        return existing.TryGetValue(name, out string? match) ? match : exact;
    }

    // ───────────── Logging / progress ─────────────

    private void Report(double percent, string message) =>
        _progress?.Invoke(Math.Clamp(percent, 0, 100), message);

    private static void Log(string level, string message) =>
        Console.WriteLine($"[{level}] {message}");

    private static void Info(string message) => Log("INFO", message);
    private static void Ok(string message) => Log("OK", message);

    private void Warn(string message)
    {
        Log("WARN", message);
        lock (_lock) _result.Warnings.Add(message);
    }

    private void Error(string message)
    {
        Log("ERROR", message);
        lock (_lock) _result.Errors.Add(message);
    }
}