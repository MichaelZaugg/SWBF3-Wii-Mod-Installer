import os
import sys
import shutil
import tkinter as tk
from tkinter import filedialog, Label, Entry, Toplevel, Checkbutton, IntVar, Frame, Button, Text, PhotoImage
import tkinter.messagebox as messagebox
from pathlib import Path
import itertools
import threading
import subprocess
import requests
import json
import signal
import time
import platform
import tempfile


# Global flags and path variables
current_version = "5.6"
TITLE = f"SWBF3 Wii Mod Installer v{current_version}"
GLOBAL_GAME_DIR = ""
GLOBAL_APPDATA_DIR = ""
GLOBAL_CUSTOM_APPDATA = False
GLOBAL_MOD_DIR = ""
ICON_PATH = 'SWBF3Icon.ico'
CONFIG_FILE = "mod_installer_config"

#--------Update Manifest--------
MANIFEST_URL = "https://raw.githubusercontent.com/MichaelZaugg/SWBF3-Wii-Mod-Installer/refs/heads/main/manifest.json"
MOD_VERSIONS_URL = "https://raw.githubusercontent.com/MichaelZaugg/SWBF3-Wii-Mod-Installer/refs/heads/main/mod_versions.json"
loading_label = None
stop_loading_flag = False

# --------------------Loading Animation------------------------


def show_loading_animation():
    """
    Display a spinning animation in the UI.
    """
    spinner = itertools.cycle(["|", "/", "-", "\\"])  # Spinner characters
    global loading_label, stop_loading_flag

    if not loading_label:
        loading_label = Label(root, text="", bg="#2b2b2b", fg="white")
        loading_label.grid(row=7, column=0, columnspan=3, sticky='ew', padx=5, pady=5)

    def animate():
        if stop_loading_flag:
            loading_label.config(text="")  # Clear the spinner
            return
        loading_label.config(text=next(spinner))
        root.after(100, animate)  # Schedule next frame

    animate()

def start_loading():
    """
    Start the spinner animation.
    """
    global stop_loading_flag
    stop_loading_flag = False
    show_loading_animation()

def stop_loading():
    """
    Stop the spinner animation.
    """
    global stop_loading_flag
    stop_loading_flag = True




# --------------------Helper Functions------------------------

def fetch_manifest():
    try:
        response = requests.get(MANIFEST_URL)
        response.raise_for_status()
        return response.json()
    except requests.RequestException as e:
        log_message(f"Error fetching manifest: {e}", "error")
        return None

def check_installer_update():
    """
    Checks for installer updates and downloads the new installer if available.
    Prints a message to delete the old installer, waits for 10 seconds, and exits.
    """
    log_message("Checking for installer updates...", "info")

    def download_and_update():
        try:
            manifest = fetch_manifest()
            if not manifest:
                log_message("Failed to fetch manifest.", "error")
                return False

            remote_installer = manifest.get("installer", {})
            remote_version = remote_installer.get("version")
            download_url = remote_installer.get("download_url")

            if not remote_version or not download_url:
                log_message("Installer version or download URL missing in manifest.", "error")
                return False

            current_installer_path = os.path.abspath(sys.argv[0])
            new_installer_path = os.path.join(
                os.path.dirname(current_installer_path),
                f"SWBF3_Wii_Mod_Installer_v{remote_version}.exe"
            )

            if remote_version > current_version:
                log_message(f"New installer version available: {remote_version}", "info")

                log_message("Downloading the updated installer...", "info")
                response = requests.get(download_url, stream=True)
                response.raise_for_status()

                with open(new_installer_path, "wb") as f:
                    for chunk in response.iter_content(chunk_size=8192):
                        if chunk:
                            f.write(chunk)

                log_message(f"Installer updated successfully. Saved to {new_installer_path}.", "success")
                log_message("Please delete the old installer.", "error")
                time.sleep(10)  # Wait for 10 seconds
                os._exit(0)  # Exit after the message
            else:
                log_message("Installer is up-to-date.", "success")
                return False
        except requests.RequestException as e:
            log_message(f"Error checking for installer updates: {e}", "error")
            return False

    start_loading()
    download_and_update()
    stop_loading()


def check_for_updates():
    def perform_update_sequence():
        try:
            log_message("Starting update check...", "info")
            installer_updated = check_installer_update()

            if installer_updated:
                log_message("Installer updated. Exiting the current program...", "info")
                root.after(0, root.destroy)
                os.kill(os.getpid(), signal.SIGTERM)
            else:
                log_message("Checking for mod updates...", "info")
                check_mod_versions()
        finally:
            stop_loading()

    start_loading()  # Start animation
    thread = threading.Thread(target=perform_update_sequence, daemon=True)
    thread.start()

def check_mod_versions():
    """
    Checks if mod_versions.json exists. If not, downloads it.
    Then checks the manifest for updates and updates mods if needed.
    """
    mod_versions_path = os.path.join(GLOBAL_MOD_DIR, "mod_versions.json")

    try:
        # Ensure mod_versions.json exists
        if not os.path.exists(mod_versions_path):
            log_message("mod_versions.json not found. Downloading the latest version...", "info")
            response = requests.get(MOD_VERSIONS_URL, stream=True)
            response.raise_for_status()
            
            with open(mod_versions_path, "wb") as file:
                for chunk in response.iter_content(chunk_size=8192):
                    file.write(chunk)
            
            log_message(f"mod_versions.json downloaded and saved to {mod_versions_path}.", "success")

        # Load local mod versions
        local_mod_versions = {}
        with open(mod_versions_path, "r", encoding="utf-8") as file:
            local_mod_versions = {mod["name"]: {"version": mod.get("version", "0.0"), "dir": mod.get("dir", mod["name"])} 
                                  for mod in json.load(file).get("mods", [])}

        # Fetch the manifest
        manifest = fetch_manifest()
        if not manifest:
            log_message("Failed to fetch manifest.", "error")
            return

        # Track if any mods are updated
        any_updates = False

        # Check each mod in the manifest
        for mod in manifest["mods"]:
            mod_name = mod.get("name")
            if not mod_name:
                log_message("Manifest mod entry missing 'name'. Skipping.", "error")
                continue

            remote_version = mod.get("version", "0.0")
            mod_dir = os.path.join(GLOBAL_MOD_DIR, mod.get("dir", mod_name))
            download_url = mod.get("download_url")

            # Retrieve local mod details
            local_mod = local_mod_versions.get(mod_name, {"version": "0.0", "dir": mod_name})
            local_version = local_mod["version"]

            # Compare versions and update if needed
            if remote_version > local_version:
                log_message(f"New version of {mod_name} available: {remote_version} (local: {local_version}). Downloading...", "info")
                if download_url:
                    if update_mod(mod_dir, download_url, mod_name, remote_version, mod_versions_path):
                        any_updates = True
                else:
                    log_message(f"No download URL provided for {mod_name}. Skipping update.", "warning")
            else:
                log_message(f"{mod_name} is up-to-date.", "success")

        if not any_updates:
            log_message("All mods are up-to-date. No changes made to mod_versions.json.", "info")

    except requests.RequestException as e:
        log_message(f"Failed to download mod_versions.json: {e}", "error")
    except Exception as e:
        log_message(f"Failed to check or update mod_versions.json: {e}", "error")



def update_mod(mod_dir, download_url, mod_name, remote_version, mod_versions_path):
    """
    Downloads and extracts a mod archive, ensuring no duplicate folder structures are created.
    Handles cases where the extracted folder has no nested folders or only one.
    """
    try:
        # Ensure the mod directory exists
        create_directory(mod_dir)

        # Download the mod archive
        log_message(f"Downloading files for {mod_name}...", "info")
        response = requests.get(download_url, stream=True)
        response.raise_for_status()

        temp_zip_path = os.path.join(mod_dir, "temp_mod.zip")
        with open(temp_zip_path, "wb") as temp_file:
            for chunk in response.iter_content(chunk_size=8192):
                temp_file.write(chunk)

        # Extract the archive to a temporary directory
        temp_extract_dir = tempfile.mkdtemp()
        shutil.unpack_archive(temp_zip_path, temp_extract_dir)
        os.remove(temp_zip_path)  # Clean up temporary zip file

        # Check the extracted content
        extracted_contents = os.listdir(temp_extract_dir)

        # If there's only one folder and its name matches the mod_dir folder name, move its contents
        if len(extracted_contents) == 1 and os.path.isdir(os.path.join(temp_extract_dir, extracted_contents[0])):
            single_folder_path = os.path.join(temp_extract_dir, extracted_contents[0])
            if os.path.basename(single_folder_path).lower() == os.path.basename(mod_dir).lower():
                # Move the contents of the single folder directly to mod_dir
                for item in os.listdir(single_folder_path):
                    src_path = os.path.join(single_folder_path, item)
                    dest_path = os.path.join(mod_dir, item)

                    if os.path.exists(dest_path):
                        if os.path.isdir(dest_path):
                            shutil.rmtree(dest_path)
                        else:
                            os.remove(dest_path)

                    if os.path.isdir(src_path):
                        shutil.copytree(src_path, dest_path)
                    else:
                        shutil.copy2(src_path, dest_path)

                log_message("Removed unnecessary duplicate folder after extraction.", "info")
            else:
                # Otherwise, treat it as a regular single folder and copy it as-is
                shutil.copytree(single_folder_path, mod_dir, dirs_exist_ok=True)
        else:
            # General case: copy all extracted content to mod_dir
            for item in extracted_contents:
                src_path = os.path.join(temp_extract_dir, item)
                dest_path = os.path.join(mod_dir, item)

                if os.path.exists(dest_path):
                    if os.path.isdir(dest_path):
                        shutil.rmtree(dest_path)
                    else:
                        os.remove(dest_path)

                if os.path.isdir(src_path):
                    shutil.copytree(src_path, dest_path)
                else:
                    shutil.copy2(src_path, dest_path)

        log_message(f"Files for {mod_name} updated successfully.", "success")

        # Clean up temporary extraction directory
        shutil.rmtree(temp_extract_dir)

        # Update mod_versions.json with the new version
        if os.path.exists(mod_versions_path):
            with open(mod_versions_path, "r", encoding="utf-8") as file:
                data = json.load(file)
        else:
            data = {"mods": []}

        # Find and update the specific mod
        updated = False
        for mod in data["mods"]:
            if mod["name"] == mod_name:
                mod["version"] = remote_version
                updated = True
                break

        # If the mod is not found, add it to the list
        if not updated:
            data["mods"].append({"name": mod_name, "version": remote_version, "dir": os.path.basename(mod_dir)})

        # Save the updated file
        with open(mod_versions_path, "w", encoding="utf-8") as file:
            json.dump(data, file, indent=4)

        log_message(f"Updated version for {mod_name} in mod_versions.json to {remote_version}.", "success")
        return True

    except requests.RequestException as e:
        log_message(f"Failed to download mod files from {download_url}: {e}", "error")
    except Exception as e:
        log_message(f"Error updating files or version for {mod_name} in {mod_dir}: {e}", "error")
    finally:
        # Ensure temporary directories are cleaned up
        if os.path.exists(temp_zip_path):
            os.remove(temp_zip_path)
        if os.path.exists(temp_extract_dir):
            shutil.rmtree(temp_extract_dir)
    return False




def download_mod_versions(mod_versions_path, url):
    """
    Downloads the mod_versions.json file from the given URL and saves it to the specified path.
    Replaces the existing file if it already exists.
    """
    try:
        response = requests.get(url, stream=True)
        response.raise_for_status()

        with open(mod_versions_path, "wb") as file:
            for chunk in response.iter_content(chunk_size=8192):
                file.write(chunk)

        log_message(f"mod_versions.json downloaded and saved to {mod_versions_path}.", "success")
    except requests.RequestException as e:
        log_message(f"Failed to download mod_versions.json: {e}", "error")


def resource_path(relative_path):
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")

    return os.path.join(base_path, relative_path)

def log_message(message, level="info"):
    """Log messages to both the terminal and the UI console."""
    levels = {"info": "INFO", "success": "SUCCESS", "error": "ERROR"}
    tag = level if level in levels else "info"
    print(f"[{levels.get(level, 'INFO')}] {message}")  # Log to terminal
    root.after(0, lambda: print_to_console(message, tag))  # Log to UI console


def print_to_console(message, tag=None, end="\n"):
    """
    Print messages to the console UI with optional tags for styling.
    """
    if 'console_text' in globals() and console_text is not None:
        if tag:
            console_text.insert('end', message + end, tag)
        else:
            console_text.insert('end', message + end)
        console_text.see('end')
        console_text.update_idletasks()  # Force the UI to update immediately
    else:
        print(message, end=end)  # Fallback to terminal logging



def create_directory(path):
    """Create a directory if it doesn't exist."""
    try:
        os.makedirs(path, exist_ok=True)
        log_message(f"Directory created or already exists: {path}", "success")
    except Exception as e:
        log_message(f"Failed to create directory {path}: {e}", "error")



def copy_files(src, dest, overwrite=True):
    """Copy files from src to dest."""
    try:
        if overwrite:
            shutil.copytree(src, dest, dirs_exist_ok=True)
        else:
            shutil.copytree(src, dest)
        log_message(f"Files copied from {src} to {dest}", "success")
    except Exception as e:
        log_message(f"Failed to copy files from {src} to {dest}: {e}", "error")

def start_install_process(mod_vars):
    """
    Start the installation process in a separate thread with loading animation.
    """
    if not check_install_conditions(mod_vars):
        log_message("Installation aborted due to errors.", 'error')
        return

    selected_mods = {mod: var for mod, var in mod_vars.items() if var.get() == 1}

    # Add confirmation for the music mod
    if "Music for all maps/modes-Fixed Clonetrooper VO" in selected_mods:
        confirm = messagebox.askyesno(
            "Confirm Installation",
            "The 'Music for all maps/modes-Fixed Clonetrooper VO' mod may override lighting changes and other mods. Do you want to continue?"
        )
        if not confirm:
            log_message("User canceled installation of the music mod.", 'warning')
            return

    log_message("Starting installation process...", 'success')

    # Start a new thread for mod installation
    threading.Thread(target=install_selected_mods, args=(selected_mods,), daemon=True).start()








# --------------------Mod Installation Functions------------------------

def install_dynamic_input_textures():
    app_data = Path(GLOBAL_APPDATA_DIR) / "Load"
    dynamic_textures_dir = app_data / "DynamicInputTextures"
    rabazz_dir = dynamic_textures_dir / "RABAZZ"
    mod_dir = Path(GLOBAL_MOD_DIR)

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

    log_message("Copying Dynamic Input Textures...")
    copy_with_xcopy(mod_dir / "DynamicInputTextures" / "DynamicInputTextures", dynamic_textures_dir)

    log_message("Copying RABAZZ textures...")
    copy_with_xcopy(mod_dir / "RABAZZ_dynamic" / "RABAZZ", rabazz_dir)




def install_muted_blank_audio():
    mod_dir = Path(GLOBAL_MOD_DIR)
    game_data_dir = Path(GLOBAL_GAME_DIR)

    copy_files(mod_dir / "SWBF3_Wii_Muted_Blank_Sounds", game_data_dir)


def install_4k_texture_pack():
    mod_dir = Path(GLOBAL_MOD_DIR) / "4kTexturePacks"
    texture_dir = Path(GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ"

    copy_files(mod_dir, texture_dir)


def install_lighting_fix():
    mod_dir = Path(GLOBAL_MOD_DIR)
    game_data_dir = Path(GLOBAL_GAME_DIR) / "DATA" / "files"

    copy_files(mod_dir / "EmbeddedResCompiler", game_data_dir)
    copy_files(mod_dir / "SWBF3_Wii_Light_Fixes", game_data_dir)

    compile_script_path = game_data_dir / "compile_all_res.bat"
    if compile_script_path.exists():
        log_message("Compiling resources...", "info")
        try:
            subprocess.run(
                str(compile_script_path),
                shell=True,
                check=True,
                text=True,
                cwd=str(game_data_dir)
            )
            log_message("Resource compilation complete.", "success")
        except subprocess.CalledProcessError as e:
            log_message(f"Error during resource compilation: {e}", "error")

    mgrsetup_path = game_data_dir / "data" / "bf" / "mgrsetup"
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

    if compile_script_path.exists():
        log_message("Compiling resources again...", "info")
        try:
            subprocess.run(
                str(compile_script_path),
                shell=True,
                check=True,
                text=True,
                cwd=str(game_data_dir)
            )
            log_message("Final resource compilation complete.", "success")
        except subprocess.CalledProcessError as e:
            log_message(f"Error during final resource compilation: {e}", "error")



def install_updated_debug_menu():
    mod_dir = Path(GLOBAL_MOD_DIR)
    sys_dir = Path(GLOBAL_GAME_DIR) / "DATA" / "sys"

    try:
        shutil.copy(mod_dir / "main.dol", sys_dir)
        log_message(f"File 'main.dol' copied to {sys_dir}", "success")
    except Exception as e:
        log_message(f"Failed to copy 'main.dol' to {sys_dir}: {e}", "error")


def install_cloth_fix():
    mod_dir = Path(GLOBAL_MOD_DIR)
    game_data_dir = Path(GLOBAL_GAME_DIR) / "DATA" / "files"

    copy_files(mod_dir / "Battlefront_III_Cloth_Fix" / "Battlefront_III_Cloth_Fix", game_data_dir)
    copy_files(mod_dir / "EmbeddedResCompiler", game_data_dir)

    compile_script_path = game_data_dir / "compile_all_res.bat"
    if compile_script_path.exists():
        log_message("Compiling resources...", "info")
        try:
            subprocess.run(
                str(compile_script_path),
                shell=True,
                check=True,
                text=True,
                cwd=str(game_data_dir)
            )
            log_message("Resource compilation complete.", "success")
        except subprocess.CalledProcessError as e:
            log_message(f"Error during resource compilation: {e}", "error")



def install_4k_characters_model_fix():
    mod_dir = Path(GLOBAL_MOD_DIR)
    app_data_textures_dir = Path(GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ" / "characters"
    game_data_dir = Path(GLOBAL_GAME_DIR) / "DATA" / "files"

    create_directory(app_data_textures_dir)
    copy_files(mod_dir / "characters", app_data_textures_dir)
    copy_files(mod_dir / "ingame_models_x1_x2_new", game_data_dir)
    log_message("4k Characters/Model Fix installation complete.", "success")


def install_faithful_health_bars():
    mod_dir = Path(GLOBAL_MOD_DIR) / "_faithful_hpbars_v2"
    app_data_textures_dir = Path(GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ" / "_faithful_hpbars_v2"

    create_directory(app_data_textures_dir)
    copy_files(mod_dir, app_data_textures_dir)

def install_minimap_fix():
    mod_dir = Path(GLOBAL_MOD_DIR) / "minimaps" / "minimaps"
    app_data_textures_dir = Path(GLOBAL_APPDATA_DIR) / "Load" / "Textures" / "RABAZZ" / "minimaps"

    create_directory(app_data_textures_dir)
    copy_files(mod_dir, app_data_textures_dir)

def install_pc_xbox_features():
    mod_dir = Path(GLOBAL_MOD_DIR) / "frontend_preview"
    game_data_dir = Path(GLOBAL_GAME_DIR) / "DATA" / "files"

    copy_files(mod_dir, game_data_dir / "data" / "bf" / "menus")

    compile_script_path = game_data_dir / "compile_all_res.bat"
    if compile_script_path.exists():
        log_message("Compiling resources...", "info")
        try:
            subprocess.run(
                str(compile_script_path),
                shell=True,
                check=True,
                text=True,
                cwd=str(game_data_dir)
            )
            log_message("Resource compilation complete.", "success")
        except subprocess.CalledProcessError as e:
            log_message(f"Error during resource compilation: {e}", "error")

def install_music_clonetrooper_vo():
    install_lighting_fix()
    mod_dir = Path(GLOBAL_MOD_DIR)
    game_data_dir = Path(GLOBAL_GAME_DIR)

    copy_files(mod_dir / "EmbeddedResCompiler", game_data_dir / "DATA" / "files")
    copy_files(mod_dir / "Music_and_Clone_VO" / "Music_and_Clone_VO", game_data_dir)

    compile_script_path = game_data_dir / "DATA" / "files" / "compile_templates_and_res.bat"
    if compile_script_path.exists():
        log_message("Compiling resources...", "info")
        try:
            subprocess.run(
                str(compile_script_path),
                shell=True,
                check=True,
                text=True,
                cwd=str(compile_script_path.parent),  # Set working directory to the .bat file's folder
            )
            log_message("Resource compilation complete.", "success")
        except subprocess.CalledProcessError as e:
            log_message(f"Error during resource compilation: {e}", "error")

def install_restored_r7_vehicles():
    mod_dir = Path(GLOBAL_MOD_DIR) / "restored_r7_vehicles"
    game_data_dir = Path(GLOBAL_GAME_DIR) / "DATA"

    copy_files(mod_dir, game_data_dir)

    compile_script_path = game_data_dir / "files" / "compile_templates_and_res.bat"
    if compile_script_path.exists():
        log_message("Compiling resources...", "info")
        try:
            subprocess.run(
                str(compile_script_path),
                shell=True,
                check=True,
                text=True,
                cwd=str(compile_script_path.parent),  # Set working directory to the .bat file's folder
            )
            log_message("Resource compilation complete.", "success")
        except subprocess.CalledProcessError as e:
            log_message(f"Error during resource compilation: {e}", "error")

MODS = {
    "Muted Blank Audio": install_muted_blank_audio,
    "4k texture pack Part 1, 2, 3, 4": install_4k_texture_pack,
    "Lighting Fix": install_lighting_fix,
    "Updated Debug Menu (main.dol from Clonetrooper163)": install_updated_debug_menu,
    "Cloth Fix": install_cloth_fix,
    "4k Characters/Model Fix": install_4k_characters_model_fix,
    "Texture Pack: Faithful Health Bars": install_faithful_health_bars,
    "Dynamic Input Textures": install_dynamic_input_textures,
    "Minimaps Fix (For r904, Enable prefetch custom textures)": install_minimap_fix,
    "Unlocked PC/Xbox 360 Features in Frontend": install_pc_xbox_features,
    "Music for all maps/modes-Fixed Clonetrooper VO": install_music_clonetrooper_vo,
    "Restored r7 Vehicles": install_restored_r7_vehicles
}

# Map mod names to their installation functions
MODS_DIRECTORY = {
    "Muted Blank Audio": lambda: os.path.join(GLOBAL_MOD_DIR, "SWBF3_Wii_Muted_Blank_Sounds"),
    "4k texture pack Part 1, 2, 3, 4": lambda: os.path.join(GLOBAL_MOD_DIR, "4kTexturePacks"),
    "Lighting Fix": lambda: os.path.join(GLOBAL_MOD_DIR, "SWBF3_Wii_Light_Fixes"),
    "Updated Debug Menu (main.dol from Clonetrooper163)": lambda: os.path.join(GLOBAL_MOD_DIR, "Updated_Debug_Menu"),
    "Cloth Fix": lambda: os.path.join(GLOBAL_MOD_DIR, "Battlefront_III_Cloth_Fix"),
    "4k Characters/Model Fix": lambda: os.path.join(GLOBAL_MOD_DIR, "characters"),
    "Texture Pack: Faithful Health Bars": lambda: os.path.join(GLOBAL_MOD_DIR, "_faithful_hpbars_v2"),
    "Dynamic Input Textures": lambda: os.path.join(GLOBAL_MOD_DIR, "DynamicInputTextures"),
    "Minimaps Fix (For r904, Enable prefetch custom textures)": lambda: os.path.join(GLOBAL_MOD_DIR, "Minimaps"),
    "Unlocked PC/Xbox 360 Features in Frontend": lambda: os.path.join(GLOBAL_MOD_DIR, "frontend_preview"),
    "Music for all maps/modes-Fixed Clonetrooper VO": lambda: os.path.join(GLOBAL_MOD_DIR, "Music_and_Clone_VO"),
    "Restored r7 Vehicles": lambda: os.path.join(GLOBAL_MOD_DIR, "restored_r7_vehicles")
}



#-------Dark Theme and About-----------------
def apply_dark_theme(widget):
    """Applies a dark theme to a given widget and its children."""
    try:
        widget.configure(bg="#2b2b2b")
    except tk.TclError:
        pass  # Ignore widgets that don't support bg

    for child in widget.winfo_children():
        if isinstance(child, Label) or isinstance(child, Button):
            child.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")
        elif isinstance(child, Entry) or isinstance(child, Text):
            child.configure(bg="#3c3f41", fg="white", insertbackground="white")
        elif isinstance(child, Checkbutton):
            child.configure(bg="#2b2b2b", fg="white", selectcolor="#3c3f41")
        elif isinstance(child, Frame):
            apply_dark_theme(child)


def show_about():
    about_window = Toplevel(root)
    about_window.title("About")
    about_window.configure(bg="#2b2b2b")
    about_text = tk.Text(about_window, height=15, width=80, bg="#3c3f41", fg="white")
    about_text.pack(padx=10, pady=10)
    about_text.insert(tk.END, """
    Made by BrokenToaster
    Tested with build r2.91120a

    This installer was made for the Free Radical Archive Mods-Wii
    Discord: https://discord.gg/VE6mDWru

    The source code for this project is here:
    https://github.com/MichaelZaugg/SWBF3-Wii-Mod-Installer/tree/swbf3_wii_mod_installer_v4.0

    Instructions:

    Please download the unpacked version of the game for modding

    Run Dolphin at least once before installing these mods

    """)
    about_text.config(state='disabled')
    about_window.geometry("")



#-----------------Setup Paths------------------------------------
def load_config(game_dir_entry=None, mod_dir_entry=None, appdata_entry=None):
    """
    Load the configuration from the JSON file and update UI entries if provided.
    """
    global GLOBAL_GAME_DIR, GLOBAL_MOD_DIR, GLOBAL_APPDATA_DIR, GLOBAL_CUSTOM_APPDATA

    if not os.path.exists(CONFIG_FILE):
        print_to_console("No configuration file found. Proceeding with default behavior.", "info")
        return

    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            config = json.load(f)

        GLOBAL_GAME_DIR = config.get("game_dir", "")
        GLOBAL_MOD_DIR = config.get("mod_dir", "")
        GLOBAL_APPDATA_DIR = config.get("appdata_dir", "")
        GLOBAL_CUSTOM_APPDATA = config.get("custom_appdata", False)

        if not GLOBAL_CUSTOM_APPDATA or not GLOBAL_APPDATA_DIR:
            GLOBAL_CUSTOM_APPDATA = False
            GLOBAL_APPDATA_DIR = ""

        # Update UI fields if the entries are provided
        if game_dir_entry:
            game_dir_entry.delete(0, tk.END)
            game_dir_entry.insert(0, GLOBAL_GAME_DIR)
        if mod_dir_entry:
            mod_dir_entry.delete(0, tk.END)
            mod_dir_entry.insert(0, GLOBAL_MOD_DIR)
        if appdata_entry:
            appdata_entry.delete(0, tk.END)
            appdata_entry.insert(0, GLOBAL_APPDATA_DIR)

        # Log loaded directories
        if GLOBAL_GAME_DIR:
            print_to_console(f"Loaded game directory from config: {GLOBAL_GAME_DIR}", "success")
        if GLOBAL_MOD_DIR:
            print_to_console(f"Loaded mod directory from config: {GLOBAL_MOD_DIR}", "success")
        if GLOBAL_CUSTOM_APPDATA:
            print_to_console(f"Loaded custom AppData directory from config: {GLOBAL_APPDATA_DIR}", "success")
        else:
            print_to_console("No custom AppData directory set. Searching for default Dolphin Emulator directory.", "info")
    except json.JSONDecodeError:
        print_to_console("Configuration file is invalid. Proceeding with default behavior.", "error")
    except Exception as e:
        print_to_console(f"Failed to load configuration: {e}", "error")




def save_config():
    """
    Save the current configuration to a JSON file.
    Ensures the file is written correctly in JSON format.
    """
    config = {
        "game_dir": GLOBAL_GAME_DIR,
        "mod_dir": GLOBAL_MOD_DIR,
        "appdata_dir": GLOBAL_APPDATA_DIR if GLOBAL_CUSTOM_APPDATA else "",
        "custom_appdata": GLOBAL_CUSTOM_APPDATA
    }

    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(config, f, indent=4)
        print_to_console(f"Configuration saved to {CONFIG_FILE}.", "success")
    except Exception as e:
        print_to_console(f"Failed to save configuration: {e}", "error")


def initialize_directories(game_dir_entry=None, mod_dir_entry=None, appdata_entry=None):
    """
    Initialize directory paths by loading the saved configuration
    and updating the UI entries if provided.
    """
    load_config(game_dir_entry, mod_dir_entry, appdata_entry)

    # If no custom AppData directory is set, search for Dolphin Emulator path
    if not GLOBAL_CUSTOM_APPDATA:
        search_dolphin_emulator(appdata_entry)


def browse_folder(entry_widget, update_global):
    def find_mod_versions_folder(base_path, max_depth=3):
        """
        Searches for a folder containing 'mod_versions.json' within a specified depth.
        :param base_path: The root directory to start searching from.
        :param max_depth: The maximum depth to search.
        :return: The path to the folder containing 'mod_versions.json', or None if not found.
        """
        def search_folder(current_path, current_depth):
            if current_depth > max_depth:
                return None
            # Check if 'mod_versions.json' exists in the current folder
            if "mod_versions.json" in os.listdir(current_path):
                return current_path
            # Recur into subdirectories
            for entry in os.listdir(current_path):
                entry_path = os.path.join(current_path, entry)
                if os.path.isdir(entry_path):
                    result = search_folder(entry_path, current_depth + 1)
                    if result:
                        return result
            return None

        return search_folder(base_path, 1)

    folder_selected = filedialog.askdirectory().replace('/', '\\')
    if folder_selected:
        if update_global == "game":
            result_path = contains_required_dirs(folder_selected)
            if result_path:
                global GLOBAL_GAME_DIR
                GLOBAL_GAME_DIR = result_path
                print_to_console(f"Game directory set to: {GLOBAL_GAME_DIR}", 'directory')
                entry_widget.delete(0, tk.END)
                entry_widget.insert(0, GLOBAL_GAME_DIR)
                save_config()
            else:
                print_to_console("The selected directory does not contain both 'DATA' and 'UPDATE' directories.", 'error')
                entry_widget.delete(0, tk.END)
                entry_widget.insert(0, "Invalid game directory. Missing 'DATA' or 'UPDATE'.")
                GLOBAL_GAME_DIR = ""

        elif update_global == "mod":
            # Search for the folder containing 'mod_versions.json' up to 3 levels deep
            mods_path = find_mod_versions_folder(folder_selected, max_depth=3)
            if mods_path:
                global GLOBAL_MOD_DIR
                GLOBAL_MOD_DIR = mods_path
                print_to_console(f"Mod directory set to: {GLOBAL_MOD_DIR}", 'directory')
                entry_widget.delete(0, tk.END)
                entry_widget.insert(0, GLOBAL_MOD_DIR)
            else:
                print_to_console("No 'mod_versions.json' found within 3 levels. Please check for updates or select the exctrated Mods.zip", 'error')
                GLOBAL_MOD_DIR = ""
                entry_widget.delete(0, tk.END)
                entry_widget.insert(0, GLOBAL_MOD_DIR)
            save_config()

        elif update_global == "appdata":
            global GLOBAL_APPDATA_DIR, GLOBAL_CUSTOM_APPDATA
            GLOBAL_APPDATA_DIR = folder_selected
            GLOBAL_CUSTOM_APPDATA = True  # Set to True as a custom path is explicitly selected
            print_to_console(f"Custom AppData Dir: {GLOBAL_APPDATA_DIR}", 'directory')
            entry_widget.delete(0, tk.END)
            entry_widget.insert(0, GLOBAL_APPDATA_DIR)
            save_config()
    else:
        print_to_console("No folder selected.", 'error')
        entry_widget.delete(0, tk.END)
        entry_widget.insert(0, "No folder selected.")
        if update_global == "game":
            GLOBAL_GAME_DIR = ""
        elif update_global == "mod":
            GLOBAL_MOD_DIR = ""
        elif update_global == "appdata":
            GLOBAL_APPDATA_DIR = ""
            GLOBAL_CUSTOM_APPDATA = False





def contains_required_dirs(base_path, depth=3):
    if depth == 0:
        return None
    data_found = 'DATA' in os.listdir(base_path)
    update_found = 'UPDATE' in os.listdir(base_path)
    if data_found and update_found:
        return base_path
    else:
        for entry in os.listdir(base_path):
            full_path = os.path.join(base_path, entry)
            if os.path.isdir(full_path):
                result = contains_required_dirs(full_path, depth - 1)
                if result:
                    return result
    return None

def find_mods_folder(base_path):
    """
    Find the 'Mods' folder in the selected path. Prefer deeper nested duplicates like 'Mods\Mods'.
    """
    for root, dirs, _ in os.walk(base_path):
        for directory in dirs:
            if directory.lower() == "mods":
                mods_path = os.path.join(root, directory)
                # Check for nested Mods\Mods and prefer the deeper folder
                if os.path.exists(os.path.join(mods_path, "Mods")):
                    return os.path.join(mods_path, "Mods")
                return mods_path
    return None



def search_dolphin_emulator(entry_widget):
    """
    Search for the Dolphin Emulator directory and set GLOBAL_APPDATA_DIR.
    If entry_widget is provided, update its value in the UI.
    """
    global GLOBAL_APPDATA_DIR, GLOBAL_CUSTOM_APPDATA
    base_path = "C:\\Users"
    found_paths = []
    for user in os.listdir(base_path):
        user_path = os.path.join(base_path, user, "AppData", "Roaming", "Dolphin Emulator").replace('/', '\\')
        if os.path.exists(user_path):
            found_paths.append(user_path)

    if found_paths:
        GLOBAL_APPDATA_DIR = found_paths[0]
        GLOBAL_CUSTOM_APPDATA = False
        print_to_console(f"AppData Dir: {GLOBAL_APPDATA_DIR}", 'directory')
        
        # Update the UI only if entry_widget is not None
        if entry_widget:
            entry_widget.delete(0, tk.END)
            entry_widget.insert(0, GLOBAL_APPDATA_DIR)
    else:
        GLOBAL_APPDATA_DIR = ""
        GLOBAL_CUSTOM_APPDATA = False
        print_to_console("Dolphin Emulator directory not found.", 'error')
        
        # Update the UI only if entry_widget is not None
        if entry_widget:
            entry_widget.delete(0, tk.END)
            entry_widget.insert(0, "Dolphin Emulator directory not found.")


#-----------------------Setup UI------------------------------------------
def toggle_mods(mod_vars, toggle_btn):
    all_selected = all(var.get() == 1 for var in mod_vars.values())
    if all_selected:
        for var in mod_vars.values():
            var.set(0)
        toggle_btn.config(text="Select All Mods")
        print_to_console("Deselected all mods.")
    else:
        for var in mod_vars.values():
            var.set(1)
        toggle_btn.config(text="Deselect All Mods")
        print_to_console("Selected all mods.")

def setup_ui(root):
    root.grid_columnconfigure(1, weight=1)
    setup_console_ui(root)
    mod_vars = setup_mod_ui(root)  # Capture mod_vars from setup_mod_ui
    setup_top_buttons(root, mod_vars)  # Pass mod_vars to setup_top_buttons
    setup_directory_ui(root)
    apply_dark_theme(root)  # Apply dark theme to all widgets after setup
    return mod_vars

def setup_top_buttons(root, mod_vars):
    # About button
    about_button = Button(root, text="About", command=show_about)
    about_button.grid(row=0, column=0, sticky='w', padx=5, pady=5)
    about_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")

    # Load the image and display it
    try:
        fun_image = PhotoImage(file=resource_path("image.png"))  # Replace with your image path
        Label(root, image=fun_image, bg="#2b2b2b").grid(row=0, column=1, sticky='w', padx=5, pady=5)
        root.fun_image = fun_image  # Keep a reference to avoid garbage collection
    except Exception as e:
        print(f"Error loading image: {e}")
        Label(root, text="Image Not Available", bg="#2b2b2b", fg="white").grid(row=0, column=1, sticky='w', padx=5, pady=5)

    # New Install button
    install_button = Button(root, text="Install", command=lambda: start_install_process(mod_vars))
    install_button.grid(row=4, column=2, sticky='w', padx=10, pady=10)
    install_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")

    # Check for Updates button
    updates_button = Button(root, text="Check for Updates", command=check_for_updates)
    updates_button.grid(row=4, column=3, sticky='w', padx=10, pady=10)
    updates_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")





def setup_directory_ui(root):
    Label(root, text="Game Directory:").grid(row=1, column=0, sticky='w', padx=5)
    game_dir_entry = Entry(root)
    game_dir_entry.grid(row=1, column=1, sticky='ew', padx=5)
    Button(root, text="Browse Game Directory", command=lambda: browse_folder(game_dir_entry, "game")).grid(row=1, column=2, padx=5)

    Label(root, text="Mod Directory:").grid(row=2, column=0, sticky='w', padx=5)
    mod_dir_entry = Entry(root)
    mod_dir_entry.grid(row=2, column=1, sticky='ew', padx=5)
    Button(root, text="Select Mod Directory", command=lambda: browse_folder(mod_dir_entry, "mod")).grid(row=2, column=2, padx=5)

    Label(root, text="AppData Directory:").grid(row=3, column=0, sticky='w', padx=5)
    appdata_entry = Entry(root)
    appdata_entry.grid(row=3, column=1, sticky='ew', padx=5)
    Button(root, text="Custom AppData", command=lambda: browse_folder(appdata_entry, "appdata")).grid(row=3, column=2, padx=5)
    Button(root, text="Reset AppData Path", command=lambda: reset_appdata_path(appdata_entry)).grid(row=3, column=3, padx=5)

    # Initialize directories and update UI
    initialize_directories(game_dir_entry, mod_dir_entry, appdata_entry)

def reset_appdata_path(entry_widget):
    """
    Reset the AppData path to the default Dolphin Emulator directory,
    update the UI, and save the updated configuration.
    """
    global GLOBAL_CUSTOM_APPDATA, GLOBAL_APPDATA_DIR
    search_dolphin_emulator(entry_widget)
    GLOBAL_CUSTOM_APPDATA = False  # Indicate the default AppData is used
    save_config()  # Save the updated configuration
    print_to_console("AppData path reset to default Dolphin Emulator directory and saved in configuration.", 'directory')

def setup_mod_ui(root):
    mods_frame = Frame(root, bg="#2b2b2b")
    mods_frame.grid(row=4, column=0, columnspan=2, sticky='w', padx=5, pady=5)
    Label(mods_frame, text="Select Mods To Install:", bg="#2b2b2b", fg="white").grid(row=0, column=0, sticky='w')

    mod_vars = {mod: IntVar() for mod in MODS}
    for i, mod in enumerate(MODS):
        Checkbutton(mods_frame, text=mod, variable=mod_vars[mod], bg="#2b2b2b", fg="white", selectcolor="#3c3f41").grid(row=i + 1, column=0, sticky='w')

    toggle_btn = Button(mods_frame, text="Select All Mods", command=lambda: toggle_mods(mod_vars, toggle_btn))
    toggle_btn.grid(row=len(MODS) + 1, column=0, sticky='w', padx=5, pady=2)

    Label(root, text="Enable custom textures in Dolphin", bg="#2b2b2b", fg="white").grid(row=5, column=0, columnspan=3, sticky='ew', padx=5, pady=2)

    install_button = Button(root, text="Install", command=lambda: start_install_process(mod_vars))
    install_button.grid(row=6, column=0, columnspan=3, sticky='ew', padx=5, pady=5)

    return mod_vars

def setup_console_ui(root):
    console_frame = Frame(root, width=400, bg="#2b2b2b")
    console_frame.grid(row=0, column=4, rowspan=6, sticky='nsew', padx=5, pady=5)
    console_frame.grid_propagate(False)
    global console_text
    console_text = Text(console_frame, bg="#1e1e1e", fg="white", height=40, insertbackground="white")
    console_text.pack(expand=True, fill='both')
    Label(console_frame, text="Console Output:", fg="white", bg="#2b2b2b").pack(side='top', anchor='w')

    # Configure tags for styled text
    console_text.tag_configure('error', foreground='red')
    console_text.tag_configure('yellow', foreground='yellow')
    console_text.tag_configure('success', foreground='#32CD32')


def check_install_conditions(mod_vars):
    errors = False
    warnings = []
    selected_mods = [mod for mod, var in mod_vars.items() if var.get() == 1]

    # Check for game directory, mod directory, and appdata directory
    if not GLOBAL_GAME_DIR:
        print_to_console("No valid game directory selected.", 'error')
        errors = True
    if not GLOBAL_MOD_DIR:
        print_to_console("No valid mod directory selected.", 'error')
        errors = True
    if not GLOBAL_APPDATA_DIR:
        print_to_console("No AppData directory selected.", 'error')
        errors = True
    elif GLOBAL_CUSTOM_APPDATA:
        print_to_console("Custom AppData directory accepted without further checks.", 'info')
    else:
        expected_path = (GLOBAL_APPDATA_DIR if GLOBAL_APPDATA_DIR.endswith('Dolphin Emulator') 
                         else os.path.join(GLOBAL_APPDATA_DIR, 'Dolphin Emulator'))
        if not os.path.exists(expected_path):
            print_to_console(f"Dolphin Emulator directory not found at {expected_path}.", 'error')
            errors = True

    if not selected_mods:
        print_to_console("No mods have been selected.", 'error')
        errors = True

    # Check for incompatibilities
    if "Music for all maps/modes-Fixed Clonetrooper VO" in selected_mods:
        if "Lighting Fix" in selected_mods or "Restored r7 Vehicles" in selected_mods:
            print_to_console("Error: 'Music for all maps/modes-Fixed Clonetrooper VO' is incompatible with 'Lighting Fix' and 'Restored r7 Vehicles'.", 'error')
            #errors = True
        else:
            warnings.append("Warning: 'Music for all maps/modes-Fixed Clonetrooper VO' will override lighting changes.")

    # Display warnings
    for warning in warnings:
        print_to_console(warning, 'warning')

    return not errors

def load_mod_directories(mod_versions_path):
    """
    Load mod directories from mod_versions.json.

    :param mod_versions_path: Path to the mod_versions.json file.
    :return: Dictionary mapping mod names to their directories.
    """
    try:
        with open(mod_versions_path, "r", encoding="utf-8") as file:
            data = json.load(file)

        # Use mod name as the directory if 'dir' is not specified
        mod_dirs = {}
        for mod in data.get("mods", []):
            name = mod.get("name")
            dir_path = mod.get("dir", name)  # Default to the mod name if 'dir' is missing
            if not name or not dir_path:
                log_message(f"Skipping invalid mod entry: {mod}", "warning")
                continue
            mod_dirs[name] = dir_path

        return mod_dirs
    except (FileNotFoundError, json.JSONDecodeError) as e:
        log_message(f"Failed to load mod_versions.json: {e}", "error")
        return {}



def check_file_name_conflicts(selected_mods):
    """
    Check for file name conflicts among selected mods using their defined directories.

    :param selected_mods: A dictionary of selected mods with their installation status.
    :return: List of conflicting file names with the mods involved.
    """
    file_names = {}  # Dictionary to map file names to mods
    conflicts = []  # List to store conflicting file entries

    # Collect all file names from each selected mod
    for mod, is_selected in selected_mods.items():
        if is_selected.get():  # If the mod is selected
            # Get the mod directory from MODS
            if mod == "Updated Debug Menu (main.dol from Clonetrooper163)":
                continue

            mod_dir_function = MODS_DIRECTORY.get(mod)
            if not mod_dir_function:
                print_to_console(f"Mod directory not found for: {mod}", "error")
                continue

            mod_dir = mod_dir_function()  # Call the function to get the directory path
            if os.path.exists(mod_dir):
                for root, _, files in os.walk(mod_dir):
                    for file in files:
                        # Only process .res and .war files
                        if file.endswith((".res", ".war")):
                            normalized_file_name = file.lower()  # Normalize to handle case sensitivity
                            if normalized_file_name in file_names:
                                file_names[normalized_file_name].add(mod)  # Add mod to the set
                            else:
                                file_names[normalized_file_name] = {mod}  # Create a new set for the file name
            else:
                print_to_console(f"Mod directory does not exist: {mod_dir}", "error")

    # Find conflicts
    for file_name, mods in file_names.items():
        if len(mods) > 1:  # If the same file is found in more than one mod
            if file_name == "invisible_hand.res" and {"Lighting Fix", "Music for all maps/modes-Fixed Clonetrooper VO"}.issubset(mods):
                # Dismiss specific conflict
                print_to_console(f"[ERROR] File: {file_name} is present in mods: {', '.join(mods)} (DISMISSED)", "info")
                continue  # Skip adding this conflict to the conflicts list

            # Log other conflicts as errors
            conflicts.append((file_name, list(mods)))  # Convert set to list for easier handling
            # Use the "yellow" tag for the file name in the UI console
            print_to_console(f"[ERROR] File: ", "error", end="")
            print_to_console(file_name, "yellow", end="")
            print_to_console(f" is present in mods: {', '.join(mods)}", "error")

    return conflicts




def install_selected_mods(selected_mods):
    """
    Install the selected mods after checking for file name conflicts.

    :param selected_mods: A dictionary of selected mods with their installation status.
    """
    try:
        # Start the loading animation
        start_loading()

        if not check_install_conditions(selected_mods):
            log_message("Installation aborted due to errors.", "error")
            return

        # Check for file name conflicts
        conflicts = check_file_name_conflicts(selected_mods)
        if conflicts:
            # Log conflicts and abort installation
            #log_message("Conflicts detected among selected mods:", "error")
            #for file_name, mods in conflicts:
                #print_to_console(f"[ERROR] File: {file_name} is present in mods: {', '.join(mods)}", "error")
            return  # Abort installation

        log_message("No conflicts detected. Proceeding with installation...", "success")

        # Install mods
        mods_to_install = [mod for mod, is_selected in selected_mods.items() if is_selected.get()]
        for mod in mods_to_install:
            log_message(f"Installing: {mod}", "info")
            try:
                mod_install_function = MODS.get(mod)
                if mod_install_function:
                    mod_install_function()  # Call the corresponding installation function
                    log_message(f"{mod} installed successfully.", "success")
                else:
                    log_message(f"No installation function found for {mod}. Skipping...", "warning")
            except Exception as e:
                log_message(f"Error installing {mod}: {e}", "error")

        log_message("-----Installation Complete-----", "success")
    finally:
        # Stop the loading animation
        stop_loading()






def main_menu():
    global root
    
    root = tk.Tk()
    root.title(TITLE)

    root.geometry("1530x740")
    root.minsize(1530, 740)

    try:
        root.iconbitmap(resource_path(ICON_PATH))
    except Exception as e:
        print("Error setting ICO icon:", e)

    mod_vars = setup_ui(root)  # Get mod_vars from setup_ui
    setup_top_buttons(root, mod_vars)  # Pass mod_vars to setup_top_buttons
    root.mainloop()


if __name__ == "__main__":
    initialize_directories()
    main_menu()