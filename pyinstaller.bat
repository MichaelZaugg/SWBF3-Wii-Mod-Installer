@echo off
python -m PyInstaller --onefile --noconsole --name "SWBF3 Wii Mod Installer" --icon "SWBF3Icon.ico" --add-data "SWBF3Icon.ico;." --add-data "image.png;." SWBF3_Wii_Mod_Installer.py
pause

