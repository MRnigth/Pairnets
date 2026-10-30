<#
.SYNOPSIS
    The cloud commands of the pre-deploy check (npm ci, npm test, wrangler dry-run), run on their own so that
    scripts/pre-deploy.ps1 can start them alongside the test suite. Output goes to the cloud step's log; the exit
    code and the seconds go to -ResultFile ("exit=<n>" and "seconds=<n>", written last).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Cloud,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Log,
    [Parameter(Mandatory = $true)][string]$ResultFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'predeploy-lib.ps1')

$watch = [Diagnostics.Stopwatch]::StartNew()
$exit = 0
try {
    foreach ($cmd in @(@('npm', 'ci'), @('npm', 'test'), @('npx', '--yes', 'wrangler', 'deploy', '--dry-run', '--outdir', $Out))) {
        $what = $cmd -join ' '
        Add-PdLogLine -Log $Log -Text ("`n### {0}" -f $what)
        $r = Invoke-PdNative -File 'cmd.exe' -Arguments (@('/d', '/c') + $cmd) -WorkingDirectory $Cloud -Log $Log -TimeoutSeconds 1800 -Quiet
        Add-PdLogLine -Log $Log -Text ("### {0}: exit {1} after {2:n0} s" -f $what, $r.ExitCode, $r.Seconds)
        if ($r.ExitCode -ne 0) {
            $exit = $r.ExitCode
            Add-PdLogLine -Log $Log -Text $(if ($r.TimedOut) { "PROBLEM: $what did not finish within 1800 seconds" } else { "PROBLEM: $what failed (exit code $($r.ExitCode))" })
            break
        }
    }
} catch {
    $exit = 1
    Add-PdLogLine -Log $Log -Text "PROBLEM: the cloud commands stopped: $($_.Exception.Message)"
}
Write-PdText $ResultFile ("exit={0}`nseconds={1}`n" -f $exit, [math]::Round($watch.Elapsed.TotalSeconds))
exit $exit
