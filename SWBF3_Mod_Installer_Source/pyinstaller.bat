@echo off
python -m PyInstaller --onefile --noconsole --name "SWBF3_Wii_Mod_Installer_v" --icon "SWBF3Icon.ico" --add-data "SWBF3Icon.ico;." --add-data "image.png;." main.py
pause


