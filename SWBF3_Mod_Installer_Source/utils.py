# utils.py
import os
import sys
import shutil
import time
from pathlib import Path

# Shared global variable for the UI console widget.
console_text = None

def resource_path(relative_path):
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
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
        import shutil
        if overwrite:
            shutil.copytree(src, dest, dirs_exist_ok=True)
        else:
            shutil.copytree(src, dest)
        log_message(f"Files copied from {src} to {dest}", "success")
    except Exception as e:
        log_message(f"Failed to copy files from {src} to {dest}: {e}", "error")
