# main.py
from ui import main_menu
from config import initialize_directories

def main():
    initialize_directories()
    main_menu()

if __name__ == "__main__":
    main()
