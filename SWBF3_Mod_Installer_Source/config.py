import os
import json
from tkinter import filedialog
from utils import print_to_console
import platform  # Added for OS detection

# Global flags and path variables
current_version = "7.2"
TITLE = f"SWBF3 Wii Mod Installer v{current_version}"
ICON_PATH = 'SWBF3Icon.ico'
CONFIG_FILE = "mod_installer_config"

GLOBAL_GAME_DIR = ""
GLOBAL_APPDATA_DIR = ""
GLOBAL_CUSTOM_APPDATA = False
GLOBAL_MOD_DIR = ""

def load_config(game_dir_entry=None, mod_dir_entry=None, appdata_entry=None):
    global GLOBAL_GAME_DIR, GLOBAL_MOD_DIR, GLOBAL_APPDATA_DIR, GLOBAL_CUSTOM_APPDATA
    if not os.path.exists(CONFIG_FILE):
        print_to_console("No configuration file found. Proceeding with default behavior.", "info")
        return
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            config_data = json.load(f)
        GLOBAL_GAME_DIR = config_data.get("game_dir", "")
        GLOBAL_MOD_DIR = config_data.get("mod_dir", "")
        GLOBAL_APPDATA_DIR = config_data.get("appdata_dir", "")
        GLOBAL_CUSTOM_APPDATA = config_data.get("custom_appdata", False)
        if not GLOBAL_CUSTOM_APPDATA or not GLOBAL_APPDATA_DIR:
            GLOBAL_CUSTOM_APPDATA = False
            GLOBAL_APPDATA_DIR = ""
        if game_dir_entry:
            game_dir_entry.delete(0, "end")
            game_dir_entry.insert(0, GLOBAL_GAME_DIR)
        if mod_dir_entry:
            mod_dir_entry.delete(0, "end")
            mod_dir_entry.insert(0, GLOBAL_MOD_DIR)
        if appdata_entry:
            appdata_entry.delete(0, "end")
            appdata_entry.insert(0, GLOBAL_APPDATA_DIR)
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
    config_data = {
        "game_dir": GLOBAL_GAME_DIR,
        "mod_dir": GLOBAL_MOD_DIR,
        "appdata_dir": GLOBAL_APPDATA_DIR if GLOBAL_CUSTOM_APPDATA else "",
        "custom_appdata": GLOBAL_CUSTOM_APPDATA
    }
    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(config_data, f, indent=4)
        print_to_console(f"Configuration saved to {CONFIG_FILE}.", "success")
    except Exception as e:
        print_to_console(f"Failed to save configuration: {e}", "error")

def initialize_directories(game_dir_entry=None, mod_dir_entry=None, appdata_entry=None):
    load_config(game_dir_entry, mod_dir_entry, appdata_entry)
    if not GLOBAL_CUSTOM_APPDATA:
        search_dolphin_emulator(appdata_entry)

def contains_required_dirs(base_path, depth=3):
    if depth == 0:
        return None
    entries = os.listdir(base_path)
    if 'DATA' in entries and 'UPDATE' in entries:
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
    for root, dirs, _ in os.walk(base_path):
        for directory in dirs:
            if directory.lower() == "mods":
                mods_path = os.path.join(root, directory)
                if os.path.exists(os.path.join(mods_path, "Mods")):
                    return os.path.join(mods_path, "Mods")
                return mods_path
    return None

def search_dolphin_emulator(entry_widget):
    global GLOBAL_APPDATA_DIR, GLOBAL_CUSTOM_APPDATA
    found_paths = []
    system = platform.system()
    if system == "Windows":
        base_path = "C:\\Users"
        for user in os.listdir(base_path):
            user_path = os.path.join(base_path, user, "AppData", "Roaming", "Dolphin Emulator").replace('/', '\\')
            if os.path.exists(user_path):
                found_paths.append(user_path)
    elif system == "Linux":
        base_path = os.path.expanduser("~")
        # Check the new Flatpak Dolphin Emulator path first
        flatpak_path = os.path.join(base_path, ".var", "app", "org.DolphinEmu.dolphin-emu", "data", "dolphin-emu")
        if os.path.exists(flatpak_path):
            found_paths.append(flatpak_path)
        # Also check the legacy Dolphin Emulator path
        legacy_path = os.path.join(base_path, ".config", "dolphin-emu")
        if os.path.exists(legacy_path):
            found_paths.append(legacy_path)
        # Only mark as custom if a valid path was found
        GLOBAL_CUSTOM_APPDATA = True if found_paths else False

    if found_paths:
        GLOBAL_APPDATA_DIR = found_paths[0]
        print_to_console(f"AppData Dir: {GLOBAL_APPDATA_DIR}", "directory")
        if entry_widget:
            entry_widget.delete(0, "end")
            entry_widget.insert(0, GLOBAL_APPDATA_DIR)
    else:
        GLOBAL_APPDATA_DIR = ""
        if system != "Linux":
            GLOBAL_CUSTOM_APPDATA = False
        print_to_console("Dolphin Emulator directory not found.", "error")
        if entry_widget:
            entry_widget.delete(0, "end")
            entry_widget.insert(0, "Dolphin Emulator directory not found.")


def browse_folder(entry_widget, update_global):
    import platform
    # Only replace slashes on Windows
    if platform.system() == "Windows":
        folder_selected = filedialog.askdirectory().replace('/', '\\')
    else:
        folder_selected = filedialog.askdirectory()
    if folder_selected:
        if update_global == "game":
            result_path = contains_required_dirs(folder_selected)
            if result_path:
                global GLOBAL_GAME_DIR
                GLOBAL_GAME_DIR = result_path
                print_to_console(f"Game directory set to: {GLOBAL_GAME_DIR}", 'directory')
                entry_widget.delete(0, "end")
                entry_widget.insert(0, GLOBAL_GAME_DIR)
                save_config()
            else:
                print_to_console("The selected directory does not contain both 'DATA' and 'UPDATE' directories.", "error")
                entry_widget.delete(0, "end")
                entry_widget.insert(0, "Invalid game directory. Missing 'DATA' or 'UPDATE'.")
                GLOBAL_GAME_DIR = ""
        elif update_global == "mod":
            mods_path = find_mods_folder(folder_selected)
            if mods_path:
                global GLOBAL_MOD_DIR
                GLOBAL_MOD_DIR = mods_path
                print_to_console(f"Mod directory set to: {GLOBAL_MOD_DIR}", 'directory')
                entry_widget.delete(0, "end")
                entry_widget.insert(0, GLOBAL_MOD_DIR)
            else:
                print_to_console("No 'mod_versions.json' found within 3 levels. Please check for updates or select the extracted Mods.zip", "error")
                GLOBAL_MOD_DIR = ""
                entry_widget.delete(0, "end")
                entry_widget.insert(0, GLOBAL_MOD_DIR)
            save_config()
        elif update_global == "appdata":
            global GLOBAL_APPDATA_DIR, GLOBAL_CUSTOM_APPDATA
            GLOBAL_APPDATA_DIR = folder_selected
            GLOBAL_CUSTOM_APPDATA = True
            print_to_console(f"Custom AppData Dir: {GLOBAL_APPDATA_DIR}", 'directory')
            entry_widget.delete(0, "end")
            entry_widget.insert(0, GLOBAL_APPDATA_DIR)
            save_config()
    else:
        print_to_console("No folder selected.", "error")
        entry_widget.delete(0, "end")
        entry_widget.insert(0, "No folder selected.")
        if update_global == "game":
            GLOBAL_GAME_DIR = ""
        elif update_global == "mod":
            GLOBAL_MOD_DIR = ""
        elif update_global == "appdata":
            GLOBAL_APPDATA_DIR = ""
            GLOBAL_CUSTOM_APPDATA = False

def reset_appdata_path(entry_widget):
    global GLOBAL_CUSTOM_APPDATA, GLOBAL_APPDATA_DIR
    search_dolphin_emulator(entry_widget)
    GLOBAL_CUSTOM_APPDATA = False
    save_config()
    print_to_console("AppData path reset to default Dolphin Emulator directory and saved in configuration.", "directory")
