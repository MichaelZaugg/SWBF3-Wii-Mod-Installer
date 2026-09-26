# updater.py
import os
import sys
import time
import threading
import requests
import shutil
import tempfile
import json
from utils import log_message
import config  # Import the entire config module
import platform

MANIFEST_URL = "https://raw.githubusercontent.com/MichaelZaugg/SWBF3-Wii-Mod-Installer/refs/heads/main/manifest.json"
MOD_VERSIONS_URL = "https://raw.githubusercontent.com/MichaelZaugg/SWBF3-Wii-Mod-Installer/refs/heads/main/mod_versions.json"

def fetch_manifest():
    try:
        response = requests.get(MANIFEST_URL)
        response.raise_for_status()
        return response.json()
    except requests.RequestException as e:
        log_message(f"Error fetching manifest: {e}", "error")
        return None

def fetch_manifest():
    try:
        response = requests.get(MANIFEST_URL)
        response.raise_for_status()
        return response.json()
    except requests.RequestException as e:
        log_message(f"Error fetching manifest: {e}", "error")
        return None

def check_installer_update():
    log_message("Checking for installer updates...", "info")
    def download_and_update():
        try:
            manifest = fetch_manifest()
            if not manifest:
                log_message("Failed to fetch manifest.", "error")
                return False

            remote_installer = manifest.get("installer", {})
            remote_version = remote_installer.get("version")

            system = platform.system()
            if system == "Windows":
                download_url = remote_installer.get("download_url")
                ext = ".exe"
            elif system == "Linux":
                download_url = remote_installer.get("download_url_linux")
                ext = ".sh"
            else:
                log_message("Unsupported OS detected. Skipping update.", "error")
                return False

            if not remote_version or not download_url:
                log_message("Installer version or download URL missing in manifest.", "error")
                return False

            current_installer_path = os.path.abspath(sys.argv[0])
            new_installer_path = os.path.join(
                os.path.dirname(current_installer_path),
                f"SWBF3_Wii_Mod_Installer_v{remote_version}{ext}"
            )

            if remote_version > config.current_version:
                log_message(f"New installer version available: {remote_version}", "info")
                log_message("Downloading the updated installer...", "info")
                response = requests.get(download_url, stream=True)
                response.raise_for_status()
                with open(new_installer_path, "wb") as f:
                    for chunk in response.iter_content(chunk_size=8192):
                        if chunk:
                            f.write(chunk)
                log_message(f"Installer updated successfully. Saved to {new_installer_path}.", "success")
                log_message("Please delete the old installer.", "error")
                time.sleep(10)
                os._exit(0)
            else:
                log_message("Installer is up-to-date.", "success")
                return False
        except requests.RequestException as e:
            log_message(f"Error checking for installer updates: {e}", "error")
            return False
    download_and_update()
    
def check_mod_versions():
    # Use the live mod directory from config
    mod_versions_path = os.path.join(config.GLOBAL_MOD_DIR, "mod_versions.json")
    try:
        if not os.path.exists(mod_versions_path):
            log_message("mod_versions.json not found. Please download Mods.zip", "error")
            raise Exception

        local_mod_versions = {}
        with open(mod_versions_path, "r", encoding="utf-8") as file:
            local_mod_versions = {
                mod["name"]: {"version": mod.get("version", "0.0"), "dir": mod.get("dir", mod["name"])}
                for mod in json.load(file).get("mods", [])
            }

        manifest = fetch_manifest()
        if not manifest:
            log_message("Failed to fetch manifest.", "error")
            return

        any_updates = False
        for mod in manifest["mods"]:
            mod_name = mod.get("name")
            if not mod_name:
                log_message("Manifest mod entry missing 'name'. Skipping.", "error")
                continue

            remote_version = mod.get("version", "0.0")
            # Use the live mod directory from config
            mod_dir_for_mod = os.path.join(config.GLOBAL_MOD_DIR, mod.get("dir", mod_name))
            download_url = mod.get("download_url")

            local_mod = local_mod_versions.get(mod_name, {"version": "0.0", "dir": mod_name})
            local_version = local_mod["version"]

            if remote_version > local_version:
                log_message(f"New version of {mod_name} available: {remote_version} (local: {local_version}). Downloading...", "info")
                if download_url:
                    if update_mod(mod_dir_for_mod, download_url, mod_name, remote_version, mod_versions_path):
                        any_updates = True
                else:
                    log_message(f"No download URL provided for {mod_name}. Skipping update.", "warning")
            else:
                log_message(f"{mod_name} is up-to-date.", "success")

        if not any_updates:
            log_message("All mods are up-to-date. No changes made to mod_versions.json.", "info")

    except requests.RequestException as e:
        log_message(f"Failed to download mod_versions.json: {e}", "error")
    except Exception as e:
        log_message(f"Failed to check or update mod_versions.json: {e}", "error")

def update_mod(mod_dir, download_url, mod_name, remote_version, mod_versions_path):
    # Initialize temporary variables to avoid UnboundLocalError
    temp_zip_path = None
    temp_extract_dir = None
    try:
        # Check if the Mod directory exists and delete
        if os.path.exists(mod_dir):
            shutil.rmtree(mod_dir)
        
        os.makedirs(mod_dir)  

        log_message(f"Downloading files for {mod_name}...", "info")
        response = requests.get(download_url, stream=True)
        response.raise_for_status()

        temp_zip_path = os.path.join(mod_dir, "temp_mod.zip")
        with open(temp_zip_path, "wb") as temp_file:
            for chunk in response.iter_content(chunk_size=8192):
                temp_file.write(chunk)

        temp_extract_dir = tempfile.mkdtemp()
        shutil.unpack_archive(temp_zip_path, temp_extract_dir)
        os.remove(temp_zip_path)  # Clean up temporary zip file

        extracted_contents = os.listdir(temp_extract_dir)

        # If there's only one folder and its name matches the mod_dir folder name, move its contents
        if len(extracted_contents) == 1 and os.path.isdir(os.path.join(temp_extract_dir, extracted_contents[0])):
            single_folder_path = os.path.join(temp_extract_dir, extracted_contents[0])
            if os.path.basename(single_folder_path).lower() == os.path.basename(mod_dir).lower():
                for item in os.listdir(single_folder_path):
                    src_path = os.path.join(single_folder_path, item)
                    dest_path = os.path.join(mod_dir, item)
                    if os.path.exists(dest_path):
                        if os.path.isdir(dest_path):
                            shutil.rmtree(dest_path)
                        else:
                            os.remove(dest_path)
                    if os.path.isdir(src_path):
                        shutil.copytree(src_path, dest_path)
                    else:
                        shutil.copy2(src_path, dest_path)
                log_message("Removed unnecessary duplicate folder after extraction.", "info")
            else:
                shutil.copytree(single_folder_path, mod_dir, dirs_exist_ok=True)
        else:
            # General case: copy all extracted content to mod_dir
            for item in extracted_contents:
                src_path = os.path.join(temp_extract_dir, item)
                dest_path = os.path.join(mod_dir, item)
                if os.path.exists(dest_path):
                    if os.path.isdir(dest_path):
                        shutil.rmtree(dest_path)
                    else:
                        os.remove(dest_path)
                if os.path.isdir(src_path):
                    shutil.copytree(src_path, dest_path)
                else:
                    shutil.copy2(src_path, dest_path)

        log_message(f"Files for {mod_name} updated successfully.", "success")
        shutil.rmtree(temp_extract_dir)

        # Update mod_versions.json with the new version
        if os.path.exists(mod_versions_path):
            with open(mod_versions_path, "r", encoding="utf-8") as file:
                data = json.load(file)
        else:
            data = {"mods": []}

        updated = False
        for mod in data["mods"]:
            if mod["name"] == mod_name:
                mod["version"] = remote_version
                updated = True
                break
        if not updated:
            data["mods"].append({"name": mod_name, "version": remote_version, "dir": os.path.basename(mod_dir)})

        with open(mod_versions_path, "w", encoding="utf-8") as file:
            json.dump(data, file, indent=4)

        log_message(f"Updated version for {mod_name} in mod_versions.json to {remote_version}.", "success")
        return True

    except requests.RequestException as e:
        log_message(f"Failed to download mod files from {download_url}: {e}", "error")
    except Exception as e:
        log_message(f"Error updating files or version for {mod_name} in {mod_dir}: {e}", "error")
    finally:
        if temp_zip_path is not None and os.path.exists(temp_zip_path):
            os.remove(temp_zip_path)
        if temp_extract_dir is not None and os.path.exists(temp_extract_dir):
            shutil.rmtree(temp_extract_dir)
    return False

def download_mod_versions(mod_versions_path, url):
    try:
        response = requests.get(url, stream=True)
        response.raise_for_status()
        with open(mod_versions_path, "wb") as file:
            for chunk in response.iter_content(chunk_size=8192):
                file.write(chunk)
        log_message(f"mod_versions.json downloaded and saved to {mod_versions_path}.", "success")
    except requests.RequestException as e:
        log_message(f"Failed to download mod_versions.json: {e}", "error")

def check_for_updates():
    """
    Check for updates while keeping the UI responsive.
    Starts the progress bar, then stops it when finished.
    """
    def perform_update_sequence():
        from ui import start_loading, stop_loading  # Lazy import to avoid circular imports
        try:
            start_loading()  # Start the progress bar
            log_message("Starting update check...", "info")
            
            check_mod_versions()

            installer_updated = check_installer_update()
            if installer_updated:
                log_message("Installer updated. Please restart.", "info")
                stop_loading()
                # Optionally, exit or prompt the user.
            else:
                log_message("No installer update found.", "success")
                stop_loading()
            
            
        except Exception as e:
            log_message(f"Error during update check: {e}", "error")
        finally:
            # Ensure that the progress bar is stopped regardless of outcome.
            stop_loading()
        stop_loading()
    update_thread = threading.Thread(target=perform_update_sequence, daemon=True)
    update_thread.start()
