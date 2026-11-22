# Changelog

## 1.0.1

- When Windows won't let the program it installed start (Smart App Control, or another rule that lets only approved apps run), setup says which, instead of only Windows' words. The README says what to do.
- A first install that fails takes away what it added: the program, its two shortcuts, and its folder unless settings.ini or the log is in it. 1.0.0 left the program and both shortcuts behind, so Windows tried to start it at every sign-in.
- Setup says "MouseFace installed successfully." once it has installed it, instead of closing without a word, and its window before a first install shows just its title and buttons.
- Uninstalling from Installed apps or from setup asks whether to also delete your settings (settings.ini, the saved brightness and the log). They are kept unless you tick it.
- The uninstall question, and setup's messages about how it went, show in MouseFace's own window, in larger text than Windows' standard message box, with Windows' current icons. Ctrl+C copies a message.
- An update that fails, and can't put the copy that was there back, says where that copy is saved, and a later update never writes over it. If setup couldn't list MouseFace in Installed apps, it says so after "MouseFace installed successfully.", with how to uninstall it later.
- Setup and install.cmd now work for a user whose name has letters outside the PC's language, like a Polish name on a PC set to English. 1.0.0 couldn't make its shortcuts for them, and said only "Exception has been thrown by the target of an invocation."
- Only one setup runs at a time; a second one says to wait.
- The release's SHA256SUMS.txt now checks with `sha256sum -c`. 1.0.0's ended its line the Windows way, so a good download failed the check.

## 1.0.0

First public release.

- Remaps only the chosen mouse: each click is matched to the device Windows names for it, so a touchpad or a second mouse isn't remapped.
- Recognises Evoluent mice over USB, USB receivers and both Bluetooth formats. Another mouse can be picked in Settings; its standard buttons should work, but only Evoluent mice have been tried.
- A Settings window: the mouse, each button's action, whole-name selection and dimming. Pressing a mouse button lights up its row. It saves to `settings.ini`, changing only the lines that changed and keeping the file's notes, encoding and line endings, and applies at once. An edit made in the file while the window is open shows in it.
- Actions: Left, Right and Middle click, Back and Forward, Double click (with whole-name selection), Click lock, keyboard shortcuts, and Disabled. A shortcut of a modifier and a key is pressed and let go at once; a single key, or modifiers on their own, are held for as long as the button is.
- Screen dimming with Shift and the wheel, off until turned on. It shows only while the chosen mouse is attached and MouseFace is not paused.
- `MouseFace-Setup.exe` installs, updates and uninstalls itself for the current user, with no admin rights. Or build it from source with `install.cmd`. Either way it is listed in Settings > Apps > Installed apps, where it uninstalls like any app. Neither replaces a newer version with an older one, and with User Account Control on, MouseFace won't run with admin rights. An update that fails part way puts back the copy you had, and starts it again if it was running.
- Its windows size themselves for each screen's scaling, including when moved to a screen scaled differently.
- Tray menu: Pause, Reset brightness, Settings, About, Quit.
- No polling and no network: it reacts to Windows' notifications only, and never goes online.
- Fails to a plain mouse: a crash lets go of every held button before exiting, an error in click handling passes the click through unchanged, and a button let go over an administrator window, where Windows hides it, is let go once you're back.
- Won't start beside Evoluent Mouse Manager or X-Mouse Button Control.
