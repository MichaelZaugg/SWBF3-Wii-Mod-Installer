import os
import sys
import shutil
import tkinter as tk
from tkinter import filedialog, Label, Entry, Toplevel, Checkbutton, IntVar, Frame, Button, Text, PhotoImage
from pathlib import Path
import itertools
import threading
import subprocess
import requests
import json
import signal
import time
import platform

# Global flags and path variables
TITLE = "SWBF3 Wii Mod Installer"
GLOBAL_GAME_DIR = ""
GLOBAL_APPDATA_DIR = ""
GLOBAL_CUSTOM_APPDATA = False
GLOBAL_MOD_DIR = ""
ICON_PATH = 'SWBF3Icon.ico'
CONFIG_FILE = "mod_installer_config"

#--------Update Manifest--------
MANIFEST_URL = "https://raw.githubusercontent.com/MichaelZaugg/SWBF3-Wii-Mod-Installer/main/manifest.json"
loading_label = None
stop_loading_flag = False
current_version = "4.1"

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
    if stop_loading_flag:  # If spinner isn't running
        stop_loading_flag = False
        show_loading_animation()

def stop_loading():
    """
    Stop the spinner animation.
    """
    global stop_loading_flag
    if not stop_loading_flag:  # If spinner is running
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

        finally:
            stop_loading()

    start_loading()  # Start animation
    thread = threading.Thread(target=perform_update_sequence, daemon=True)
    thread.start()

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


def print_to_console(message, tag=None):
    """Print messages to the console UI if available; otherwise, print to terminal."""
    if 'console_text' in globals() and console_text is not None:
        console_text.insert('end', message + '\n', tag)
        console_text.see('end')
        console_text.update_idletasks()  # Force the UI to update immediately
    else:
        print(f"[{tag or 'INFO'}] {message}")


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
    if check_install_conditions(mod_vars):
        log_message("Starting installation process...", 'success')
        start_loading()  # Start the loading animation
        selected_mods = {mod: var for mod, var in mod_vars.items()}
        threading.Thread(target=install_selected_mods, args=(selected_mods,), daemon=True).start()
    else:
        log_message("Installation aborted due to errors.", 'error')






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




# Map mod names to their installation functions
MODS = {
    "Muted Blank Audio": install_muted_blank_audio,
    "4k texture pack Part 1, 2, 3, 4": install_4k_texture_pack,
    "Lighting Fix": install_lighting_fix,
    "Updated Debug Menu (main.dol from Clonetrooper163)": install_updated_debug_menu,
    "Cloth Fix": install_cloth_fix,
    "4k Characters/Model Fix": install_4k_characters_model_fix,
    "Texture Pack: Faithful Health Bars": install_faithful_health_bars,
    "Dynamic Input Textures": install_dynamic_input_textures,
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
            mods_path = find_mods_folder(folder_selected)
            if not mods_path:
                mods_path = os.path.join(folder_selected, "Mods")
                os.makedirs(mods_path, exist_ok=True)
                print_to_console("No 'Mods' folder found. Created a new 'Mods' folder. Mods will need to be downloaded.", "error")

            global GLOBAL_MOD_DIR
            GLOBAL_MOD_DIR = mods_path
            print_to_console(f"Mod directory set to: {GLOBAL_MOD_DIR}", 'directory')
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
    setup_top_buttons(root)
    setup_directory_ui(root)
    setup_mod_ui(root)
    apply_dark_theme(root)

def setup_top_buttons(root):
    # About button
    Button(root, text="About", command=show_about).grid(row=0, column=0, sticky='w', padx=5, pady=5)

    # Load the image and display it
    try:
        fun_image = PhotoImage(file=resource_path("image.png"))  # Replace with your image path
        Label(root, image=fun_image, bg="#2b2b2b").grid(row=0, column=1, sticky='w', padx=5, pady=5)
        root.fun_image = fun_image  # Keep a reference to avoid garbage collection
    except Exception as e:
        print(f"Error loading image: {e}")
        Label(root, text="Image Not Available", bg="#2b2b2b", fg="white").grid(row=0, column=1, sticky='w', padx=5, pady=5)

    # Check for Updates button near the console
    Button(root, text="Check for Updates", command=check_for_updates).grid(row=4, column=3, sticky='w', padx=10, pady=10)

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

    # Add the new label for enabling custom textures
    Label(root, text="Enable custom textures in Dolphin", bg="#2b2b2b", fg="white").grid(row=5, column=0, columnspan=3, sticky='ew', padx=5, pady=2)

    install_button = Button(root, text="Install", command=lambda: start_install_process(mod_vars))
    install_button.grid(row=6, column=0, columnspan=3, sticky='ew', padx=5, pady=5)


def setup_console_ui(root):
    console_frame = Frame(root, width=400, bg="#2b2b2b")
    console_frame.grid(row=0, column=4, rowspan=6, sticky='nsew', padx=5, pady=5)
    console_frame.grid_propagate(False)
    global console_text
    console_text = Text(console_frame, bg="#1e1e1e", fg="white", height=40, insertbackground="white")
    console_text.pack(expand=True, fill='both')
    Label(console_frame, text="Console Output:", fg="white", bg="#2b2b2b").pack(side='top', anchor='w')

    console_text.tag_configure('directory', foreground='yellow')
    console_text.tag_configure('error', foreground='red')
    console_text.tag_configure('success', foreground='#32CD32')

def check_install_conditions(mod_vars):
    errors = False
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
        if GLOBAL_APPDATA_DIR.endswith('Dolphin Emulator'):
            expected_path = GLOBAL_APPDATA_DIR
        else:
            expected_path = os.path.join(GLOBAL_APPDATA_DIR, 'Dolphin Emulator')

        if not os.path.exists(expected_path):
            print_to_console(f"Dolphin Emulator directory not found at {expected_path}.", 'error')
            errors = True

    if not any(var.get() for var in mod_vars.values()):
        print_to_console("No mods have been selected.", 'error')
        errors = True

    return not errors

def install_selected_mods(selected_mods):
    try:
        mods_to_install = [mod for mod, is_selected in selected_mods.items() if is_selected.get()]
        if not mods_to_install:
            log_message("No mods selected for installation.", 'error')
            return

        for mod in mods_to_install:
            log_message(f"Installing: {mod}")
            try:
                MODS[mod]()  # Call the corresponding installation function
                log_message(f"{mod} installed successfully.", 'success')
            except Exception as e:
                log_message(f"Error installing {mod}: {e}", 'error')

        log_message("-----DONE-----", 'success')
    finally:
        root.after(0, stop_loading)  # Stop the loading animation after installation completes




def main_menu():
    global root
    
    root = tk.Tk()
    root.title(TITLE)

    try:
        root.iconbitmap(resource_path(ICON_PATH))
    except Exception as e:
        print("Error setting ICO icon:", e)

    setup_ui(root)
    root.mainloop()


if __name__ == "__main__":
    initialize_directories()
    main_menu()