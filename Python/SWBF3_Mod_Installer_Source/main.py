import customtkinter as ctk
import ui
import config
from ui import resource_path, on_resize, setup_ui, load_language
from config import current_version
import platform
from tkinter import PhotoImage

def main():
    # Load configuration; this updates config.GLOBAL_LANGUAGE
    config.load_config()
    # Use config.GLOBAL_LANGUAGE (from the module) rather than an imported copy
    ui.lang = load_language(config.GLOBAL_LANGUAGE)
    
    # Create the main window.
    root = ctk.CTk()
    root.title(f"SWBF3 Wii Mod Installer v{current_version}")
    root.geometry("1530x790")
    
    try:
        if platform.system() == "Windows":
            root.iconbitmap(resource_path("SWBF3Icon.ico"))
        else:
            icon = PhotoImage(file=resource_path("SWBF3Icon.png"))
            root.iconphoto(False, icon)
    except Exception as e:
        print("Error setting icon:", e)
    
    root.bind("<Configure>", on_resize)
    ui.root = root  # Set the ui module's root to our window.
    mod_vars = setup_ui()  # Build the UI (this uses ui.lang which is now up-to-date)
    root.mainloop()

if __name__ == "__main__":
    main()
