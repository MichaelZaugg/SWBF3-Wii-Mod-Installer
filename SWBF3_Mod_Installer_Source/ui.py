# ui.py
import threading
import tkinter  # for IntVar and file dialogs
from PIL import Image

import customtkinter as ctk
import tkinter  # Ensure tkinter is imported for StringVar and messagebox
import tkinter.messagebox as messagebox
from utils import resource_path, log_message, print_to_console
import config
from updater import check_for_updates
from installer import start_install_process, repair_game, uninstall_textures
import json
import os
# Global variables for debouncing and progress bar animation

resize_after_id = None
# Global variables for progress bar
progress_bar = None
progress_bar_animation_id = None
progress_value = 0
progress_active = False


# ---------------- Global Variables ----------------
root = None
console_text = None

# Set appearance mode and color theme (customizable)
ctk.set_appearance_mode("dark")
ctk.set_default_color_theme("dark-blue")

def load_language(lang_code="en"):
    # Build the path to the languages folder within the mod directory.
    # Ensure that config.load_config() has been called so that GLOBAL_MOD_DIR is set.
    languages_folder = os.path.join(config.GLOBAL_MOD_DIR, "languages")
    json_path = os.path.join(languages_folder, f"{lang_code}.json")
    try:
        with open(json_path, "r", encoding="utf-8") as file:
            return json.load(file)
    except Exception as e:
        print(f"Error loading language file from {json_path}: {e}")
        return {}
        
# Example usage:
lang = load_language()
#print(lang.get("about", "About"))

def change_language():
    import config
    new_lang_code = select_language()  # Let the user choose a language
    new_lang = load_language(new_lang_code)
    if not new_lang:
        messagebox.showerror("Language Error", f"Could not load language: {new_lang_code}")
        return
    global lang
    lang = new_lang
    config.GLOBAL_LANGUAGE = new_lang_code
    config.save_config()
    
    # Instead of asking for a restart, update the UI:
    global main_container
    if main_container is not None:
        main_container.destroy()  # Remove the old UI
    # Rebuild the UI with the new language texts:
    setup_ui()

def select_language():
    """
    Displays a simple window for language selection based on the JSON files available
    in the "languages" folder inside the mod directory.
    Returns the selected language code.
    """
    import config
    # Build the path to the languages folder inside the mod directory
    languages_dir = os.path.join(config.GLOBAL_MOD_DIR, "languages")
    try:
        files = os.listdir(languages_dir)
    except Exception as e:
        print(f"Error reading languages directory: {e}")
        files = []
    
    # Extract language codes (e.g. "en" from "en.json", "german" from "german.json", etc.)
    language_codes = [f[:-5] for f in files if f.endswith(".json")]
    if not language_codes:
        language_codes = ["en"]  # Fallback if no languages are found

    # Create a temporary window for language selection.
    import customtkinter as ctk
    import tkinter
    lang_window = ctk.CTkToplevel()
    lang_window.title("Select Language")
    lang_window.geometry("300x150")
    
    # Create a StringVar with the first available language as default.
    selected_lang = tkinter.StringVar(value=language_codes[0])
    
    # Create a dropdown menu populated with available language codes.
    option_menu = ctk.CTkOptionMenu(lang_window, values=language_codes, variable=selected_lang)
    option_menu.pack(padx=20, pady=20)
    
    # Create a confirm button to close the window.
    def confirm_selection():
        lang_window.destroy()
    
    confirm_button = ctk.CTkButton(lang_window, text="Confirm", command=confirm_selection)
    confirm_button.pack(padx=20, pady=10)
    
    lang_window.grab_set()  # Make the window modal.
    lang_window.wait_window()
    
    return selected_lang.get()



# ---------------- Utility Functions ----------------
def toggle_mods(mod_vars, toggle_btn):
    """
    Toggle all mod checkboxes on or off.
    If all are already selected, deselect them; otherwise, select them.
    """
    if all(var.get() == 1 for var in mod_vars.values()):
        for var in mod_vars.values():
            var.set(0)
        toggle_btn.configure(text=lang.get("select_all_mods", "Select All Mods"))
        print_to_console("Deselected all mods.")
    else:
        for var in mod_vars.values():
            var.set(1)
        toggle_btn.configure(text=lang.get("deselect_all_mods", "Deselect All Mods"))
        print_to_console("Selected all mods.")


def on_resize(event):
    """
    Called on each <Configure> event.
    If a progress bar animation is running (i.e. during a repair),
    cancel its update and debounce the resize event.
    """
    global resize_after_id, progress_bar_animation_id
    if progress_bar_animation_id is not None:
        root.after_cancel(progress_bar_animation_id)
        progress_bar_animation_id = None
    if resize_after_id is not None:
        root.after_cancel(resize_after_id)
    resize_after_id = root.after(300, resume_after_resize)


def resume_after_resize():
    """
    If a progress bar is active, resume its animation after resizing.
    """
    global progress_active
    if progress_active:
        animate_progress_bar()


# ---------------- Progress Bar Functions ----------------
def create_progress_bar(root):
    """Creates and places the progress bar in the root window (row 1)."""
    global progress_bar
    progress_bar = ctk.CTkProgressBar(root, progress_color="yellow")  # Set progress bar to yellow.
    progress_bar.grid(row=1, column=0, sticky="ew", padx=5, pady=5)
    progress_bar.set(0)

def animate_progress_bar():
    """Updates the progress bar value and schedules the next update."""
    global progress_value, progress_bar_animation_id
    progress_value = (progress_value + 0.02) % 1.0
    if progress_bar:
        progress_bar.set(progress_value)
    progress_bar_animation_id = root.after(500, animate_progress_bar)

def start_progress_bar():
    """Starts the progress bar animation."""
    global progress_value, progress_active
    progress_value = 0
    progress_active = True
    if progress_bar is None:
        create_progress_bar(root)
    animate_progress_bar()

def stop_progress_bar():
    """Stops the progress bar animation and resets its value."""
    global progress_bar_animation_id, progress_active
    if progress_bar_animation_id:
        root.after_cancel(progress_bar_animation_id)
        progress_bar_animation_id = None
    if progress_bar:
        progress_bar.set(0)
    progress_active = False


# ---------------- About Window ----------------
def show_about():
    about_window = ctk.CTkToplevel(root)
    about_window.title("About")
    about_window.geometry("600x300")
    about_window.resizable(False, False)
    about_text = ctk.CTkTextbox(about_window, width=580, height=260)
    about_text.pack(padx=10, pady=10)
    about_text.insert("0.0", lang.get("about_text", "Default about text..."))

    about_text.configure(state="disabled")


# ---------------- Top Panel (with Image on the Left) ----------------
def setup_top_panel(parent):
    """
    Top panel with the image on the left and the About and Language buttons on the right.
    """
    top_panel = ctk.CTkFrame(parent)
    top_panel.pack(fill="x", padx=5, pady=5)
    
    # Load and pack the image on the left.
    try:
        image_path = resource_path("image.png")
        fun_image = ctk.CTkImage(light_image=Image.open(image_path), size=(245, 103))
        image_label = ctk.CTkLabel(top_panel, image=fun_image, text="")
        image_label.pack(side="left", padx=5, pady=5)
        # Keep a reference so it is not garbage-collected.
        top_panel.fun_image = fun_image
    except Exception as e:
        print(f"Error loading image: {e}")
        image_label = ctk.CTkLabel(top_panel, text="Image Not Available")
        image_label.pack(side="left", padx=5, pady=5)
    
    # Create the Language button.
    language_button = ctk.CTkButton(top_panel, text=lang.get("language", "Language"), command=change_language)
    language_button.pack(side="right", padx=5, pady=5)
    
    # Pack the About button on the right.
    about_button = ctk.CTkButton(top_panel, text=lang.get("about", "About"), command=show_about)
    about_button.pack(side="right", padx=5, pady=5)
    
    return top_panel


def setup_directory_frame(parent):
    """
    Frame for directory entries (Game, Mod, and AppData).
    """
    dir_frame = ctk.CTkFrame(parent)
    dir_frame.pack(fill="x", padx=5, pady=5)

    # Game Directory.
    game_dir_label = ctk.CTkLabel(dir_frame, text=lang.get("game_directory", "Game Directory:"))
    game_dir_label.grid(row=0, column=0, sticky="w", padx=5, pady=2)
    game_dir_entry = ctk.CTkEntry(dir_frame)
    game_dir_entry.grid(row=0, column=1, sticky="ew", padx=5, pady=2)
    browse_game_button = ctk.CTkButton(dir_frame, text=lang.get("browse", "Browse"), command=lambda: config.browse_folder(game_dir_entry, "game"))

    browse_game_button.grid(row=0, column=2, padx=5, pady=2)

    # Mod Directory.
    mod_dir_label = ctk.CTkLabel(dir_frame, text=lang.get("mod_directory", "Mod Directory:"))
    mod_dir_label.grid(row=1, column=0, sticky="w", padx=5, pady=2)
    mod_dir_entry = ctk.CTkEntry(dir_frame)
    mod_dir_entry.grid(row=1, column=1, sticky="ew", padx=5, pady=2)
    browse_mod_button = ctk.CTkButton(dir_frame, text=lang.get("browse", "Browse"), command=lambda: config.browse_folder(mod_dir_entry, "mod"))
    browse_mod_button.grid(row=1, column=2, padx=5, pady=2)

    # AppData Directory.
    appdata_label = ctk.CTkLabel(dir_frame, text=lang.get("appdata_directory", "AppData Directory:"))
    appdata_label.grid(row=2, column=0, sticky="w", padx=5, pady=2)
    appdata_entry = ctk.CTkEntry(dir_frame)
    appdata_entry.grid(row=2, column=1, sticky="ew", padx=5, pady=2)
    custom_appdata_button = ctk.CTkButton(dir_frame, text=lang.get("custom", "Custom"), command=lambda: config.browse_folder(appdata_entry, "appdata"))
    custom_appdata_button.grid(row=2, column=2, padx=5, pady=2)
    reset_appdata_button = ctk.CTkButton(dir_frame, text=lang.get("reset", "Reset"), command=lambda: config.reset_appdata_path(appdata_entry))
    reset_appdata_button.grid(row=2, column=3, padx=5, pady=2)

    # Allow the entry column to expand.
    dir_frame.grid_columnconfigure(1, weight=1)

    # Initialize directories (and print messages to the console).
    config.initialize_directories(game_dir_entry, mod_dir_entry, appdata_entry)
    return game_dir_entry, mod_dir_entry, appdata_entry


def setup_mod_frame(parent):
    """
    Creates a mod selection UI using a simulated tab view.
    A left-side navigation panel contains two buttons:
      - "Texture Mods"
      - "Game Mods"
    When one button is clicked, the corresponding content frame is shown.
    
    Returns a dictionary mapping each mod name to its tkinter.IntVar.
    """
    # Container for the entire mod selection area.
    container = ctk.CTkFrame(parent)
    container.pack(fill="both", expand=True, padx=5, pady=5)
    
    # Create a frame to hold the navigation (the simulated tab buttons) on the left.
    nav_frame = ctk.CTkFrame(container, width=150)
    nav_frame.pack(side="left", fill="y", padx=5, pady=5)
    
    # Create a frame for the content on the right.
    content_frame = ctk.CTkFrame(container)
    content_frame.pack(side="left", fill="both", expand=True, padx=5, pady=5)
    
    # Create two frames inside content_frame—one for each "tab".
    texture_frame = ctk.CTkFrame(content_frame)
    game_frame = ctk.CTkFrame(content_frame)
    
    # Place them in the same grid cell so that one can be raised over the other.
    texture_frame.grid(row=0, column=0, sticky="nsew")
    game_frame.grid(row=0, column=0, sticky="nsew")
    content_frame.grid_rowconfigure(0, weight=1)
    content_frame.grid_columnconfigure(0, weight=1)
    
    # Example lists of mods.
    texture_mods = [
        "4k texture pack Part 1, 2, 3, 4, 5",
        "Texture Pack: Faithful Health Bars",
        "Dynamic Input Textures",
        "Minimaps Fix (For r904, Enable prefetch custom textures)",
        "HD User Interface"
    ]
    game_mods = [
        "Lighting Fix",
        "Updated Debug Menu (main.dol from Clonetrooper163)",
        "Cloth Fix",
        "4k Characters/Model Fix",
        "Music for all maps/modes-Fixed Clonetrooper VO",
        "Restored r7 Vehicles",
        "r9 Restored Melee Classes(Class Unique Icons Fix Included)",
        "Class Unique Icons Fix",
        "Muted Blank Audio",
        "Unlocked PC/Xbox 360 Features in Frontend"
    ]
    
    # Dictionary to hold IntVar for each mod.
    mod_vars = {}
    
    # Function to populate a given frame with mod checkboxes from a list.
    def populate_frame(frame, mod_list):
        # Clear any existing widgets.
        for widget in frame.winfo_children():
            widget.destroy()
        for mod in mod_list:
            # Create an IntVar if not already created.
            if mod not in mod_vars:
                mod_vars[mod] = tkinter.IntVar(value=0)
            cb = ctk.CTkCheckBox(frame, text=mod, variable=mod_vars[mod])
            cb.pack(anchor="w", padx=5, pady=2)
    
    # Populate both frames.
    populate_frame(texture_frame, texture_mods)
    populate_frame(game_frame, game_mods)
    
    # Function to switch visible frame.
    def show_frame(frame):
        frame.tkraise()
    
    # Create navigation buttons in nav_frame.
    texture_btn = ctk.CTkButton(nav_frame, text="Texture Mods",
                                command=lambda: show_frame(texture_frame))
    texture_btn.pack(fill="x", padx=5, pady=5)
    
    game_btn = ctk.CTkButton(nav_frame, text="Game Mods",
                             command=lambda: show_frame(game_frame))
    game_btn.pack(fill="x", padx=5, pady=5)
    
    # Initially show the texture mods.
    show_frame(texture_frame)
    
    # Below the content area, add a toggle button (optional).
    toggle_btn = ctk.CTkButton(container, text=lang.get("select_all_mods", "Select All Mods"), command=lambda: toggle_mods(mod_vars, toggle_btn))

    toggle_btn.pack(anchor="w", padx=5, pady=5)
    
    # (Optional) Add a label for additional instructions.
    custom_texture_label = ctk.CTkLabel(container, text=lang.get("enable_custom_textures", "Enable custom textures in Dolphin"))
    custom_texture_label.pack(anchor="w", padx=5, pady=5)
    
    return mod_vars


def setup_actions_frame(parent, mod_vars):
    """
    Frame for action buttons (Install, Check for Updates, Repair).
    """
    actions_frame = ctk.CTkFrame(parent)
    actions_frame.pack(fill="x", padx=5, pady=5)

    install_button = ctk.CTkButton(actions_frame, text=lang.get("install", "Install"), command=lambda: start_install_process(mod_vars))
    install_button.pack(side="left", expand=True, padx=5, pady=5)

    updates_button = ctk.CTkButton(actions_frame, text=lang.get("check_updates", "Check for Updates"), command=check_for_updates)
    updates_button.pack(side="left", expand=True, padx=5, pady=5)

    repair_button = ctk.CTkButton(actions_frame, text=lang.get("repair", "Repair"), command=repair_game_async)
    repair_button.pack(side="left", expand=True, padx=5, pady=5)

    uninstall_tex_button = ctk.CTkButton(actions_frame, text=lang.get("uninstall_texture_mods", "Uninstall Texture Mods"), command=uninstall_tex_async)
    uninstall_tex_button.pack(side="left", expand=True, padx=5, pady=5)

    return actions_frame


def setup_console_frame(parent):
    """
    Frame for console output. This console frame will fill its parent container.
    """
    console_frame = ctk.CTkFrame(parent)
    console_frame.grid(row=0, column=0, sticky="nsew", padx=5, pady=5)
    console_frame.grid_rowconfigure(1, weight=1)
    console_frame.grid_columnconfigure(0, weight=1)

    label = ctk.CTkLabel(console_frame, text=lang.get("console_output", "Console Output:"))
    label.grid(row=0, column=0, sticky="nw", padx=5, pady=2)

    console_text_widget = ctk.CTkTextbox(console_frame)
    console_text_widget.grid(row=1, column=0, sticky="nsew", padx=5, pady=5)

    # Configure the underlying tkinter widget for colored text.
    tk_text = console_text_widget._textbox
    tk_text.tag_configure('error', foreground='red')
    tk_text.tag_configure('warning', foreground='yellow')
    tk_text.tag_configure('success', foreground='#32CD32')
    tk_text.tag_configure('info', foreground='white')

    global console_text
    console_text = console_text_widget
    import utils
    utils.console_text = console_text
    return console_frame


def repair_game_async():
    """
    Run the repair process in a separate thread to prevent UI freezing.
    Uses the progress bar instead of a spinner.
    """
    def run():
        try:
            start_progress_bar()
            log_message("Starting game repair...", "info")
            repair_game(all=True)
            log_message("Game repair completed successfully.", "success")
        except Exception as e:
            log_message(f"Error during game repair: {e}", "error")
        finally:
            stop_progress_bar()
    repair_thread = threading.Thread(target=run, daemon=True)
    repair_thread.start()

def uninstall_tex_async():
    """
    Run the repair process in a separate thread to prevent UI freezing.
    Uses the progress bar instead of a spinner.
    """
    def run():
        try:
            start_progress_bar()
            log_message("Uninstalling Texture Mods...", "info")
            uninstall_textures()
            log_message("Uninstall completed successfully.", "success")
        except Exception as e:
            log_message(f"Error during game repair: {e}", "error")
        finally:
            stop_progress_bar()
    repair_thread = threading.Thread(target=run, daemon=True)
    repair_thread.start()

def setup_ui():
    """
    Build the main UI using a container with two columns:
      - Left panel: UI controls (top banner, directory settings, mod selection, action buttons).
      - Right panel: Console output.
      
    In this layout, the left panel occupies 3/4 of the width and the right panel occupies 1/4.
    """
    global main_container  # declare a global variable for the main container
    main_container = ctk.CTkFrame(root)
    main_container.grid(row=0, column=0, sticky="nsew", padx=5, pady=5)
    root.grid_rowconfigure(0, weight=1)
    root.grid_columnconfigure(0, weight=1)

    # Configure two columns: left (weight=3) and right (weight=1)
    main_container.grid_columnconfigure(0, weight=3)
    main_container.grid_columnconfigure(1, weight=1)
    main_container.grid_rowconfigure(0, weight=1)

    # Create the right panel (Console)
    right_panel = ctk.CTkFrame(main_container)
    right_panel.grid(row=0, column=1, sticky="nsew", padx=(5, 0), pady=5)
    right_panel.grid_rowconfigure(0, weight=1)
    right_panel.grid_columnconfigure(0, weight=1)
    setup_console_frame(right_panel)

    # Create the left panel (UI Controls)
    left_panel = ctk.CTkFrame(main_container)
    left_panel.grid(row=0, column=0, sticky="nsew", padx=(0, 5), pady=5)
    left_panel.grid_rowconfigure(2, weight=1)
    setup_top_panel(left_panel)
    setup_directory_frame(left_panel)
    mod_vars = setup_mod_frame(left_panel)
    setup_actions_frame(left_panel, mod_vars)

    return mod_vars



# ---------------- Aliases for Backward Compatibility ----------------
# Provide aliases so that other modules that import start_loading and stop_loading will
# get the progress bar functions.
start_loading = start_progress_bar
stop_loading = stop_progress_bar

if __name__ == "__main__":
    setup_ui()