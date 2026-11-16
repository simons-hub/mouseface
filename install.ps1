<#
.SYNOPSIS
  Builds, tests and installs MouseFace for the current user. Needs no admin rights, downloads
  nothing and writes nothing to the registry.

.DESCRIPTION
  MouseFace is compiled here from the C# in src\, with the C# compiler that ships inside Windows
  (.NET Framework 4). Use it in stages, so you only keep it once it has worked in your hands:

      .\install.ps1 -Test        build and test; installs nothing
      .\install.ps1 -Trial       the same, then run it now, for this sign-in only
      .\install.ps1              the same, then also start it at every sign-in
      .\install.ps1 -Uninstall   stop it and remove the program and its shortcuts

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
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk"
$menuLink = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

function Show-Box([string]$Text) {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($Text, $AppName, 'OK', 'Warning')
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
    $p = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probe -PassThru -WindowStyle Hidden
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
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) { $found += "$($processes[$name]) (running)" }
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
    if (-not (Get-Process -Name $AppName -ErrorAction SilentlyContinue)) { return }
    if (Test-Path -LiteralPath $appExe) {
        # Asked politely first, so it lets go of any button it holds.
        & $appExe --quit | Out-Null
        for ($i = 0; $i -lt 30 -and (Get-Process -Name $AppName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
    }
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

if ($Uninstall) {
    Stop-Running
    foreach ($path in @($startupLink, $menuLink, $appExe)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    "$AppName stopped and removed. Your settings, log and brightness are still in $appDir; delete that folder to remove them too."
    return
}

$conflicts = Get-Conflicts -RunningOnly:$Test
if ($conflicts.Count -gt 0) {
    $list = ($conflicts | ForEach-Object { "  - $_" }) -join "`n"
    $text = "Other mouse software is on this computer:`n`n$list`n`nIt remaps the mouse too, and the two would fight over every click. " +
            "Uninstall it first (Settings > Apps > Installed apps), restart if it asks, then run this again."
    if (-not ($UnitOnly -or $Quiet)) { Show-Box $text }
    Write-Host $text
    exit 1
}
if (Get-Service -Name 'EvoMouse*' -ErrorAction SilentlyContinue) {
    Write-Warning "Evoluent's mouse driver is still installed, though its software is not. If buttons act strangely, remove the driver: Device Manager > Mice and other pointing devices."
}

# It runs as you: started from an admin window, the copy it starts would run as administrator.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$uacOn = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction SilentlyContinue).EnableLUA -ne 0
if (-not $UnitOnly -and $uacOn -and $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $text = "This window has admin rights, which $AppName doesn't use. Run this again from a normal window (not Run as administrator)."
    if (-not $Quiet) { Show-Box $text }
    Write-Host $text
    exit 1
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
        if (-not $Quiet) { Show-Box $text }
        Write-Host $text
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
# up installed (a test run, or a build or test that fails), the copy that was running is started again.
$restart = $false
if (-not ($Test -and $UnitOnly)) {
    $restart = [bool](Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe })
    Stop-Running
}
function Restart-Previous {
    if ($restart) {
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
Copy-Item -LiteralPath $built.App -Destination $appExe -Force
# Started from its own folder, like the shortcuts do: a program keeps its working folder in use,
# and this folder may be the download you want to delete afterwards.
Start-Process -FilePath $appExe -WorkingDirectory $appDir
Start-Sleep -Seconds 2
if (-not (Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe })) {
    throw "$AppName did not start; see $appDir\$AppName.log"
}
if ($Trial) {
    "$AppName is running now, for this sign-in only. Its icon is in the tray; Quit is in its menu."
} else {
    # One shortcut starts it at sign-in; the same one in the Start menu turns it back on by hand.
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @($startupLink, $menuLink)) {
        $lnk = $shell.CreateShortcut($path)
        $lnk.TargetPath = $appExe
        $lnk.WorkingDirectory = $appDir
        $lnk.Description = "$AppName`: mouse button remapper and screen dimmer"
        $lnk.Save()
    }
    "$AppName is running and starts at every sign-in (turn that off in Settings > Apps > Startup)."
    "To turn it back on by hand: Start menu > $AppName."
}
if ($firstRun) { "The Settings window is open: choose what each button does. Nothing is remapped until you save." }
Get-Content -LiteralPath (Join-Path $appDir "$AppName.log") -Tail 1 | Out-Host
