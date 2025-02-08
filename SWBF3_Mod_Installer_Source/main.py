import customtkinter as ctk
import ui  # Import the module so we can modify its globals
from ui import resource_path, on_resize, setup_ui
from config import current_version

def main():
    global root
    root = ctk.CTk()
    root.title(f"SWBF3 Wii Mod Installer v{current_version}")
    root.geometry("1530x790")
    try:
        root.iconbitmap(resource_path("SWBF3Icon.ico"))
    except Exception as e:
        print("Error setting ICO icon:", e)
    
    root.bind("<Configure>", on_resize)
    
    # Assign the created root to the ui module's global variable.
    ui.root = root
    
    mod_vars = setup_ui()  # Now setup_ui() finds that ui.root is properly set.
    root.mainloop()

if __name__ == "__main__":
    main()