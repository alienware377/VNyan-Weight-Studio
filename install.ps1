# Weight Studio + Weight Studio Reader installer for VNyan
# - Looks for your VNyan folder (a running VNyan, VNyan's own log, Steam, installed apps, itch),
#   then lets you confirm it or open your VNyan folder yourself
# - Installs both plugins: Weight Studio (the weight painter) and Weight Studio Reader (loads .vbx)
# - Only asks for admin rights if your VNyan folder is actually write-protected
# Usage: double-click install.bat (or: powershell -File install.ps1)

param(
    [string]$Target = "",     # VNyan folder (skips the search and the folder picker)
    [switch]$Yes,             # answer yes to every question (unattended install)
    [switch]$ListFound,       # only print the VNyan folders the search finds, then exit
    [switch]$Elevated         # internal: set when relaunched with admin rights
)

$ErrorActionPreference = "Stop"
# Folder = the plugin folder inside Items\Assemblies (and in the release zip);
# RepoDist = where the same files sit in the source repository
$plugins = @(
    @{ Name = "Weight Studio";        Folder = "WeightStudio";       RepoDist = "dist";
       Files = @("VNyanWeightStudio.dll", "WeightStudio.vnobj") },
    @{ Name = "Weight Studio Reader"; Folder = "WeightStudioReader"; RepoDist = "Reader\dist";
       Files = @("VNyanWeightStudioReader.dll", "WeightStudioReader.vnobj") }
)

function Ask([string]$question, [bool]$defaultYes) {
    if ($Yes) { return $true }
    $a = Read-Host $question
    if ($a -eq "") { return $defaultYes }
    return ($a -match "^[Yy]")
}

function Stop-Here([int]$code) {
    if (-not $Yes) { Read-Host "Press Enter to close" | Out-Null }
    exit $code
}

# an unexpected error stays on screen instead of the window just closing
trap {
    Write-Host ""
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "The install stopped here. Fix the problem above, then run install.bat again."
    Stop-Here 1
}

# ---- find VNyan by itself (it has no installer, so there is no fixed place to look) ----
function Find-VNyan {
    $found = New-Object System.Collections.Generic.List[string]
    $add = {
        param($dir)
        if (-not $dir) { return }
        try { $dir = [IO.Path]::GetFullPath(([string]$dir -replace '/', '\').TrimEnd('\')) } catch { return }
        if ((Test-Path -LiteralPath (Join-Path $dir "VNyan.exe")) -and -not $found.Contains($dir)) { $found.Add($dir) }
    }

    # 1. a VNyan that is open right now
    try { Get-Process -Name "VNyan" -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Path) { & $add (Split-Path $_.Path) } } } catch {}

    # 2. VNyan's own log remembers where it ran from last time
    $logDir = Join-Path $env:USERPROFILE "AppData\LocalLow\Suvidriel\VNyan"
    foreach ($log in @("Player.log", "Player-prev.log")) {
        try {
            $p = Join-Path $logDir $log
            if (Test-Path -LiteralPath $p) {
                foreach ($line in (Get-Content -LiteralPath $p -TotalCount 80)) {
                    if ($line -match "Mono path\[0\] = '(.+?)[\\/]VNyan_Data[\\/]Managed'") { & $add $Matches[1]; break }
                }
            }
        } catch {}
    }

    # 3. Steam libraries
    try {
        $steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
        if ($steam) {
            $libs = @($steam)
            $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                    $libs += ($m.Groups[1].Value -replace '\\\\', '\')
                }
            }
            foreach ($lib in $libs) { & $add (Join-Path $lib "steamapps\common\VNyan") }
        }
    } catch {}

    # 4. anything Windows lists under installed apps
    foreach ($root in @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
                        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
                        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")) {
        try {
            Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
                $k = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                if ($k -and $k.DisplayName -like "*VNyan*" -and $k.InstallLocation) { & $add $k.InstallLocation }
            }
        } catch {}
    }

    # 5. the itch.io app
    try {
        $itch = Join-Path $env:APPDATA "itch\apps"
        if (Test-Path -LiteralPath $itch) {
            Get-ChildItem -LiteralPath $itch -Directory -Filter "*vnyan*" -ErrorAction SilentlyContinue | ForEach-Object {
                & $add $_.FullName
                Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object { & $add $_.FullName }
            }
        }
    } catch {}

    return $found.ToArray()
}

# A folder near the right one still counts: VNyan_Data or Items picked by mistake (walk up),
# or the folder that holds the VNyan folder (look one level down).
function Resolve-VNyanFolder([string]$dir) {
    if (-not $dir) { return $null }
    $d = $dir
    for ($i = 0; $i -lt 4 -and $d; $i++) {
        if (Test-Path -LiteralPath (Join-Path $d "VNyan.exe")) { return $d }
        $d = Split-Path $d -Parent
    }
    $child = Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "VNyan.exe") } | Select-Object -First 1
    if ($child) { return $child.FullName }
    return $null
}

if ($ListFound) {
    $f = @(Find-VNyan)
    if ($f.Count -eq 0) { Write-Host "No VNyan folder found." } else { $f | ForEach-Object { Write-Host $_ } }
    exit 0
}

# ---- locate the plugin files next to this script (release zip layout OR repo layout) ----
$here = Split-Path -Parent $PSCommandPath
foreach ($pl in $plugins) {
    $pl.Payload = $null
    foreach ($base in @((Join-Path $here $pl.Folder), (Join-Path $here $pl.RepoDist))) {
        if (Test-Path -LiteralPath (Join-Path $base $pl.Files[0])) { $pl.Payload = $base; break }
    }
    if (-not $pl.Payload) {
        Write-Host ("ERROR: the " + $pl.Folder + " folder wasn't found next to this script.") -ForegroundColor Red
        Write-Host "Unzip the whole download first, then run install.bat from inside the unzipped folder."
        Stop-Here 1
    }
    foreach ($f in $pl.Files) {
        if (-not (Test-Path -LiteralPath (Join-Path $pl.Payload $f))) {
            Write-Host ("ERROR: " + $f + " is missing from " + $pl.Payload) -ForegroundColor Red
            Stop-Here 1
        }
    }
}

# ---- pick the VNyan folder ----
if (-not $Target) {
    Write-Host ""
    Write-Host "Weight Studio installer" -ForegroundColor Cyan
    Write-Host "-----------------------"
    Write-Host "Looking for VNyan..."
    $found = @(Find-VNyan)
    if ($found.Count -eq 1) {
        Write-Host ("Found VNyan at: " + $found[0])
        if (Ask "Install there? [Y] yes / [n] open a different folder" $true) { $Target = $found[0] }
    } elseif ($found.Count -gt 1) {
        Write-Host "Found VNyan in more than one place:"
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $found[$i]) }
        if ($Yes) { $Target = $found[0] }
        else {
            $a = Read-Host "Type a number to install there, or just press Enter to open a different folder"
            $n = 0
            if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $found.Count) { $Target = $found[$n - 1] }
        }
    } else {
        Write-Host "Couldn't find VNyan by myself, but that's okay."
    }

    if (-not $Target) {
        if ($Yes) { Write-Host "ERROR: no VNyan folder found. Run again with -Target <your VNyan folder>." -ForegroundColor Red; exit 1 }
        Write-Host ""
        Write-Host "Please open your VNyan folder (the one with VNyan.exe inside it)."
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Open your VNyan folder (the one with VNyan.exe inside it)"
        $dlg.ShowNewFolderButton = $false
        if ($found.Count -gt 0) { $dlg.SelectedPath = $found[0] }
        $owner = New-Object System.Windows.Forms.Form
        $owner.TopMost = $true   # otherwise the picker can open hidden behind this window
        $ok = $dlg.ShowDialog($owner)
        $owner.Dispose()
        if ($ok -ne [System.Windows.Forms.DialogResult]::OK) { Write-Host "Cancelled."; Stop-Here 1 }
        $Target = $dlg.SelectedPath
    }
}

# ---- sanity-check the chosen folder ----
$resolved = Resolve-VNyanFolder $Target
if ($resolved) { $Target = $resolved }
else {
    Write-Host ""
    Write-Host "Hmm, there's no VNyan.exe in '$Target'." -ForegroundColor Yellow
    if (-not (Ask "Install there anyway? [y/N]" $false)) { Write-Host "Cancelled."; Stop-Here 1 }
}
$assemblies = Join-Path $Target "Items\Assemblies"

# ---- VNyan locks plugin files while it's open ----
while (Get-Process -Name "VNyan" -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "VNyan is open, and it locks its plugin files. Please close it first." -ForegroundColor Yellow
    if ($Yes) { exit 1 }
    Read-Host "Press Enter once VNyan is closed" | Out-Null
}

# ---- admin rights only if the folder really needs them ----
function Test-Writable([string]$dir) {
    try {
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $probe = Join-Path $dir ("_probe_" + [Guid]::NewGuid().ToString("N") + ".tmp")
        [IO.File]::WriteAllText($probe, "x")
        Remove-Item -LiteralPath $probe -Force
        return $true
    } catch { return $false }
}

if (-not (Test-Writable $assemblies)) {
    if ($Elevated) {
        Write-Host "ERROR: still can't write to '$assemblies', even with admin rights." -ForegroundColor Red
        Stop-Here 1
    }
    Write-Host ""
    Write-Host "Your VNyan folder is write-protected, so Windows will ask for admin rights..." -ForegroundColor Yellow
    # Admin windows can't always see the drive this was unzipped on (mapped, network or cloud
    # drives), so hand them a copy in a folder every account can read.
    $stage = Join-Path $env:ProgramData ("WeightStudioInstall_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    foreach ($pl in $plugins) {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage $pl.Folder) | Out-Null
        foreach ($f in $pl.Files) {
            Copy-Item -LiteralPath (Join-Path $pl.Payload $f) -Destination (Join-Path $stage $pl.Folder) -Force
        }
    }
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $stage "install.ps1") -Force
    # a trailing \ (for example a drive root like D:\) would escape the closing quote and
    # swallow -Elevated, so double it
    $targetArg = $Target
    if ($targetArg.EndsWith("\")) { $targetArg += "\" }
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$(Join-Path $stage 'install.ps1')`"", "-Target", "`"$targetArg`"", "-Elevated")
    if ($Yes) { $argList += "-Yes" }
    try { Start-Process powershell -Verb RunAs -ArgumentList $argList }
    catch { Write-Host "Admin rights were declined, so nothing was installed." -ForegroundColor Yellow; Stop-Here 1 }
    exit 0
}

# ---- install ----
Write-Host ""
foreach ($pl in $plugins) {
    $dstDir = Join-Path $assemblies $pl.Folder
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
    foreach ($f in $pl.Files) {
        $from = Join-Path $pl.Payload $f
        $to = Join-Path $dstDir $f
        Copy-Item -LiteralPath $from -Destination $to -Force
        try { Unblock-File -LiteralPath $to } catch {}
        if ((Get-FileHash -LiteralPath $from).Hash -ne (Get-FileHash -LiteralPath $to).Hash) {
            Write-Host "ERROR: $f didn't copy correctly." -ForegroundColor Red
            Stop-Here 1
        }
    }
    Write-Host ("  installed " + $pl.Name + " to " + $dstDir) -ForegroundColor Green
}

# the admin copy made above is no longer needed
if ($Elevated -and $here -like (Join-Path $env:ProgramData "WeightStudioInstall_*")) {
    try { Remove-Item -LiteralPath $here -Recurse -Force } catch {}
}

Write-Host ""
Write-Host "Done!" -ForegroundColor Cyan
Write-Host "Start VNyan and open Weight Studio (or Weight Studio Reader) from the Plugins window."
Write-Host "No plugin buttons? Turn on Settings > Misc > Allow Third Party Plugins in VNyan."
Write-Host "Docs: https://github.com/alienware377/VNyan-Weight-Studio"
Stop-Here 0
