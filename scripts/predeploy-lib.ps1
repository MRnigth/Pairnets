# Shared helpers for scripts/pre-deploy.ps1 and scripts/wsl-testbox.ps1 (Windows PowerShell 5.1 and later).
# Dot-source it:  . (Join-Path $PSScriptRoot 'predeploy-lib.ps1')

Set-StrictMode -Version 2

# Where the pre-deploy check keeps everything (outside every checkout): the box's base image, the
# temporary checkouts, the reports and the pass records.
$script:PdHome = Join-Path $env:LOCALAPPDATA 'Pairnets-predeploy'

if (-not ('PdProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

public sealed class PdResult
{
    public int ExitCode;
    public bool TimedOut;
    public double Seconds;
    public string Output;
}

// Runs a program with its output (stdout and stderr, in order of arrival) saved to a log file and,
// optionally, shown live. Never waits forever: a timeout kills the whole process tree, and output from
// grandchildren that keep the pipe open (build servers) is not waited for after the program ended.
public static class PdProcess
{
    public static PdResult Run(string file, string arguments, string workingDirectory, string logPath,
        IDictionary environment, int timeoutSeconds, bool echo)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(file, arguments ?? string.Empty);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = utf8;
        psi.StandardErrorEncoding = utf8;
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        if (environment != null)
        {
            foreach (DictionaryEntry e in environment)
            {
                var key = (string)e.Key;
                if (e.Value == null) { if (psi.EnvironmentVariables.ContainsKey(key)) psi.EnvironmentVariables.Remove(key); }
                else psi.EnvironmentVariables[key] = e.Value.ToString();
            }
        }

        var text = new StringBuilder();
        var gate = new object();
        StreamWriter log = null;
        if (!string.IsNullOrEmpty(logPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            log = new StreamWriter(logPath, true, utf8);
            log.AutoFlush = true;
        }
        var outDone = new ManualResetEvent(false);
        var errDone = new ManualResetEvent(false);
        Action<string> line = delegate(string data)
        {
            lock (gate)
            {
                text.AppendLine(data);
                if (log != null) log.WriteLine(data);
                if (echo) { try { Console.WriteLine(data); } catch (IOException) { } }
            }
        };

        var result = new PdResult();
        var watch = Stopwatch.StartNew();
        try
        {
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs a) { if (a.Data == null) outDone.Set(); else line(a.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a) { if (a.Data == null) errDone.Set(); else line(a.Data); };
                p.Start();
                try { p.StandardInput.Close(); } catch (IOException) { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                var exited = p.WaitForExit(timeoutSeconds > 0 ? timeoutSeconds * 1000 : -1);
                if (!exited)
                {
                    result.TimedOut = true;
                    KillTree(p.Id);
                    p.WaitForExit(30000);
                    line(string.Format("*** stopped after {0} seconds (timeout)", timeoutSeconds));
                }
                // The rest of the output, but do not wait for a grandchild that still holds the pipe.
                // (One handle at a time: PowerShell runs scripts on an STA thread, where WaitAll is not allowed.)
                var drain = Stopwatch.StartNew();
                outDone.WaitOne(20000);
                errDone.WaitOne((int)Math.Max(0, 20000 - drain.ElapsedMilliseconds));
                result.ExitCode = exited ? p.ExitCode : -1;
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            line("*** could not start " + file + ": " + ex.Message);
            result.ExitCode = -2;
        }
        finally
        {
            watch.Stop();
            result.Seconds = watch.Elapsed.TotalSeconds;
            lock (gate)
            {
                result.Output = text.ToString();
                if (log != null) { log.Dispose(); log = null; }
            }
        }
        return result;
    }

    public static void KillTree(int pid)
    {
        try
        {
            var k = new ProcessStartInfo("taskkill.exe", "/T /F /PID " + pid);
            k.UseShellExecute = false;
            k.CreateNoWindow = true;
            using (var p = Process.Start(k)) { p.WaitForExit(30000); }
        }
        catch (Exception) { }
    }
}
'@
}

# One argument for a Windows command line (the rules CommandLineToArgvW and the C runtime use).
function ConvertTo-PdArg([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($c in $Value.ToCharArray()) {
        if ($c -eq [char]'\') { $slashes++; continue }
        if ($c -eq [char]'"') {
            [void]$sb.Append('\' * (2 * $slashes + 1)); [void]$sb.Append('"'); $slashes = 0; continue
        }
        if ($slashes -gt 0) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
        [void]$sb.Append($c)
    }
    [void]$sb.Append('\' * (2 * $slashes))
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Join-PdArgs([string[]]$Arguments) {
    if (-not $Arguments) { return '' }
    return (($Arguments | ForEach-Object { ConvertTo-PdArg $_ }) -join ' ')
}

# Runs a program: Invoke-PdNative -File dotnet -Arguments @('build', ...) -Log <file> [-Environment @{..}]
# Returns a PdResult (ExitCode, TimedOut, Seconds, Output).
function Invoke-PdNative {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $null,
        [string]$Log = $null,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 3600,
        [switch]$Quiet
    )
    $env2 = @{ 'WSL_UTF8' = '1'; 'MSBUILDDISABLENODEREUSE' = '1'; 'DOTNET_CLI_TELEMETRY_OPTOUT' = '1'; 'DOTNET_NOLOGO' = '1' }
    foreach ($k in $Environment.Keys) { $env2[$k] = $Environment[$k] }
    if ($Log) { Add-PdLogLine -Log $Log -Text ("> " + $File + " " + (Join-PdArgs $Arguments)) }
    return [PdProcess]::Run($File, (Join-PdArgs $Arguments), $WorkingDirectory, $Log, $env2, $TimeoutSeconds, -not $Quiet)
}

function Add-PdLogLine([string]$Log, [string]$Text) {
    $dir = Split-Path -Parent $Log
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::AppendAllText($Log, $Text + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

# Text files written by these scripts: UTF-8 without a byte order mark, LF line ends.
function Write-PdText([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))
}

# Git for Windows' bash (never C:\Windows\System32\bash.exe, which is WSL's).
function Get-PdGitBash {
    $candidates = @()
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($git) {
        $gitDir = Split-Path -Parent (Split-Path -Parent $git.Source) # ...\Git\cmd\git.exe -> ...\Git
        $candidates += (Join-Path $gitDir 'bin\bash.exe')
        $candidates += (Join-Path $gitDir 'usr\bin\bash.exe')
    }
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ($root) {
            $candidates += (Join-Path $root 'Git\bin\bash.exe')
            $candidates += (Join-Path $root 'Programs\Git\bin\bash.exe')
        }
    }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

# A Windows path as WSL sees it (C:\x\y -> /mnt/c/x/y); the box mounts drives under /mnt/.
function ConvertTo-PdWslPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^([A-Za-z]):\\?(.*)$') { throw "not a drive path: $full" }
    return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + ($Matches[2] -replace '\\', '/')
}

# A Windows path as Git Bash sees it (C:\x\y -> /c/x/y).
function ConvertTo-PdBashPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^([A-Za-z]):\\?(.*)$') { throw "not a drive path: $full" }
    return '/' + $Matches[1].ToLowerInvariant() + '/' + ($Matches[2] -replace '\\', '/')
}
