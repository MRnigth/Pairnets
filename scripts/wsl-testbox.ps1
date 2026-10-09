<#
.SYNOPSIS
    The throwaway Linux test box of the pre-deploy check (see docs/PREDEPLOY.md): a WSL 2 Ubuntu 24.04
    with systemd, made fresh from a saved base image for every run.

.DESCRIPTION
    -EnsureBase  Once per computer (a few minutes): installs Ubuntu 24.04 as its own WSL distro
                 "pairnets-test-base", turns on systemd, installs curl, openssl, python3 and shellcheck,
                 saves it as base.tar and removes the distro again. Does nothing when base.tar exists
                 (add -Rebuild to make it again).
    -Fresh       Throws away the box (distro "pairnets-test") and imports a new one from base.tar, waits
                 until systemd is up and keeps it running until -Remove.
    -Stop        Stops the box but keeps it (a failed box, kept for a look: wsl -d pairnets-test starts it again).
    -Remove      Throws the box away (base.tar stays).
    -Status      Shows what exists (the default).

    Everything lives in %LOCALAPPDATA%\Pairnets-predeploy\wsl. Other WSL distros are never touched.
    A root shell in the box:  wsl -d pairnets-test

.EXAMPLE
    scripts\wsl-testbox.ps1 -EnsureBase
    scripts\wsl-testbox.ps1 -Fresh
    scripts\wsl-testbox.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$EnsureBase,
    [switch]$Rebuild,
    [switch]$Fresh,
    [switch]$Stop,
    [switch]$Remove,
    [switch]$Status,
    [string]$Name = 'pairnets-test'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'predeploy-lib.ps1')

$WslHome = Join-Path $script:PdHome 'wsl'
$BaseTar = Join-Path $WslHome 'base.tar'
$BaseName = 'pairnets-test-base'
$BaseDir = Join-Path $WslHome 'base'
$BoxDir = Join-Path $WslHome $Name
$KeepAliveFile = Join-Path $WslHome "$Name.keepalive"
$Log = Join-Path $WslHome 'testbox.log'

# Canonical's WSL root file systems, for a WSL that cannot install a second copy of a distro under its
# own name (wsl --install --name/--location). Tried in order; the first that answers wins.
$RootfsUrls = @(
    'https://cloud-images.ubuntu.com/wsl/releases/24.04/current/ubuntu-noble-wsl-amd64-24.04lts.rootfs.tar.gz',
    'https://cloud-images.ubuntu.com/wsl/releases/noble/current/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz',
    'https://cloud-images.ubuntu.com/wsl/noble/current/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz'
)

New-Item -ItemType Directory -Force -Path $WslHome | Out-Null

function Say([string]$Text) {
    Write-Host "==> $Text"
    Add-PdLogLine -Log $Log -Text ("{0:u} {1}" -f (Get-Date).ToUniversalTime(), $Text)
}

function Invoke-Wsl([string[]]$Arguments, [int]$TimeoutSeconds = 600, [switch]$Quiet) {
    return Invoke-PdNative -File 'wsl.exe' -Arguments $Arguments -Log $Log -TimeoutSeconds $TimeoutSeconds -Quiet:$Quiet
}

# A bash script run as root in a distro, passed as one argument (no second shell in between).
function Invoke-InDistro([string]$Distro, [string]$Script, [int]$TimeoutSeconds = 900, [switch]$Quiet) {
    return Invoke-Wsl -Arguments @('-d', $Distro, '-u', 'root', '--exec', 'bash', '-c', ($Script -replace "`r`n", "`n")) -TimeoutSeconds $TimeoutSeconds -Quiet:$Quiet
}

function Get-Distros {
    $r = Invoke-Wsl -Arguments @('--list', '--quiet') -TimeoutSeconds 60 -Quiet
    if ($r.ExitCode -ne 0) { return @() } # no distros at all
    return @($r.Output -split "`r?`n" | ForEach-Object { $_.Trim([char]0, ' ', "`t") } | Where-Object { $_ })
}

function Test-Distro([string]$Distro) { return (Get-Distros) -contains $Distro }

# The keep-alive sessions of this box, found by their command line (wsl.exe starts a second copy of
# itself, so the process id Start-Process returns is not the only one).
function Get-KeepAlive {
    $pattern = '-d\s+"?' + [regex]::Escape($Name) + '"?\s+-u\s+root\s+--exec\s+sleep\s+infinity'
    return @(Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match $pattern })
}

function Stop-KeepAlive {
    foreach ($p in (Get-KeepAlive)) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item $KeepAliveFile -Force -ErrorAction SilentlyContinue
}

# WSL stops a distro a few seconds after its last session ends, even with systemd running in it. A
# hidden "sleep infinity" session keeps the box up between the runner's commands.
function Start-KeepAlive {
    Stop-KeepAlive
    $p = Start-Process -FilePath 'wsl.exe' -ArgumentList @('-d', $Name, '-u', 'root', '--exec', 'sleep', 'infinity') -WindowStyle Hidden -PassThru
    Set-Content -Path $KeepAliveFile -Value $p.Id -Encoding ASCII
}

function Remove-Distro([string]$Distro, [string]$Dir) {
    if (Test-Distro $Distro) {
        Say "removing the WSL distro $Distro"
        $r = Invoke-Wsl -Arguments @('--unregister', $Distro) -TimeoutSeconds 300
        if ($r.ExitCode -ne 0) { throw "wsl --unregister $Distro failed (exit $($r.ExitCode))" }
    }
    if ($Dir -and (Test-Path $Dir)) { Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue }
}

# Waits until systemd has finished booting the distro. Returns its state ("running" is the goal).
function Wait-Systemd([string]$Distro, [int]$TimeoutSeconds = 180) {
    $r = Invoke-InDistro -Distro $Distro -TimeoutSeconds $TimeoutSeconds -Quiet -Script @'
if [ "$(cat /proc/1/comm 2>/dev/null)" != systemd ]; then echo "state=no-systemd"; exit 0; fi
state="$(timeout 150 systemctl is-system-running --wait 2>/dev/null || true)"
echo "state=${state:-unknown}"
if [ "$state" != running ]; then systemctl --failed --no-legend --plain 2>/dev/null | sed 's/^/failed-unit: /'; fi
'@
    $state = ([regex]::Match($r.Output, 'state=(\S+)')).Groups[1].Value
    if (-not $state) { $state = 'unknown' }
    foreach ($l in ($r.Output -split "`r?`n" | Where-Object { $_ -like 'failed-unit:*' })) { Say $l }
    return $state
}

function Get-InstallSupport {
    $r = Invoke-PdNative -File 'wsl.exe' -Arguments @('--help') -TimeoutSeconds 60 -Quiet # (not logged: long and localized)
    return ($r.Output -match '--location' -and $r.Output -match '--name' -and $r.Output -match '--no-launch')
}

function New-BaseByInstall {
    Say "installing Ubuntu 24.04 as $BaseName (wsl --install --name --location --no-launch)"
    $r = Invoke-Wsl -Arguments @('--install', 'Ubuntu-24.04', '--name', $BaseName, '--location', $BaseDir, '--no-launch', '--web-download') -TimeoutSeconds 1800
    if ($r.ExitCode -ne 0 -or -not (Test-Distro $BaseName)) { throw "wsl --install failed (exit $($r.ExitCode))" }
}

function New-BaseByImport {
    $download = Join-Path $WslHome 'download'
    New-Item -ItemType Directory -Force -Path $download | Out-Null
    $file = Join-Path $download 'ubuntu-24.04-wsl.rootfs.tar.gz'
    if (-not (Test-Path $file)) {
        $old = [Net.ServicePointManager]::SecurityProtocol
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ok = $false
        foreach ($url in $RootfsUrls) {
            try {
                Say "downloading $url"
                $ProgressPreference = 'SilentlyContinue'
                Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
                Move-Item -Force "$file.part" $file
                $ok = $true
                break
            } catch {
                Say "  not available: $($_.Exception.Message)"
            }
        }
        [Net.ServicePointManager]::SecurityProtocol = $old
        if (-not $ok) { throw "could not download an Ubuntu 24.04 WSL image" }
    }
    Say "importing $BaseName from $file"
    New-Item -ItemType Directory -Force -Path $BaseDir | Out-Null
    $r = Invoke-Wsl -Arguments @('--import', $BaseName, $BaseDir, $file, '--version', '2') -TimeoutSeconds 1800
    if ($r.ExitCode -ne 0) { throw "wsl --import failed (exit $($r.ExitCode))" }
}

$ConfigureScript = @'
set -euo pipefail
cat > /etc/wsl.conf <<'CONF'
# Written by scripts/wsl-testbox.ps1: the Pairnets pre-deploy test box.
[boot]
systemd=true

[user]
default=root

[interop]
appendWindowsPath=false

[automount]
root=/mnt/
CONF
# The copies never ask for a first user: the box is used as root only.
if [ -f /etc/wsl-distribution.conf ]; then
  sed -i -E '/^[[:space:]]*(command|defaultUid)[[:space:]]*=/d' /etc/wsl-distribution.conf
fi
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl openssl python3 shellcheck sudo >/dev/null
# Nothing installs or upgrades packages behind the tests' back.
systemctl disable apt-daily.timer apt-daily-upgrade.timer >/dev/null 2>&1 || true
systemctl mask unattended-upgrades.service >/dev/null 2>&1 || true
apt-get clean
rm -rf /var/lib/apt/lists/*
echo "configured: $(. /etc/os-release && echo "$PRETTY_NAME"), shellcheck $(shellcheck --version | sed -n 's/^version: //p')"
'@

$VerifyScript = @'
set -u
fail=0
for t in curl openssl python3 shellcheck systemctl journalctl systemd-run; do
  command -v "$t" >/dev/null || { echo "missing: $t"; fail=1; }
done
[ "$(cat /proc/1/comm)" = systemd ] || { echo "PID 1 is not systemd"; fail=1; }
[ "$(id -u)" = 0 ] || { echo "not root"; fail=1; }
grep -q '^appendWindowsPath=false' /etc/wsl.conf || { echo "wsl.conf: appendWindowsPath is not off"; fail=1; }
echo "kernel: $(uname -r)"
exit $fail
'@

function Invoke-EnsureBase {
    if ((Test-Path $BaseTar) -and -not $Rebuild) {
        Say "base image already there: $BaseTar (use -Rebuild to make it again)"
        return
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Remove-Distro -Distro $BaseName -Dir $BaseDir
    if (Get-InstallSupport) { New-BaseByInstall } else { Say "this WSL cannot install a named copy; using Canonical's root file system"; New-BaseByImport }

    Say "configuring $BaseName (systemd, root, packages)"
    $r = Invoke-InDistro -Distro $BaseName -Script $ConfigureScript -TimeoutSeconds 1800
    if ($r.ExitCode -ne 0) { throw "configuring the base failed (exit $($r.ExitCode)); see $Log" }

    Say "restarting $BaseName with systemd"
    Invoke-Wsl -Arguments @('--terminate', $BaseName) -TimeoutSeconds 120 -Quiet | Out-Null
    $state = Wait-Systemd -Distro $BaseName
    Say "systemd state: $state"
    if ($state -ne 'running') { throw "systemd in the base is '$state', not 'running' (failed units listed above)" }
    $r = Invoke-InDistro -Distro $BaseName -Script $VerifyScript -TimeoutSeconds 120
    if ($r.ExitCode -ne 0) { throw "the base is missing something (see above)" }
    Invoke-InDistro -Distro $BaseName -Quiet -TimeoutSeconds 120 -Script 'journalctl --rotate >/dev/null 2>&1; journalctl --vacuum-time=1s >/dev/null 2>&1; rm -rf /tmp/* /var/tmp/*; true' | Out-Null

    Say "saving $BaseTar"
    Invoke-Wsl -Arguments @('--terminate', $BaseName) -TimeoutSeconds 120 -Quiet | Out-Null
    $tmp = "$BaseTar.part"
    if (Test-Path $tmp) { Remove-Item -Force $tmp }
    $r = Invoke-Wsl -Arguments @('--export', $BaseName, $tmp) -TimeoutSeconds 1800
    if ($r.ExitCode -ne 0 -or -not (Test-Path $tmp)) { throw "wsl --export failed (exit $($r.ExitCode))" }
    Move-Item -Force $tmp $BaseTar
    Remove-Distro -Distro $BaseName -Dir $BaseDir
    $size = [math]::Round((Get-Item $BaseTar).Length / 1MB)
    Say ("base image ready: {0} ({1} MB) in {2:n0} s" -f $BaseTar, $size, $watch.Elapsed.TotalSeconds)
}

function Invoke-Fresh {
    if (-not (Test-Path $BaseTar)) { throw "no base image yet: run scripts\wsl-testbox.ps1 -EnsureBase first" }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Stop-KeepAlive
    Remove-Distro -Distro $Name -Dir $BoxDir
    Say "making a fresh box $Name from base.tar"
    New-Item -ItemType Directory -Force -Path $BoxDir | Out-Null
    $r = Invoke-Wsl -Arguments @('--import', $Name, $BoxDir, $BaseTar, '--version', '2') -TimeoutSeconds 900
    if ($r.ExitCode -ne 0) { throw "wsl --import $Name failed (exit $($r.ExitCode))" }
    Start-KeepAlive
    $state = Wait-Systemd -Distro $Name
    Say "systemd state: $state"
    if ($state -ne 'running') { throw "systemd in the box is '$state', not 'running'" }
    $r = Invoke-InDistro -Distro $Name -Script $VerifyScript -TimeoutSeconds 120 -Quiet
    if ($r.ExitCode -ne 0) { throw "the fresh box is missing something: $($r.Output)" }
    Say ("box {0} is running ({1:n0} s). A root shell: wsl -d {0}" -f $Name, $watch.Elapsed.TotalSeconds)
}

function Invoke-Stop {
    Stop-KeepAlive
    if (Test-Distro $Name) {
        Invoke-Wsl -Arguments @('--terminate', $Name) -TimeoutSeconds 120 -Quiet | Out-Null
        Say "box $Name stopped (kept; a root shell: wsl -d $Name)"
    }
}

function Invoke-Remove {
    Stop-KeepAlive
    Remove-Distro -Distro $Name -Dir $BoxDir
    Say "box $Name removed"
}

function Show-Status {
    $distros = Get-Distros
    $base = if (Test-Path $BaseTar) { "{0} ({1} MB, {2:u})" -f $BaseTar, [math]::Round((Get-Item $BaseTar).Length / 1MB), (Get-Item $BaseTar).LastWriteTimeUtc } else { 'missing (run -EnsureBase)' }
    Write-Host "base image: $base"
    Write-Host ("box {0}: {1}" -f $Name, $(if ($distros -contains $Name) { 'exists' } else { 'not there' }))
    Write-Host ("keep-alive sessions: {0}" -f @(Get-KeepAlive).Count)
    if ($distros -contains $BaseName) { Write-Host "leftover base distro $BaseName exists (re-run -EnsureBase -Rebuild)" }
}

$oldOut = $null
try { $oldOut = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { $oldOut = $null }
try {
    if ($Stop) { Invoke-Stop }
    if ($Remove) { Invoke-Remove }
    if ($EnsureBase) { Invoke-EnsureBase }
    if ($Fresh) { Invoke-Fresh }
    if ($Status -or -not ($EnsureBase -or $Fresh -or $Stop -or $Remove)) { Show-Status }
    exit 0
} catch {
    Write-Host "wsl-testbox: $($_.Exception.Message)" -ForegroundColor Red
    Add-PdLogLine -Log $Log -Text ("ERROR " + $_.Exception.Message)
    exit 1
} finally {
    if ($oldOut) { try { [Console]::OutputEncoding = $oldOut } catch { } }
}
