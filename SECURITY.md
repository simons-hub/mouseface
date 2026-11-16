# Security

MouseFace sees every mouse click on your computer, so its security matters. If you find a problem, please report it privately: open this repository's **Security** tab and choose **Report a vulnerability**. Please don't open a public issue for a security problem.

## What MouseFace can and can't do

- It runs as you and does not ask Windows for any extra permissions. Started with **Run as administrator**, it refuses to run.
- It never goes online: nothing is downloaded or uploaded, and nothing is sent anywhere.
- It watches mice only, through a low-level mouse hook and Raw Input for mice. No keyboard hook is installed; it only checks whether the dimming key is held when the mouse wheel turns.
- When a button is set to Double click with whole-name selection on, it reads up to 300 characters either side of the double-clicked word in the program you clicked, through Windows' accessibility interface (UI Automation), and widens the selection to the whole name. That text is never written to the log or any file, and never sent anywhere; only the `--watch` console shows it, on screen.
- It sends input only as the replacements you set in settings, with one exception: pressing the Windows key in the Settings window's shortcut box sends one unused key (0xE8), so the Start menu doesn't open.
- You can build it on your own machine from this source, with the C# compiler that ships with Windows. The release download is built by GitHub from the same source.

## Supported versions

Fixes go into the latest release only.
