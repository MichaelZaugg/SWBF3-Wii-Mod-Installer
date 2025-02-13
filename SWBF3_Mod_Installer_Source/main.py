import customtkinter as ctk
import ui  # Import the module so we can modify its globals
from ui import resource_path, on_resize, setup_ui
from config import current_version
import platform
from tkinter import PhotoImage

def main():
    global root
    root = ctk.CTk()
    root.title(f"SWBF3 Wii Mod Installer v{current_version}")
    root.geometry("1530x790")
    try:
        if platform.system() == "Windows":
            root.iconbitmap(resource_path("SWBF3Icon.ico"))
        else:
            # On Linux, use iconphoto with a PNG (make sure SWBF3Icon.png exists, or handle missing file)
            try:
                icon = PhotoImage(file=resource_path("SWBF3Icon.png"))
                root.iconphoto(False, icon)
            except Exception as e:
                print("Icon file not found, continuing without a custom icon.", e)
    except Exception as e:
        print("Error setting icon:", e)
    
    root.bind("<Configure>", on_resize)
    ui.root = root  # Assign the created root to the ui module's global variable.
    mod_vars = setup_ui()  # Setup the rest of the UI.
    root.mainloop()

if __name__ == "__main__":
    main()
