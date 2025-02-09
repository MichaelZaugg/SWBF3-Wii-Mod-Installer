# installer.py
import os
import shutil
import subprocess
import threading
import platform
from pathlib import Path
from utils import log_message, create_directory, copy_files, print_to_console
import config  # Always reference globals as config.GLOBAL_...


# ------------------Mod Installation Functions-------------------------

def repair_game(all=False, progress_callback=None):
    from pathlib import Path
    import shutil
    # Assume copy_files is defined/imported elsewhere.
    
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "files"
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "repair_files"
    sys_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "sys"

    # Build a list of tasks.
    tasks = []
    
    def task_embed():
        copy_files(mod_dir_path / "embed_wi_v4", game_data_dir / "assets" / "bf" / "embed_wi_v4")
    tasks.append(("Copy embed_wi_v4", task_embed))
    
    if all:
        def task_data():
            copy_files(mod_dir_path / "data", game_data_dir / "assets" / "bf" / "data")
        tasks.append(("Copy data", task_data))
        
        def task_main_dol():
            try:
                shutil.copy(mod_dir_path / "main.dol", sys_dir)
                log_message(f"File 'main.dol' copied to {sys_dir}", "success")
            except Exception as e:
                log_message(f"Failed to copy 'main.dol' to {sys_dir}: {e}", "error")
        tasks.append(("Copy main.dol", task_main_dol))
    
    total_tasks = len(tasks)
    for index, (desc, task_func) in enumerate(tasks):
        log_message(f"Starting: {desc}", "info")
        task_func()
        if progress_callback:
            # Calculate progress (e.g., if there are 3 tasks, after first task progress is 0.33, etc.)
            progress = (index + 1) / total_tasks
            # Schedule the update on the main thread (if necessary):
            from ui import root  # Ensure root is accessible.
            root.after(0, lambda p=progress: progress_callback(p))
    if progress_callback:
        root.after(0, lambda: progress_callback(1.0))


def compile_templates_res(only_res=False, forced=False):
    """
    Defult Compiler: compile_templates_and_res.bat
    (only_res= True): Only does compile_all_res.bat
    (only_res= True, forced= True): Only does compile_all_res_forced.bat
    """
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "files"
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)

    if only_res:
        batch_file = game_data_dir / "compile_all_res.bat"
    elif only_res and forced:
        batch_file = game_data_dir / "compile_all_res_forced.bat"
    else:
        batch_file = game_data_dir / "compile_templates_and_res.bat"

    if not batch_file.exists():
        log_message("compile_templates_and_res.bat not found. Copying necessary files...", "error")
        copy_files(mod_dir_path / "EmbeddedResCompiler", game_data_dir)

    if batch_file.exists():
        log_message("Starting compilation process...", "info")
        def run_batch_file():
            try:
                process = subprocess.Popen(
                    str(batch_file),
                    shell=True,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    cwd=str(batch_file.parent)
                )
                while True:
                    output = process.stdout.readline()
                    if output == "" and process.poll() is not None:
                        break
                    if output:
                        log_message(output.strip(), "info")
                stdout, stderr = process.communicate()
                if stdout:
                    log_message(stdout.strip(), "info")
                if stderr:
                    log_message(stderr.strip(), "error")
                if process.returncode == 0:
                    log_message("Resource compilation completed successfully.", "success")
                else:
                    log_message("Error during resource compilation.", "error")
            finally:
                pass
        compile_thread = threading.Thread(target=run_batch_file)
        compile_thread.start()
        compile_thread.join()
    else:
        log_message("compile_templates_and_res.bat still not found after copying. Please check manually.", "error")

def install_dynamic_input_textures():
    app_data = Path(config.GLOBAL_APPDATA_DIR) / "Load"
    dynamic_textures_dir = app_data / "DynamicInputTextures"
    rabazz_dir = dynamic_textures_dir / "RABAZZ"
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)

    create_directory(dynamic_textures_dir)
    create_directory(rabazz_dir)

    def copy_with_xcopy(src, dest):
        if platform.system() == "Windows":
            cmd = f'xcopy "{src}" "{dest}" /E /I /Q /Y'
            log_message(f"Running: {cmd}", "info")
            try:
                result = subprocess.run(cmd, shell=True, capture_output=True, text=True)
                if result.returncode == 0:
                    log_message(f"Copied {src} to {dest} successfully.", "success")
                else:
                    log_message(f"Error during copying {src} to {dest}: {result.stderr}", "error")
            except Exception as e:
                log_message(f"Failed to copy {src} to {dest}: {e}", "error")
        else:
            log_message("xcopy is not supported on non-Windows systems.", "error")

    log_message("Copying Dynamic Input Textures...", "info")
    copy_with_xcopy(mod_dir_path / "DynamicInputTextures" / "DynamicInputTextures", dynamic_textures_dir)
    log_message("Copying RABAZZ textures...", "info")
    copy_with_xcopy(mod_dir_path / "RABAZZ_dynamic" / "RABAZZ", rabazz_dir)

def install_muted_blank_audio():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)
    game_data_dir = Path(config.GLOBAL_GAME_DIR)
    copy_files(mod_dir_path / "SWBF3_Wii_Muted_Blank_Sounds", game_data_dir)

def install_4k_texture_pack():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "4kTexturePacks"
    texture_dir = Path(config.GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ"
    copy_files(mod_dir_path, texture_dir)

def install_lighting_fix(scene_descriptor=False):
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "files" / "data"

    if scene_descriptor:
        mgrsetup_path = game_data_dir / "bf" / "mgrsetup"
        scene_descriptor_file = mgrsetup_path / "scene_descriptors.res"
        if scene_descriptor_file.exists():
            log_message("Updating scene descriptors...", "info")
            try:
                subprocess.run(
                    [
                        "powershell",
                        "-Command",
                        f"(Get-Content '{scene_descriptor_file}') -replace '0.118128', '0.118128' | Set-Content '{scene_descriptor_file}'"
                    ],
                    check=True,
                    text=True
                )
                log_message("Scene descriptor updated successfully.", "success")
            except subprocess.CalledProcessError as e:
                log_message(f"Error updating scene descriptors: {e}", "error")

        return
    
    copy_files(mod_dir_path / "SWBF3_Wii_Light_Fixes" / "data" , game_data_dir)

def install_updated_debug_menu():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)
    sys_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "sys"
    try:
        shutil.copy(mod_dir_path / "main.dol", sys_dir)
        log_message(f"File 'main.dol' copied to {sys_dir}", "success")
    except Exception as e:
        log_message(f"Failed to copy 'main.dol' to {sys_dir}: {e}", "error")

def install_cloth_fix():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "files"
    copy_files(mod_dir_path / "Battlefront_III_Cloth_Fix" / "Battlefront_III_Cloth_Fix", game_data_dir)

def install_4k_characters_model_fix():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)
    app_data_textures_dir = Path(config.GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ" / "characters"
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "files"
    create_directory(app_data_textures_dir)
    copy_files(mod_dir_path / "characters", app_data_textures_dir)
    copy_files(mod_dir_path / "ingame_models_x1_x2_new", game_data_dir)
    log_message("4k Characters/Model Fix installation complete.", "success")

def install_faithful_health_bars():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "_faithful_hpbars_v2"
    app_data_textures_dir = Path(config.GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ" / "_faithful_hpbars_v2"
    create_directory(app_data_textures_dir)
    copy_files(mod_dir_path, app_data_textures_dir)

def install_minimap_fix():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "minimaps" / "minimaps"
    app_data_textures_dir = Path(config.GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ" / "minimaps"
    create_directory(app_data_textures_dir)
    copy_files(mod_dir_path, app_data_textures_dir)

def install_pc_xbox_features():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "frontend_preview"
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA" / "files"
    copy_files(mod_dir_path, game_data_dir / "data" / "bf" / "menus")

def install_music_clonetrooper_vo():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR)
    game_data_dir = Path(config.GLOBAL_GAME_DIR)
    copy_files(mod_dir_path / "Music_and_Clone_VO" / "Music_and_Clone_VO", game_data_dir)

def install_restored_r7_vehicles():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "restored_r7_vehicles"
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA"
    copy_files(mod_dir_path, game_data_dir)

def install_restored_melee_classes():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "RestoredMeleeClasses"
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA"
    copy_files(mod_dir_path, game_data_dir)

def install_unique_icons():
    mod_dir_path = Path(config.GLOBAL_MOD_DIR) / "ClassUniqueIcons"
    game_data_dir = Path(config.GLOBAL_GAME_DIR) / "DATA"
    copy_files(mod_dir_path, game_data_dir)

# Mapping of mod names to their installation functions
MODS = {
    "Muted Blank Audio": install_muted_blank_audio,
    "4k texture pack Part 1, 2, 3, 4, 5": install_4k_texture_pack,
    "Lighting Fix": install_lighting_fix,
    "Updated Debug Menu (main.dol from Clonetrooper163)": install_updated_debug_menu,
    "Cloth Fix": install_cloth_fix,
    "4k Characters/Model Fix": install_4k_characters_model_fix,
    "Texture Pack: Faithful Health Bars": install_faithful_health_bars,
    "Dynamic Input Textures": install_dynamic_input_textures,
    "Minimaps Fix (For r904, Enable prefetch custom textures)": install_minimap_fix,
    "Unlocked PC/Xbox 360 Features in Frontend": install_pc_xbox_features,
    "Music for all maps/modes-Fixed Clonetrooper VO": install_music_clonetrooper_vo,
    "Restored r7 Vehicles": install_restored_r7_vehicles,
    "r9 Restored Melee Classes(Class Unique Icons Fix Included)": install_restored_melee_classes,
    "Class Unique Icons Fix": install_unique_icons
}

MODS_REQUIRING_COMPILATION = {
    "Lighting Fix", #compile_all_res
    "Cloth Fix", #compile_all_res
    "Unlocked PC/Xbox 360 Features in Frontend", #compile_all_res
    "Music for all maps/modes-Fixed Clonetrooper VO", #compile_all_res_force
    "Restored r7 Vehicles", #compile_templates
    "r9 Restored Melee Classes(Class Unique Icons Fix Included)", #compile_templates
    "Class Unique Icons Fix", #compile_templates
}

# Mapping mod names to functions that return their directories
MODS_DIRECTORY = {
    "Muted Blank Audio": lambda: os.path.join(config.GLOBAL_MOD_DIR, "SWBF3_Wii_Muted_Blank_Sounds"),
    "4k texture pack Part 1, 2, 3, 4, 5": lambda: os.path.join(config.GLOBAL_MOD_DIR, "4kTexturePacks"),
    "Lighting Fix": lambda: os.path.join(config.GLOBAL_MOD_DIR, "SWBF3_Wii_Light_Fixes"),
    "Updated Debug Menu (main.dol from Clonetrooper163)": lambda: os.path.join(config.GLOBAL_MOD_DIR, "Updated_Debug_Menu"),
    "Cloth Fix": lambda: os.path.join(config.GLOBAL_MOD_DIR, "Battlefront_III_Cloth_Fix"),
    "4k Characters/Model Fix": lambda: os.path.join(config.GLOBAL_MOD_DIR, "characters"),
    "Texture Pack: Faithful Health Bars": lambda: os.path.join(config.GLOBAL_MOD_DIR, "_faithful_hpbars_v2"),
    "Dynamic Input Textures": lambda: os.path.join(config.GLOBAL_MOD_DIR, "DynamicInputTextures"),
    "Minimaps Fix (For r904, Enable prefetch custom textures)": lambda: os.path.join(config.GLOBAL_MOD_DIR, "Minimaps"),
    "Unlocked PC/Xbox 360 Features in Frontend": lambda: os.path.join(config.GLOBAL_MOD_DIR, "frontend_preview"),
    "Music for all maps/modes-Fixed Clonetrooper VO": lambda: os.path.join(config.GLOBAL_MOD_DIR, "Music_and_Clone_VO"),
    "Restored r7 Vehicles": lambda: os.path.join(config.GLOBAL_MOD_DIR, "restored_r7_vehicles"),
    "r9 Restored Melee Classes(Class Unique Icons Fix Included)": lambda: os.path.join(config.GLOBAL_MOD_DIR, "RestoredMeleeClasses"),
    "Class Unique Icons Fix": lambda: os.path.join(config.GLOBAL_MOD_DIR, "ClassUniqueIcons"),
}

# -------------------Installation Process Functions--------------------

def check_install_conditions(mod_vars):
    import config
    errors = False
    warnings = []
    selected_mods = [mod for mod, var in mod_vars.items() if var.get() == 1]
    if not config.GLOBAL_GAME_DIR:
        print_to_console("No valid game directory selected.", "error")
        errors = True
    if not config.GLOBAL_MOD_DIR:
        print_to_console("No valid mod directory selected.", "error")
        errors = True
    if not config.GLOBAL_APPDATA_DIR:
        print_to_console("No AppData directory selected.", "error")
        errors = True
    elif config.GLOBAL_CUSTOM_APPDATA:
        print_to_console("Custom AppData directory accepted without further checks.", "info")
    else:
        expected_path = (config.GLOBAL_APPDATA_DIR if config.GLOBAL_APPDATA_DIR.endswith('Dolphin Emulator')
                         else os.path.join(config.GLOBAL_APPDATA_DIR, 'Dolphin Emulator'))
        if not os.path.exists(expected_path):
            print_to_console(f"Dolphin Emulator directory not found at {expected_path}.", "error")
            errors = True

    if not selected_mods:
        print_to_console("No mods have been selected.", "error")
        errors = True

    if "Music for all maps/modes-Fixed Clonetrooper VO" in selected_mods:
        if "Restored r7 Vehicles" in selected_mods:
            print_to_console("Error: 'Music for all maps/modes-Fixed Clonetrooper VO' is incompatible with 'Restored r7 Vehicles'.", "error")
        else:
            warnings.append("Warning: 'Music for all maps/modes-Fixed Clonetrooper VO' will override lighting changes.")
    if "Class Unique Icons Fix" in selected_mods:
        if "r9 Restored Melee Classes(Class Unique Icons Fix Included)" in selected_mods:
            print_to_console("Error: 'Class Unique Icons Fix' is incompatible with 'r9 Restored Melee Classes(Class Unique Icons Fix Included)'. Please install one or the other.", "error")
        else:
            warnings.append("Warning: 'Class Unique Icons Fix Included' is not campatible with 'r9 Restored Melee Classes(Class Unique Icons Fix Included)'.")

    for warning in warnings:
        print_to_console(warning, "warning")

    return not errors

def check_file_name_conflicts(selected_mods):
    file_names = {}
    conflicts = []

    for mod, is_selected in selected_mods.items():
        if is_selected.get():
            if mod == "Updated Debug Menu (main.dol from Clonetrooper163)":
                continue
            mod_dir_function = MODS_DIRECTORY.get(mod)
            if not mod_dir_function:
                print_to_console(f"Mod directory not found for: {mod}", "error")
                continue
            mod_dir = mod_dir_function()
            if os.path.exists(mod_dir):
                for root_dir, _, files in os.walk(mod_dir):
                    for file in files:
                        if file.endswith((".res", ".war")):
                            normalized_file_name = file.lower()
                            if normalized_file_name in file_names:
                                file_names[normalized_file_name].add(mod)
                            else:
                                file_names[normalized_file_name] = {mod}
            else:
                print_to_console(f"Mod directory does not exist: {mod_dir}", "error")

    for file_name, mods in file_names.items():
        if len(mods) > 1:
            if file_name == "hudmgr.res" or {"Restored r7 Vehicles", "r9 Restored Melee Classes(Class Unique Icons Fix Included)", "Class Unique Icons Fix"}.issubset(mods):
                print_to_console(f"[ERROR] File: {file_name} is present in mods: {', '.join(mods)} (DISMISSED)", "info")
                continue
            if file_name == "invisible_hand.res" and {"Lighting Fix", "Music for all maps/modes-Fixed Clonetrooper VO"}.issubset(mods):
                print_to_console(f"[ERROR] File: {file_name} is present in mods: {', '.join(mods)} (DISMISSED)", "info")
                continue
            conflicts.append((file_name, list(mods)))
            print_to_console(f"[ERROR] File: ", "error", end="")
            print_to_console(file_name, "yellow", end="")
            print_to_console(f" is present in mods: {', '.join(mods)}", "error")
    return conflicts

def install_selected_mods(selected_mods):
    """
    Install the selected mods while ensuring the UI remains responsive.
    The function will:
    - Display a loading animation.
    - Run installation in a background thread.
    - Install "Lighting Fix" last if selected.
    - Only compile resources if required.
    """
    
    def install():
        try:
            from ui import start_loading, stop_loading  # Lazy import to prevent circular imports
            start_loading()  # Start loading animation

            if not check_install_conditions(selected_mods):
                log_message("Installation aborted due to errors.", "error")
                return

            # Check for file name conflicts
            conflicts = check_file_name_conflicts(selected_mods)
            if conflicts:
                return  # Abort installation if conflicts exist

            log_message("No conflicts detected. Proceeding with installation...", "success")

            # Separate "Lighting Fix" from other mods
            mods_to_install = [mod for mod, is_selected in selected_mods.items() if is_selected.get()]
            lighting_fix_selected = "Lighting Fix" in mods_to_install
            other_compilation_mods_selected = any(
                mod in MODS_REQUIRING_COMPILATION for mod in mods_to_install #if mod != "Lighting Fix"
            )

            #install mods
            for mod in mods_to_install:
                log_message(f"Installing: {mod}", "info")
                try:
                    mod_install_function = MODS.get(mod)
                    if mod_install_function:
                        mod_install_function()
                        log_message(f"{mod} installed successfully.", "success")
                    else:
                        log_message(f"No installation function found for {mod}. Skipping...", "warning")
                except Exception as e:
                    log_message(f"Error installing {mod}: {e}", "error")

            # Compile resources if necessary
            if other_compilation_mods_selected:
                repair_game()
                log_message("Compilation required. Starting compilation process...", "info")

                compile_templates_res()

            # Install "Lighting Fix" last
            if lighting_fix_selected:
                install_lighting_fix(scene_descriptor=True)
                compile_templates_res(only_res=True)
                log_message("Lighting Fix installed successfully.", "success")

            log_message("----- Installation Complete -----", "success")


        finally:
            stop_loading()  # Stop loading animation

    # Start installation in a separate thread to prevent UI freezing
    install_thread = threading.Thread(target=install, daemon=True)
    install_thread.start()

# Alias for backward compatibility with UI imports
start_install_process = install_selected_mods

# --------------------End of installer.py------------------------------
