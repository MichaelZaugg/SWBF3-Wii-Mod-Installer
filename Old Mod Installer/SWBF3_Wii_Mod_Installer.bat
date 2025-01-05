@echo off
title SWBIII-Wii-Mod-Installer
rem Startup and get initial directory
echo This was tested with build r2.91120a
echo.
echo Instructions:
echo.
echo Please download the unpacked version and rename "Battlefront III r2.91120a Unpacked" with "Battlefront_III_r2.91120a_Unpacked"
echo.
echo Run Dolphin at least once before installing these mods
echo.
echo Please make sure to download and extract Mods.zip into the game directory.
echo.
echo Currently Supported Mods:
echo Muted Blank Audio 
echo 4k texture pack Part 1, 2, 3, 4
echo Lighting Fix
echo Updated Debug Menu (main.dol from Clonetrooper163)
echo Cloth Fix
echo 4k Characters/Model Fix
echo Texture Pack: Faithful Health Bars
echo Dynamic Input Textures
echo.
echo If you've already done this, continue
pause
set game_dir=%cd%
set mod_dir=%game_dir%\Mods

if not exist "%mod_dir%" (
    color 0C     
    echo Error: Mods directory does not exist.
    echo Please create a "Mods" folder with all the mods extracted into it in the same directory as this installer.
    pause

    color 07
    exit
)

if exist "%mod_dir%\Mods" (
    set mod_dir="%game_dir%\Mods\Mods"
)

:startup
cls
set "folderName=Dolphin Emulator"
set "rootDir=C:\Users"
echo.
echo Searching for "AppData\%folderName%"...

for /d %%i in ("%rootDir%\*") do (
    if exist "%%i\AppData\Roaming\%folderName%" (
        echo Found "AppData\%folderName%" in user: %%~nxi
        set user=%%~nxi
	goto :ConfirmAppData
    ) else (
        echo "AppData\%folderName%" not found in user: %%~nxi
    )
)

if not defined user (
    color 0C
    echo.
    echo Current User: %username%
    echo.
    echo Error: "AppData\%folderName%" not found in any user directory.
    echo Please make sure that Dolphin is setup and running from the right user.
    echo.
    goto :isitPortable
)

:isitPortable
    echo Are you using the portable version of Dolphin or have a custom AppData location?
    echo.
    set /p choice=[Y/N]:

    if "%choice%"=="Y" goto :CustomAppData
    if "%choice%"=="y" goto :CustomAppData

    echo.
    echo Please double check your AppData folder.
    pause
    color 07
    exit



:ConfirmAppData
set app_data=""
echo.
echo Game Directory: %game_dir%
echo.
rem Check if the AppData directory exists for the selected user
set app_data=C:\Users\%user%\AppData\Roaming\Dolphin Emulator

rem If the directory doesn't exist, change color and display the error message
if not exist "%app_data%" (
    color 0C     
    echo Error: The AppData\Dolphin Emulator directory for user %user% does not exist.
    echo Make sure Dolphin has been setup and ran once and Dolphin Emulator in AppData is correct
    pause

    color 07
    goto :startup
)
goto :SetAppData


:CustomAppData
color 07
cls
echo Please enter the Dolphin path to "Dolphin-x64\User\" or other custom path (custom might not work)
echo.
set /p custompath=Path: 
set app_data=%custompath%
goto :SetAppData

:SetAppData
echo AppData or custom path Directory: %app_data%

if not exist "%app_data%\Load\Textures" (
   cd /d %app_data%\
   mkdir Load
   cd /d %app_data%\Load\
   mkdir Textures
)

echo Making RABAZZ folder in \Load\Textures
cd /d %app_data%\Load\Textures
mkdir RABAZZ
echo RABAZZ folder created

set app_data_texture=%app_data%\Load\Textures\RABAZZ

timeout /t 7 /nobreak >nul

call :CheckInstallation

goto Menu

:Menu
rem -------------------------MENU-------------------------------
cls
echo Game Directory: %game_dir%
echo Mods Directory: %mod_dir%
echo AppData Directory: %app_data%
echo.
echo Open Dolphin, go to Graphics, Advanced, Utility and enable Load Custom Textures. Close the graphics window.
echo.
echo Select which mod to install
echo.
echo 1: Muted Blank Audio
echo 2: 4k texture pack Part 1, 2, 3, 4
echo 3: Lighting Fix
echo 4: Updated Debug Menu
echo 5: Cloth Fix
echo 6: 4k Characters/Model Fix
echo 7: Texture Pack: Faithful Health Bars
echo 8: Dynamic Input Textures
echo 9: Check Mod Folder
echo Type: REPAIR to repair all mods or install all mods at once
echo.
set /p choice=Enter your choice:

rem Simulate a switch-case using if-else
if "%choice%"=="1" goto :InstallMutedAudio
if "%choice%"=="2" goto :InstallTexturePack
if "%choice%"=="3" goto :InstallLightingFix
if "%choice%"=="4" goto :InstallDebugMenu
if "%choice%"=="5" goto :InstallClothFix
if "%choice%"=="6" goto :InstallCharacterFix
if "%choice%"=="7" goto :InstallHealthBars
if "%choice%"=="8" goto :InstallInputTextures
if "%choice%"=="9" goto :CheckInstallation
if "%choice%"=="REPAIR" goto :Repair

rem If invalid input, show an error and restart the menu
echo Invalid choice. Please try again.
pause
goto :Menu


:Repair
call :RepairFunction
goto :Menu

:InstallMutedAudio
echo Installing Muted Blank Audio...
call :MutedAudioFunction
goto :Menu

:InstallTexturePack
echo Installing 4k Texture Pack Part 1, 2, 3, 4...
call :TexturePackFunction
goto :Menu

:InstallLightingFix
echo Installing Lighting Fix...
call :LightingFixFunction
goto :Menu


:InstallDebugMenu
echo Installing Updated Debug Menu...
call :DebugMenuFunction
goto :Menu

:InstallClothFix
echo Installing Cloth Fix...
call :ClothFixFunction
goto :Menu

:InstallCharacterFix
echo Installing 4k Characters/Model Fix...
call :CharacterFixFunction
goto :Menu

:InstallHealthBars
echo Installing Texture Pack: Faithful Health Bars...
call :HealthBarsFunction
goto :Menu

:CheckInstallation
echo Checking Mod installations...
call :CheckModInstall
goto :Menu

:InstallInputTextures
echo Installing Dynamic Input Textures
call :InputTexturesFunction
goto :Menu

rem ----------------functions-----------------------------


:RepairFunction
cls
echo Warning: This will erase all mods currently installed and reinstall them.
echo.
echo Please wait for the "---Repair/InstallComplete---"
echo.
set /p choice=Continue? [Y/N]:
if "%choice%"=="N" goto :eof
if "%choice%"=="n" goto :eof

cd /d %app_data%
rmdir /s /q "%app_data%\Load\DynamicInputTextures"
rmdir /s /q "%app_data%\Load\Textures"
mkdir "%app_data%\Load\Textures"
cd /d %game_dir%

call :InputTexturesFunction
call :MutedAudioFunction
call :TexturePackFunction
call :LightingFixFunction
call :DebugMenuFunction
call :ClothFixFunction
call :CharacterFixFunction
call :HealthBarsFunction

echo.
echo ---Repair/InstallComplete---
timeout /t 5 /nobreak >nul
goto: eof




:InputTexturesFunction
rem Installation for dynamic input textures
cd /d %app_data%\Load\

if not exist "%app_data%\Load\DynamicInputTextures" (
    mkdir DynamicInputTextures
)
if not exist "%app_data%\Load\DynamicInputTextures\RABAZZ" (
    cd /d %app_data%\Load\DynamicInputTextures
    mkdir RABAZZ
)

xcopy "%mod_dir%\DynamicInputTextures\DynamicInputTextures\*" "%app_data%\Load\DynamicInputTextures\" /E /H /Y
xcopy "%mod_dir%\RABAZZ_dynamic\RABAZZ\*" "%app_data%\Load\DynamicInputTextures\RABAZZ\" /E /H /Y
echo.
echo -----DONE-----
timeout /t 3 /nobreak >nul
goto :eof



:MutedAudioFunction
rem Add actual commands for Muted Audio installation here.
cd /d %mod_dir%
xcopy "%mod_dir%\SWBF3_Wii_Muted_Blank_Sounds\*" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\" /E /H /Y
echo.
echo -----DONE-----
timeout /t 3 /nobreak >nul
goto :eof




:TexturePackFunction
rem Add actual commands for Texture Pack installation here.
cd /d %mod_dir%
xcopy "%mod_dir%\4kTexturePacks\*" "%app_data_texture%\" /E /H /Y
echo.
echo -----DONE-----

timeout /t 3 /nobreak >nul
goto :eof




:LightingFixFunction
rem Add actual commands for Lighting Fix installation here.
cd /d %mod_dir%
xcopy "%mod_dir%\EmbeddedResCompiler\*" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files" /E /H /Y

xcopy "%mod_dir%\SWBF3_Wii_Light_Fixes\*" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files" /E /H /Y
echo.
echo Please wait for compiler to finish
cd /d %game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files\
call compile_all_res.bat
cd /d %game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files\data\bf\mgrsetup
powershell -Command "(Get-Content scene_descriptors.res) -replace '0.118128', '0.118128' | Set-Content scene_descriptors.res"

echo scene_descriptor updated

echo Please wait for compiler to finish
cd /d %game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files\
call compile_all_res.bat
echo.
echo ------DONE-------
timeout /t 3 /nobreak >nul

goto :eof






:DebugMenuFunction
rem Add actual commands for Debug Menu installation here.

cd /d %mod_dir%
copy /Y "main.dol" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\sys\"
echo.
echo -----DONE-----
timeout /t 3 /nobreak >nul
goto :eof



:ClothFixFunction
rem Add actual commands for Cloth Fix installation here.

cd /d %mod_dir%
xcopy "%mod_dir%\Battlefront_III_Cloth_Fix\Battlefront_III_Cloth_Fix\*" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files" /E /H /Y

xcopy "%mod_dir%\EmbeddedResCompiler\*" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files" /E /H /Y

echo.
echo Please wait for compiler to finish
cd /d %game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files\
call compile_all_res.bat
echo.
echo ------DONE-------
timeout /t 3 /nobreak >nul
goto :eof






:CharacterFixFunction
rem Add actual commands for Character Fix installation here.
cd /d %mod_dir%
xcopy "%mod_dir%\characters\*" "%app_data_texture%\characters\" /E /H /Y
xcopy "%mod_dir%\ingame_models_x1_x2_new\*" "%game_dir%\Battlefront_III_r2.91120a_Unpacked\DATA\files" /E /H /Y
echo.
echo -----DONE-----
timeout /t 3 /nobreak >nul
goto :eof






:HealthBarsFunction
cls
cd /d %mod_dir%
xcopy "%mod_dir%\_faithful_hpbars_v2\*" "%app_data_texture%\_faithful_hpbars_v2\" /E /H /Y
echo.
echo -----DONE-----
timeout /t 3 /nobreak >nul

goto :eof



:CheckModInstall
cls
echo -----Checking Mod Folder-----
cd /d %mod_dir%
if not exist "%mod_dir%\_faithful_hpbars_v2\health_bar\" (
    echo.
    echo Error: _faithful_hpbars_v2 not extracted correctly
    echo.
) else (
    echo Directory found: _faithful_hpbars_v2\health_bar
)

if not exist "%mod_dir%\Battlefront_III_Cloth_Fix\Battlefront_III_Cloth_Fix\assets\bf\" (
    echo.
    echo Error: Battlefront_III_Cloth_Fix not extracted correctly
    echo.
) else (
    echo Directory found: Battlefront_III_Cloth_Fix\assets\bf
)

if not exist "%mod_dir%\characters\characters\lando\" (
    echo.
    echo Error: characters not extracted correctly
    echo.
) else (
    echo Directory found: characters\lando
)

if not exist "%mod_dir%\EmbeddedResCompiler\data\bf\mgrsetup\" (
    echo.
    echo Error: EmbeddedResCompiler not extracted correctly
    echo.
) else (
    echo Directory found: EmbeddedResCompiler\data\bf\mgrsetup
)

if not exist "%mod_dir%\ingame_models_x1_x2_new\assets\bf\ob_wi_v194\" (
    echo.
    echo Error: ingame_models_x1_x2_new not extracted correctly
    echo.
) else (
    echo Directory found: ingame_models_x1_x2_new\assets\bf\ob_wi_v194
)


if not exist "%mod_dir%\SWBF3_Wii_Light_Fixes\data\bf\setup\invisible_hand\" (
    echo.
    echo Error: SWBF3_Wii_Light_Fixes not extracted correctly
    echo.
) else (
    echo Directory found: SWBF3_Wii_Light_Fixes\data\bf\setup\invisible_hand
)

if not exist "%mod_dir%\SWBF3_Wii_Muted_Blank_Sounds\DATA\files\" (
    echo.
    echo Error: SWBF3_Wii_Muted_Blank_Sounds not extracted correctly
    echo.
) else (
    echo Directory found: SWBF3_Wii_Muted_Blank_Sounds\DATA\files
)

if exist "%mod_dir%\4kTexturePacks\kas\" (
    echo Directory found: 4kTexturePacks\kas
) else (
    echo.
    echo Error: 4kTexturePacks not extracted correctly
    echo.
)

if not exist "%mod_dir%\main.dol" (
    echo.
    echo Error: main.dol Updated debug menu not extracted correctly
    echo.
) else (
    echo File found: main.dol
)

if not exist "%mod_dir%\DynamicInputTextures\DynamicInputTextures\" (
    echo.
    echo Error: DynamicInputTextures not extracted correctly
    echo.
) else (
    echo Directory found: DynamicInputTextures
)

if not exist "%mod_dir%\RABAZZ_dynamic\RABAZZ\" (
    echo.
    echo Error: DynamicInputTextures RABAZZ folder not extracted correctly
    echo.
) else (
    echo Directory found: DynamicInputTextures RABAZZ Folder
)

echo.
echo Check Complete, No Errors mean everything is correctly extracted
echo If you have Errors: You may continue if you know what you're doing, otherwise certain mods will not work
timeout /t 20 

goto :eof
