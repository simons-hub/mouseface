# Security

MouseFace sees every mouse click on your computer, so its security matters. If you find a problem, please report it privately: open this repository's **Security** tab and choose **Report a vulnerability**. Please don't open a public issue for a security problem.

## What MouseFace can and can't do

- It runs as you and does not ask Windows for any extra permissions. With User Account Control on, as Windows has it by default, it refuses to run if started with **Run as administrator**.
- It installs for your account only: the program in `%LOCALAPPDATA%\MouseFace`, a shortcut in your Startup folder and one in the Start menu, and its entry in Installed apps, which is its only registry entry. Uninstalling removes all of them and keeps your settings, unless you tick **Also delete my settings**. Uninstalled from Installed apps, the running program can't delete its own file, so it moves it to your temp folder for Disk Cleanup or Storage Sense to delete (or, if the temp folder is on another drive, renames it beside your settings, for the next install to delete).
- It never goes online: nothing is downloaded or uploaded, and nothing is sent anywhere.
- It watches mice through a low-level mouse hook and Raw Input for mice. No keyboard hook is installed; it only checks whether the dimming key is held when the mouse wheel turns. While the screens are dimmed, Windows also tells it when a window comes to the front or appears, so the dimming stays on top; and while a held key waits for an administrator window to go, it is told when a window comes to the front. That names the window, not what is in it.
- When a button is set to Double click with whole-name selection on, it reads up to 300 characters either side of the double-clicked word in the program you clicked, through Windows' accessibility interface (UI Automation), and widens the selection to the whole name. It skips password boxes. That text is never written to the log or any file, and never sent anywhere; only the `--watch` console shows it, on screen. If a selection fails, the log names the program, not its text.
- It sends input only as the replacements you set in settings, with one exception: pressing the Windows key in the Settings window's shortcut box sends one unused key (0xE8), so the Start menu doesn't open.
- You can build it on your own machine from this source, with the C# compiler that ships with Windows. The release download is built by GitHub from the same source.

## Supported versions

Fixes go into the latest release only.
