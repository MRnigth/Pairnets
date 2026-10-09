<#
.SYNOPSIS
    The pre-deploy check (docs/PREDEPLOY.md): tests one commit of Pairnets everywhere before it may be
    deployed, and writes the pass record the deploy gate looks for.

.DESCRIPTION
    Runs every step in a clean temporary checkout of the commit (a git worktree, removed afterwards):

      0 preflight       tools, disk space, free ports
      1 line-endings    no Windows line ends in the files Linux runs
      2 secrets         scripts/check-secrets.sh --tree and --history
      3 gate            the deploy gate's own tests (scripts/deploy-gate, when it is there)
      4 build           dotnet build Pairnets.sln -c Release, and the Windows screenshot tool
      5 publish         the server for Linux (packaged into two update feeds) and Windows, and the Windows app
      6 render-screens  every Windows window, light and dark
      7 tests           the whole test suite (250 convergence seeds), against the published Windows server too
      8 linux-box       a throwaway WSL Ubuntu: install, the API tour and website tests against it, update,
                        upgrade from the published release, shellcheck
      9 cloud           cloud/ tests and a dry-run deploy (when cloud/package.json is there)

    Only one run at a time (others wait). Writes a report folder under
    %LOCALAPPDATA%\Pairnets-predeploy\reports, and only after a full pass the record
    %LOCALAPPDATA%\Pairnets-predeploy\stamps\<sha>.pass. Exit code 0 only when everything passed.

.PARAMETER Repo
    The git checkout to take the commit from (its top folder).
.PARAMETER Commit
    The commit to test (a sha or anything git rev-parse understands).
.PARAMETER SummaryFile
    Also write a JSON summary here.
.PARAMETER Only
    Run only these steps (names or numbers, e.g. -Only build,tests or -Only 8). Steps they need run too.
    A run with -Only never writes the pass record.
.PARAMETER KeepBox
    Keep the Linux test box after the run (it is always kept when something in it failed).

.EXAMPLE
    scripts\pre-deploy.ps1 -Repo . -Commit HEAD
    scripts\pre-deploy.ps1 -Repo . -Commit HEAD -Only linux-box -KeepBox
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Commit,
    [string]$SummaryFile,
    [string[]]$Only,
    [switch]$KeepBox
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'predeploy-lib.ps1')

# ---------------------------------------------------------------------------------------------- settings
$LocalVersion = '1.0.65000'       # the version of the packages built here (newer than any real release)
$BoxName = 'pairnets-test'
$BoxShell = "wsl.exe -d $BoxName -u root --exec" # --exec: the script reaches bash as written, without a second shell
$Ports = @(15075, 15443, 15025, 15480)
$MinFreeGB = 15

$StepOrder = @('preflight', 'line-endings', 'secrets', 'gate', 'build', 'publish', 'render-screens', 'tests', 'linux-box', 'cloud')
$StepTitles = @{
    'preflight' = 'Tools, disk space and ports'
    'line-endings' = 'Linux files have Linux line ends'
    'secrets' = 'No secrets in the files or the history'
    'gate' = "The deploy gate's own tests"
    'build' = 'Build everything (Release)'
    'publish' = 'Publish the server and the Windows app'
    'render-screens' = 'Render every Windows window'
    'tests' = 'The whole test suite'
    'linux-box' = 'Linux test box: install, tour, update, upgrade'
    'cloud' = 'Cloud worker tests'
}
# What a step needs from earlier steps in the same run (every run starts from a clean checkout).
$StepNeeds = @{
    'render-screens' = @('build')
    'tests' = @('build', 'publish')
    'linux-box' = @('build', 'publish')
}

# ------------------------------------------------------------------------------------------- the commit
$Repo = (Resolve-Path $Repo).Path
if ($SummaryFile -and -not [System.IO.Path]::IsPathRooted($SummaryFile)) { $SummaryFile = Join-Path (Get-Location).Path $SummaryFile }
$git = (Get-Command git.exe -ErrorAction SilentlyContinue)
if (-not $git) { Write-Host 'pre-deploy: git is not installed' -ForegroundColor Red; exit 2 }
function Git([string[]]$Arguments) {
    $r = Invoke-PdNative -File 'git.exe' -Arguments (@('-C', $Repo) + $Arguments) -Quiet -TimeoutSeconds 600
    if ($r.ExitCode -ne 0) { throw ("git {0} failed: {1}" -f ($Arguments -join ' '), $r.Output.Trim()) }
    return $r.Output.Trim()
}
try {
    $Sha = Git @('rev-parse', '--verify', "$Commit^{commit}")
    $Tree = Git @('rev-parse', "$Sha^{tree}")
    $Subject = Git @('log', '-1', '--format=%s', $Sha)
} catch {
    Write-Host "pre-deploy: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
$Sha8 = $Sha.Substring(0, 8)

# Which steps run.
$selected = New-Object System.Collections.Generic.List[string]
$neededBy = @{}
$onlyMode = $false
if ($Only) {
    $onlyMode = $true
    foreach ($item in ($Only -join ',').Split(',')) {
        $s = $item.Trim().ToLowerInvariant()
        if (-not $s) { continue }
        if ($s -match '^\d+$' -and [int]$s -lt $StepOrder.Count) { $s = $StepOrder[[int]$s] }
        if ($StepOrder -notcontains $s) {
            Write-Host "pre-deploy: unknown step '$item' (steps: $($StepOrder -join ', '))" -ForegroundColor Red
            exit 2
        }
        if (-not $selected.Contains($s)) { $selected.Add($s) }
    }
    foreach ($s in @($selected)) {
        if ($StepNeeds.ContainsKey($s)) {
            foreach ($n in $StepNeeds[$s]) {
                if (-not $selected.Contains($n)) { $selected.Add($n); $neededBy[$n] = $s }
            }
        }
    }
    if (-not $selected.Contains('preflight')) { $selected.Add('preflight'); $neededBy['preflight'] = 'every run' }
} else {
    foreach ($s in $StepOrder) { $selected.Add($s) }
}

# --------------------------------------------------------------------------------------------- folders
$WorkRoot = Join-Path $script:PdHome 'work'
$Work = Join-Path $WorkRoot $Sha8
$ReportDir = Join-Path $script:PdHome ("reports\{0}-{1}" -f $Sha8, (Get-Date -Format 'yyyyMMdd-HHmmss'))
$StampDir = Join-Path $script:PdHome 'stamps'
$Dist = Join-Path $Work 'dist'
$TestResults = Join-Path $ReportDir 'TestResults'
$TourReport = Join-Path $ReportDir 'tour-installed-linux.json'
foreach ($d in @($script:PdHome, $WorkRoot, $ReportDir, $StampDir)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$MainLog = Join-Path $ReportDir 'run.log'

$script:Secrets = New-Object System.Collections.Generic.List[string] # redacted from every report file
$script:Results = New-Object System.Collections.Generic.List[object]
$script:GitBash = Get-PdGitBash

function Say([string]$Text, [string]$Color = '') {
    if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
    Add-PdLogLine -Log $MainLog -Text $Text
}

# ------------------------------------------------------------------------------------- step machinery
function New-StepContext([string]$Name, [int]$Index) {
    return [pscustomobject]@{
        Name = $Name
        Index = $Index
        Log = (Join-Path $ReportDir ('{0:D2}-{1}.log' -f $Index, $Name))
        Problems = (New-Object System.Collections.Generic.List[string])
        Notes = (New-Object System.Collections.Generic.List[string])
        Skipped = $false
    }
}

function Add-Problem($Ctx, [string]$Text) { $Ctx.Problems.Add($Text); Add-PdLogLine -Log $Ctx.Log -Text "PROBLEM: $Text" }
function Add-Note($Ctx, [string]$Text) { $Ctx.Notes.Add($Text); Add-PdLogLine -Log $Ctx.Log -Text "NOTE: $Text" }

# Runs a program for a step, its output into the step log (and on screen with -Echo); a non-zero exit is a
# problem unless -AllowFail.
function Invoke-StepTool {
    param($Ctx, [string]$What, [string]$File, [string[]]$Arguments = @(), [string]$WorkingDirectory = $Work,
        [hashtable]$Environment = @{}, [int]$TimeoutSeconds = 3600, [switch]$AllowFail, [switch]$Quiet, [switch]$Echo)
    Add-PdLogLine -Log $Ctx.Log -Text ("`n### {0}" -f $What)
    $r = Invoke-PdNative -File $File -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Log $Ctx.Log `
        -Environment $Environment -TimeoutSeconds $TimeoutSeconds -Quiet:(-not $Echo) # (the step log has it all)
    Add-PdLogLine -Log $Ctx.Log -Text ("### {0}: exit {1} after {2:n0} s" -f $What, $r.ExitCode, $r.Seconds)
    if (-not $AllowFail -and $r.ExitCode -ne 0) {
        if ($r.TimedOut) { Add-Problem $Ctx "$What did not finish within $TimeoutSeconds seconds" }
        else { Add-Problem $Ctx "$What failed (exit code $($r.ExitCode))" }
    }
    return $r
}

function Invoke-Bash($Ctx, [string]$What, [string[]]$Arguments, [int]$TimeoutSeconds = 1800, [switch]$AllowFail) {
    if (-not $script:GitBash) { Add-Problem $Ctx "$What needs Git Bash (Git for Windows), which was not found"; return $null }
    return Invoke-StepTool -Ctx $Ctx -What $What -File $script:GitBash -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -AllowFail:$AllowFail
}

# "error XX1234: ..." lines of a build, once each.
function Get-BuildErrors([string]$Output) {
    $seen = @{}
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Output -split "`r?`n")) {
        if ($line -match ':\s+error\s+[A-Z]+\d+:\s*(.+?)(\s+\[[^\]]+\])?\s*$') {
            $file = ''
            if ($line -match '([^\\/:]+\.(cs|xaml|axaml|csproj|props|targets))\(\d+') { $file = $Matches[1] + ': ' }
            $text = $file + ($line -replace '^.*:\s+error\s+', 'error ' -replace '\s+\[[^\]]+\]\s*$', '')
            if (-not $seen.ContainsKey($text)) { $seen[$text] = $true; $list.Add($text) }
        }
    }
    return $list
}

# "SomeTests.Sign_in_works" -> "Sign in works"
function ConvertTo-Words([string]$Name) {
    $n = $Name -replace '\(.*$', ''
    $n = $n.Substring($n.LastIndexOf('.') + 1)
    $n = $n -replace '_', ' '
    # "APasswordSignsIn" -> "A Password Signs In", "HTTPSWorks" -> "HTTPS Works"
    $n = [regex]::Replace($n, '(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])', ' ')
    $n = $n.Trim()
    if ($n.Length -gt 1) { $n = $n.Substring(0, 1).ToUpperInvariant() + $n.Substring(1).ToLowerInvariant() }
    return $n
}

$AreaNames = [ordered]@{
    'Pairnets.Tests.E2E' = 'The API tour'
    'Pairnets.Tests.Ui' = 'The Mac and Linux app (Avalonia)'
    'Pairnets.Tests.Integration' = 'Server and apps together'
    'Pairnets.Tests.Unit' = 'Single parts (unit tests)'
    'Pairnets.Tests.Convergence' = 'Two computers syncing at random (convergence)'
    'Pairnets.Tests.Faults' = 'Broken networks and disks (faults)'
    'Pairnets.Client.Tests' = 'The Windows app'
    'Pairnets.Browser.Tests' = "The nest's website in a real browser"
}

function Get-Area([string]$ClassName) {
    foreach ($k in $AreaNames.Keys) { if ($ClassName.StartsWith($k + '.') -or $ClassName -eq $k) { return $AreaNames[$k] } }
    $parts = $ClassName.Split('.')
    if ($parts.Count -ge 3) { return ($parts[0..($parts.Count - 2)] -join '.') }
    return 'Other tests'
}

# Failing tests of the trx files matching a pattern, as plain lines grouped by area.
function Get-FailedTests([string]$Pattern) {
    $byArea = [ordered]@{}
    $count = 0
    $total = 0
    if (-not (Test-Path $TestResults)) { return @{ Lines = @(); Failed = 0; Total = 0 } }
    foreach ($trx in (Get-ChildItem -Path $TestResults -Filter $Pattern -Recurse -File -ErrorAction SilentlyContinue)) {
        try {
            $xml = New-Object System.Xml.XmlDocument
            $xml.Load($trx.FullName)
            $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
            $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
            $classes = @{}
            foreach ($u in $xml.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
                $m = $u.SelectSingleNode('t:TestMethod', $ns)
                if ($m) { $classes[$u.GetAttribute('id')] = $m.GetAttribute('className') }
            }
            foreach ($r in $xml.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
                $total++
                if ($r.GetAttribute('outcome') -ne 'Failed') { continue }
                $count++
                $class = $classes[$r.GetAttribute('testId')]
                if (-not $class) { $class = 'Unknown' }
                $msgNode = $r.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
                $msg = if ($msgNode) { ($msgNode.InnerText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1) } else { 'no message' }
                if ($msg.Length -gt 220) { $msg = $msg.Substring(0, 217) + '...' }
                $area = Get-Area $class
                if (-not $byArea.Contains($area)) { $byArea[$area] = New-Object System.Collections.Generic.List[string] }
                $short = $class.Substring($class.LastIndexOf('.') + 1)
                $byArea[$area].Add(('{0}: "{1}" failed: {2}' -f $short, (ConvertTo-Words $r.GetAttribute('testName')), $msg.Trim()))
            }
        } catch {
            if (-not $byArea.Contains('Unreadable results')) { $byArea['Unreadable results'] = New-Object System.Collections.Generic.List[string] }
            $byArea['Unreadable results'].Add("$($trx.Name): $($_.Exception.Message)")
        }
    }
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($area in $byArea.Keys) {
        foreach ($l in $byArea[$area]) { $lines.Add("$area - $l") }
    }
    return @{ Lines = $lines; Failed = $count; Total = $total }
}

function Add-TestProblems($Ctx, [string]$Pattern, $Run, [string]$What, [switch]$RequireTests) {
    $failed = Get-FailedTests $Pattern
    if ($failed.Total -eq 0 -and $Run -and $Run.ExitCode -eq 0) {
        if ($RequireTests) { Add-Problem $Ctx "${What}: no test ran" } else { Add-Note $Ctx "${What}: no test matched" }
    } elseif ($failed.Failed -gt 0) {
        Add-Problem $Ctx ("{0}: {1} of {2} tests failed" -f $What, $failed.Failed, $failed.Total)
        foreach ($l in ($failed.Lines | Select-Object -First 60)) { Add-Problem $Ctx $l }
        if ($failed.Lines.Count -gt 60) { Add-Problem $Ctx ("... and {0} more (see the .trx files)" -f ($failed.Lines.Count - 60)) }
    } elseif ($Run -and $Run.ExitCode -ne 0) {
        if ($Run.TimedOut) { Add-Problem $Ctx "$What did not finish in time (see the log)" }
        else { Add-Problem $Ctx ("{0} failed (exit code {1}) without a failing test in the results: a build or test-host problem, see the log" -f $What, $Run.ExitCode) }
    } else {
        Add-Note $Ctx ("{0}: {1} tests passed" -f $What, $failed.Total)
    }
}

# --------------------------------------------------------------------------------------- the box (WSL)
# The box the box commands below talk to (phase A uses $BoxName; phase B another one when A is kept).
$script:CurrentBox = $BoxName

function Invoke-Box($Ctx, [string]$What, [string]$Script, [int]$TimeoutSeconds = 900, [switch]$Quiet, [switch]$NoLog) {
    $log = if ($NoLog) { $null } else { $Ctx.Log }
    if ($log) { Add-PdLogLine -Log $log -Text ("`n### {0} ({1})" -f $What, $script:CurrentBox) }
    $r = Invoke-PdNative -File 'wsl.exe' -Arguments @('-d', $script:CurrentBox, '-u', 'root', '--exec', 'bash', '-c', ($Script -replace "`r`n", "`n")) `
        -Log $log -TimeoutSeconds $TimeoutSeconds -Quiet:($Quiet -or $NoLog)
    if ($log) { Add-PdLogLine -Log $log -Text ("### {0}: exit {1} after {2:n0} s" -f $What, $r.ExitCode, $r.Seconds) }
    return $r
}

# A check script in the box; its FAIL lines become the step's problems. Returns $true when it passed.
function Invoke-BoxCheck($Ctx, [string]$What, [string]$Command, [int]$TimeoutSeconds = 1200) {
    Say "   - $What"
    $r = Invoke-Box -Ctx $Ctx -What $What -Script "cd /root/pd && $Command" -TimeoutSeconds $TimeoutSeconds -Quiet
    $fails = @($r.Output -split "`r?`n" | Where-Object { $_ -match '^\s+FAIL\s+' } | ForEach-Object { ($_ -replace '^\s+FAIL\s+', '').TrimEnd(':') })
    if ($r.ExitCode -eq 0) {
        Say "     passed ($([int]$r.Seconds) s)"
        return $true
    }
    if ($r.TimedOut) { Add-Problem $Ctx "$What did not finish within $TimeoutSeconds seconds" }
    elseif ($fails.Count -eq 0) { Add-Problem $Ctx "$What failed (exit code $($r.ExitCode)); see the log" }
    foreach ($f in $fails) { Add-Problem $Ctx "${What}: $f" }
    Say "     FAILED ($([int]$r.Seconds) s)" 'Red'
    return $false
}

# scripts\wsl-testbox.ps1 -Fresh / -Stop / -Remove / -EnsureBase for a box.
function Invoke-TestBox($Ctx, [string]$Switch, [string]$Box = $script:CurrentBox) {
    $r = Invoke-StepTool -Ctx $Ctx -What "wsl-testbox $Switch $Box" -File 'powershell.exe' -Quiet -TimeoutSeconds 1800 -AllowFail `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Work 'scripts\wsl-testbox.ps1'), $Switch, '-Name', $Box)
    return ($r.ExitCode -eq 0)
}

# Copies what the box needs (the scripts and the two feeds) into it, so it never reads the checkout later.
function Copy-ToBox($Ctx) {
    $w = ConvertTo-PdWslPath $Work
    $script = @"
set -e
rm -rf /root/pd
mkdir -p /root/pd/tests /root/pd/docs
cp -R '$w/deploy' '$w/scripts' /root/pd/
cp -R '$w/tests/linux-server' /root/pd/tests/
cp '$w/docs/DEPLOY.md' /root/pd/docs/
cp -R '$w/dist/feed1' '$w/dist/feed2' /root/pd/
echo "copied into the box: `$(du -sh /root/pd | cut -f1)"
"@
    $r = Invoke-Box -Ctx $Ctx -What 'copy the scripts and feeds into the box' -Script $script -Quiet
    if ($r.ExitCode -ne 0) { Add-Problem $Ctx 'could not copy the scripts and feeds into the box'; return $false }
    return $true
}

# The box's shared token: for the tour, and to hide it in every report file.
function Read-BoxToken($Ctx) {
    $r = Invoke-Box -Ctx $Ctx -What 'read the token' -Script "sed -n 's/^SYNC_TOKEN=//p' /etc/pairnets/pairnets.env" -NoLog
    $t = $r.Output.Trim()
    if ($r.ExitCode -eq 0 -and $t.Length -ge 16) {
        if (-not $script:Secrets.Contains($t)) { $script:Secrets.Add($t) }
        return $t
    }
    return $null
}

# The Pairnets units' journal of the current box, into the report (the token is hidden afterwards).
function Save-BoxJournal($Ctx, [string]$Phase) {
    $file = Join-Path $ReportDir ("box-journal-{0}.log" -f $Phase)
    $r = Invoke-Box -Ctx $Ctx -What "journal ($Phase)" -NoLog -Script "journalctl --no-pager -o short-iso -u 'pairnets-*'; echo; echo '--- update.log'; cat /var/lib/pairnets/update/update.log 2>/dev/null"
    Write-PdText $file $r.Output
}

# ----------------------------------------------------------------------------------------------- steps
function Step-Preflight($Ctx) {
    $need = @{
        'git.exe' = $true
        'dotnet.exe' = ($selected.Contains('build') -or $selected.Contains('publish') -or $selected.Contains('tests'))
        'node.exe' = ($selected.Contains('gate') -or $selected.Contains('cloud'))
        'wsl.exe' = $selected.Contains('linux-box')
        'curl.exe' = $selected.Contains('linux-box')
    }
    foreach ($tool in $need.Keys) {
        $found = Get-Command $tool -ErrorAction SilentlyContinue
        if ($found) { Add-PdLogLine -Log $Ctx.Log -Text ("{0}: {1}" -f $tool, $found.Source) }
        elseif ($need[$tool]) { Add-Problem $Ctx "$tool is not installed (or not on PATH)" }
    }
    if ($script:GitBash) { Add-PdLogLine -Log $Ctx.Log -Text "Git Bash: $script:GitBash" }
    elseif ($selected.Contains('secrets') -or $selected.Contains('publish')) { Add-Problem $Ctx 'Git Bash (Git for Windows) was not found' }
    if (Get-Command dotnet.exe -ErrorAction SilentlyContinue) {
        $sdks = Invoke-PdNative -File 'dotnet.exe' -Arguments @('--list-sdks') -Quiet -TimeoutSeconds 120
        Add-PdLogLine -Log $Ctx.Log -Text $sdks.Output
        if ($sdks.Output -notmatch '(?m)^8\.0\.') { Add-Problem $Ctx 'the .NET 8 SDK is not installed (dotnet --list-sdks)' }
    }
    $drive = (Get-Item $script:PdHome).PSDrive
    $freeGB = [math]::Round((Get-PSDrive $drive.Name).Free / 1GB, 1)
    Add-PdLogLine -Log $Ctx.Log -Text "free on ${drive}: $freeGB GB"
    if ($freeGB -lt $MinFreeGB) { Add-Problem $Ctx "only $freeGB GB free on ${drive}: (the check needs about $MinFreeGB GB)" }

    if ($selected.Contains('linux-box')) {
        $boxExists = $false
        $list = Invoke-PdNative -File 'wsl.exe' -Arguments @('--list', '--quiet') -Quiet -TimeoutSeconds 60
        if ($list.ExitCode -eq 0) { $boxExists = @($list.Output -split "`r?`n" | ForEach-Object { $_.Trim([char]0, ' ') }) -contains $BoxName }
        foreach ($port in $Ports) {
            $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
            foreach ($l in $listeners) {
                $proc = Get-Process -Id $l.OwningProcess -ErrorAction SilentlyContinue
                $pname = if ($proc) { $proc.ProcessName } else { "process $($l.OwningProcess)" }
                if ($boxExists -and $pname -match '^(wslrelay|wsl|wslhost)$') {
                    Add-Note $Ctx "port $port is held by an old test box; it is replaced"
                } else {
                    Add-Problem $Ctx "port $port is already in use by $pname (pid $($l.OwningProcess)); the Linux test box needs it"
                }
            }
        }
        if (-not (Test-Path (Join-Path $script:PdHome 'wsl\base.tar'))) {
            Add-Note $Ctx 'the Linux test box has no base image yet: it is made first (about 5 minutes, downloads Ubuntu)'
        }
    }
}

function Step-LineEndings($Ctx) {
    $r = Invoke-StepTool -Ctx $Ctx -What 'git ls-files --eol' -File 'git.exe' -Arguments @('ls-files', '--eol') -Quiet
    $linux = '\.(sh|py|mjs|service|path|timer)$|^deploy/pairnets\.env\.example$'
    $count = 0
    foreach ($line in ($r.Output -split "`r?`n")) {
        if ($line -notmatch '^i/(\S+)\s+w/(\S+)\s+attr/.*?\t(.+)$') { continue }
        $index = $Matches[1]; $worktree = $Matches[2]; $path = $Matches[3]
        if ($path -notmatch $linux) { continue }
        $count++
        if ($index -match 'crlf' -or $worktree -match 'crlf') {
            Add-Problem $Ctx "$path has Windows line ends (CRLF); Linux cannot run it like that"
        }
    }
    Add-Note $Ctx "$count Linux files checked"
}

function Step-Secrets($Ctx) {
    foreach ($mode in @('--tree', '--history')) {
        $r = Invoke-Bash $Ctx "check-secrets.sh $mode" @('scripts/check-secrets.sh', $mode) -AllowFail
        if ($r -and $r.ExitCode -ne 0) {
            Add-Problem $Ctx "check-secrets.sh $mode found something that looks like a secret or personal data:"
            foreach ($l in ($r.Output -split "`r?`n" | Where-Object { $_ -and $_ -notmatch '^check-secrets' } | Select-Object -First 8)) {
                $short = if ($l.Length -gt 160) { $l.Substring(0, 157) + '...' } else { $l }
                Add-Problem $Ctx "  $short"
            }
        }
    }
}

function Step-Gate($Ctx) {
    if (-not (Test-Path (Join-Path $Work 'scripts\deploy-gate'))) {
        $Ctx.Skipped = $true
        Add-Note $Ctx 'scripts/deploy-gate is not in this commit'
        return
    }
    # node expands the quoted glob itself (a folder argument fails on Node 22 and later).
    Invoke-StepTool -Ctx $Ctx -What 'node --test scripts/deploy-gate' -File 'node.exe' -Arguments @('--test', 'scripts/deploy-gate/*.test.mjs') -TimeoutSeconds 900 | Out-Null
}

function Step-Build($Ctx) {
    $r = Invoke-StepTool -Ctx $Ctx -What 'dotnet build Pairnets.sln -c Release' -File 'dotnet.exe' -Arguments @('build', 'Pairnets.sln', '-c', 'Release') -TimeoutSeconds 2400
    if ($r.ExitCode -ne 0) { foreach ($e in (Get-BuildErrors $r.Output | Select-Object -First 15)) { Add-Problem $Ctx $e } }
    $r = Invoke-StepTool -Ctx $Ctx -What 'dotnet build tools/RenderScreens.Wpf' -File 'dotnet.exe' -Arguments @('build', 'tools/RenderScreens.Wpf/RenderScreens.Wpf.csproj', '-c', 'Release') -TimeoutSeconds 1200
    if ($r.ExitCode -ne 0) { foreach ($e in (Get-BuildErrors $r.Output | Select-Object -First 15)) { Add-Problem $Ctx $e } }
}

function Step-Publish($Ctx) {
    $linux = Join-Path $Dist 'pairnets-server-linux-x64'
    $r = Invoke-StepTool -Ctx $Ctx -What 'publish the server for Linux' -File 'dotnet.exe' -TimeoutSeconds 1800 -Arguments @(
        'publish', 'src/Pairnets.Server/Pairnets.Server.csproj', '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:DebugType=none',
        "-p:Version=$LocalVersion", '-o', $linux)
    if ($r.ExitCode -eq 0) {
        $b = ConvertTo-PdBashPath $Dist
        Invoke-Bash $Ctx 'feed 1 (package-server.sh)' @('scripts/make-feed.sh', "$b/pairnets-server-linux-x64", $LocalVersion, "$b/feed1", $Sha) | Out-Null
        Invoke-Bash $Ctx 'feed 2 (the same build, one version higher)' @('scripts/make-feed.sh', "$b/feed1/pairnets-server-linux-x64.tar.gz", 'next', "$b/feed2", $Sha) | Out-Null
        foreach ($f in @('feed1', 'feed2')) {
            if (-not (Test-Path (Join-Path $Dist "$f\pairnets-server-linux-x64.tar.gz"))) { Add-Problem $Ctx "the server package for $f was not made" }
        }
    }
    $win = Join-Path $Dist 'pairnets-server-win-x64'
    Invoke-StepTool -Ctx $Ctx -What 'publish the server for Windows (for the tests)' -File 'dotnet.exe' -TimeoutSeconds 1800 -Arguments @(
        'publish', 'src/Pairnets.Server/Pairnets.Server.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:DebugType=none',
        "-p:Version=$LocalVersion", '-o', $win) | Out-Null
    if (-not (Test-Path (Join-Path $win 'pairnets-server.exe'))) { Add-Problem $Ctx 'pairnets-server.exe was not published' }
    # The Windows app, exactly as release.yml publishes it (a single-file smoke test).
    $client = Join-Path $Dist 'client'
    Invoke-StepTool -Ctx $Ctx -What 'publish the Windows app (single file)' -File 'dotnet.exe' -TimeoutSeconds 1800 -Arguments @(
        'publish', 'src/Pairnets.Client/Pairnets.Client.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none',
        "-p:Version=$LocalVersion", '-o', $client) | Out-Null
    if (-not (Test-Path (Join-Path $client 'Pairnets.exe'))) { Add-Problem $Ctx 'Pairnets.exe (the Windows app) was not published' }
}

function Step-RenderScreens($Ctx) {
    $screens = Join-Path $Work 'screens'
    Invoke-StepTool -Ctx $Ctx -What 'RenderScreens.Wpf screens --print' -File 'dotnet.exe' -TimeoutSeconds 1200 -Arguments @(
        'run', '--project', 'tools/RenderScreens.Wpf/RenderScreens.Wpf.csproj', '-c', 'Release', '--no-build', '--', 'screens', '--print') | Out-Null
    $png = @(Get-ChildItem -Path $screens -Filter *.png -Recurse -ErrorAction SilentlyContinue)
    if ($png.Count -eq 0) { Add-Problem $Ctx 'no screenshots were made' }
    else {
        Copy-Item -Recurse -Force $screens (Join-Path $ReportDir 'screens')
        Add-Note $Ctx "$($png.Count) screenshots (in the report's screens folder)"
    }
}

# The website tests install Chromium themselves the first time (Microsoft.Playwright, about 300 MB, once).
function Step-Tests($Ctx) {
    $server = Join-Path $Dist 'pairnets-server-win-x64\pairnets-server.exe'
    $r = Invoke-StepTool -Ctx $Ctx -What 'dotnet test Pairnets.sln' -File 'dotnet.exe' -TimeoutSeconds 7200 -AllowFail -Arguments @(
        'test', 'Pairnets.sln', '-c', 'Release', '--no-build',
        '--logger', 'trx;LogFilePrefix=windows', '--logger', 'console;verbosity=normal',
        '--results-directory', $TestResults, '--blame-hang-timeout', '10m') `
        -Environment @{ 'PAIRNETS_CONVERGENCE_SEEDS' = '250'; 'PAIRNETS_E2E_SERVER' = $server; 'PAIRNETS_E2E_TARGET' = $null }
    Add-TestProblems $Ctx 'windows*.trx' $r 'the test suite' -RequireTests
}

function Step-LinuxBox($Ctx) {
    $feed1 = Join-Path $Dist 'feed1\pairnets-server-linux-x64.tar.gz'
    $feed2 = Join-Path $Dist 'feed2\pairnets-server-linux-x64.tar.gz'
    if (-not ((Test-Path $feed1) -and (Test-Path $feed2))) { Add-Problem $Ctx 'the server packages were not made (see the publish step)'; return }

    if (-not (Test-Path (Join-Path $script:PdHome 'wsl\base.tar'))) {
        Say '   - making the base image (once, a few minutes)'
        if (-not (Invoke-TestBox $Ctx '-EnsureBase' $BoxName)) { Add-Problem $Ctx 'could not make the base image for the Linux test box (see the log)'; return }
    }

    # A phase B box kept from an earlier run would hold the ports (every WSL box shares one network).
    Invoke-TestBox $Ctx '-Stop' "$BoxName-b" | Out-Null

    # ---- Phase A: install feed 1 with get.sh, the test-only settings, the tour, the update to feed 2.
    $script:CurrentBox = $BoxName
    $failedA = $false
    Say "   - a fresh box ($BoxName)"
    if (-not (Invoke-TestBox $Ctx '-Fresh')) { Add-Problem $Ctx 'could not start a fresh Linux test box (see the log)'; return }
    if (-not (Copy-ToBox $Ctx)) { return }
    $installed = Invoke-BoxCheck $Ctx 'install (phase A)' 'bash tests/linux-server/install-check.sh --feed /root/pd/feed1'
    $token = Read-BoxToken $Ctx
    if (-not $installed) { $failedA = $true }
    else {
        if (-not (Invoke-BoxCheck $Ctx 'test-only nest settings' 'bash tests/linux-server/test-nest-config.sh')) { $failedA = $true }
        # From Windows, through WSL's port forwarding.
        $h1 = Invoke-PdNative -File 'curl.exe' -Arguments @('-fsS', '--max-time', '10', 'http://127.0.0.1:15075/api/health') -Quiet -Log $Ctx.Log
        if ($h1.ExitCode -ne 0 -or $h1.Output.Trim() -ne 'ok') {
            Add-Problem $Ctx 'Windows cannot reach the box at http://127.0.0.1:15075 (WSL port forwarding)'; $failedA = $true
        }
        $h2 = Invoke-PdNative -File 'curl.exe' -Arguments @('-ksS', '--max-time', '10', '-o', 'NUL', '-w', '%{http_code}', 'https://localhost:15443/') -Quiet -Log $Ctx.Log
        if ($h2.ExitCode -ne 0 -or $h2.Output.Trim() -notmatch '^[23]\d\d$') {
            Add-Problem $Ctx "Windows cannot reach the box's website at https://localhost:15443/ (WSL port forwarding)"; $failedA = $true
        }
        $prepared = Invoke-BoxCheck $Ctx 'update, before the tour' 'bash tests/linux-server/update-check.sh prepare --feed /root/pd/feed2'
        if (-not $prepared) { $failedA = $true }

        # The API tour and the website tests, from Windows, against the installed server.
        Say '   - the API tour and the website tests against the box (installed-linux)'
        if (Test-Path $TourReport) { Remove-Item -Force $TourReport }
        if (-not $token) { Add-Problem $Ctx 'could not read the token from the box'; $failedA = $true }
        else {
            $e2e = @{
                'PAIRNETS_E2E_TARGET' = 'installed-linux'
                'PAIRNETS_E2E_URL' = 'http://127.0.0.1:15075/'
                'PAIRNETS_E2E_WEBSITE_URL' = 'https://localhost:15443/'
                'PAIRNETS_E2E_TOKEN' = $token
                'PAIRNETS_E2E_SHELL' = $BoxShell
                'PAIRNETS_E2E_DATA_DIR' = '/var/lib/pairnets'
                'PAIRNETS_E2E_SERVICE' = 'pairnets-server'
                'PAIRNETS_E2E_MAIL_DIR' = '/var/lib/pairnets-e2e/mail'
                'PAIRNETS_E2E_REPORT' = $TourReport
                'PAIRNETS_E2E_SERVER' = $null
            }
            $before = $Ctx.Problems.Count
            # The website tests first: they restart the server (which also resets its email limit), and the tour
            # ends with a real "Update server".
            if (Test-Path (Join-Path $Work 'tests\Pairnets.Browser.Tests')) {
                $r = Invoke-StepTool -Ctx $Ctx -What 'website tests (installed-linux)' -File 'dotnet.exe' -TimeoutSeconds 3600 -AllowFail -Quiet -Environment $e2e -Arguments @(
                    'test', 'tests\Pairnets.Browser.Tests', '-c', 'Release', '--no-build',
                    '--logger', 'trx;LogFilePrefix=installed-linux-website', '--logger', 'console;verbosity=normal',
                    '--results-directory', $TestResults, '--blame-hang-timeout', '10m')
                Add-TestProblems $Ctx 'installed-linux-website*.trx' $r 'the website tests on the box' -RequireTests
            }
            $r = Invoke-StepTool -Ctx $Ctx -What 'API tour (installed-linux)' -File 'dotnet.exe' -TimeoutSeconds 3600 -AllowFail -Quiet -Environment $e2e -Arguments @(
                'test', 'tests\Pairnets.Tests', '-c', 'Release', '--no-build', '--filter', 'FullyQualifiedName~InstalledLinux',
                '--logger', 'trx;LogFilePrefix=installed-linux-tour', '--logger', 'console;verbosity=normal',
                '--results-directory', $TestResults, '--blame-hang-timeout', '10m')
            Add-TestProblems $Ctx 'installed-linux-tour*.trx' $r 'the API tour on the box'
            if (-not (Test-Path $TourReport)) {
                Add-Problem $Ctx "the installed-linux API tour did not run: it wrote no report ($TourReport)"
            } else {
                Say "     tour report: $TourReport"
            }
            if ($Ctx.Problems.Count -gt $before) { $failedA = $true }
        }
        if ($prepared -and -not (Invoke-BoxCheck $Ctx 'update, after the tour' 'bash tests/linux-server/update-check.sh verify')) { $failedA = $true }
    }
    Save-BoxJournal $Ctx 'phase-a'
    if ($failedA) {
        # Kept for a look, but stopped: every WSL box shares one network, so phase B needs the ports.
        Invoke-TestBox $Ctx '-Stop' $BoxName | Out-Null
        Add-Note $Ctx "phase A failed: its box is kept (stopped); open it with: wsl -d $BoxName"
    }

    # ---- Phase B: the published release, upgraded by its own updater to feed 1; then shellcheck.
    $script:CurrentBox = if ($failedA) { "$BoxName-b" } else { $BoxName }
    Say "   - a fresh box for phase B ($script:CurrentBox)"
    $failedB = $true
    if (-not (Invoke-TestBox $Ctx '-Fresh')) { Add-Problem $Ctx 'could not start a fresh Linux test box for phase B (see the log)' }
    elseif (Copy-ToBox $Ctx) {
        $okB = Invoke-BoxCheck $Ctx 'upgrade from the published release (phase B)' 'bash tests/linux-server/upgrade-check.sh --feed /root/pd/feed1'
        Read-BoxToken $Ctx | Out-Null
        $okLint = Invoke-BoxCheck $Ctx 'shellcheck' 'bash tests/linux-server/lint.sh'
        $failedB = -not ($okB -and $okLint)
        Save-BoxJournal $Ctx 'phase-b'
    }
    $kept = @()
    if ($failedA) { $kept += $BoxName }
    if ($failedB -or $KeepBox) {
        $why = if ($failedB) { 'it failed' } else { '-KeepBox' }
        Add-Note $Ctx "the phase B box is kept ($why); open it with: wsl -d $script:CurrentBox"
        if ($kept -notcontains $script:CurrentBox) { $kept += $script:CurrentBox }
    } else {
        Invoke-TestBox $Ctx '-Remove' | Out-Null
    }
    foreach ($k in $kept) { Add-Note $Ctx "throw $k away when done: scripts\wsl-testbox.ps1 -Remove -Name $k" }
    $script:CurrentBox = $BoxName
}

function Step-Cloud($Ctx) {
    $cloud = Join-Path $Work 'cloud'
    if (-not (Test-Path (Join-Path $cloud 'package.json'))) {
        $Ctx.Skipped = $true
        Add-Note $Ctx 'cloud/package.json is not in this commit'
        return
    }
    $out = Join-Path $Work 'dist\wrangler-dry-run'
    foreach ($cmd in @(@('npm', 'ci'), @('npm', 'test'), @('npx', '--yes', 'wrangler', 'deploy', '--dry-run', '--outdir', $out))) {
        $r = Invoke-StepTool -Ctx $Ctx -What ($cmd -join ' ') -File 'cmd.exe' -WorkingDirectory $cloud -TimeoutSeconds 1800 -Arguments (@('/d', '/c') + $cmd)
        if ($r.ExitCode -ne 0) { break }
    }
}

# ------------------------------------------------------------------------------------------- the run
$lock = $null
$lockPath = Join-Path $script:PdHome 'run.lock'
$waitStart = Get-Date
$lastSaid = [datetime]::MinValue
while (-not $lock) {
    try {
        $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    } catch {
        if (((Get-Date) - $lastSaid).TotalSeconds -ge 60) {
            Write-Host ("pre-deploy: another run is going (waiting for {0:n0} s)" -f ((Get-Date) - $waitStart).TotalSeconds)
            $lastSaid = Get-Date
        }
        Start-Sleep -Seconds 5
    }
}
$lockText = [System.Text.Encoding]::UTF8.GetBytes("pid=$PID`nsha=$Sha`nstarted=$((Get-Date).ToUniversalTime().ToString('o'))`n")
$lock.SetLength(0); $lock.Write($lockText, 0, $lockText.Length); $lock.Flush()

# Two deploys of one commit at once: the second waited above, and the first one's pass counts for it too.
$existing = Join-Path $StampDir "$Sha.pass"
if (-not $onlyMode -and (Test-Path $existing) -and ((Get-Content -Path $existing -Encoding UTF8) -contains "sha=$Sha")) {
    $previous = ((Get-Content -Path $existing -Encoding UTF8) | Where-Object { $_ -like 'report=*' } | Select-Object -First 1) -replace '^report=', ''
    Say "Commit $Sha8 already passed the pre-deploy check (report: $previous)" 'Green'
    if ($SummaryFile) {
        Write-PdText $SummaryFile (ConvertTo-Json -Depth 4 -InputObject ([ordered]@{ passed = $true; sha = $Sha; report = $previous; partial = $false; steps = @() }))
    }
    Remove-Item -Recurse -Force $ReportDir -ErrorAction SilentlyContinue
    $lock.Dispose()
    exit 0
}

$oldOut = $null
try { $oldOut = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { $oldOut = $null }
$started = Get-Date
$runWatch = [Diagnostics.Stopwatch]::StartNew()
$worktreeAdded = $false
try {
    Say ("Pairnets pre-deploy check of {0} ({1})" -f $Sha8, $Subject) 'Cyan'
    Say "report: $ReportDir"
    if ($onlyMode) { Say ("only: {0}" -f (($StepOrder | Where-Object { $selected.Contains($_) }) -join ', ')) }

    # A clean checkout of exactly this commit.
    if (Test-Path $Work) {
        Invoke-PdNative -File 'git.exe' -Arguments @('-C', $Repo, 'worktree', 'remove', '--force', $Work) -Quiet -Log $MainLog | Out-Null
        if (Test-Path $Work) { Remove-PdTree $Work }
    }
    Invoke-PdNative -File 'git.exe' -Arguments @('-C', $Repo, 'worktree', 'prune') -Quiet -Log $MainLog | Out-Null
    $r =Invoke-PdNative -File 'git.exe' -Arguments @('-C', $Repo, 'worktree', 'add', '--detach', $Work, $Sha) -Quiet -Log $MainLog
    if ($r.ExitCode -ne 0) { throw "git worktree add failed: $($r.Output.Trim())" }
    $worktreeAdded = $true

    $index = 0
    foreach ($name in $StepOrder) {
        $ctx = New-StepContext $name $index
        $index++
        if (-not $selected.Contains($name)) { continue }
        $label = "[$($ctx.Index)] $name - $($StepTitles[$name])"
        if ($neededBy.ContainsKey($name)) { $label += " (needed by $($neededBy[$name]))" }
        Say "`n== $label" 'Cyan'
        Say "   log: $($ctx.Log)"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            switch ($name) {
                'preflight' { Step-Preflight $ctx }
                'line-endings' { Step-LineEndings $ctx }
                'secrets' { Step-Secrets $ctx }
                'gate' { Step-Gate $ctx }
                'build' { Step-Build $ctx }
                'publish' { Step-Publish $ctx }
                'render-screens' { Step-RenderScreens $ctx }
                'tests' { Step-Tests $ctx }
                'linux-box' { Step-LinuxBox $ctx }
                'cloud' { Step-Cloud $ctx }
            }
        } catch {
            Add-Problem $ctx ("the step stopped: {0}" -f $_.Exception.Message)
            Add-PdLogLine -Log $ctx.Log -Text $_.ScriptStackTrace
        }
        $watch.Stop()
        $passed = ($ctx.Problems.Count -eq 0)
        $script:Results.Add([pscustomobject]@{
            Name = $name; Index = $ctx.Index; Passed = $passed; Skipped = $ctx.Skipped
            Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
            Problems = @($ctx.Problems); Notes = @($ctx.Notes)
        })
        $verdict = if ($ctx.Skipped) { 'SKIP' } elseif ($passed) { 'PASS' } else { 'FAIL' }
        $color = if ($passed) { 'Green' } else { 'Red' }
        Say ("   {0} in {1:n0} s" -f $verdict, $watch.Elapsed.TotalSeconds) $color
        foreach ($p in ($ctx.Problems | Select-Object -First 10)) { Say "     - $p" 'Red' }
        foreach ($n in $ctx.Notes) { Say "     ($n)" }
    }
} catch {
    $script:Results.Add([pscustomobject]@{
        Name = 'setup'; Index = -1; Passed = $false; Skipped = $false; Seconds = 0
        Problems = @("the run could not start: $($_.Exception.Message)"); Notes = @()
    })
    Say "pre-deploy: $($_.Exception.Message)" 'Red'
} finally {
    # The temporary checkout goes, always.
    if ($worktreeAdded -or (Test-Path $Work)) {
        Invoke-PdNative -File 'dotnet.exe' -Arguments @('build-server', 'shutdown') -Quiet -TimeoutSeconds 120 | Out-Null
        Invoke-PdNative -File 'git.exe' -Arguments @('-C', $Repo, 'worktree', 'remove', '--force', $Work) -Quiet -Log $MainLog -TimeoutSeconds 600 | Out-Null
        if (Test-Path $Work) { Remove-PdTree $Work }
        Invoke-PdNative -File 'git.exe' -Arguments @('-C', $Repo, 'worktree', 'prune') -Quiet -Log $MainLog | Out-Null
    }
}
$runWatch.Stop()

# ------------------------------------------------------------------------------------------- the report
$allPassed = ($script:Results.Count -gt 0) -and -not ($script:Results | Where-Object { -not $_.Passed })
$finished = (Get-Date).ToUniversalTime()
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add(("Pairnets pre-deploy check: {0}" -f $(if ($allPassed) { 'PASSED' } else { 'FAILED' })))
$lines.Add('')
$lines.Add("Commit:   $Sha")
$lines.Add("          $Subject")
$lines.Add("Tree:     $Tree")
$lines.Add(("Started:  {0:yyyy-MM-dd HH:mm:ss}   Took: {1:n0} min {2} s" -f $started, [math]::Floor($runWatch.Elapsed.TotalMinutes), $runWatch.Elapsed.Seconds))
if ($onlyMode) { $lines.Add('Only:     ' + (($StepOrder | Where-Object { $selected.Contains($_) }) -join ', ') + '  (a partial run never counts as a pass)') }
$lines.Add("Report:   $ReportDir")
$lines.Add('')
$lines.Add('Steps')
foreach ($s in $script:Results) {
    $verdict = if ($s.Skipped) { 'SKIP' } elseif ($s.Passed) { 'PASS' } else { 'FAIL' }
    $extra = if ($s.Skipped -and $s.Notes.Count) { '  (' + $s.Notes[0] + ')' } else { '' }
    $lines.Add(('  {0}  {1,2} {2,-15} {3,7:n0} s{4}' -f $verdict, $(if ($s.Index -ge 0) { $s.Index } else { '' }), $s.Name, $s.Seconds, $extra))
}
$failedSteps = @($script:Results | Where-Object { -not $_.Passed })
if ($failedSteps.Count) {
    $lines.Add('')
    $lines.Add('What went wrong')
    foreach ($s in $failedSteps) {
        $lines.Add("  $($s.Name):")
        foreach ($p in $s.Problems) { $lines.Add("    - $p") }
    }
}
$notes = @($script:Results | Where-Object { $_.Notes.Count -and -not $_.Skipped })
if ($notes.Count) {
    $lines.Add('')
    $lines.Add('Notes')
    foreach ($s in $notes) { foreach ($n in $s.Notes) { $lines.Add("  $($s.Name): $n") } }
}
$lines.Add('')
$lines.Add('Each step has its log next to this file (00-preflight.log, ...); test results are in TestResults.')
$reportFile = Join-Path $ReportDir 'report.txt'
Write-PdText $reportFile (($lines -join "`n") + "`n")

# The shared token of the test boxes never stays in a report file.
function Hide-Secrets([string]$Path) {
    if (-not $script:Secrets.Count) { return }
    foreach ($f in (Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match '^\.(log|txt|trx|json|xml|html|md|coverage|csv)$' })) {
        $text = [System.IO.File]::ReadAllText($f.FullName)
        $new = $text
        foreach ($s in $script:Secrets) { $new = $new.Replace($s, '(token hidden)') }
        if ($new -ne $text) { [System.IO.File]::WriteAllText($f.FullName, $new, (New-Object System.Text.UTF8Encoding($false))) }
    }
}
Hide-Secrets $ReportDir

if ($SummaryFile) {
    $steps = @()
    foreach ($s in $script:Results) {
        $steps += [ordered]@{ name = $s.Name; passed = [bool]$s.Passed; skipped = [bool]$s.Skipped; seconds = $s.Seconds; problems = [string[]]@($s.Problems); notes = [string[]]@($s.Notes) }
    }
    $summary = [ordered]@{ passed = [bool]$allPassed; sha = $Sha; report = $reportFile; partial = [bool]$onlyMode; steps = $steps }
    $json = ConvertTo-Json -InputObject $summary -Depth 6
    foreach ($s in $script:Secrets) { $json = $json.Replace($s, '(token hidden)') }
    Write-PdText $SummaryFile $json
}

# The pass record the deploy gate reads: only after a full run where everything passed.
if ($allPassed -and -not $onlyMode) {
    $stamp = @(
        'schema=1',
        "sha=$Sha",
        "tree=$Tree",
        ('finished=' + $finished.ToString('yyyy-MM-ddTHH:mm:ssZ')),
        ('steps=' + (($script:Results | ForEach-Object { $_.Name }) -join ',')),
        "report=$reportFile"
    ) -join "`n"
    Write-PdText (Join-Path $StampDir "$Sha.pass") ($stamp + "`n")
}

Say ''
Get-Content -Path $reportFile -Encoding UTF8 | ForEach-Object { Write-Host $_ }
if ($allPassed -and -not $onlyMode) { Say "pass record: $(Join-Path $StampDir "$Sha.pass")" 'Green' }

if ($oldOut) { try { [Console]::OutputEncoding = $oldOut } catch { } }
$lock.Dispose()
if ($allPassed) { exit 0 } else { exit 1 }
