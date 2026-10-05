// Services/ModInstaller.cs
// Installs mods from the manifest into a build (C# port of the Python installer.py).
//
// InstallAsync:
//   1. Pre-checks: required folders, incompatible mods
//   2. Download + extract each mod (reused if that version is already downloaded):
//        mods for all builds → <General Mods>/<zip name>
//        build-specific mods → <Builds>/<tag>/mods/<zip name>
//   3. .res / .war file-name conflict check between the selected mods
//   4. Copy the mods that need compiling into place by their recipes, in Order (Lighting Fix last):
//        game files → the target build's game folder (the folder containing DATA)
//   5. Compile ONCE: put the Embedded ResCompiler in DATA/files, restore embed_wi_v4 from the
//      repair files, then run a single batch file (compile_templates_and_res.bat when templates are
//      needed, otherwise compile_all_res_forced.bat).
//   6. Copy the mods that don't need compiling, after the compile, in Order
//      (4K pack → skyboxes → others → 4K Character models):
//        textures → Dolphin's User/Load folder (Textures/RABAZZ, DynamicInputTextures)
//
// A build may only be compiled once — compiling over already-compiled mod files breaks the game.
// If an install needs a compile on a build that was compiled before, the build's stock files are
// restored from the repair files first and its earlier game-file mods are reinstalled with the new ones.
//
// Mods, rules and tools are matched by their manifest "name" (case-insensitive).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static SWBF_C_build.Services.ModTarget;

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
    // An install runs ONE compile: TemplatesAndRes if any mod needs it (it compiles the .res files too),
    // otherwise compile_all_res_forced.bat for either res mode (see CompileAsync).
    Res,             // the modder says compile_all_res.bat
    TemplatesAndRes, // compile_templates_and_res.bat
    ResForced        // the modder says a forced/second res compile (Lighting Fix)
}

/// <summary>
/// Copies one folder from the download to <see cref="Target"/>/<see cref="TargetSubpath"/>.
/// Sources are tried in order: a folder path inside the download (case-insensitive, also found if
/// nested deeper), or "" for the download's content root.
/// </summary>
public sealed record ModCopyStep(ModTarget Target, string TargetSubpath, string[] Sources, bool Optional = false);

/// <summary>What the installer passes to a custom install function.</summary>
/// <param name="Mod">The manifest entry.</param>
/// <param name="Stage">Folder the download was extracted to.</param>
/// <param name="Content">The stage with any single wrapper folder unwrapped.</param>
public sealed record ModContext(ModItem Mod, string Stage, string Content);

/// <summary>How one mod is installed.</summary>
public sealed record ModRecipe(params ModCopyStep[] Steps)
{
    public CompileMode Compile { get; init; } = CompileMode.None;

    /// <summary>Install order within one install; lower goes first.</summary>
    public int Order { get; init; } = 50;

    public bool ReplacesMainDol { get; init; }

    /// <summary>Hand-written installer used instead of <see cref="Steps"/>. Returns the compile it needs.</summary>
    public Func<ModInstaller, ModContext, CompileMode>? Custom { get; init; }

    /// <summary>For custom installers that write game files (checked before anything is downloaded).</summary>
    public bool NeedsGame { get; init; }

    /// <summary>For custom installers that write to Dolphin's User folder (checked before anything is downloaded).</summary>
    public bool NeedsAppData { get; init; }

    /// <summary>
    /// Copy this mod before the compile (with the mods that need compiling) even though it doesn't need
    /// one itself — as installer.py did, which copied every mod before compiling.
    /// </summary>
    public bool BeforeCompile { get; init; }

    /// <summary>Manifest names of mods that can't be installed together with this one.</summary>
    public string[] IncompatibleWith { get; init; } = Array.Empty<string>();

    /// <summary>True when the mod writes into a build's game files (so it's tracked per build).</summary>
    public bool TouchesGame => NeedsGame || ReplacesMainDol || Steps.Any(s => s.Target is GameRoot or Data or DataFiles or DataSys);

    public bool TouchesAppData => NeedsAppData || Steps.Any(s => s.Target is AppDataLoad or Textures or DolphinUser);
}

public sealed class ModInstallRequest
{
    public IReadOnlyList<ModItem> Mods { get; init; } = Array.Empty<ModItem>();

    /// <summary>Tag of the installed target build, or null if none is installed.</summary>
    public string? TargetBuildTag { get; init; }

    /// <summary>Builds/&lt;tag&gt; for the installed target build, or null.</summary>
    public string? BuildDir { get; init; }

    /// <summary>Download folder for build-specific mods (Builds/&lt;tag&gt;/mods).</summary>
    public string? BuildModsDir { get; init; }

    /// <summary>Download folder for mods compatible with all builds.</summary>
    public string GeneralModsDir { get; init; } = "";

    /// <summary>Dolphin's User/Load folder (config.AppDataDir).</summary>
    public string AppDataLoadDir { get; init; } = "";

    /// <summary>Tools folder next to the program.</summary>
    public string ToolsDir { get; init; } = "";

    /// <summary>The portable Dolphin folder next to the program (for its Sys folder).</summary>
    public string DolphinDir { get; init; } = "";

    /// <summary>
    /// Mods that have already changed the target build's game files (from earlier installs).
    /// Reinstalled along with the new mods when the build has to be recompiled from its stock files.
    /// </summary>
    public IReadOnlyList<ModItem> InstalledGameMods { get; init; } = Array.Empty<ModItem>();

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
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>A compiler message that means the compile failed (FrdEmbeddedResCompiler, cmd).</summary>
    private static readonly Regex CompilerProblem = new(
        @"can'?t open|cannot open|could not|\berror\b|failed|is not recognized",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A .res file name in the compiler's output (for compile progress).</summary>
    private static readonly Regex ResFileName = new(@"[\w\-.]+\.res\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly EnumerationOptions RecursiveCi = new()
    {
        RecurseSubdirectories = true,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private const string GameId = "RABAZZ";
    private const string StageMarker = ".swbf3_mod_version";

    // Tools the installer uses (manifest "name")
    private const string ResCompilerTool = "Embedded ResCompiler with Templates";
    public const string RepairFilesTool = "r911 Repair Files";

    // Install order (lower goes first)
    private const int OrderFirst = 10, OrderAfterTexturePack = 20, OrderAfterClothFix = 60, OrderLast = 100;

    /// <summary>Folder names that are real content, so ContentRoot doesn't unwrap past them.</summary>
    private static readonly HashSet<string> StructuralNames = new(Ci)
    {
        "DATA", "files", "sys", "data", "assets", "bf", "menus",
        "Load", "Textures", "DynamicInputTextures", GameId,
        "Config", "GameSettings", "characters", "minimaps"
    };

    // ───────────── Recipes, keyed by manifest mod name ─────────────
    //
    // Copy(target, subpath, sources...): the first source is the folder name installer.py used;
    // later ones are fallbacks for the current archives. "" = the download's content root.

    public static readonly IReadOnlyDictionary<string, ModRecipe> Recipes = new Dictionary<string, ModRecipe>(Ci)
    {
        ["4k Texture Packs"] = new(
            Copy(Textures, "", "4kTexturePacks", ""))
        { Order = OrderFirst },

        // Ported directly from installer.py (see Install4kCharactersModelFix). Like there, it's copied before
        // the compile (after r911 Cloth Fix, which ships the same X1 bundles), without needing one itself.
        ["4k Character Texture Pack and Model Fix"] = new()
        {
            NeedsGame = true, NeedsAppData = true, BeforeCompile = true, Order = OrderAfterClothFix,
            Custom = (i, c) => i.Install4kCharactersModelFix(c)
        },

        ["Class Unique Icons"] = new(
            Copy(Data, "", "ClassUniqueIcons", ""))
        { Compile = CompileMode.TemplatesAndRes, IncompatibleWith = new[] { "r9 Restored Melee Classes" } },

        ["Dynamic Input Textures"] = new(
            Copy(AppDataLoad, "DynamicInputTextures", "DynamicInputTextures/DynamicInputTextures", "DynamicInputTextures", ""),
            OptionalCopy(AppDataLoad, "DynamicInputTextures/RABAZZ", "RABAZZ_dynamic/RABAZZ")),

        ["HD User Interface"] = new(
            Copy(Textures, "", "HD_interface", "")),

        ["Music and Clone VO"] = new(
            Copy(GameRoot, "", "Music_and_Clone_VO/Music_and_Clone_VO", "Music_and_Clone_VO", ""))
        { Compile = CompileMode.TemplatesAndRes, IncompatibleWith = new[] { "Restored r7 Vehicles" } },

        // installer.py copied its folder into the game root; the current zip has the 2994 .dsp files loose,
        // so they replace the game's files of the same name (see InstallMutedBlankSounds)
        ["Muted Blank Sounds"] = new() { NeedsGame = true, Custom = (i, c) => i.InstallMutedBlankSounds(c) },

        ["Restored r7 Vehicles"] = new(
            Copy(Data, "", "restored_r7_vehicles", ""))
        { Compile = CompileMode.TemplatesAndRes },

        ["Faithful HP Bars v2"] = new(
            Copy(Textures, "_faithful_hpbars_v2", "_faithful_hpbars_v2", "")),

        // Modder: everything in "SWBF3 Wii Light Fixes" → DATA/files, then compile with scene_descriptors.res
        // re-saved (see InstallLightingFix). Installed last.
        ["Lighting Fix"] = new()
        {
            NeedsGame = true, Compile = CompileMode.ResForced, Order = OrderLast,
            Custom = (i, c) => i.InstallLightingFix(c)
        },

        ["r904 Fixed Minimaps"] = new(
            Copy(Textures, "minimaps", "minimaps/minimaps", "minimaps", "")),

        ["r91120a Updated Main DOL"] = new() { ReplacesMainDol = true },

        // Modder: extract both folders into DATA/files, overwriting, then run compile_all_res.bat
        ["r911 Cloth Fix"] = new(
            Copy(DataFiles, "", "Battlefront_III_Cloth_Fix/Battlefront_III_Cloth_Fix", "Battlefront_III_Cloth_Fix", ""))
        { Compile = CompileMode.Res },

        ["r9 Restored Melee Classes"] = new(
            Copy(Data, "", "RestoredMeleeClasses", ""))
        { Compile = CompileMode.TemplatesAndRes },

        // Modder: place frontend_preview.res in data/bf/menus, then run compile_all_res
        ["Unlocked PC Xbox Frontend"] = new() { NeedsGame = true, Compile = CompileMode.Res, Custom = (i, c) => i.InstallUnlockedFrontend(c) },

        // Not in installer.py: hand-written installers (see "Custom installers" below)
        ["r2-91120a Custom Skyboxes"] = new() { Order = OrderAfterTexturePack, NeedsAppData = true, Custom = (i, c) => i.InstallCustomSkyboxes(c) },
        ["Fixed HvV and Hunt Audio"] = new() { NeedsGame = true, Custom = (i, c) => i.InstallFixedHvVAndHuntAudio(c) },
        ["KBM Controls"] = new() { NeedsGame = true, NeedsAppData = true, Compile = CompileMode.TemplatesAndRes, Custom = (i, c) => i.InstallKbmControls(c) },
        ["Mustafar Water Fix"] = new() { NeedsGame = true, Custom = (i, c) => i.InstallMustafarWaterFix(c) },
        ["r904 Dantooine Crash Fix"] = new() { NeedsGame = true, Compile = CompileMode.Res, Custom = (i, c) => i.InstallR904DantooineCrashFix(c) },
    };

    /// <summary>Selected together → warning only.</summary>
    private static readonly (string A, string B, string Message)[] WarnPairs =
    {
        ("Music and Clone VO", "Lighting Fix",
            "'Music and Clone VO' and 'Lighting Fix' both change lighting files. Lighting Fix is installed last, so its versions are kept."),
    };

    /// <summary>
    /// .res/.war files that may appear in more than one selected mod (from installer.py).
    /// File null = any file; the rule applies when all listed mods are among the owners (empty = any mods).
    /// </summary>
    private static readonly (string? File, string[] Mods)[] AllowedSharedFiles =
    {
        ("hudmgr.res", Array.Empty<string>()),
        (null, new[] { "Restored r7 Vehicles", "r9 Restored Melee Classes", "Class Unique Icons" }),
        ("invisible_hand.res", new[] { "Lighting Fix", "Music and Clone VO" }),

        // Both ship the X1 cloth model bundles (x1_texbone_cloth*). They can be installed together;
        // the 4K pack is installed after the Cloth Fix so its X1 models are the ones kept.
        (null, new[] { "4k Character Texture Pack and Model Fix", "r911 Cloth Fix" }),
    };

    private static ModCopyStep Copy(ModTarget target, string subpath, params string[] sources) =>
        new(target, subpath, sources);

    private static ModCopyStep OptionalCopy(ModTarget target, string subpath, params string[] sources) =>
        new(target, subpath, sources, Optional: true);

    public static ModRecipe? GetRecipe(ModItem mod) =>
        !string.IsNullOrEmpty(mod.Name) && Recipes.TryGetValue(mod.Name, out var recipe) ? recipe : null;

    // ───────────── State ─────────────

    private readonly DownloadService _downloads;
    private readonly Action<double, string>? _progress;
    private readonly object _lock = new();

    private ModInstallRequest _req = new();
    private ModInstallResult _result = new();
    private string? _gameDir;

    /// <param name="onProgress">Overall progress (0–100) and a status line. May be called from any thread.</param>
    public ModInstaller(DownloadService downloads, Action<double, string>? onProgress = null)
    {
        _downloads = downloads;
        _progress = onProgress;
    }

    private void Begin(ModInstallRequest req)
    {
        _req = req;
        _result = new ModInstallResult();
        _gameDir = string.IsNullOrEmpty(req.BuildDir) ? null : ResolveGameDir(req.BuildDir);
        LogNamesMissingFromManifest(req.Manifest);
    }

    // ───────────── Install (install_selected_mods) ─────────────

    public async Task<ModInstallResult> InstallAsync(ModInstallRequest req)
    {
        Begin(req);
        var selected = req.Mods.Distinct().ToList();

        if (selected.Count == 0)
            return Abort("No mods have been selected.", 0);

        if (_gameDir != null) Info($"Target game folder: {_gameDir}");

        // A build may only be compiled once: compiling over already-compiled mod files breaks the game.
        // If this install needs a compile and the build was compiled before, restore the stock files
        // and reinstall the build's earlier game-file mods together with the new ones, then compile once.
        bool rebuild = selected.Any(NeedsCompile) && req.InstalledGameMods.Any(NeedsCompile);
        var mods = selected;
        if (rebuild)
        {
            var reinstall = req.InstalledGameMods.Where(m => !selected.Contains(m)).ToList();
            mods = reinstall.Concat(selected).Distinct().ToList();
            Info($"This build has been compiled before, so its stock files will be restored and {reinstall.Count} " +
                 "earlier mod(s) reinstalled with the new one(s) before a single compile" +
                 (reinstall.Count > 0 ? $": {string.Join(", ", reinstall.Select(m => m.Name))}." : "."));
        }

        // 1. Pre-checks
        CheckInstallConditions(mods);
        if (rebuild && (_req.TargetBuildTag == null || !RepairFilesApplyTo(_req.Manifest, _req.TargetBuildTag)))
        {
            Error($"This build has already been compiled, and compiling it again would break it. Recompiling cleanly " +
                  $"needs the {RepairFilesTool}, which aren't made for {_req.TargetBuildTag ?? "this build"}.");
        }
        if (!_result.Success)
            return Abort("Installation aborted due to errors.", 0);

        // 2. Download + extract
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
            return Abort("Installation aborted due to errors.", 80);

        // 3. Conflicts
        CheckFileNameConflicts(staged);
        if (!_result.Success)
            return Abort("Installation aborted: the selected mods have conflicting files.", 80);
        Ok("No conflicts detected. Proceeding with installation...");

        // Rebuild: put back the stock data, embed_wi_v4 and main.dol before copying the mods in again
        if (rebuild)
        {
            Info("Restoring the build's stock files before reinstalling...");
            await RepairGameAsync(all: true, progressFrom: 78, progressTo: 80);
            if (!_result.Success)
                return Abort("Installation aborted: the stock files couldn't be restored.", 80);
        }

        // 4–5. Two phases, each in recipe order (stable for equal Order):
        //   a) mods that need compiling, then the single compile
        //   b) mods that don't, after the compile — so nothing they put in the game is compiled or
        //      overwritten afterwards (e.g. the 4K Character models after the Cloth Fix and the compile)
        var ordered = staged.OrderBy(s => GetRecipe(s.Mod)?.Order ?? 50).ToList();
        bool CopiedBeforeCompile(ModItem m) => NeedsCompile(m) || GetRecipe(m)?.BeforeCompile == true;
        var compilePhase = ordered.Where(s => CopiedBeforeCompile(s.Mod)).ToList();
        var afterCompile = ordered.Where(s => !CopiedBeforeCompile(s.Mod)).ToList();

        var compileModes = new HashSet<CompileMode>();
        if (compilePhase.Count > 0)
        {
            Info($"Installing {compilePhase.Count} mod(s) before the compile...");
            foreach (var mode in await ApplyModsAsync(compilePhase, 80, 84))
                compileModes.Add(mode);
        }

        // The single compile, with the most complete batch file any of them needs
        CompileMode compile =
            compileModes.Contains(CompileMode.TemplatesAndRes) ? CompileMode.TemplatesAndRes :
            compileModes.Contains(CompileMode.ResForced) ? CompileMode.ResForced :
            compileModes.Contains(CompileMode.Res) ? CompileMode.Res :
            CompileMode.None;

        double phaseBStart = compile != CompileMode.None ? 96 : 84;
        if (compile != CompileMode.None)
            await CompileAsync(compile, restoreEmbed: !rebuild, progressFrom: 84, progressTo: 96); // a rebuild already restored embed_wi_v4

        if (afterCompile.Count > 0)
        {
            Info(compile != CompileMode.None
                ? $"Installing {afterCompile.Count} mod(s) that don't need compiling, after the compile..."
                : $"Installing {afterCompile.Count} mod(s)...");
            foreach (var mode in await ApplyModsAsync(afterCompile, phaseBStart, 100))
            {
                if (mode != CompileMode.None)
                    Warn("A mod without a declared compile reported needing one; it wasn't compiled. Give its recipe a Compile mode.");
            }
        }

        // Texture packs (e.g. the 4K pack's MM folder) duplicate some 4K character textures; make sure Dolphin uses the 4K ones
        if (!string.IsNullOrWhiteSpace(_req.AppDataLoadDir) && _result.Installed.Any(m => IsTextureMod(m) || GetRecipe(m)?.TouchesAppData == true))
        {
            await Task.Run(EnforceCharacterTexturePriority);
        }

        Report(100, _result.Success ? "Mod installation complete!" : "Mod installation finished with errors");
        Log(_result.Success ? "OK" : "WARN", "----- Installation Complete -----");
        return _result;
    }

    /// <summary>Copies each mod into place in the given order; returns the compile each one asked for.</summary>
    private async Task<List<CompileMode>> ApplyModsAsync(List<(ModItem Mod, string Stage)> mods, double progressFrom, double progressTo)
    {
        var modes = new List<CompileMode>();
        await Task.Run(() =>
        {
            for (int k = 0; k < mods.Count; k++)
            {
                var (mod, stage) = mods[k];
                Report(progressFrom + (progressTo - progressFrom) * k / mods.Count, $"Installing {mod.Name}...");
                Info($"Installing: {mod.Name}");
                try
                {
                    modes.Add(ApplyMod(mod, stage));
                    lock (_lock) _result.Installed.Add(mod);
                    Ok($"{mod.Name} installed successfully.");
                }
                catch (Exception ex)
                {
                    Error($"Error installing {mod.Name}: {ex.Message}");
                }
            }
        });
        return modes;
    }

    private ModInstallResult Abort(string message, double progress)
    {
        Error(message);
        Report(progress, "Installation aborted");
        return _result;
    }

    // ───────────── Pre-checks (check_install_conditions) ─────────────

    private void CheckInstallConditions(List<ModItem> mods)
    {
        var selected = new HashSet<string>(mods.Select(m => m.Name), Ci);

        foreach (var mod in mods)
        {
            var recipe = GetRecipe(mod);
            bool needsAppData = recipe?.TouchesAppData == true
                                || ((recipe == null || recipe.Custom != null) && IsTextureMod(mod));

            if (recipe?.TouchesGame == true && _gameDir == null)
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

            // Each incompatible pair is listed on one side only, so this reports it once
            foreach (string other in recipe?.IncompatibleWith ?? Array.Empty<string>())
            {
                if (selected.Contains(other))
                    Error($"'{mod.Name}' is incompatible with '{other}'. Please install one or the other.");
            }
        }

        foreach (var (a, b, message) in WarnPairs)
        {
            if (selected.Contains(a) && selected.Contains(b))
                Warn(message);
        }
    }

    /// <summary>Logs installer names that aren't in the manifest (e.g. after a mod or tool was renamed there).</summary>
    private static void LogNamesMissingFromManifest(AppManifest manifest)
    {
        var modNames = new HashSet<string>((manifest.Mods ?? Array.Empty<ModItem>()).Select(m => m.Name), Ci);
        var toolNames = new HashSet<string>((manifest.Tools ?? Array.Empty<ToolItem>()).Select(t => t.Name), Ci);

        var referenced = Recipes.Keys
            .Concat(Recipes.Values.SelectMany(r => r.IncompatibleWith))
            .Concat(WarnPairs.SelectMany(p => new[] { p.A, p.B }))
            .Concat(AllowedSharedFiles.SelectMany(r => r.Mods))
            .Distinct(Ci);

        foreach (string name in referenced.Where(n => !modNames.Contains(n)))
            Log("WARN", $"Mod '{name}' is used by the installer but isn't in the manifest.");

        foreach (string name in new[] { ResCompilerTool, RepairFilesTool }.Where(n => !toolNames.Contains(n)))
            Log("WARN", $"Tool '{name}' is used by the installer but isn't in the manifest.");
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

    /// <summary>Download folder name: the manifest file name without its extension.</summary>
    private static string StageName(ModItem mod)
    {
        string name = new[]
            {
                Path.GetFileNameWithoutExtension(mod.Filename),
                Path.GetFileNameWithoutExtension(mod.Url),
                mod.Id,
                mod.Name
            }
            .First(n => !string.IsNullOrWhiteSpace(n));

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ───────────── Conflict check (check_file_name_conflicts) ─────────────

    /// <summary>
    /// Flags .res/.war files that more than one selected mod would write.
    ///   .res files are compared by name (as installer.py did; their names are distinctive).
    ///   .war files are compared by bundle folder + name: every asset bundle folder has its own
    ///   ob.war / rdata.war, so the same name in different bundles isn't a clash.
    /// </summary>
    private void CheckFileNameConflicts(List<(ModItem Mod, string Stage)> staged)
    {
        // conflict key → (mod name → the file's path inside that mod's download)
        var owners = new Dictionary<string, Dictionary<string, string>>(Ci);

        foreach (var (mod, stage) in staged)
        {
            // Only mods that write game files can clash on .res/.war; texture packs (thousands of files) are skipped
            var recipe = GetRecipe(mod);
            if (recipe != null && (recipe.ReplacesMainDol || (recipe.Custom == null && !recipe.TouchesGame)))
                continue;

            foreach (string file in Directory.EnumerateFiles(stage, "*", RecursiveCi))
            {
                string ext = Path.GetExtension(file);
                bool isRes = Ci.Equals(ext, ".res");
                if (!isRes && !Ci.Equals(ext, ".war")) continue;

                string name = Path.GetFileName(file);
                string key = isRes ? name : $"{Path.GetFileName(Path.GetDirectoryName(file))}/{name}";

                if (!owners.TryGetValue(key, out var mods))
                    owners[key] = mods = new Dictionary<string, string>(Ci);
                mods.TryAdd(mod.Name, Path.GetRelativePath(stage, file));
            }
        }

        foreach (var (key, mods) in owners)
        {
            if (mods.Count < 2) continue;

            string file = Path.GetFileName(key);
            string modList = string.Join(", ", mods.Keys);
            bool allowed = AllowedSharedFiles.Any(rule =>
                (rule.File == null || Ci.Equals(rule.File, file)) && rule.Mods.All(mods.ContainsKey));

            if (allowed)
            {
                Info($"File {key} is present in mods: {modList} (dismissed)");
            }
            else
            {
                string where = string.Join("; ", mods.Select(m => $"{m.Key}: {m.Value}"));
                Error($"File conflict: {key} is in more than one mod ({where})");
            }
        }
    }

    // ───────────── Applying a mod ─────────────

    private CompileMode ApplyMod(ModItem mod, string stage)
    {
        var ctx = new ModContext(mod, stage, ContentRoot(stage));
        var recipe = GetRecipe(mod);

        if (recipe == null)
            return ApplyAutoDetected(ctx);

        if (recipe.Custom != null)
        {
            var mode = recipe.Custom(this, ctx);
            if (recipe.NeedsGame) MarkBuildScoped(mod);
            return mode;
        }

        if (recipe.ReplacesMainDol)
        {
            ReplaceMainDol(stage);
            MarkBuildScoped(mod);
            return CompileMode.None;
        }

        foreach (var step in recipe.Steps)
        {
            string? src = ResolveSource(ctx, step.Sources);
            if (src != null)
                ApplyStep(src, step.Target, step.TargetSubpath);
            else if (step.Optional)
                Info($"Optional folder '{step.Sources[0]}' not in the download; skipping.");
            else
                throw new DirectoryNotFoundException($"couldn't find '{step.Sources[0]}' in the downloaded files ({stage})");
        }

        if (recipe.TouchesGame) MarkBuildScoped(mod);
        return recipe.Compile;
    }

    // ───────────── Custom installers (mods that weren't in installer.py) ─────────────
    //
    // Each gets a ModContext (Mod, Stage = extracted download, Content = Stage with wrapper folders
    // unwrapped) and returns the compile it needs (CompileMode.None if it changes no .res files).
    //
    // Building blocks:
    //   string src = RequireSource(c, "FolderInZip", "OtherName", "");  → find a folder ("" = content root) or throw
    //   ApplyStep(src, DataFiles, "data/bf/xyz");    → copy a folder into the game or appdata
    //   PlaceIntoGame(src, DataFiles);               → copy into the game at the level the layout implies
    //   PlaceTextures(src);                          → copy into Load/Textures/RABAZZ
    //   CopyFile(srcFile, DataSys, "main.dol");      → copy a single file
    //   MarkBuildScoped(c.Mod);                      → track as installed per build (wrote game files)
    //
    // If a function writes game files, also set NeedsGame = true on its recipe so a missing build is
    // caught before downloading. Mods with no recipe at all fall back to ApplyAutoDetected.

    /// <summary>
    /// r2-91120a Custom Skyboxes — replaces the sky in Bespin, Cato Neimoidia, Coruscant, Dathomir,
    /// Endor, Hoth and Tatooine. Its instructions: extract the zip into the same folder as the
    /// 4K Texture Pack, after the pack, overwriting what it asks to overwrite.
    /// The recipe's Order (after the 4K pack) handles "after" when both are installed together.
    /// </summary>
    private CompileMode InstallCustomSkyboxes(ModContext c)
    {
        // Same folder the 4K pack uses (its recipe copies to Load/Textures/RABAZZ via PlaceTextures).
        // If the zip mirrors the pack's own top folder, use what's inside it so the layouts line up.
        string src = RequireSource(c, "4kTexturePacks", "");

        string packFolder = TargetPath(Textures, "");
        bool packInstalled = Directory.Exists(packFolder) && Directory.EnumerateFileSystemEntries(packFolder).Any();
        Info(packInstalled
            ? $"Adding the custom skyboxes over the textures in {packFolder} (existing files are overwritten)."
            : $"Adding the custom skyboxes to {packFolder}. Install the 4K Texture Pack before them if you use it.");

        PlaceTextures(src); // overwrites existing files

        Ok("Skies replaced for Bespin, Cato Neimoidia, Coruscant, Dathomir, Endor, Hoth and Tatooine.");
        return CompileMode.None;
    }

    /// <summary>
    /// 4k Character Texture Pack and Model Fix — a direct port of installer.py's install_4k_characters_model_fix:
    ///   copy_files(mod_dir / "characters", appdata/Load/Textures/RABAZZ/characters)
    ///   copy_files(mod_dir / "ingame_models_x1_x2_new", game/DATA/files)
    /// The current zip has the models as a plain "assets" folder instead of ingame_models_x1_x2_new,
    /// which is copied into DATA/files the same way (→ DATA/files/assets). Everything is copied, overwriting.
    /// </summary>
    private CompileMode Install4kCharactersModelFix(ModContext c)
    {
        // characters → Load/Textures/RABAZZ/characters
        string characters = ResolveRelative(c.Content, "characters") ?? RequireSource(c, "characters");
        string texturesDir = TargetPath(Textures, "characters");
        Directory.CreateDirectory(texturesDir);
        CopyLogged(characters, texturesDir);

        // The download has files like "tex1_1024x1024_m_b3326c6627a33072_14 - Copy - Copy - Copy.png",
        // which Dolphin can't match to a texture — give them their proper name
        FixCopySuffixedTextureNames(texturesDir);

        // in-game models → DATA/files
        string filesDir = TargetPath(DataFiles, "");
        string? models = ResolveSource(c, new[] { "ingame_models_x1_x2_new" });
        if (models != null)
        {
            CopyLogged(models, filesDir);
        }
        else
        {
            string assets = ResolveRelative(c.Content, "assets")
                            ?? throw new DirectoryNotFoundException($"neither ingame_models_x1_x2_new nor assets was found in the download ({c.Stage})");
            CopyLogged(assets, ResolveDestPath(filesDir, "assets"));
        }

        MarkBuildScoped(c.Mod);
        return CompileMode.None;
    }


    /// <summary>
    /// Dolphin loads only one copy of a texture when the same file name appears more than once under
    /// Load/Textures/RABAZZ, and ignores the rest. Other packs (4k Texture Packs, HD User Interface, ...)
    /// ship their own copies of character textures, which can win over Load/Textures/RABAZZ/characters.
    /// This removes the other copies of every texture in RABAZZ/characters so the 4K character textures load.
    /// Folders starting with "_" (e.g. _faithful_hpbars_v2) are left alone: that prefix is how a pack
    /// deliberately takes priority. Removed files come back if their pack is reinstalled (and this runs again).
    /// </summary>
    private void EnforceCharacterTexturePriority()
    {
        string rabazz = TargetPath(Textures, "");
        string? characters = FindChildDir(rabazz, "characters");
        if (characters == null) return;

        var characterNames = new HashSet<string>(
            Directory.EnumerateFiles(characters, "*", SearchOption.AllDirectories).Select(DolphinTextureKey), Ci);
        if (characterNames.Count == 0) return;

        string charactersPrefix = characters.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int removed = 0;

        foreach (string file in Directory.EnumerateFiles(rabazz, "*", SearchOption.AllDirectories).ToList())
        {
            if (file.StartsWith(charactersPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!characterNames.Contains(DolphinTextureKey(file))) continue;

            // Leave deliberately prioritised packs (top-level folders starting with "_") alone
            string top = SplitPath(Path.GetRelativePath(rabazz, file))[0];
            if (top.StartsWith('_') && top != Path.GetFileName(file)) continue;

            File.Delete(file);
            removed++;
        }

        if (removed > 0)
            Ok($"Removed {removed} other cop{(removed == 1 ? "y" : "ies")} of the 4K character textures from other packs so Dolphin loads the 4K ones.");
        else
            Info("No other packs have copies of the 4K character textures.");
    }

    /// <summary>
    /// Renames texture files with Windows " - Copy" suffixes back to their proper texture name
    /// (or removes the copy if a properly named file is already there), so Dolphin can load them.
    /// </summary>
    private static void FixCopySuffixedTextureNames(string dir)
    {
        int renamed = 0, removed = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!CopySuffix.IsMatch(name)) continue;

            string proper = Path.Combine(Path.GetDirectoryName(file)!, CopySuffix.Replace(name, "") + Path.GetExtension(file));
            if (File.Exists(proper))
            {
                File.Delete(file);
                removed++;
            }
            else
            {
                File.Move(file, proper);
                Info($"Renamed {Path.GetFileName(file)} to {Path.GetFileName(proper)} so Dolphin can load it.");
                renamed++;
            }
        }

        if (removed > 0)
            Info($"Removed {removed} \" - Copy\" duplicate(s) that already had a properly named file.");
    }

    /// <summary>" - Copy", " - Copy - Copy", " - Copy (2)" ... at the end of a file name.</summary>
    private static readonly Regex CopySuffix = new(@"( - Copy( \(\d+\))?)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The texture a custom-texture file stands for, the way Dolphin matches them: the file name without
    /// its extension (.png and .dds are the same texture) and without a _mipN suffix (mip levels belong
    /// to their base texture). E.g. "tex1_64x64_ab12_14_mip2.DDS" → "tex1_64x64_ab12_14".
    /// </summary>
    private static string DolphinTextureKey(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return MipSuffix.Replace(name, "").ToLowerInvariant();
    }

    private static readonly Regex MipSuffix = new(@"_mip\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Lighting Fix — fixes darkness/dimness/far dark units on all maps and cutscene lighting. Modder's steps:
    ///   2–3. everything in "SWBF3 Wii Light Fixes" → DATA/files, replacing existing files
    ///   4–7. compile, re-save data/bf/mgrsetup/scene_descriptors.res, compile again — the re-save only makes
    ///        the compiler see that file as changed, so here it's marked changed and the install's single
    ///        forced compile picks everything up in one pass
    /// </summary>
    private CompileMode InstallLightingFix(ModContext c)
    {
        ApplyStep(RequireSource(c, "SWBF3_Wii_Light_Fixes", "SWBF3 Wii Light Fixes", ""), DataFiles, "");

        string mgrsetup = ResolveDestPath(TargetPath(DataFiles, ""), "data/bf/mgrsetup");
        string? sceneDescriptors = FindFile(mgrsetup, "scene_descriptors.res");
        if (sceneDescriptors != null)
        {
            File.SetLastWriteTime(sceneDescriptors, DateTime.Now);
            Ok("Marked scene_descriptors.res as changed so it's compiled (the modder's re-save step).");
        }
        else
        {
            Warn($"scene_descriptors.res wasn't found in {mgrsetup}; the cutscene lighting part may not apply.");
        }

        MarkBuildScoped(c.Mod);
        return CompileMode.ResForced;
    }

    /// <summary>
    /// Muted Blank Sounds — replaces 2994 blank audio cues with silence.
    /// With a DATA/files layout the folder is copied in (as installer.py did); with loose .dsp files
    /// each one replaces the game's file of the same name under DATA/files.
    /// </summary>
    private CompileMode InstallMutedBlankSounds(ModContext c)
    {
        string src = RequireSource(c, "SWBF3_Wii_Muted_Blank_Sounds", "");

        if (HasGameLayout(src))
            PlaceIntoGame(src, GameRoot);
        else
            ReplaceExistingFiles(c, src, TargetPath(DataFiles, ""));

        MarkBuildScoped(c.Mod);
        return CompileMode.None;
    }

    /// <summary>
    /// Fixed HvV and Hunt Audio — re-enables music for Heroes vs Villains and Hunt.
    /// Modder's instructions: replace the "mapname.res" files in their respective map's setup folder,
    /// under DATA/files/data/bf/templates. No compile needed.
    /// </summary>
    private CompileMode InstallFixedHvVAndHuntAudio(ModContext c)
    {
        string templates = TargetPath(DataFiles, "data/bf/templates");
        if (!Directory.Exists(templates))
            throw new DirectoryNotFoundException($"the build has no templates folder ({templates})");

        // The zip may include the data/bf/templates path itself; start from inside it if so
        string src = RequireSource(c, "data/bf/templates", "bf/templates", "templates", "");

        // Map folders (e.g. <map>/setup/<map>.res) already mirror the templates layout: merge them in
        foreach (string dir in Directory.GetDirectories(src).Where(d => !Ci.Equals(Path.GetFileName(d), "__MACOSX")))
            CopyModFiles(dir, ResolveDestPath(templates, Path.GetFileName(dir)));

        // Loose mapname.res files: put each over the file of the same name in its map's setup folder
        foreach (string file in Directory.GetFiles(src).Where(f => Path.GetFileName(f) != StageMarker))
        {
            string name = Path.GetFileName(file);
            var matches = Directory.EnumerateFiles(templates, name, RecursiveCi).ToList();
            var inSetup = matches.Where(m => Ci.Equals(Path.GetFileName(Path.GetDirectoryName(m)), "setup")).ToList();
            var targets = inSetup.Count > 0 ? inSetup : matches.Count == 1 ? matches : new List<string>();

            if (targets.Count == 0)
            {
                Warn(matches.Count == 0
                    ? $"{c.Mod.Name}: no {name} found under {templates}; skipped it."
                    : $"{c.Mod.Name}: {name} exists in several places under {templates} but none in a setup folder; skipped it.");
                continue;
            }

            foreach (string target in targets)
            {
                File.Copy(file, target, overwrite: true);
                Ok($"Replaced {Path.GetRelativePath(templates, target)}");
            }
        }

        MarkBuildScoped(c.Mod);
        return CompileMode.None;
    }

    /// <summary>
    /// KBM Controls — full keyboard controls and crosshair fix, all builds. Follows the modder's steps:
    ///   1–3. playercontrols.res → DATA/files/data/bf/templates, menus_common.res → DATA/files/data/bf/menus
    ///        (originals backed up as *.original)
    ///   4.   compile_templates_and_res.bat (returned as the compile mode)
    ///   5–6. BF3_KBM.ini → Dolphin's Sys/Profiles/Wiimote (also User/Config/Profiles/Wiimote)
    ///   7–11. Dolphin settings done in its config files instead of the menus:
    ///        Wii Remote 1 uses the BF3_KBM profile, "Connect USB Keyboard" on, Reset hotkey cleared
    /// </summary>
    private CompileMode InstallKbmControls(ModContext c)
    {
        PlaceGameFile(c, "playercontrols.res", "data/bf/templates");
        PlaceGameFile(c, "menus_common.res", "data/bf/menus");

        string profile = FindFileRecursive(c.Stage, "BF3_KBM.ini")
                         ?? throw new FileNotFoundException($"BF3_KBM.ini not found in the downloaded files ({c.Stage})");
        InstallWiimoteProfile(profile);
        ApplyKbmDolphinSettings(profile);

        MarkBuildScoped(c.Mod);
        return CompileMode.TemplatesAndRes;
    }

    /// <summary>Copies one file from the download into DATA/files/&lt;subpath&gt;, backing up the original once.</summary>
    private void PlaceGameFile(ModContext c, string fileName, string dataFilesSubpath)
    {
        string src = FindFileRecursive(c.Stage, fileName)
                     ?? throw new FileNotFoundException($"{fileName} not found in the downloaded files ({c.Stage})");

        string dir = TargetPath(DataFiles, dataFilesSubpath);
        Directory.CreateDirectory(dir);
        string dest = MatchExisting(dir, fileName, isDirectory: false);

        BackupOnce(dest);
        File.Copy(src, dest, overwrite: true);
        Ok($"Placed {fileName} in {dir}");
    }

    /// <summary>Puts a Wii Remote profile in Dolphin's Sys/Profiles/Wiimote and User/Config/Profiles/Wiimote.</summary>
    private void InstallWiimoteProfile(string profileFile)
    {
        var dirs = new List<string>();

        string? sys = FindDolphinSysDir();
        if (sys != null)
            dirs.Add(ResolveDestPath(sys, "Profiles/Wiimote"));
        else
            Warn("Couldn't find Dolphin's Sys folder; the KBM profile was only added to User/Config/Profiles/Wiimote.");

        // Dolphin also lists profiles from the user folder, so the profile shows up either way
        dirs.Add(ResolveDestPath(RequireDolphinUserDir(), "Config/Profiles/Wiimote"));

        string name = Path.GetFileName(profileFile);
        foreach (string dir in dirs)
        {
            Directory.CreateDirectory(dir);
            File.Copy(profileFile, MatchExisting(dir, name, isDirectory: false), overwrite: true);
            Ok($"Placed {name} in {dir}");
        }
    }

    /// <summary>The Sys folder of the portable Dolphin (next to the executable).</summary>
    private string? FindDolphinSysDir()
    {
        string? userParent = Path.GetDirectoryName(RequireDolphinUserDir());
        foreach (string? root in new[] { userParent, _req.DolphinDir })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            string? sys = FindChildDir(root, "Sys") ?? FindBySuffix(root, "Sys");
            if (sys != null) return sys;
        }
        return null;
    }

    /// <summary>
    /// Makes the same changes as the modder's Dolphin menu steps, in Dolphin's User/Config files
    /// (originals backed up as *.original):
    ///   WiimoteNew.ini [Wiimote1] ← the profile's mappings, emulated (Options > Controller Settings > Profile)
    ///   Dolphin.ini [Core] WiiKeyboard = True               (Options > Configuration > Wii > Connect USB Keyboard)
    ///   Hotkeys.ini [Hotkeys] General/Reset cleared          (Options > Hotkey Settings > Reset > Clear)
    /// </summary>
    private void ApplyKbmDolphinSettings(string profileFile)
    {
        string configDir = ResolveDestPath(RequireDolphinUserDir(), "Config");
        Directory.CreateDirectory(configDir);

        // Wii Remote 1 → BF3_KBM profile (selecting a profile copies its mappings into WiimoteNew.ini)
        var mappings = Ini.ReadSection(profileFile, "Profile");
        if (mappings.Count == 0)
        {
            Warn("BF3_KBM.ini has no [Profile] section; select the BF3_KBM profile for Wii Remote 1 in Dolphin manually.");
        }
        else
        {
            string wiimoteIni = MatchExisting(configDir, "WiimoteNew.ini", isDirectory: false);
            BackupOnce(wiimoteIni);
            var entries = mappings.Where(kv => !Ci.Equals(kv.Key, "Source")).Prepend(("Source", "1")); // 1 = emulated
            Ini.ReplaceSection(wiimoteIni, "Wiimote1", entries);
            Ok("Wii Remote 1 now uses the BF3_KBM profile.");
        }

        // Connect USB Keyboard
        string dolphinIni = MatchExisting(configDir, "Dolphin.ini", isDirectory: false);
        BackupOnce(dolphinIni);
        Ini.SetValue(dolphinIni, "Core", "WiiKeyboard", "True");
        Ok("Enabled 'Connect USB Keyboard'.");

        // Clear the Reset hotkey (optional step in the instructions, recommended)
        string hotkeysIni = MatchExisting(configDir, "Hotkeys.ini", isDirectory: false);
        BackupOnce(hotkeysIni);
        Ini.SetValue(hotkeysIni, "Hotkeys", "General/Reset", "");
        Ok("Cleared the Reset hotkey.");

        SetEscapeStopsGame(configDir);

        Info("Dolphin saves its settings when it closes, so close Dolphin before installing KBM Controls.");
    }

    /// <summary>Copies a file to &lt;file&gt;.original the first time it's about to be replaced.</summary>
    private static void BackupOnce(string path)
    {
        string backup = path + ".original";
        if (!File.Exists(path) || File.Exists(backup)) return;

        File.Copy(path, backup);
        Info($"Backed up {Path.GetFileName(path)} to {backup}");
    }

    /// <summary>
    /// Mustafar Water Fix — removes the Mustafar water effect (works in r911, untested in r904).
    /// Modder's instructions: place in bf\ob_wi_v194\bg\mus\mus_bg — the folder's contents are replaced
    /// with the mod's mus_bg. The original contents are backed up once to Builds/&lt;tag&gt;/backups.
    /// No compile needed (asset files, not .res).
    /// </summary>
    private CompileMode InstallMustafarWaterFix(ModContext c)
    {
        const string gamePath = "bf/ob_wi_v194/bg/mus/mus_bg";

        string filesDir = TargetPath(DataFiles, "");
        string target = FindBySuffix(filesDir, gamePath)
                        ?? throw new DirectoryNotFoundException($"this build has no {gamePath} folder under {filesDir}");

        string src = RequireSource(c, "mus_bg", "");

        // Back up the original contents once, outside the game folder
        if (!string.IsNullOrEmpty(_req.BuildDir))
        {
            string backup = Path.Combine(_req.BuildDir, "backups", "mustafar_water_fix", "mus_bg");
            if (!Directory.Exists(backup))
            {
                CopyLogged(target, backup);
                Info($"Backed up the original mus_bg to {backup}");
            }
        }

        // Replace the folder's contents with the mod's
        foreach (string entry in Directory.EnumerateFileSystemEntries(target).ToList())
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        CopyModFiles(src, target);

        MarkBuildScoped(c.Mod);
        return CompileMode.None;
    }

    /// <summary>
    /// r904 Dantooine Crash Fix — for Hybrid r904.
    /// Modder's instructions: put dantooine.res in data/bf/templates, then compile the res files.
    /// The original dantooine.res is backed up as dantooine.res.original.
    /// </summary>
    private CompileMode InstallR904DantooineCrashFix(ModContext c)
    {
        PlaceGameFile(c, "dantooine.res", "data/bf/templates");
        MarkBuildScoped(c.Mod);
        return CompileMode.Res;
    }

    /// <summary>
    /// Unlocked PC/Xbox Frontend — for r911-M.
    /// Modder's instructions: place frontend_preview.res in data/bf/menus, then run compile_all_res.
    /// The file is found anywhere in the download (whatever folders the zip has) and the original is
    /// backed up as frontend_preview.res.original.
    /// </summary>
    private CompileMode InstallUnlockedFrontend(ModContext c)
    {
        string menus = TargetPath(DataFiles, "data/bf/menus");
        if (!Directory.Exists(menus))
            Warn($"{menus} didn't exist in this build; it was created. Check that this build is r911-M.");
        else if (FindFile(menus, "frontend_preview.res") == null)
            Warn($"This build has no frontend_preview.res in {menus} to replace; the mod's copy was added anyway.");

        PlaceGameFile(c, "frontend_preview.res", "data/bf/menus");
        MarkBuildScoped(c.Mod);
        return CompileMode.Res;
    }

    private static string RequireSource(ModContext c, params string[] sources) =>
        ResolveSource(c, sources)
        ?? throw new DirectoryNotFoundException($"couldn't find '{sources.FirstOrDefault()}' in the downloaded files ({c.Stage})");

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
    /// Fallback for mods without a recipe: textures go to Load/Textures/RABAZZ; otherwise Dolphin
    /// user folders (Config, GameSettings, Load) go to Dolphin's User folder and game folders
    /// (DATA, files, sys, assets, data) into the build. Compiles if any .res files were copied.
    /// </summary>
    private CompileMode ApplyAutoDetected(ModContext c)
    {
        Info($"{c.Mod.Name}: placing files based on the download's folder layout.");

        if (IsTextureMod(c.Mod))
        {
            PlaceTextures(c.Content);
            return CompileMode.None;
        }

        bool placed = false;
        var userFolders = new HashSet<string>(Ci) { "Config", "GameSettings", "Load" };

        foreach (string dir in Directory.GetDirectories(c.Content).Where(d => userFolders.Contains(Path.GetFileName(d))))
        {
            CopyModFiles(dir, ResolveDestPath(RequireDolphinUserDir(), Path.GetFileName(dir)));
            placed = true;
        }

        var compile = CompileMode.None;
        if (HasGameLayout(c.Content))
        {
            PlaceIntoGame(c.Content, GameRoot, userFolders);
            MarkBuildScoped(c.Mod);
            placed = true;

            if (Directory.EnumerateFiles(c.Content, "*.res", RecursiveCi).Any())
                compile = CompileMode.TemplatesAndRes;
        }

        if (!placed)
            throw new InvalidOperationException(
                $"couldn't work out where its files go. They're in {c.Stage}; please copy them in manually");

        return compile;
    }

    private void ApplyStep(string src, ModTarget target, string subpath)
    {
        // If the source already contains the sub-path's first folder (e.g. it has "data/..." and the
        // target is ".../data"), copy it one level up so it isn't nested twice.
        if (!string.IsNullOrEmpty(subpath) && HasChild(src, subpath.Split('/', '\\')[0]))
            subpath = "";

        if (string.IsNullOrEmpty(subpath))
        {
            if (target is GameRoot or Data or DataFiles)
            {
                PlaceIntoGame(src, target);
                return;
            }
            if (target == Textures)
            {
                PlaceTextures(src);
                return;
            }
        }

        CopyModFiles(src, TargetPath(target, subpath));
    }

    /// <summary>
    /// Copies into the game at the level the source's layout implies
    /// (DATA/... → game root, files/sys → DATA, assets/data → DATA/files), else at <paramref name="fallback"/>.
    /// </summary>
    private void PlaceIntoGame(string src, ModTarget fallback, ISet<string>? skipTopLevel = null)
    {
        // "DATA" (the disc partition) and "data" (inside DATA/files) are different folders, so DATA is
        // matched by exact case — a mod's lowercase data folder must never land in the game's root.
        string dest =
            HasChildExactCase(src, "DATA") ? RequireGameDir() :
            HasChild(src, "files") || HasChild(src, "sys") ? TargetPath(Data, "") :
            HasChild(src, "assets") || HasChild(src, "data") ? TargetPath(DataFiles, "") :
            fallback != GameRoot ? TargetPath(fallback, "") :
            throw new InvalidOperationException(
                $"its files don't have a DATA/files layout, so they weren't copied into the game's root folder ({src})");

        CopyModFiles(src, dest, skipTopLevel);
    }

    /// <summary>True if the download has any of the game's folders (DATA, files, sys, assets, data).</summary>
    private static bool HasGameLayout(string dir) =>
        HasChildExactCase(dir, "DATA") || new[] { "files", "sys", "assets", "data" }.Any(n => HasChild(dir, n));

    /// <summary>
    /// For mods that ship loose files with no folder layout: replaces each file wherever the game already
    /// has a file of the same name under <paramref name="root"/>. Files with no match are skipped.
    /// </summary>
    private void ReplaceExistingFiles(ModContext c, string src, string root)
    {
        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != StageMarker && !IsDocFile(f))
            .ToList();
        var extensions = new HashSet<string>(files.Select(f => Path.GetExtension(f)), Ci);

        // Index the game's files once (only the extensions the mod ships)
        var index = new Dictionary<string, List<string>>(Ci);
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(f))) continue;
            string name = Path.GetFileName(f);
            if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>();
            list.Add(f);
        }

        int replaced = 0, missing = 0;
        foreach (string file in files)
        {
            if (!index.TryGetValue(Path.GetFileName(file), out var targets))
            {
                missing++;
                continue;
            }
            foreach (string target in targets)
                File.Copy(file, target, overwrite: true);
            replaced++;
        }

        if (replaced == 0)
            throw new InvalidOperationException($"none of its {files.Count} file(s) match a file in {root}, so nothing was replaced");

        Ok($"Replaced {replaced} file(s) in place under {root}.");
        if (missing > 0)
            Warn($"{c.Mod.Name}: {missing} of its {files.Count} file(s) have no matching file in the game and were skipped.");
    }

    /// <summary>Copies textures into Load/Textures/RABAZZ, adjusting if the download already includes those folders.</summary>
    private void PlaceTextures(string src)
    {
        string dest =
            HasChild(src, "Load") ? RequireDolphinUserDir() :
            HasChild(src, "Textures") ? RequireLoadDir() :
            HasChild(src, GameId) ? ResolveDestPath(RequireLoadDir(), "Textures") :
            TargetPath(Textures, "");

        CopyModFiles(src, dest);
    }

    private void ReplaceMainDol(string stage)
    {
        string dol = FindFileRecursive(stage, "main.dol")
                     ?? throw new FileNotFoundException($"main.dol not found in the downloaded files ({stage})");

        string sysDir = TargetPath(DataSys, "");
        Directory.CreateDirectory(sysDir);
        string dest = MatchExisting(sysDir, "main.dol", isDirectory: false);

        BackupOnce(dest); // keep the original once, so it can be restored
        File.Copy(dol, dest, overwrite: true);
        Ok($"File 'main.dol' copied to {sysDir}");
    }

    // ───────────── Compiling (compile_templates_res) ─────────────

    /// <summary>
    /// Runs the single compile for this install: restores the stock embed_wi_v4 first (unless a rebuild
    /// already did), then one batch file — compile_templates_and_res.bat when any mod needs templates
    /// (it compiles the .res files too, forced), otherwise compile_all_res_forced.bat. Res-only installs
    /// use the forced compile because the freshly restored embed_wi_v4 can look up to date to the normal
    /// compile_all_res.bat, which would then skip the mods' files.
    /// </summary>
    private async Task CompileAsync(CompileMode mode, bool restoreEmbed, double progressFrom, double progressTo)
    {
        double At(double fraction) => progressFrom + (progressTo - progressFrom) * fraction;

        if (_gameDir == null)
        {
            Error("Resource compilation needs an installed target build.");
            return;
        }

        string filesDir = TargetPath(DataFiles, "");
        Report(progressFrom, "Preparing the resource compiler...");
        if (!await EnsureResCompilerAsync(filesDir, progressFrom, At(0.2))) return;

        if (restoreEmbed)
            await RepairGameAsync(all: false, progressFrom: At(0.2), progressTo: At(0.3));

        var (batch, label) = mode == CompileMode.TemplatesAndRes
            ? ("compile_templates_and_res.bat", "Compiling templates and resources")
            : ("compile_all_res_forced.bat", "Compiling resources");

        Info($"Compilation required. Running {batch} (the only compile for this install)...");
        await RunCompileBatchAsync(filesDir, batch, label, At(0.3), progressTo);
    }

    /// <summary>True if installing this mod requires a compile (declared on its recipe).</summary>
    private static bool NeedsCompile(ModItem mod) => (GetRecipe(mod)?.Compile ?? CompileMode.None) != CompileMode.None;

    /// <summary>
    /// Files the compile needs in DATA/files. all_res_files_r9.txt is the list of .res files the
    /// batch files compile; without it the resource step silently does nothing.
    /// </summary>
    private static readonly string[] RequiredCompilerFiles =
    {
        "compile_templates_and_res.bat", "compile_all_res_forced.bat", "all_res_files_r9.txt"
    };

    /// <summary>Makes sure the compiler files are all in DATA/files, copying in the Embedded ResCompiler if any are missing.</summary>
    private async Task<bool> EnsureResCompilerAsync(string filesDir, double progressFrom, double progressTo)
    {
        var missing = RequiredCompilerFiles.Where(f => FindFile(filesDir, f) == null).ToList();
        if (missing.Count == 0) return true;

        Info($"Compiler files missing from DATA/files ({string.Join(", ", missing)}). Copying the Embedded ResCompiler...");
        string? toolDir = await EnsureToolAsync(ResCompilerTool, progressFrom, progressTo);
        if (toolDir == null) return false;

        string? bat = FindFileRecursive(toolDir, RequiredCompilerFiles[0]);
        if (bat == null)
        {
            Error($"{RequiredCompilerFiles[0]} wasn't found in the {ResCompilerTool} download ({toolDir}).");
            return false;
        }

        // The whole compiler, nothing skipped (its .txt file lists are required)
        await Task.Run(() => CopyLogged(Path.GetDirectoryName(bat)!, filesDir));

        missing = RequiredCompilerFiles.Where(f => FindFile(filesDir, f) == null).ToList();
        if (missing.Count == 0) return true;

        Error($"Still missing after copying the {ResCompilerTool}: {string.Join(", ", missing)}. Compiling now would break the game, so it was skipped.");
        return false;
    }

    /// <summary>
    /// Runs a compile batch file and reports progress between <paramref name="progressFrom"/> and
    /// <paramref name="progressTo"/>. The percent is an estimate: distinct .res file names the compiler
    /// prints, out of the .res files under DATA/files/data. The elapsed time updates every second either way.
    /// </summary>
    private async Task<bool> RunCompileBatchAsync(string filesDir, string batchName, string label,
        double progressFrom, double progressTo)
    {
        string? bat = FindFile(filesDir, batchName);
        if (bat == null)
        {
            Error($"{batchName} not found in {filesDir}.");
            return false;
        }

        // What the compiler is expected to work through
        string? dataDir = FindChildDir(filesDir, "data");
        int total = dataDir == null
            ? 0
            : Directory.EnumerateFiles(dataDir, "*.res", RecursiveCi).Select(f => Path.GetFileName(f)).Distinct(Ci).Count();

        var seen = new HashSet<string>(Ci);
        var clock = Stopwatch.StartNew();
        Report(progressFrom, $"{label}...");

        void ReportProgress()
        {
            int done;
            lock (seen) done = seen.Count;

            string elapsed = clock.Elapsed.ToString(@"m\:ss");
            if (total > 0 && done > 0)
            {
                double fraction = Math.Min((double)done / total, 0.99); // 100% only once it has finished
                Report(progressFrom + (progressTo - progressFrom) * fraction,
                    $"{label}... {(int)(fraction * 100)}% ({elapsed})");
            }
            else
            {
                Report(progressFrom, $"{label}... ({elapsed} elapsed)");
            }
        }

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "wine",
            WorkingDirectory = Path.GetDirectoryName(bat)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closed right away so a "pause" in the batch file can't hang
            CreateNoWindow = true
        };
        if (!isWindows) psi.ArgumentList.Add("cmd");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(Path.GetFileName(bat));

        Info($"Starting compilation: {batchName}{(isWindows ? "" : " (via Wine)")}");

        using var proc = new Process { StartInfo = psi };
        var problems = new List<string>();

        proc.OutputDataReceived += (_, a) =>
        {
            if (string.IsNullOrWhiteSpace(a.Data)) return;
            Log("OUT", a.Data);

            // Count each .res file the compiler mentions
            foreach (Match m in ResFileName.Matches(a.Data))
                lock (seen) seen.Add(m.Value);

            // The batch files exit normally even when the compiler fails (e.g. a missing file list),
            // so watch the compiler's own messages
            string line = a.Data.Trim();
            if (!line.StartsWith("Compiling ", StringComparison.OrdinalIgnoreCase) && CompilerProblem.IsMatch(line))
                lock (problems) problems.Add(line);
        };
        // Wine prints its own diagnostics to stderr; those are logged but not treated as compile failures
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

        // Refresh the status line every second until the compiler exits
        Task exited = proc.WaitForExitAsync();
        while (await Task.WhenAny(exited, Task.Delay(1000)) != exited)
            ReportProgress();
        await exited;

        if (problems.Count > 0)
        {
            foreach (string problem in problems.Take(5))
                Error($"Compiler: {problem}");
            Error($"{batchName} reported {problems.Count} problem(s), so the compile didn't fully succeed and the game may crash. " +
                  "Use Tools > Repair, then install the mods again.");
            return false;
        }

        if (proc.ExitCode == 0)
        {
            Report(progressTo, $"{label}... 100% ({clock.Elapsed:m\\:ss})");
            Ok($"Resource compilation completed successfully ({batchName}) in {clock.Elapsed:m\\:ss}.");
            return true;
        }

        Error($"Error during resource compilation: {batchName} exited with code {proc.ExitCode}.");
        return false;
    }

    // ───────────── Repair (repair_game) ─────────────

    /// <summary>True if the manifest's repair files are made for this build (the tool's "build" tag list; empty = any).</summary>
    public static bool RepairFilesApplyTo(AppManifest manifest, string buildTag)
    {
        var tool = (manifest.Tools ?? Array.Empty<ToolItem>()).FirstOrDefault(t => Ci.Equals(t.Name, RepairFilesTool));
        if (tool == null) return false;
        if (string.IsNullOrWhiteSpace(tool.Build)) return true;

        return tool.Build
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(buildTag, Ci);
    }

    /// <summary>
    /// Full repair (the Tools tab's Repair button): restores every stock file from the repair files,
    /// overwriting the build's copies (per the repair files' readme):
    ///   data        → DATA/files/data
    ///   embed_wi_v4 → DATA/files/assets/bf/embed_wi_v4
    ///   main.dol    → DATA/sys
    /// Files that mods added (rather than replaced) are left in place.
    /// </summary>
    public async Task<ModInstallResult> RepairGameAsync(ModInstallRequest req)
    {
        Begin(req);
        if (_gameDir == null)
            Error("Repair needs an installed target build.");
        else
            await RepairGameAsync(all: true, progressFrom: 0, progressTo: 100);

        Report(100, _result.Success ? "Repair complete!" : "Repair finished with errors");
        return _result;
    }

    /// <summary>
    /// all = false: only embed_wi_v4 (done before compiling; skipped quietly for builds the repair files
    /// aren't for). all = true: data, embed_wi_v4 and main.dol, and a missing piece is an error.
    /// </summary>
    private async Task RepairGameAsync(bool all, double progressFrom, double progressTo)
    {
        double At(double fraction) => progressFrom + (progressTo - progressFrom) * fraction;

        string? tag = _req.TargetBuildTag;
        if (tag == null || !RepairFilesApplyTo(_req.Manifest, tag))
        {
            string message = $"The {RepairFilesTool} aren't made for {tag ?? "this build"}";
            if (all) Error($"{message}, so it can't be repaired with them.");
            else Info($"{message}; skipping the embed_wi_v4 restore.");
            return;
        }

        string? toolDir = await EnsureToolAsync(RepairFilesTool, progressFrom, At(0.4));
        if (toolDir == null) return;

        string filesDir = TargetPath(DataFiles, "");

        await Task.Run(() =>
        {
            void Missing(string what)
            {
                if (all) Error($"{what} not found in the repair files ({toolDir}).");
                else Warn($"{what} not found in the repair files.");
            }

            // embed_wi_v4 → DATA/files/assets/bf
            Report(At(0.45), "Restoring embed_wi_v4...");
            string? embed = FindBySuffix(toolDir, "embed_wi_v4");
            if (embed != null) CopyLogged(embed, ResolveDestPath(filesDir, "assets/bf/embed_wi_v4"));
            else Missing("embed_wi_v4");

            if (!all) return;

            // data → DATA/files (the top-level data folder, not the ones inside embed_wi_v4)
            Report(At(0.6), "Restoring data...");
            string? data = Directory.EnumerateDirectories(toolDir, "*", SearchOption.AllDirectories)
                .Where(d => Ci.Equals(Path.GetFileName(d), "data")
                            && !SplitPath(Path.GetRelativePath(toolDir, d)).Contains("embed_wi_v4", Ci))
                .OrderBy(d => d.Length)
                .FirstOrDefault();
            if (data != null) CopyLogged(data, ResolveDestPath(filesDir, "data"));
            else Missing("The data folder");

            // main.dol → DATA/sys
            Report(At(0.9), "Restoring main.dol...");
            string? dol = FindFileRecursive(toolDir, "main.dol");
            if (dol != null)
            {
                string sysDir = TargetPath(DataSys, "");
                Directory.CreateDirectory(sysDir);
                File.Copy(dol, MatchExisting(sysDir, "main.dol", isDirectory: false), overwrite: true);
                Ok($"File 'main.dol' copied to {sysDir}");
            }
            else
            {
                Missing("main.dol");
            }
        });
    }

    // ───────────── Restore Dolphin controls ─────────────

    /// <summary>
    /// Puts back the Dolphin settings KBM Controls changed, from the *.original backups it made:
    /// Wii Remote 1's mappings, "Connect USB Keyboard" and the Reset hotkey. Only those settings are
    /// touched, so anything else changed in Dolphin since stays. Returns what was restored.
    /// </summary>
    public static List<string> RestoreDolphinControls(string loadDir)
    {
        string userDir = Path.GetDirectoryName(loadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         ?? throw new InvalidOperationException("couldn't find Dolphin's User folder");
        string configDir = ResolveDestPath(userDir, "Config");
        var restored = new List<string>();

        string wiimote = MatchExisting(configDir, "WiimoteNew.ini", isDirectory: false);
        if (File.Exists(wiimote + ".original"))
        {
            Ini.ReplaceSection(wiimote, "Wiimote1", Ini.ReadSection(wiimote + ".original", "Wiimote1"));
            restored.Add("Wii Remote 1 controls");
        }

        string dolphin = MatchExisting(configDir, "Dolphin.ini", isDirectory: false);
        if (File.Exists(dolphin + ".original"))
        {
            RestoreIniValue(dolphin, "Core", "WiiKeyboard");
            restored.Add("Connect USB Keyboard setting");
        }

        string hotkeys = MatchExisting(configDir, "Hotkeys.ini", isDirectory: false);
        if (File.Exists(hotkeys + ".original"))
        {
            RestoreIniValue(hotkeys, "Hotkeys", "General/Reset");
            restored.Add("Reset hotkey");
        }

        foreach (string item in restored)
            Ok($"Restored Dolphin's original {item}.");

        // Esc always stops the game, whatever the original hotkeys were
        SetEscapeStopsGame(configDir);
        return restored;
    }

    /// <summary>
    /// Binds Dolphin's Stop hotkey to Esc. The keyboard is named in the binding, so it works even
    /// when Dolphin's hotkey device is set to a controller.
    /// </summary>
    private static void SetEscapeStopsGame(string configDir)
    {
        string escape = OperatingSystem.IsWindows()
            ? "`DInput/0/Keyboard Mouse:ESCAPE`"
            : "`XInput2/0/Virtual core pointer:Escape`";

        Directory.CreateDirectory(configDir);
        Ini.SetValue(MatchExisting(configDir, "Hotkeys.ini", isDirectory: false), "Hotkeys", "General/Stop", escape);
        Ok("Esc stops the game (Dolphin's Stop hotkey).");
    }

    /// <summary>Sets a key back to its value in the file's .original backup, or removes it if the backup didn't have it.</summary>
    private static void RestoreIniValue(string path, string section, string key)
    {
        var original = Ini.ReadSection(path + ".original", section);
        int i = original.FindIndex(kv => Ci.Equals(kv.Key, key));

        if (i >= 0) Ini.SetValue(path, section, key, original[i].Value);
        else Ini.RemoveValue(path, section, key); // wasn't set before, so Dolphin's default applies
    }

    // ───────────── Uninstall textures (uninstall_textures) ─────────────

    /// <summary>Removes everything in Load/Textures and Load/DynamicInputTextures.</summary>
    public static void UninstallTextures(string loadDir)
    {
        foreach (string sub in new[] { "Textures", "DynamicInputTextures" })
        {
            string dir = MatchExisting(loadDir, sub, isDirectory: true);
            if (!Directory.Exists(dir))
            {
                Info($"Path {dir} does not exist.");
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
                    Log("ERROR", $"Failed to delete {entry}: {ex.Message}");
                }
            }
            Ok($"All files and folders in {dir} have been deleted.");
        }
    }

    // ───────────── Tools ─────────────

    private ToolItem? FindTool(string name) =>
        _req.Manifest.Tools.FirstOrDefault(t => Ci.Equals(t.Name, name));

    /// <summary>Downloads a manifest tool into Tools/&lt;Name&gt; if it isn't there yet.</summary>
    private async Task<string?> EnsureToolAsync(string name, double progressFrom, double progressTo)
    {
        var tool = FindTool(name);
        if (tool == null)
        {
            Error($"The tool '{name}' is not listed in the manifest.");
            return null;
        }

        string dir = Path.Combine(_req.ToolsDir, tool.Name);
        if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any())
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

        _req.Config.InstalledVersions[!string.IsNullOrEmpty(tool.Id) ? tool.Id : tool.Name] = tool.Version;
        ConfigManager.Save(_req.Config);
        return dir;
    }

    // ───────────── Paths ─────────────

    /// <summary>The folder that contains DATA for an installed build (e.g. Builds/r911-M/Battlefront III r2.91120a Unpacked).</summary>
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
            .Where(d => Ci.Equals(Path.GetFileName(d), "DATA") && !d.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase))
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
            GameRoot => RequireGameDir(),
            Data => ResolveDestPath(RequireGameDir(), "DATA"),
            DataFiles => ResolveDestPath(RequireGameDir(), "DATA/files"),
            DataSys => ResolveDestPath(RequireGameDir(), "DATA/sys"),
            AppDataLoad => RequireLoadDir(),
            Textures => ResolveDestPath(RequireLoadDir(), $"Textures/{GameId}"),
            DolphinUser => RequireDolphinUserDir(),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        return string.IsNullOrEmpty(subpath) ? basePath : ResolveDestPath(basePath, subpath);
    }

    /// <summary>Builds a destination path, reusing existing folders whatever their case (Linux is case-sensitive).</summary>
    private static string ResolveDestPath(string basePath, string relative) =>
        SplitPath(relative).Aggregate(basePath, (current, segment) => MatchExisting(current, segment, isDirectory: true));

    private static string MatchExisting(string parent, string name, bool isDirectory)
    {
        string exact = Path.Combine(parent, name);
        if (isDirectory ? Directory.Exists(exact) : File.Exists(exact)) return exact;
        if (!Directory.Exists(parent)) return exact;

        var entries = isDirectory ? Directory.EnumerateDirectories(parent) : Directory.EnumerateFiles(parent);
        return entries.FirstOrDefault(e => Ci.Equals(Path.GetFileName(e), name)) ?? exact;
    }

    private static string[] SplitPath(string path) =>
        path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    // ───────────── Finding things inside a download ─────────────

    /// <summary>Unwraps single wrapper folders down to the real content, stopping at structural folders.</summary>
    private static string ContentRoot(string stage)
    {
        string dir = stage;
        while (true)
        {
            bool hasFiles = Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f) != StageMarker);
            var dirs = Directory.GetDirectories(dir).Where(d => !Ci.Equals(Path.GetFileName(d), "__MACOSX")).ToArray();

            if (hasFiles || dirs.Length != 1 || StructuralNames.Contains(Path.GetFileName(dirs[0])))
                return dir;
            dir = dirs[0];
        }
    }

    private static string? ResolveSource(ModContext c, string[] sources)
    {
        foreach (string source in sources)
        {
            if (source == "") return c.Content;

            string? found = ResolveRelative(c.Stage, source)
                            ?? ResolveRelative(c.Content, source)
                            ?? FindBySuffix(c.Stage, source);
            if (found != null) return found;
        }
        return null;
    }

    private static string? ResolveRelative(string root, string relative)
    {
        string? current = root;
        foreach (string segment in SplitPath(relative))
        {
            current = FindChildDir(current, segment);
            if (current == null) return null;
        }
        return current;
    }

    /// <summary>Finds the shallowest folder whose path ends with <paramref name="relative"/> (case-insensitive).</summary>
    private static string? FindBySuffix(string root, string relative)
    {
        string[] segments = SplitPath(relative);

        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => (Dir: d, Parts: SplitPath(Path.GetRelativePath(root, d))))
            .Where(x => x.Parts.Length >= segments.Length
                        && x.Parts.Skip(x.Parts.Length - segments.Length).SequenceEqual(segments, Ci))
            .OrderBy(x => x.Parts.Length)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static string? FindChildDir(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (Directory.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir).FirstOrDefault(d => Ci.Equals(Path.GetFileName(d), name));
    }

    private static bool HasChild(string dir, string name) => FindChildDir(dir, name) != null;

    /// <summary>Like HasChild, but the folder name must match exactly (Windows paths ignore case, so names are compared).</summary>
    private static bool HasChildExactCase(string dir, string name) =>
        Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(d => Path.GetFileName(d) == name);

    /// <summary>Readme / instruction files that come with mods and don't belong in the game.</summary>
    private static bool IsDocFile(string path) =>
        new[] { ".txt", ".md", ".pdf", ".rtf", ".url", ".docx" }.Contains(Path.GetExtension(path), Ci);

    private static string? FindFile(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => Ci.Equals(Path.GetFileName(f), name));
    }

    /// <summary>Shallowest file with this name anywhere under <paramref name="root"/> (case-insensitive).</summary>
    private static string? FindFileRecursive(string root, string name) =>
        Directory.EnumerateFiles(root, name, RecursiveCi).OrderBy(p => p.Length).FirstOrDefault();

    private static bool IsTextureMod(ModItem mod) =>
        mod.Category?.Contains("Texture", StringComparison.OrdinalIgnoreCase) == true;

    // ───────────── Copying ─────────────

    /// <summary>Copies everything (tools, repair files, backups) — never skips any file.</summary>
    private static void CopyLogged(string src, string dest, ISet<string>? skipTopLevel = null)
    {
        int count = CopyMerge(src, dest, skipTopLevel, skipDocs: false);
        Ok($"Copied {count} file(s) from {src} to {dest}");
    }

    /// <summary>Copies a mod's files into place, leaving out the mod's own readme/instruction files at the top level.</summary>
    private static void CopyModFiles(string src, string dest, ISet<string>? skipTopLevel = null)
    {
        int count = CopyMerge(src, dest, skipTopLevel, skipDocs: true);
        Ok($"Copied {count} file(s) from {src} to {dest}");
    }

    /// <summary>
    /// Recursively copies src into dest, overwriting files and merging into existing folders
    /// regardless of case. Skips the download marker and __MACOSX folders, and (skipDocs) top-level
    /// readme/instruction files. Tools must be copied with skipDocs = false: the compiler's .txt
    /// file lists (e.g. all_res_files_r9.txt) are required.
    /// </summary>
    private static int CopyMerge(string src, string dest, ISet<string>? skipTopLevel, bool skipDocs, bool topLevel = true)
    {
        int count = 0;
        Directory.CreateDirectory(dest);

        // Index dest once (texture folders can hold thousands of files)
        var existingFiles = IndexByName(Directory.EnumerateFiles(dest));
        var existingDirs = IndexByName(Directory.EnumerateDirectories(dest));

        foreach (string file in Directory.EnumerateFiles(src))
        {
            string name = Path.GetFileName(file);
            if (name == StageMarker || skipTopLevel?.Contains(name) == true) continue;
            if (skipDocs && topLevel && IsDocFile(file))
            {
                Info($"Skipped {name} (mod documentation).");
                continue;
            }

            File.Copy(file, PickTarget(dest, name, existingFiles, File.Exists), overwrite: true);
            count++;
        }

        foreach (string dir in Directory.EnumerateDirectories(src))
        {
            string name = Path.GetFileName(dir);
            if (skipTopLevel?.Contains(name) == true || Ci.Equals(name, "__MACOSX")) continue;

            count += CopyMerge(dir, PickTarget(dest, name, existingDirs, Directory.Exists), null, skipDocs, topLevel: false);
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

    /// <summary>The exact name if it exists, else an existing entry with the same name in another case, else the exact name.</summary>
    private static string PickTarget(string dest, string name, Dictionary<string, string> existing, Func<string, bool> exists)
    {
        string exact = Path.Combine(dest, name);
        if (exists(exact)) return exact;
        if (!existing.TryGetValue(name, out string? match)) return exact;

        // DATA (disc partition) and data (game folder) are different folders: never merge one into the other
        bool eitherIsData = Path.GetFileName(match) == "DATA" || name == "DATA";
        return eitherIsData ? exact : match;
    }

    // ───────────── Dolphin INI files ─────────────

    /// <summary>
    /// Minimal editor for Dolphin's "Key = Value" INI files. Keeps every other line (and comment) as it is;
    /// sections and keys are matched case-insensitively. Missing files and sections are created.
    /// </summary>
    private static class Ini
    {
        public static List<(string Key, string Value)> ReadSection(string path, string section)
        {
            var lines = Read(path);
            var (start, end) = FindSection(lines, section);
            var entries = new List<(string Key, string Value)>();
            if (start < 0) return entries;

            for (int i = start + 1; i < end; i++)
            {
                if (TryParse(lines[i], out string key, out string value))
                    entries.Add((key, value));
            }
            return entries;
        }

        public static void SetValue(string path, string section, string key, string value)
        {
            var lines = Read(path);
            var (start, end) = FindSection(lines, section);
            string line = $"{key} = {value}";

            if (start < 0)
            {
                AppendSection(lines, section, new[] { line });
            }
            else
            {
                int existing = -1;
                for (int i = start + 1; i < end && existing < 0; i++)
                {
                    if (TryParse(lines[i], out string k, out _) && Ci.Equals(k, key))
                        existing = i;
                }

                if (existing >= 0) lines[existing] = line;
                else lines.Insert(LastContentLine(lines, start, end) + 1, line);
            }

            File.WriteAllLines(path, lines);
        }

        public static void ReplaceSection(string path, string section, IEnumerable<(string Key, string Value)> entries)
        {
            var lines = Read(path);
            var (start, end) = FindSection(lines, section);
            var newLines = entries.Select(e => $"{e.Key} = {e.Value}").ToList();

            if (start < 0)
            {
                AppendSection(lines, section, newLines);
            }
            else
            {
                int last = LastContentLine(lines, start, end);
                lines.RemoveRange(start + 1, last - start);
                lines.InsertRange(start + 1, newLines);
            }

            File.WriteAllLines(path, lines);
        }

        private static List<string> Read(string path) =>
            File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

        /// <summary>Header line index (or -1) and the index where the section ends.</summary>
        private static (int Start, int End) FindSection(List<string> lines, string section)
        {
            int start = lines.FindIndex(l => Ci.Equals(l.Trim(), $"[{section}]"));
            if (start < 0) return (-1, -1);

            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            return (start, end < 0 ? lines.Count : end);
        }

        /// <summary>Last non-blank line in the section (the header if it's empty), so blank separators stay put.</summary>
        private static int LastContentLine(List<string> lines, int start, int end)
        {
            int last = end - 1;
            while (last > start && string.IsNullOrWhiteSpace(lines[last])) last--;
            return last;
        }

        public static void RemoveValue(string path, string section, string key)
        {
            if (!File.Exists(path)) return;

            var lines = Read(path);
            var (start, end) = FindSection(lines, section);
            if (start < 0) return;

            for (int i = start + 1; i < end; i++)
            {
                if (TryParse(lines[i], out string k, out _) && Ci.Equals(k, key))
                {
                    lines.RemoveAt(i);
                    File.WriteAllLines(path, lines);
                    return;
                }
            }
        }

        private static void AppendSection(List<string> lines, string section, IEnumerable<string> body)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{section}]");
            lines.AddRange(body);
        }

        private static bool TryParse(string line, out string key, out string value)
        {
            key = value = "";
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';' or '[') return false;

            int eq = trimmed.IndexOf('=');
            if (eq <= 0) return false;

            key = trimmed[..eq].Trim();
            value = trimmed[(eq + 1)..].Trim();
            return true;
        }
    }

    // ───────────── Logging / progress ─────────────

    private void Report(double percent, string message) =>
        _progress?.Invoke(Math.Clamp(percent, 0, 100), message);

    private static void Log(string level, string message) => Console.WriteLine($"[{level}] {message}");
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