#!/bin/bash
python3 -m PyInstaller --onefile --noconsole --name "SWBF3_Wii_Mod_Installer_v" --icon "SWBF3Icon.png" --add-data "SWBF3Icon.png:." --add-data "image.png:." --hidden-import=PIL._tkinter_finder main.py
read -p "Press any key to continue..."

