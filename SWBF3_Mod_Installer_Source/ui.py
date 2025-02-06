# ui.py
import os
import tkinter as tk
from tkinter import Toplevel, Label, Entry, Button, Checkbutton, IntVar, Frame, Text, PhotoImage
from tkinter import Frame, Text, Label, PhotoImage
from utils import resource_path, log_message, print_to_console
import config
from updater import check_for_updates
from installer import start_install_process, repair_game
import json
import threading

# Global variables for the UI
root = None
loading_label = None
stop_loading_flag = False
console_text = None

# --------------------Loading Animation------------------------

def show_loading_animation():
    """
    Display a spinning animation in the UI.
    """
    import itertools
    spinner = itertools.cycle(["|", "/", "-", "\\"])
    global loading_label, stop_loading_flag
    if not loading_label:
        loading_label = Label(root, text="", bg="#2b2b2b", fg="white")
        loading_label.grid(row=7, column=0, columnspan=3, sticky='ew', padx=5, pady=5)
    def animate():
        if stop_loading_flag:
            loading_label.config(text="")
            return
        loading_label.config(text=next(spinner))
        root.after(100, animate)
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

# --------------------Dark Theme and About-----------------------

def apply_dark_theme(widget):
    """Applies a dark theme to a given widget and its children."""
    try:
        widget.configure(bg="#2b2b2b")
    except tk.TclError:
        pass
    for child in widget.winfo_children():
        if isinstance(child, (Label, Button)):
            child.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")
        elif isinstance(child, (Entry, Text)):
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

# --------------------Setup UI Functions-------------------------

def toggle_mods(mod_vars, toggle_btn):
    if all(var.get() == 1 for var in mod_vars.values()):
        for var in mod_vars.values():
            var.set(0)
        toggle_btn.config(text="Select All Mods")
        print_to_console("Deselected all mods.")
    else:
        for var in mod_vars.values():
            var.set(1)
        toggle_btn.config(text="Deselect All Mods")
        print_to_console("Selected all mods.")

def setup_console_ui(root):
    console_frame = Frame(root, width=400, bg="#2b2b2b")
    console_frame.grid(row=0, column=4, rowspan=6, sticky='nsew', padx=5, pady=5)
    console_frame.grid_propagate(False)
    global console_text
    console_text = Text(console_frame, bg="#1e1e1e", fg="white", height=40, insertbackground="white")
    console_text.pack(expand=True, fill='both')
    Label(console_frame, text="Console Output:", fg="white", bg="#2b2b2b").pack(side='top', anchor='w')

    # Configure tags for styled text using the lowercase names:
    console_text.tag_configure('error', foreground='red')
    console_text.tag_configure('warning', foreground='yellow')
    console_text.tag_configure('success', foreground='#32CD32')
    console_text.tag_configure('info', foreground='white')
    
    # Share the widget with utils:
    import utils
    utils.console_text = console_text
    return console_text

def setup_top_buttons(root, mod_vars):
    about_button = Button(root, text="About", command=show_about)
    about_button.grid(row=0, column=0, sticky='w', padx=5, pady=5)
    about_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")
    try:
        fun_image = PhotoImage(file=resource_path("image.png"))
        Label(root, image=fun_image, bg="#2b2b2b").grid(row=0, column=1, sticky='w', padx=5, pady=5)
        root.fun_image = fun_image
    except Exception as e:
        print(f"Error loading image: {e}")
        Label(root, text="Image Not Available", bg="#2b2b2b", fg="white").grid(row=0, column=1, sticky='w', padx=5, pady=5)
    install_button = Button(root, text="Install", command=lambda: start_install_process(mod_vars))
    install_button.grid(row=4, column=2, sticky='w', padx=10, pady=10)
    install_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")
    updates_button = Button(root, text="Check for Updates", command=check_for_updates)
    updates_button.grid(row=4, column=3, sticky='w', padx=10, pady=10)
    updates_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")
    repair_button = Button(root, text="Repair", command=repair_game_async)
    repair_button.grid(row=5, column=2, sticky='w', padx=0, pady=0)
    repair_button.configure(bg="#2b2b2b", fg="white", activebackground="#3c3f41", activeforeground="white")

def setup_directory_ui(root):
    Label(root, text="Game Directory:").grid(row=1, column=0, sticky='w', padx=5)
    game_dir_entry = Entry(root)
    game_dir_entry.grid(row=1, column=1, sticky='ew', padx=5)
    Button(root, text="Browse Game Directory", command=lambda: config.browse_folder(game_dir_entry, "game")).grid(row=1, column=2, padx=5)

    Label(root, text="Mod Directory:").grid(row=2, column=0, sticky='w', padx=5)
    mod_dir_entry = Entry(root)
    mod_dir_entry.grid(row=2, column=1, sticky='ew', padx=5)
    Button(root, text="Select Mod Directory", command=lambda: config.browse_folder(mod_dir_entry, "mod")).grid(row=2, column=2, padx=5)

    Label(root, text="AppData Directory:").grid(row=3, column=0, sticky='w', padx=5)
    appdata_entry = Entry(root)
    appdata_entry.grid(row=3, column=1, sticky='ew', padx=5)
    Button(root, text="Custom AppData", command=lambda: config.browse_folder(appdata_entry, "appdata")).grid(row=3, column=2, padx=5)
    Button(root, text="Reset AppData Path", command=lambda: config.reset_appdata_path(appdata_entry)).grid(row=3, column=3, padx=5)

    # Initialize directories and update UI by calling the function from config.py
    config.initialize_directories(game_dir_entry, mod_dir_entry, appdata_entry)

    return game_dir_entry, mod_dir_entry, appdata_entry

def setup_mod_ui(root):
    mods_frame = Frame(root, bg="#2b2b2b")
    mods_frame.grid(row=4, column=0, columnspan=2, sticky='w', padx=5, pady=5)
    Label(mods_frame, text="Select Mods To Install:", bg="#2b2b2b", fg="white").grid(row=0, column=0, sticky='w')
    from installer import MODS
    mod_vars = {mod: IntVar() for mod in MODS}
    for i, mod in enumerate(MODS):
        Checkbutton(mods_frame, text=mod, variable=mod_vars[mod], bg="#2b2b2b", fg="white", selectcolor="#3c3f41").grid(row=i+1, column=0, sticky='w')
    toggle_btn = Button(mods_frame, text="Select All Mods", command=lambda: toggle_mods(mod_vars, toggle_btn))
    toggle_btn.grid(row=len(MODS)+1, column=0, sticky='w', padx=5, pady=2)
    Label(root, text="Enable custom textures in Dolphin", bg="#2b2b2b", fg="white").grid(row=5, column=0, columnspan=3, sticky='ew', padx=5, pady=2)
    install_button = Button(root, text="Install", command=lambda: start_install_process(mod_vars))
    install_button.grid(row=6, column=0, columnspan=3, sticky='ew', padx=5, pady=5)
    return mod_vars

def setup_ui(root):
    root.grid_columnconfigure(1, weight=1)
    setup_console_ui(root)
    mod_vars = setup_mod_ui(root)
    setup_top_buttons(root, mod_vars)
    setup_directory_ui(root)
    apply_dark_theme(root)
    return mod_vars

def check_install_conditions(mod_vars):
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
        if "Lighting Fix" in selected_mods or "Restored r7 Vehicles" in selected_mods:
            print_to_console("Error: 'Music for all maps/modes-Fixed Clonetrooper VO' is incompatible with 'Lighting Fix' and 'Restored r7 Vehicles'.", "error")
        else:
            warnings.append("Warning: 'Music for all maps/modes-Fixed Clonetrooper VO' will override lighting changes.")

    for warning in warnings:
        print_to_console(warning, "warning")

    return not errors

def load_mod_directories(mod_versions_path):
    try:
        with open(mod_versions_path, "r", encoding="utf-8") as file:
            data = json.load(file)
        mod_dirs = {}
        for mod in data.get("mods", []):
            name = mod.get("name")
            dir_path = mod.get("dir", name)
            if not name or not dir_path:
                log_message(f"Skipping invalid mod entry: {mod}", "warning")
                continue
            mod_dirs[name] = dir_path
        return mod_dirs
    except (FileNotFoundError, json.JSONDecodeError) as e:
        log_message(f"Failed to load mod_versions.json: {e}", "error")
        return {}

def check_file_name_conflicts(selected_mods):
    file_names = {}
    conflicts = []
    from installer import MODS_DIRECTORY
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
            if file_name == "invisible_hand.res" and {"Lighting Fix", "Music for all maps/modes-Fixed Clonetrooper VO"}.issubset(mods):
                print_to_console(f"[ERROR] File: {file_name} is present in mods: {', '.join(mods)} (DISMISSED)", "info")
                continue
            conflicts.append((file_name, list(mods)))
            print_to_console(f"[ERROR] File: ", "error", end="")
            print_to_console(file_name, "yellow", end="")
            print_to_console(f" is present in mods: {', '.join(mods)}", "error")
    return conflicts

def install_selected_mods(selected_mods):
    try:
        start_loading()
        if not check_install_conditions(selected_mods):
            log_message("Installation aborted due to errors.", "error")
            return
        conflicts = check_file_name_conflicts(selected_mods)
        if conflicts:
            return
        log_message("No conflicts detected. Proceeding with installation...", "success")
        mods_to_install = [mod for mod, is_selected in selected_mods.items() if is_selected.get()]
        lighting_fix_selected = "Lighting Fix" in mods_to_install
        other_compilation_mods_selected = any(mod in __import__("installer").MODS_REQUIRING_COMPILATION for mod in mods_to_install if mod != "Lighting Fix")
        if lighting_fix_selected:
            mods_to_install.remove("Lighting Fix")
        for mod in mods_to_install:
            log_message(f"Installing: {mod}", "info")
            try:
                mod_install_function = __import__("installer").MODS.get(mod)
                if mod_install_function:
                    mod_install_function()
                    log_message(f"{mod} installed successfully.", "success")
                else:
                    log_message(f"No installation function found for {mod}. Skipping...", "warning")
            except Exception as e:
                log_message(f"Error installing {mod}: {e}", "error")
        if other_compilation_mods_selected:
            log_message("Compilation required. Starting compilation process...", "info")
            __import__("installer").compile_templates_res()
        if lighting_fix_selected:
            log_message("Installing Lighting Fix last...", "info")
            __import__("installer").install_lighting_fix()
            log_message("Lighting Fix installed successfully.", "success")
        log_message("----- Installation Complete -----", "success")
    finally:
        stop_loading()

def repair_game_async():
    """Runs the repair process in a separate thread to prevent UI freeze."""
    def run():
        try:
            start_loading()
            log_message("Starting game repair...", "info")
            repair_game(all=True)  # Runs the actual repair process
            log_message("Game repair completed successfully.", "success")
        except Exception as e:
            log_message(f"Error during game repair: {e}", "error")
        finally:
            stop_loading()

    repair_thread = threading.Thread(target=run, daemon=True)
    repair_thread.start()

def main_menu():
    global root
    root = tk.Tk()
    root.title(f"SWBF3 Wii Mod Installer v{config.current_version}")
    root.geometry("1530x790")
    root.minsize(1530, 770)
    try:
        root.iconbitmap(resource_path("SWBF3Icon.ico"))
    except Exception as e:
        print("Error setting ICO icon:", e)
    mod_vars = setup_ui(root)
    setup_top_buttons(root, mod_vars)
    root.mainloop()

if __name__ == "__main__":
    from config import initialize_directories
    initialize_directories()
    main_menu()
