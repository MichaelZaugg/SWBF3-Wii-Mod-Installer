# utils.py
import os
import sys
import shutil

# Shared global variable for the UI console widget.
console_text = None

def resource_path(relative_path):
    base_path = getattr(sys, '_MEIPASS', os.path.abspath("."))
    print("Base path:", base_path)  # Debug print
    print("Files in base path:", os.listdir(base_path))  # Debug print to see what's included
    return os.path.join(base_path, relative_path)


def print_to_console(message, tag=None, end="\n"):
    global console_text
    if console_text is not None:
        if tag:
            console_text.insert('end', message + end, tag)
        else:
            console_text.insert('end', message + end)
        console_text.see('end')
        console_text.update_idletasks()
    else:
        print(message, end=end)

def log_message(message, level="info"):
    levels = {"info": "info", "success": "success", "error": "error", "warning": "warning"}
    tag = levels.get(level, "info")
    print(f"[{tag.upper()}] {message}")
    print_to_console(message, tag)

def create_directory(path):
    try:
        os.makedirs(path, exist_ok=True)
        log_message(f"Directory created or already exists: {path}", "success")
    except Exception as e:
        log_message(f"Failed to create directory {path}: {e}", "error")

def copy_files(src, dest, overwrite=True):
    try:
        if overwrite:
            shutil.copytree(src, dest, dirs_exist_ok=True)
        else:
            shutil.copytree(src, dest)
        log_message(f"Files copied from {src} to {dest}", "success")
    except Exception as e:
        log_message(f"Failed to copy files from {src} to {dest}: {e}", "error")

def delete_files(path):

    try:
        # First, check if the path exists.
        if not os.path.exists(path):
            log_message(f"Path {path} does not exist.", "error")
            return

        # Iterate over all items in the directory.
        for item in os.listdir(path):
            item_path = os.path.join(path, item)
            try:
                # If it's a file or a symbolic link, remove it.
                if os.path.isfile(item_path) or os.path.islink(item_path):
                    os.unlink(item_path)
                # If it's a directory, remove it and all its contents.
                elif os.path.isdir(item_path):
                    shutil.rmtree(item_path)
            except Exception as e:
                log_message(f"Failed to delete {item_path}: {e}", "error")
        log_message(f"All files and folders in {path} have been deleted.", "success")
    except Exception as e:
        log_message(f"Failed to delete files in {path}: {e}", "error")
