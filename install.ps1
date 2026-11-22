<#
.SYNOPSIS
  Builds, tests and installs MouseFace for the current user. Needs no admin rights and downloads
  nothing.

.DESCRIPTION
  MouseFace is compiled here from the C# in src\, with the C# compiler that ships inside Windows
  (.NET Framework 4). Use it in stages, so you only keep it once it has worked in your hands:

      .\install.ps1 -Test        build and test; installs nothing
      .\install.ps1 -Trial       the same, then run it now, without the sign-in shortcut
      .\install.ps1              the same, then also start it at every sign-in and list it in Installed apps
      .\install.ps1 -Uninstall   stop it and remove the program, its shortcuts and its Installed apps entry

  Run with:  powershell -ExecutionPolicy Bypass -File .\install.ps1 [option]

  -UnitOnly  test only the logic, skipping the checks that need a desktop and the live test (CI).
  -Quiet     never show a message box (for scripts and AI agents); problems go to the output.
  -BuildDir  where to build (default: a temporary folder).

  Your settings.ini, log and saved brightness live in %LOCALAPPDATA%\MouseFace and are kept on
  uninstall.
#>
param(
    [switch]$Test,
    [switch]$Trial,
    [switch]$Uninstall,
    [switch]$UnitOnly,
    [switch]$Quiet,
    [string]$BuildDir
)
$ErrorActionPreference = 'Stop'

$AppName = 'MouseFace'
$root = $PSScriptRoot
$appDir = Join-Path $env:LOCALAPPDATA $AppName
$appExe = Join-Path $appDir "$AppName.exe"
# Where a shortcut goes, or $null when Windows has no such folder for this account. -Create makes
# the folder if it is missing.
function Get-Link([string]$Folder, [switch]$Create) {
    $dir = [Environment]::GetFolderPath($Folder, $(if ($Create) { 'Create' } else { 'None' }))
    if ($dir) { Join-Path $dir "$AppName.lnk" } else { $null }
}
$startupLink = Get-Link 'Startup'
$menuLink = Get-Link 'Programs'
# Only this sign-in's copy is stopped or counted: another sign-in's MouseFace is that person's.
$session = (Get-Process -Id $PID).SessionId
function Get-Running { Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session } }
# Its entry in Settings > Apps > Installed apps, under the current user. MouseFace.exe --register
# writes it, as its setup does (Installer.UninstallKey).
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"
# An uninstall from Installed apps renames the running program here when it can't move it to the
# temp folder (Installer.MoveAside).
$asideExe = "$appExe.uninstalled"

# The same text as a message box, for someone who double-clicked install.cmd. Where there is no
# desktop to show it on, the printed text is all there is.
function Show-Box([string]$Text) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show($Text, $AppName, 'OK', 'Warning')
    } catch { }
}

function Find-Compiler {
    foreach ($dir in @([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory(),
                       (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'),
                       (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'))) {
        $csc = Join-Path $dir 'csc.exe'
        if (Test-Path -LiteralPath $csc) { return $csc }
    }
    throw 'No C# compiler found. MouseFace needs the .NET Framework 4.x that ships with Windows 10 and 11.'
}

# Builds the program and, from the same sources plus tests\, the test program.
function Build-All([string]$OutDir) {
    $csc = Find-Compiler
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $src = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName)
    $tests = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter *.cs | ForEach-Object FullName)
    $assets = Join-Path $root 'assets'
    $common = @('/nologo', '/platform:anycpu', '/optimize+', '/warnaserror+', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
                "/win32icon:$(Join-Path $assets "$AppName.ico")",
                "/resource:$(Join-Path $assets "$AppName.ico"),$AppName.ico",
                "/resource:$(Join-Path $assets "$AppName-paused.ico"),$AppName-paused.ico")
    $app = Join-Path $OutDir "$AppName.exe"
    $log = & $csc @common /target:winexe "/out:$app" @src 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Out-Host; throw "Compiling $AppName failed." }
    $testExe = Join-Path $OutDir "$AppName.Tests.exe"
    $log = & $csc @common /target:exe "/main:MouseFace.Tests.TestMain" "/out:$testExe" @src @tests 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Out-Host; throw "Compiling the tests failed." }
    return @{ App = $app; Tests = $testExe }
}

# Runs one test mode with a time limit: if input injection is blocked on this machine the live
# test could never finish, and must not hang the installer with it.
function Invoke-Tests([string]$Exe, [string]$Mode) {
    $report = Join-Path ([IO.Path]::GetTempPath()) "$AppName-$($Mode.TrimStart('-')).txt"
    $p = Start-Process -FilePath $Exe -ArgumentList $Mode -PassThru -NoNewWindow -RedirectStandardOutput $report
    $null = $p.Handle # without this PowerShell reports no ExitCode after a timed wait
    if (-not $p.WaitForExit(60000)) {
        $p | Stop-Process -Force
        Get-Content -LiteralPath $report -ErrorAction SilentlyContinue | Out-Host
        throw "The $Mode tests did not finish in 60 s. Input injection is probably blocked on this machine, usually by another program's frozen mouse hook; sign out or restart, then try again. Nothing was installed."
    }
    Get-Content -LiteralPath $report | Out-Host
    Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue
    if ($p.ExitCode -eq 2) { Write-Warning "The $Mode tests were skipped (see above)."; return }
    if ($p.ExitCode -ne 0) { throw "The $Mode tests failed (exit $($p.ExitCode)). Nothing was installed." }
}

# Twenty seconds, no hook: does one harmless injected input come back at all on this machine?
function Test-SendInputWorks {
    $probe = Join-Path ([IO.Path]::GetTempPath()) "$AppName-sendcheck.ps1"
    Set-Content -LiteralPath $probe -Encoding ASCII -Value @'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class SI { [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; } [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public MI mi; } [DllImport("user32.dll")] public static extern uint SendInput(uint n, IN[] i, int s); }'
$one = New-Object 'SI+IN[]' 1; $one[0].mi.flags = 0x1000
[void][SI]::SendInput(1, $one, [Runtime.InteropServices.Marshal]::SizeOf([type]'SI+IN'))
'@
    # Quoted by hand: Start-Process passes its arguments on as they are, and a temp folder can have spaces.
    $p = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$probe`"" -PassThru -WindowStyle Hidden
    $ok = $p.WaitForExit(20000) # the first Add-Type compile can be slow
    if (-not $ok) { $p | Stop-Process -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
    return $ok
}

# Other mouse software would fight MouseFace over every click.
function Get-Conflicts([switch]$RunningOnly) {
    $found = @()
    $processes = @{ EvoMouseExec = 'Evoluent Mouse Manager'; EvoMouseListener = 'Evoluent Mouse Manager';
                    EvoluentSystemTray = 'Evoluent Mouse Manager'; XMouseButtonControl = 'X-Mouse Button Control' }
    foreach ($name in $processes.Keys) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }) { $found += "$($processes[$name]) (running)" }
    }
    if (-not $RunningOnly) {
        $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
        foreach ($app in (Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue)) {
            if ($app.DisplayName -like 'Evoluent Mouse Manager*' -or $app.DisplayName -like 'X-Mouse Button Control*') {
                $found += "$($app.DisplayName) (installed)"
            }
        }
    }
    return @($found | Sort-Object -Unique)
}

function Stop-Running {
    if (-not (Get-Running)) { return }
    if (Test-Path -LiteralPath $appExe) {
        # Asked politely first, so it lets go of any button it holds.
        & $appExe --quit | Out-Null
        for ($i = 0; $i -lt 30 -and (Get-Running); $i++) { Start-Sleep -Milliseconds 100 }
    }
    Get-Running | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

# It runs as you: started from an admin window, the copy it starts would run as administrator, and
# an admin window of another account would install or uninstall for that account, not yours.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$uacOn = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction SilentlyContinue).EnableLUA -ne 0
if (-not $UnitOnly -and $uacOn -and $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $text = "This window has admin rights, which $AppName doesn't use. Run this again from a normal window (not Run as administrator)."
    Write-Host $text
    if (-not $Quiet) { Show-Box $text }
    exit 1
}

if ($Uninstall) {
    $was = [bool]((Get-Running) -or (Test-Path -LiteralPath $appExe) -or (Test-Path -LiteralPath $uninstallKey))
    Stop-Running
    foreach ($path in @($appExe, $asideExe, "$appExe.old", $startupLink, $menuLink)) {
        if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force; $was = $true }
    }
    if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse }
    if ($was) { "$AppName stopped and removed. Your settings, log and brightness are still in $appDir; delete that folder to remove them too." }
    else { "$AppName is not installed for $env:USERNAME, so nothing was changed." }
    return
}

$conflicts = Get-Conflicts -RunningOnly:$Test
if ($conflicts.Count -gt 0) {
    $list = ($conflicts | ForEach-Object { "  - $_" }) -join "`n"
    $text = "Other mouse software is on this computer:`n`n$list`n`nIt remaps the mouse too, and the two would fight over every click. " +
            "Uninstall it first (Settings > Apps > Installed apps), restart if it asks, then run this again."
    Write-Host $text
    if (-not ($UnitOnly -or $Quiet)) { Show-Box $text }
    exit 1
}
if (Get-Service -Name 'EvoMouse*' -ErrorAction SilentlyContinue) {
    Write-Warning "Evoluent's mouse driver is still installed, though its software is not. If buttons act strangely, remove the driver: Device Manager > Mice and other pointing devices."
}
# Checked before anything changes: without these folders it could not start at sign-in.
if (-not ($Test -or $Trial)) {
    $startupLink = Get-Link 'Startup' -Create
    $menuLink = Get-Link 'Programs' -Create
    if (-not ($startupLink -and $menuLink)) {
        $text = "Windows has no Startup or Start menu folder for your account, so $AppName was not installed. Nothing was changed."
        Write-Host $text
        if (-not $Quiet) { Show-Box $text }
        exit 1
    }
}

# Never over a newer version: that one has to be uninstalled first (-Uninstall).
if (-not $Test -and (Test-Path -LiteralPath $appExe)) {
    $source = [regex]::Match((Get-Content -Raw -LiteralPath (Join-Path $root 'src\App.cs')), 'Version = "([0-9.]+)"').Groups[1].Value
    $installed = (Get-Item -LiteralPath $appExe).VersionInfo.ProductVersion
    $newer = $false
    try { $newer = [version]$installed -gt [version]$source } catch { }
    if ($newer) {
        $text = "$AppName $installed is installed, which is newer than this $source. To use $source instead, uninstall $installed first " +
                "(install.cmd -Uninstall; your settings are kept), then run this again."
        Write-Host $text
        if (-not $Quiet) { Show-Box $text }
        exit 1
    }
}

$dir = if ($BuildDir) { $BuildDir } else { Join-Path ([IO.Path]::GetTempPath()) "$AppName-build" }
if (-not $UnitOnly) {
    if (-not (Test-SendInputWorks)) {
        throw "One harmless injected input did not come back within 20 s, so input injection is blocked on this machine (usually another program's frozen mouse hook). Sign out or restart, then run this again. Nothing was changed."
    }
}
# The live test refuses to run beside a running copy, and an install replaces it. When nothing ends
# up installed (a test run, or any step that fails), the copy that was running is started again.
$restart = $false
if (-not ($Test -and $UnitOnly)) {
    $restart = [bool](Get-Running | Where-Object { $_.Path -eq $appExe })
    Stop-Running
}
function Restart-Previous {
    if ($restart -and (Test-Path -LiteralPath $appExe) -and -not (Get-Running | Where-Object { $_.Path -eq $appExe })) {
        Start-Process -FilePath $appExe -WorkingDirectory $appDir
        Write-Host "Your $AppName was restarted, as it was."
    }
}
try {
    $built = Build-All $dir
    "Built $($built.App)"
    Invoke-Tests $built.Tests '--unit'
    if (-not $UnitOnly) {
        Invoke-Tests $built.Tests '--system'
        Invoke-Tests $built.Tests '--live'
    }
} catch {
    Restart-Previous
    throw
}
if ($Test) {
    "Built and tested in $dir. Nothing was installed."
    Restart-Previous
    return
}

New-Item -ItemType Directory -Force -Path $appDir | Out-Null
$firstRun = -not (Test-Path -LiteralPath (Join-Path $appDir 'settings.ini'))
# The copy in place is kept aside until the new one has started, and put back if anything fails.
$kept = "$appExe.old"
$moved = $false # only a copy this run moved aside is ever put back, never an older leftover
# A first install that fails takes its program away again, so a later setup doesn't take it for an
# installed copy to update. Only a program this run placed, where there was none, is ever deleted.
$hadExe = Test-Path -LiteralPath $appExe
$placed = $false
try {
    Copy-Item -LiteralPath $built.App -Destination "$appExe.new" -Force
    if (Test-Path -LiteralPath $appExe) { Move-Item -LiteralPath $appExe -Destination $kept -Force; $moved = $true }
    Move-Item -LiteralPath "$appExe.new" -Destination $appExe -Force
    $placed = $true
    Remove-Item -LiteralPath $asideExe -ErrorAction SilentlyContinue
    # Started from its own folder, like the shortcuts do: a program keeps its working folder in use,
    # and this folder may be the download you want to delete afterwards.
    Start-Process -FilePath $appExe -WorkingDirectory $appDir
    Start-Sleep -Seconds 2
    if (-not (Get-Running | Where-Object { $_.Path -eq $appExe })) {
        throw "$AppName did not start; see $appDir\$AppName.log"
    }
    if (-not $Trial) {
        # One shortcut starts it at sign-in; the same one in the Start menu turns it back on by hand.
        $shell = New-Object -ComObject WScript.Shell
        foreach ($path in @($startupLink, $menuLink)) {
            $lnk = $shell.CreateShortcut($path)
            $lnk.TargetPath = $appExe
            $lnk.WorkingDirectory = $appDir
            $lnk.Description = "$AppName`: mouse button remapper and screen dimmer"
            $lnk.Save()
        }
    }
    Remove-Item -LiteralPath $kept -ErrorAction SilentlyContinue
} catch {
    $failure = $_
    Remove-Item -LiteralPath "$appExe.new" -ErrorAction SilentlyContinue
    if ($moved -and (Test-Path -LiteralPath $kept)) {
        try {
            Stop-Running # the new copy, if it got as far as starting
            Move-Item -LiteralPath $kept -Destination $appExe -Force
            Write-Host "The $AppName you had is back in place."
        } catch { Write-Warning "Could not put back the $AppName you had ($($_.Exception.Message)); it is in $kept." }
    } elseif ($placed -and -not $hadExe) {
        try {
            # The new copy, if it got as far as starting (one Windows blocked never did, and can't be asked to quit).
            if (Get-Running | Where-Object { $_.Path -eq $appExe }) { Stop-Running }
            Remove-Item -LiteralPath $appExe -Force
            Write-Host "Nothing was installed."
        } catch { Write-Warning "Could not remove the $AppName this left in $appExe ($($_.Exception.Message)); delete it by hand." }
    }
    Restart-Previous
    throw $failure
}
if ($Trial) {
    if ($startupLink -and (Test-Path -LiteralPath $startupLink)) {
        "$AppName is running now. It was installed before, so it also starts at sign-in. Its icon is in the tray; Quit is in its menu."
    } else {
        "$AppName is running now, for this sign-in only. Its icon is in the tray; Quit is in its menu. To remove it: install.cmd -Uninstall."
    }
} else {
    "$AppName is running and starts at every sign-in (turn that off in Settings > Apps > Startup)."
    "To turn it back on by hand: Start menu > $AppName."
}
# Listed in Installed apps, where it uninstalls like any app. A trial only refreshes the entry an
# earlier install made, so it shows the version now in place. Out-String waits for the program.
if (-not $Trial -or (Test-Path -LiteralPath $uninstallKey)) {
    $said = & $appExe --register | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Warning "$AppName works, but is not listed in Installed apps: $($said.Trim())" }
    elseif (-not $Trial) { "To uninstall it: Settings > Apps > Installed apps." }
}
if ($firstRun) { "The Settings window is open: choose what each button does. Nothing is remapped until you save." }
# Its own word that it started, if it has written it yet (a first start can take a moment).
Get-Content -LiteralPath (Join-Path $appDir "$AppName.log") -Tail 1 -ErrorAction SilentlyContinue | Out-Host
