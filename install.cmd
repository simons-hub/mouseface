@echo off
rem Double-click to build MouseFace from this folder's source, test it, and install it for you.
rem From a command prompt it also takes -Trial (this sign-in only), -Test or -Uninstall.
rem Opened straight from the ZIP, Windows copies out this one file and nothing else, so say so.
if not exist "%~dp0install.ps1" (
    echo This needs the whole folder. Close this window, right-click the ZIP file, choose
    echo Extract All, then double-click install.cmd in the folder that appears.
    echo.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
