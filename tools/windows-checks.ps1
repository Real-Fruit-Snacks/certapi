<#
.SYNOPSIS
    Checks certapi on a real Windows machine: the test suite, then the exe the release ships,
    run against local test servers, then the parts that need admin rights or the internet.

.DESCRIPTION
    Everything is written to a new folder under %TEMP%, and a PASS / FAIL / SKIP / INFO table is
    printed at the end. results.txt in that folder holds the table, the details and the failed
    test names: that file is what to send back when something fails.

    Your own certapi configuration (certapi.config.json, %AppData%\certapi\config.json,
    CERTAPI_CONFIG) is ignored for the whole run, so a saved profile cannot change the results.

    Nothing outside the results folder is changed, except:
      - dotnet restores packages into the NuGet cache and writes bin and obj folders in the
        clone, as it always does
      - a throwaway client certificate "CN=certapi-check-<id>" in CurrentUser\My, removed at the end
      - with -Admin, the same certificate in LocalMachine\My and a netsh TLS binding on
        127.0.0.1:18443 (only when that port has none), both removed at the end
      - with -State, %AppData%\CertApiTester\state.json, copied into the results folder first
        and put back at the end (close the desktop app before using -State)
    A run that was cut off (the window closed) is cleaned up by the next one: leftover
    certapi-check certificates are removed and leftover test servers stopped.

    Run it with the patches applied to a clean clone. Windows PowerShell 5.1 and PowerShell 7
    both work.

.PARAMETER Repo
    The certapi clone. Defaults to the folder above this script.

.PARAMETER Admin
    Also run what needs an elevated terminal: the virtual-host tests (they skip otherwise), a
    certificate installed in both CurrentUser and LocalMachine, and the netsh binding test.

.PARAMETER Network
    Also run what reaches the internet: HTTP/3 against cloudflare.com, doctor against github.com
    through this machine's proxy, and a real update of a scratch copy of the exe from GitHub.

.PARAMETER State
    Also check what happens when the live workspace cannot be read, using your real
    %AppData%\CertApiTester\state.json (backed up first, restored after).

.PARAMETER SkipTests
    Skip the build and the test suite; publish and check the exe only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\windows-checks.ps1

.EXAMPLE
    # Everything, from an elevated terminal:
    powershell -ExecutionPolicy Bypass -File tools\windows-checks.ps1 -Admin -Network -State
#>
[CmdletBinding()]
param(
    [string]$Repo,
    [switch]$Admin,
    [switch]$Network,
    [switch]$State,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1

# This file is plain ASCII on purpose: Windows PowerShell 5.1 reads a script without a byte-order
# mark in the ANSI code page. Non-ASCII test text is built from code points instead.
$Zoe = 'Zo' + [string][char]0x00EB           # "Zoe" with an e-diaeresis

# The console's code pages, read straight from the console (.NET caches its own copy, and
# `chcp` runs cmd's AutoRun, which can itself switch the console to UTF-8).
if (-not ('CertapiChecks.Kernel32' -as [type])) { Add-Type -Namespace CertapiChecks -Name Kernel32 -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetConsoleCP();
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetConsoleOutputCP();
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool SetConsoleCP(uint codePage);
[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool SetConsoleOutputCP(uint codePage);
'@ }
function Get-CodePages { [pscustomobject]@{ In = [CertapiChecks.Kernel32]::GetConsoleCP(); Out = [CertapiChecks.Kernel32]::GetConsoleOutputCP() } }
$StartPages = Get-CodePages

if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
$Repo = (Resolve-Path $Repo).Path
if (-not (Test-Path (Join-Path $Repo 'ApiTester.sln'))) {
    throw "No ApiTester.sln in $Repo. Run this from tools\ in the clone, or pass -Repo <clone>."
}

$id = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$Work = Join-Path $env:TEMP ("certapi-checks-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $Work | Out-Null
$IsElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# An empty configuration, named by the variable that outranks every other place certapi looks.
$NoConfig = Join-Path $Work 'no-config.json'
[IO.File]::WriteAllText($NoConfig, '{}')
$ConfigBefore = $env:CERTAPI_CONFIG
$env:CERTAPI_CONFIG = $NoConfig

Write-Host "certapi Windows checks"
Write-Host "  clone:   $Repo"
Write-Host "  results: $Work"
Write-Host ""

# ---------------------------------------------------------------------------------------------
# Plumbing
# ---------------------------------------------------------------------------------------------

$script:Results = New-Object System.Collections.Generic.List[object]
$script:Mocks = New-Object System.Collections.Generic.List[object]
$script:Outcome = $null
$script:Detail = ''

function Pass([string]$Detail = '') { $script:Outcome = 'PASS'; $script:Detail = $Detail }
function Fail([string]$Detail = '') { $script:Outcome = 'FAIL'; $script:Detail = $Detail }
function Skip([string]$Detail = '') { $script:Outcome = 'SKIP'; $script:Detail = $Detail }
function Info([string]$Detail = '') { $script:Outcome = 'INFO'; $script:Detail = $Detail }

function Add-Result([string]$Name, [string]$Outcome, [string]$Detail) {
    $script:Results.Add([pscustomobject]@{ Check = $Name; Result = $Outcome; Detail = $Detail })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'DarkYellow'; INFO = 'Cyan' }[$Outcome]
    Write-Host ("[{0}] {1}" -f $Outcome, $Name) -ForegroundColor $color
    if ($Detail) { Write-Host ('       ' + ($Detail -replace "`n", "`n       ")) }
}

function Invoke-Check([string]$Name, [scriptblock]$Body) {
    $script:Outcome = $null
    $script:Detail = ''
    try { $null = & $Body }
    catch {
        $script:Outcome = 'FAIL'
        $script:Detail = "the check itself failed (line $($_.InvocationInfo.ScriptLineNumber)): " + $_.Exception.Message
    }
    if (-not $script:Outcome) { $script:Outcome = 'FAIL'; $script:Detail = 'the check reached no verdict' }
    Add-Result $Name $script:Outcome $script:Detail
}

# One argument for a Windows command line, quoted the way the C runtime and .NET split it.
function Format-Arg([string]$Arg) {
    if ($Arg -eq '') { return '""' }
    if ($Arg -notmatch '[\s"]') { return $Arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($ch in $Arg.ToCharArray()) {
        if ($ch -eq '\') { $slashes++; continue }
        if ($ch -eq '"') { [void]$sb.Append('\' * ($slashes * 2 + 1)); [void]$sb.Append('"'); $slashes = 0; continue }
        if ($slashes) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
        [void]$sb.Append($ch)
    }
    [void]$sb.Append('\' * ($slashes * 2))
    [void]$sb.Append('"')
    $sb.ToString()
}

# Runs a program and returns its exit code, its stdout as raw bytes (and as UTF-8 text) and its
# stderr. Raw bytes, because what the console encoding does to them is one of the things checked.
# Stdin is redirected only when bytes are given; otherwise the program shares this console.
function Invoke-Exe {
    param([string]$Exe, [string[]]$Arguments = @(), [byte[]]$Stdin, [int]$TimeoutSec = 120, [string]$WorkingDirectory = $Work)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = (@($Arguments) | ForEach-Object { Format-Arg $_ }) -join ' '
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = ($null -ne $Stdin)
    $psi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = New-Object System.IO.MemoryStream
    $copy = $p.StandardOutput.BaseStream.CopyToAsync($out)
    $err = $p.StandardError.ReadToEndAsync()
    if ($null -ne $Stdin) {
        try {
            $p.StandardInput.BaseStream.Write($Stdin, 0, $Stdin.Length)
            $p.StandardInput.BaseStream.Flush()
            $p.StandardInput.Close()
        } catch { }   # the program ended before reading it all; its output says why
    }
    $timedOut = -not $p.WaitForExit($TimeoutSec * 1000)
    if ($timedOut) {
        try { if ($PSVersionTable.PSVersion.Major -ge 7) { $p.Kill($true) } else { $p.Kill() } } catch { }
        $p.WaitForExit()
    }
    [void]$copy.Wait(10000)
    $errText = $(if ($err.Wait(10000)) { $err.Result } else { '' })
    $bytes = $out.ToArray()
    [pscustomobject]@{
        Code     = $(if ($timedOut) { -1 } else { $p.ExitCode })
        TimedOut = $timedOut
        Bytes    = $bytes
        Out      = (New-Object System.Text.UTF8Encoding($false)).GetString($bytes)
        Err      = $errText
    }
}

# A JSON array as a PowerShell array, the same on 5.1 (which emits the array as one object) and 7.
function ConvertFrom-JsonArray([string]$Json) {
    $x = $Json | ConvertFrom-Json
    if ($null -eq $x) { return , @() }
    , @($x)
}

function Get-FreePort {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
    $port
}

function Wait-Port([int]$Port, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $c = New-Object System.Net.Sockets.TcpClient
        try { $c.Connect('127.0.0.1', $Port); return $true } catch { Start-Sleep -Milliseconds 250 } finally { $c.Close() }
    }
    $false
}

# Starts a long-running certapi (mock or serve) in its own hidden window; stopped at the end.
function Start-Background([string]$Name, [string[]]$Arguments, [int]$Port) {
    $argLine = (@($Arguments) | ForEach-Object { Format-Arg $_ }) -join ' '
    $log = Join-Path $Work "$Name.log"
    $p = Start-Process -FilePath $script:Exe -ArgumentList $argLine -WorkingDirectory $Work -WindowStyle Hidden `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru
    $null = $p.Handle      # keeps the process object bound to this process, so its id is not reused under it
    $script:Mocks.Add($p)
    if (-not (Wait-Port $Port)) { throw "$Name did not start listening on port $Port (see $log.err)" }
    $p
}

function Start-Mock([string]$Name, [string[]]$Extra = @()) {
    $port = Get-FreePort
    $null = Start-Background $Name (@('mock', '--port', "$port", '-q') + $Extra) $port
    $scheme = $(if ($Extra -contains '--tls' -or $Extra -contains '--mtls') { 'https' } else { 'http' })
    "${scheme}://127.0.0.1:$port"
}

function Show-Hex([byte[]]$Bytes, [int]$Max = 48) {
    (@($Bytes | Select-Object -First $Max) | ForEach-Object { $_.ToString('X2') }) -join ' '
}

function Find-Bytes([byte[]]$Haystack, [byte[]]$Needle) {
    for ($i = 0; $i -le $Haystack.Length - $Needle.Length; $i++) {
        $hit = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) { if ($Haystack[$i + $j] -ne $Needle[$j]) { $hit = $false; break } }
        if ($hit) { return $i }
    }
    -1
}

function Tail([string]$Text, [int]$Lines = 15) {
    (($Text -split "`r?`n") | Where-Object { $_ -ne '' } | Select-Object -Last $Lines) -join "`n"
}

function Remove-TestCertificate([string]$Thumbprint) {
    $stores = @('Cert:\CurrentUser\My', 'Cert:\CurrentUser\CA')
    if ($IsElevated) { $stores += 'Cert:\LocalMachine\My' }
    foreach ($store in $stores) {
        $item = Join-Path $store $Thumbprint
        if (Test-Path $item) {
            try { Remove-Item $item -DeleteKey -ErrorAction Stop }
            catch { try { Remove-Item $item -ErrorAction Stop } catch { Write-Host "could not remove $item : $($_.Exception.Message)" -ForegroundColor Yellow } }
        }
    }
}

$Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
$script:Exe = $null
$script:TestCert = $null
$script:Thumb = $null
$script:built = $false
$PagesBeforeCertapi = $null
$PublishCli = Join-Path $Work 'publish-cli'
$PublishApp = Join-Path $Work 'publish-app'

# ---------------------------------------------------------------------------------------------
# The checks
# ---------------------------------------------------------------------------------------------

try {

Write-Host "== This machine" -ForegroundColor White

Invoke-Check 'Machine' {
    $os = (Get-CimInstance Win32_OperatingSystem)
    $dn = $(if ($Dotnet) { (Invoke-Exe $Dotnet.Source @('--version')).Out.Trim() } else { 'not found' })
    Info ("{0} (build {1}) | PowerShell {2} | .NET SDK {3} | elevated: {4} | console code pages: input {5}, output {6}" -f
        $os.Caption, $os.BuildNumber, $PSVersionTable.PSVersion, $dn, $IsElevated, $StartPages.In, $StartPages.Out)
}
if (-not $Dotnet) { throw 'The .NET SDK (dotnet) is not on PATH; install the .NET 9 SDK and run again.' }

Invoke-Check 'Left behind by an earlier run that was cut off' {
    $notes = @()
    $stale = @(Get-Process -Name certapi -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -like (Join-Path $env:TEMP 'certapi-checks-*') } catch { $false } })
    foreach ($p in $stale) { try { Stop-Process -Id $p.Id -Force; $notes += "stopped a test server left running (pid $($p.Id))" } catch { } }
    $stores = @('Cert:\CurrentUser\My', 'Cert:\CurrentUser\CA')
    if ($IsElevated) { $stores += 'Cert:\LocalMachine\My' }
    foreach ($store in $stores) {
        foreach ($c in @(Get-ChildItem $store | Where-Object { $_.Subject -like 'CN=certapi-check-*' })) {
            Remove-TestCertificate $c.Thumbprint
            $notes += "removed $($c.Subject) from $store"
        }
    }
    foreach ($d in @(Get-ChildItem $env:TEMP -Directory -Filter 'certapi-checks-*' | Where-Object { $_.FullName -ne $Work })) {
        if ((Test-Path (Join-Path $d.FullName 'state.json.original')) -and -not (Test-Path (Join-Path $d.FullName 'state-restored.txt'))) {
            $notes += "$($d.Name) holds a state.json.original that was never put back: if your workspace is broken, copy it to %AppData%\CertApiTester\state.json"
        }
    }
    if ($notes.Count -eq 0) { Pass 'nothing' }
    elseif (@($notes | Where-Object { $_ -like '*never put back*' }).Count -gt 0) { Fail ($notes -join "`n") }
    else { Info ($notes -join "`n") }
}

Write-Host ""
Write-Host "== Build and test suite" -ForegroundColor White

function Invoke-Tests([string]$Filter, [string]$Trx) {
    $r = Invoke-Exe $Dotnet.Source @('test', (Join-Path $Repo 'ApiTester.sln'), '--disable-build-servers', '-c', 'Release', '--no-build', '--filter', $Filter,
        '--results-directory', (Join-Path $Work 'tests'), '--logger', "trx;LogFileName=$Trx") -TimeoutSec 3600 -WorkingDirectory $Repo
    [IO.File]::WriteAllText((Join-Path $Work "$Trx.log"), $r.Out + $r.Err)
    $path = Join-Path (Join-Path $Work 'tests') $Trx
    if (-not (Test-Path $path)) { return [pscustomobject]@{ Ran = $false; Code = $r.Code; Log = (Tail ($r.Out + $r.Err)) } }
    [xml]$x = Get-Content -Raw -Encoding UTF8 $path
    $c = $x.TestRun.ResultSummary.Counters
    $all = @($x.TestRun.Results.UnitTestResult)
    $failed = @($all | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object {
        $msg = ''
        if ($_.Output -and $_.Output.ErrorInfo -and $_.Output.ErrorInfo.Message) {
            $msg = (($_.Output.ErrorInfo.Message -split "`r?`n") | Where-Object { $_ -ne '' } | Select-Object -First 2) -join ' | '
        }
        "$($_.testName)`n    $msg"
    })
    $skipped = @($all | Where-Object { $_.outcome -eq 'NotExecuted' } | ForEach-Object { $_.testName })
    [pscustomobject]@{
        Ran = $true; Code = $r.Code; Outcome = [string]$x.TestRun.ResultSummary.outcome
        Total = [int]$c.total; Passed = [int]$c.passed; Failed = $failed; Skipped = $skipped; Log = (Tail ($r.Out + $r.Err))
    }
}

# A run counts only when it finished, ran something, and nothing failed.
function Get-TestVerdict($t, [string]$Summary) {
    if (-not $t.Ran) { Fail ("no test results were written (exit $($t.Code)):`n" + $t.Log); return }
    if ($t.Failed.Count -gt 0) { Fail ($Summary + "`n" + (($t.Failed | Select-Object -First 40) -join "`n")); return }
    if ($t.Total -eq 0) { Fail ("no tests ran (exit $($t.Code))`n" + $t.Log); return }
    if ($t.Code -ne 0 -or $t.Outcome -in @('Aborted', 'Error', 'Failed', 'Timeout')) {
        Fail ("$Summary, but the run ended with exit $($t.Code), outcome $($t.Outcome):`n" + $t.Log); return
    }
    Pass $Summary
}

if ($SkipTests) {
    Invoke-Check 'Build and test suite' { Skip '-SkipTests' }
} else {
    Invoke-Check 'Build (Release, warnings as errors)' {
        $r = Invoke-Exe $Dotnet.Source @('build', (Join-Path $Repo 'ApiTester.sln'), '--disable-build-servers', '-c', 'Release', '-warnaserror', '-nologo', '-v', 'q') -TimeoutSec 1800 -WorkingDirectory $Repo
        [IO.File]::WriteAllText((Join-Path $Work 'build.log'), $r.Out + $r.Err)
        if ($r.Code -eq 0) { $script:built = $true; Pass }
        else { Fail ((($r.Out + $r.Err) -split "`r?`n" | Where-Object { $_ -match ': (error|warning) ' } | Select-Object -Unique -First 15) -join "`n") }
    }

    Invoke-Check 'Test suite (everything CI runs)' {
        if (-not $script:built) { Skip 'the build failed'; return }
        $t = Invoke-Tests 'Category!=StoreRoundTrip' 'suite.trx'
        $summary = "$($t.Passed) passed, $($t.Failed.Count) failed, $($t.Skipped.Count) skipped, of $($t.Total)"
        if ($t.Ran -and $t.Skipped.Count -gt 0 -and -not $IsElevated) { $summary += " (the virtual-host tests run only from an elevated terminal)" }
        Get-TestVerdict $t $summary
    }

    Invoke-Check 'Certificate-store round trip (adds, then removes, a test certificate in CurrentUser\My)' {
        if (-not $script:built) { Skip 'the build failed'; return }
        $t = Invoke-Tests 'Category=StoreRoundTrip' 'store.trx'
        Get-TestVerdict $t "$($t.Passed) passed"
    }
}

Write-Host ""
Write-Host "== The exe the release ships" -ForegroundColor White

Invoke-Check 'Publish the CLI and the app exactly as release.yml does' {
    foreach ($pair in @(@('src\ApiTester.Cli', $PublishCli), @('src\ApiTester.App', $PublishApp))) {
        $r = Invoke-Exe $Dotnet.Source @('publish', (Join-Path $Repo $pair[0]), '--disable-build-servers', '-c', 'Release', '-r', 'win-x64', '--self-contained', '-o', $pair[1], '-nologo', '-v', 'q') -TimeoutSec 1800 -WorkingDirectory $Repo
        if ($r.Code -ne 0) { Fail ("dotnet publish $($pair[0]) failed:`n" + (Tail ($r.Out + $r.Err))); return }
    }
    if (-not (Test-Path (Join-Path $PublishCli 'certapi.exe'))) { Fail 'publish produced no certapi.exe'; return }
    # The release uploads certapi.exe and ApiTester.App.exe and nothing else.
    $released = Join-Path $Work 'released'
    New-Item -ItemType Directory -Path $released | Out-Null
    Copy-Item (Join-Path $PublishCli 'certapi.exe') $released
    $script:Exe = Join-Path $released 'certapi.exe'
    $extraCli = @(Get-ChildItem $PublishCli -File | Where-Object { $_.Name -ne 'certapi.exe' -and $_.Extension -ne '.pdb' } | ForEach-Object { $_.Name })
    $extraApp = @(Get-ChildItem $PublishApp -File | Where-Object { $_.Name -ne 'ApiTester.App.exe' -and $_.Extension -ne '.pdb' } | ForEach-Object { $_.Name })
    $note = "the checks below use certapi.exe alone, as a release user gets it"
    if ($extraCli.Count -gt 0) { $note += "`nleft beside certapi.exe (not in the release): " + ($extraCli -join ', ') }
    if ($extraApp.Count -gt 0) { $note += "`nleft beside ApiTester.App.exe (not in the release): " + ($extraApp -join ', ') }
    Pass $note
}
if (-not $script:Exe) { throw 'Nothing to check without a published certapi.exe.' }
$PagesBeforeCertapi = Get-CodePages

Invoke-Check 'certapi --version' {
    $r = Invoke-Exe $script:Exe @('--version')
    if ($r.Code -eq 0) { Pass $r.Out.Trim() } else { Fail ("exit $($r.Code)`n" + $r.Err) }
}

Invoke-Check 'selftest: mutual TLS end to end through Windows TLS (SChannel)' {
    $r = Invoke-Exe $script:Exe @('selftest')
    if ($r.Code -eq 0) { Pass (Tail $r.Out 3) } else { Fail ("exit $($r.Code)`n" + (Tail ($r.Out + $r.Err))) }
}

Invoke-Check 'Start-up cleanup removes what an update left (the patched exe)' {
    $dir = Join-Path $Work 'cleanup'
    New-Item -ItemType Directory -Path $dir | Out-Null
    $copy = Join-Path $dir 'certapi.exe'
    Copy-Item $script:Exe $copy
    foreach ($f in @('certapi.exe.old', 'certapi.exe.old-1234abcd', 'certapi.exe.new')) { [IO.File]::WriteAllText((Join-Path $dir $f), 'left behind') }
    $r = Invoke-Exe $copy @('--version') -WorkingDirectory $dir
    $left = @(Get-ChildItem $dir -File | Where-Object { $_.Name -like 'certapi.exe.?*' } | ForEach-Object { $_.Name })
    # A fresh .new may be an update in progress in the other program, so it stays.
    if ($r.Code -ne 0) { Fail ("exit $($r.Code)`n" + $r.Err) }
    elseif (($left -join ',') -eq 'certapi.exe.new') { Pass 'removed .old and .old-1234abcd; kept the fresh .new' }
    else { Fail ("left: " + $(if ($left.Count) { $left -join ', ' } else { 'nothing (the fresh .new should have stayed)' })) }
}

Write-Host ""
Write-Host "== Against local test servers" -ForegroundColor White

$routesFile = Join-Path $Work 'routes.json'
# A trap: the first route answers only a request that carries Basic credentials.
[IO.File]::WriteAllText($routesFile, @'
{"routes":[
 {"match":{"path":"/basic","headers":{"Authorization":"Basic"}},"respond":{"status":200,"body":"LEAKED"}},
 {"match":{"path":"/basic"},"respond":{"status":401,"headers":{"WWW-Authenticate":"Basic realm=\"certapi-check\""},"body":"sign in"}}
]}
'@)
$Echo = $null; $Routes = $null; $Tls = $null; $Mtls = $null
Invoke-Check 'Start the local test servers (certapi mock)' {
    $script:Echo = Start-Mock 'mock-echo'
    $script:Routes = Start-Mock 'mock-routes' @('--routes', $routesFile)
    $script:Tls = Start-Mock 'mock-tls' @('--tls', '--cert-dir', (Join-Path $Work 'mock-tls-certs'))
    $script:Mtls = Start-Mock 'mock-mtls' @('--mtls', '--cert-dir', (Join-Path $Work 'mock-mtls-certs'))
    Pass "$($script:Echo), $($script:Routes), $($script:Tls), $($script:Mtls)"
}
if (-not $Mtls) { throw 'The test servers did not start; the checks against them cannot run.' }

Invoke-Check 'Redirected output is UTF-8 (send --pretty > file)' {
    # --pretty goes through the console's text encoding; a plain send writes the body as bytes.
    $r = Invoke-Exe $script:Exe @('send', "$Echo/echo", '-X', 'POST', '-d', ('{"name":"' + $Zoe + '"}'), '--pretty')
    if ($r.Code -ne 0) { Fail ("exit $($r.Code)`n" + $r.Err); return }
    $at = Find-Bytes $r.Bytes ([byte[]](0x5A, 0x6F, 0xC3, 0xAB))
    if ($at -ge 0) {
        if ($r.Bytes.Length -ge 3 -and $r.Bytes[0] -eq 0xEF -and $r.Bytes[1] -eq 0xBB -and $r.Bytes[2] -eq 0xBF) { Fail 'UTF-8, but with a byte-order mark at the start' }
        else { Pass "Zo + C3 AB found at byte $at" }
    } else {
        $z = Find-Bytes $r.Bytes ([byte[]](0x5A, 0x6F))
        $around = $(if ($z -ge 0) { Show-Hex ($r.Bytes[$z..([Math]::Min($z + 5, $r.Bytes.Length - 1))]) } else { Show-Hex $r.Bytes })
        Fail "the e-diaeresis did not arrive as UTF-8 (C3 AB); bytes after 'Zo': $around"
    }
}

Invoke-Check 'Piped input is read as UTF-8 (an MCP client sending non-ASCII)' {
    $init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"windows-checks","version":"1"}}}'
    $ready = '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    $json = '{\"name\":\"' + $Zoe + '\"}'
    $call = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"send_request","arguments":{"method":"POST","url":"' + "$Echo/echo" + '","body":"' + $json + '","contentType":"application/json"}}}'
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes(($init, $ready, $call -join "`n") + "`n")
    $r = Invoke-Exe $script:Exe @('mcp', '--allow', '127.0.0.1') -Stdin $bytes -TimeoutSec 60
    $line = ($r.Out -split "`r?`n") | Where-Object { $_ -match '"id":\s*2' } | Select-Object -First 1
    if (-not $line) { Fail ("no answer to the tool call (exit $($r.Code))`n" + (Tail $r.Err)); return }
    $answer = $line | ConvertFrom-Json
    $response = $answer.result.content[0].text | ConvertFrom-Json
    $echoed = ($response.body | ConvertFrom-Json).body
    $expected = '{"name":"' + $Zoe + '"}'
    if ($echoed -ceq $expected) { Pass 'the server received {"name":"Zo<e-diaeresis>"} intact' }
    else {
        $codes = (@($echoed.ToCharArray()) | ForEach-Object { ([int]$_).ToString('X4') }) -join ' '
        Fail "the server received something else; its UTF-16 code units: $codes"
    }
}

Invoke-Check 'The console code pages are put back after every run' {
    $now = Get-CodePages
    $notes = @()
    if ($PagesBeforeCertapi.In -ne $StartPages.In -or $PagesBeforeCertapi.Out -ne $StartPages.Out) {
        $notes += "dotnet changed them before certapi ran: input $($StartPages.In) -> $($PagesBeforeCertapi.In), output $($StartPages.Out) -> $($PagesBeforeCertapi.Out)"
    }
    $base = $PagesBeforeCertapi
    if ($base.Out -eq 0) { Info 'this host has no console to check'; return }
    if ($base.In -eq 65001 -and $base.Out -eq 65001) { Info (@("this console is already UTF-8 (65001), so this shows nothing; run from a console on its default code page") + $notes -join "`n"); return }
    if ($now.In -ne $base.In -or $now.Out -ne $base.Out) {
        Fail (@("the runs above left input $($now.In), output $($now.Out) (it was input $($base.In), output $($base.Out))") + $notes -join "`n"); return
    }
    & $script:Exe --version | Out-Null
    $afterOut = Get-CodePages
    '' | & $script:Exe --version | Out-Null        # piped stdin: the input code page is switched too
    $afterIn = Get-CodePages
    if ($afterOut.In -ne $base.In -or $afterOut.Out -ne $base.Out) { Fail (@("after a run with output piped: input $($afterOut.In), output $($afterOut.Out); was $($base.In), $($base.Out)") + $notes -join "`n") }
    elseif ($afterIn.In -ne $base.In -or $afterIn.Out -ne $base.Out) { Fail (@("after a run with input piped: input $($afterIn.In), output $($afterIn.Out); was $($base.In), $($base.Out)") + $notes -join "`n") }
    else { Pass (@("input $($base.In), output $($base.Out) before and after, with output and with input piped") + $notes -join "`n") }
}

Invoke-Check 'send --windows-auth never answers a Basic challenge (fake credentials)' {
    $r = Invoke-Exe $script:Exe @('send', "$Routes/basic", '--windows-auth', '--windows-user', 'CHECK\nobody', '--windows-password', 'not-a-real-password')
    if ($r.Out -match 'LEAKED') { Fail 'the Basic challenge was answered with the Windows credentials' }
    elseif ($r.Err -match '(?m)^401 ') { Pass 'got the 401 back; nothing was sent' }
    else { Fail ("unexpected answer (exit $($r.Code)):`n" + (Tail ($r.Out + $r.Err))) }
}

Invoke-Check 'serve --windows-auth never answers a Basic challenge (fake credentials)' {
    $port = Get-FreePort
    $null = Start-Background 'serve-basic' @('serve', $Routes, '--port', "$port", '--windows-auth', '--windows-user', 'CHECK\nobody', '--windows-password', 'not-a-real-password') $port
    $r = Invoke-Exe $script:Exe @('send', "http://127.0.0.1:$port/basic")
    if ($r.Out -match 'LEAKED') { Fail 'the gateway answered the Basic challenge with the Windows credentials' }
    elseif ($r.Err -match '(?m)^401 ') { Pass 'the 401 came back through the gateway; nothing was sent' }
    else { Fail ("unexpected answer (exit $($r.Code)):`n" + (Tail ($r.Out + $r.Err))) }
}

Invoke-Check 'send --windows-auth answers an NTLM challenge with your sign-in (mock /windows-auth)' {
    $r = Invoke-Exe $script:Exe @('send', "$Echo/windows-auth", '--windows-auth')
    if ($r.Err -match '(?m)^200 ' -and $r.Out -match '"authenticated":"(NTLM|Negotiate)"') { Pass ("answered with " + $Matches[1]) }
    else { Fail ("exit $($r.Code)`n" + (Tail ($r.Out + $r.Err))) }
}

Invoke-Check 'A client certificate from CurrentUser\My over mTLS' {
    $script:TestCert = New-SelfSignedCertificate -Subject "CN=certapi-check-$id" -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature -KeyExportPolicy Exportable -NotAfter (Get-Date).AddDays(2) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.2')
    $script:Thumb = $script:TestCert.Thumbprint
    $found = ConvertFrom-JsonArray (Invoke-Exe $script:Exe @('certs', '--json', '--filter', "certapi-check-$id")).Out
    if ($found.Count -ne 1) { Fail "certs listed it $($found.Count) times"; return }
    $r = Invoke-Exe $script:Exe @('send', "$Mtls/", '--cert', $script:Thumb, '--insecure')
    if ($r.Code -eq 0 -and $r.Out -match "CN=certapi-check-$id") { Pass "presented CN=certapi-check-$id (a CNG software key)" }
    else { Fail ("exit $($r.Code)`n" + (Tail ($r.Out + $r.Err))) }
}

Invoke-Check '--cert-pin does not break a key that has no PIN' {
    if (-not $script:Thumb) { Skip 'no test certificate'; return }
    $r = Invoke-Exe $script:Exe @('send', "$Mtls/", '--cert', $script:Thumb, '--cert-pin', '0000', '--insecure')
    if ($r.Code -eq 0 -and $r.Out -match "CN=certapi-check-$id") { Pass 'sent with the certificate presented' }
    else { Fail ("exit $($r.Code)`n" + (Tail ($r.Out + $r.Err))) }
}

Invoke-Check 'doctor against a local TLS server' {
    $r = Invoke-Exe $script:Exe @('doctor', "$Tls/", '--insecure')
    if ($r.Code -eq 0) { Pass (Tail $r.Out 1) } else { Fail (Tail ($r.Out + $r.Err)) }
}

Invoke-Check 'doctor reports that an mTLS server asked for a certificate' {
    if (-not $script:Thumb) { Skip 'no test certificate'; return }
    $r = Invoke-Exe $script:Exe @('doctor', "$Mtls/", '--insecure', '--cert', $script:Thumb)
    $lines = ($r.Out -split "`r?`n") | Where-Object { $_ -match 'client certificate' }
    if ($r.Out -match 'did not ask for one') { Fail ("it says the server did not ask:`n" + ($lines -join "`n")) }
    elseif ($r.Out -match 'asked for one without naming|accepts client certificates from') { Pass (($lines | ForEach-Object { $_.Trim() }) -join "`n") }
    else { Fail (Tail ($r.Out + $r.Err)) }
}

Invoke-Check 'proxy: Windows (WinHTTP) and .NET agree on the proxy' {
    $lines = @()
    $bad = $false
    $s = $null
    foreach ($url in @('https://github.com', 'http://localhost:8080/')) {
        $r = Invoke-Exe $script:Exe @('proxy', $url, '--json') -TimeoutSec 60
        if ($r.Code -ne 0) { Fail ("proxy $url exited $($r.Code)`n" + (Tail $r.Err)); return }
        $j = $r.Out | ConvertFrom-Json
        $d = $j.decision
        $lines += "$url -> WinHTTP: $($d.winHttp)   .NET: $($d.dotNet)" + $(if ($d.winHttpError) { "   ($($d.winHttpError))" } else { '' })
        if ($d.disagrees) { $bad = $true }
        $s = $j.settings
    }
    $lines += "settings: auto-detect $($s.autoDetect), PAC $($s.autoConfigUrl), proxy $($s.proxyServer), bypass $($s.proxyOverride)"
    if ($bad) { Fail ("they disagree (this is the finding to send back):`n" + ($lines -join "`n")) } else { Pass ($lines -join "`n") }
}

Write-Host ""
Write-Host "== Needs an elevated terminal (-Admin)" -ForegroundColor White

if (-not $Admin) {
    Invoke-Check 'A certificate in both stores; the netsh binding test' { Skip 'run with -Admin from an elevated terminal' }
} elseif (-not $IsElevated) {
    Invoke-Check 'A certificate in both stores; the netsh binding test' { Skip '-Admin needs an elevated terminal' }
} else {
    $pfx = Join-Path $Work 'check.pfx'
    $cer = Join-Path $Work 'check.cer'
    $pw = ConvertTo-SecureString -String "certapi-check-$id" -AsPlainText -Force

    Invoke-Check 'A certificate in both CurrentUser and LocalMachine is listed once' {
        if (-not $script:Thumb) { Skip 'no test certificate'; return }
        # By path, not by object: under PowerShell 7's Windows compatibility the object has lost its key.
        $null = Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($script:Thumb)" -FilePath $pfx -Password $pw
        $null = Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\LocalMachine\My' -Password $pw -Exportable
        $found = ConvertFrom-JsonArray (Invoke-Exe $script:Exe @('certs', '--store', 'LocalMachine', '--json', '--filter', "certapi-check-$id")).Out
        if ($found.Count -eq 1) { Pass 'listed once' } else { Fail "listed $($found.Count) times" }
    }

    Invoke-Check 'Key only in LocalMachine (a .cer in CurrentUser): still listed, and usable' {
        if (-not (Test-Path "Cert:\LocalMachine\My\$($script:Thumb)")) { Skip 'the LocalMachine copy is not there'; return }
        $null = Export-Certificate -Cert "Cert:\CurrentUser\My\$($script:Thumb)" -FilePath $cer -Type CERT
        Remove-Item "Cert:\CurrentUser\My\$($script:Thumb)" -DeleteKey
        $null = Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\My'
        $found = ConvertFrom-JsonArray (Invoke-Exe $script:Exe @('certs', '--store', 'LocalMachine', '--json', '--filter', "certapi-check-$id")).Out
        if ($found.Count -ne 1) { Fail "listed $($found.Count) times (expected once, from LocalMachine)"; return }
        $r = Invoke-Exe $script:Exe @('send', "$Mtls/", '--cert', $script:Thumb, '--store', 'LocalMachine', '--insecure')
        if ($r.Code -eq 0 -and $r.Out -match "CN=certapi-check-$id") { Pass 'listed once and presented with the LocalMachine key' }
        else { Fail ("listed once, but the send failed (exit $($r.Code))`n" + (Tail ($r.Out + $r.Err))) }
    }

    Invoke-Check 'netsh binding test (TlsBinding): certapi reads a binding as netsh shows it' {
        if (-not $script:built) { Skip 'needs the build (not with -SkipTests)'; return }
        if (-not (Test-Path "Cert:\LocalMachine\My\$($script:Thumb)")) { Skip 'the LocalMachine copy is not there'; return }
        $ipport = '127.0.0.1:18443'
        $existing = Invoke-Exe 'netsh.exe' @('http', 'show', 'sslcert', "ipport=$ipport")
        if ($existing.Code -eq 0 -and $existing.Out -match '[0-9a-fA-F]{40}') { Skip "$ipport already has a binding; it is left alone"; return }
        $add = Invoke-Exe 'netsh.exe' @('http', 'add', 'sslcert', "ipport=$ipport", "certhash=$($script:Thumb)", 'appid={8c1f4a5e-6b7d-4e2a-9f3c-0123456789ab}', 'certstorename=MY')
        if ($add.Code -ne 0) { Fail ("netsh could not add the binding:`n" + (Tail ($add.Out + $add.Err))); return }
        try {
            $env:CERTAPI_TLS_BINDING_TESTS = '1'
            $t = Invoke-Tests 'Category=TlsBinding' 'tlsbinding.trx'
            Get-TestVerdict $t "$($t.Passed) passed (netsh output in this machine's language)"
        } finally {
            Remove-Item Env:\CERTAPI_TLS_BINDING_TESTS -ErrorAction SilentlyContinue
            $del = Invoke-Exe 'netsh.exe' @('http', 'delete', 'sslcert', "ipport=$ipport")
            if ($del.Code -ne 0) { Write-Host "could not remove the netsh binding on $ipport; remove it with: netsh http delete sslcert ipport=$ipport" -ForegroundColor Yellow }
        }
    }
}

Write-Host ""
Write-Host "== Needs the internet (-Network)" -ForegroundColor White

if (-not $Network) {
    Invoke-Check 'HTTP/3, doctor through your proxy, update' { Skip 'run with -Network' }
} else {
    Invoke-Check 'HTTP/3 from certapi.exe alone (as released)' {
        $r = Invoke-Exe $script:Exe @('send', 'https://cloudflare.com/cdn-cgi/trace', '--http3') -TimeoutSec 60
        $folder = Invoke-Exe (Join-Path $PublishCli 'certapi.exe') @('send', 'https://cloudflare.com/cdn-cgi/trace', '--http3') -TimeoutSec 60
        $inFolder = $(if ($folder.Out -match 'http=http/3') { 'works' } else { 'fails too: ' + (Tail $folder.Err 2) })
        if ($r.Out -match 'http=http/3') { Pass "answered over HTTP/3 (from the publish folder: $inFolder)" }
        else { Fail ("no HTTP/3 from the exe alone: " + (Tail ($r.Out + $r.Err) 3) + "`nfrom the publish folder, with its other files: $inFolder`n(HTTP/3 is UDP and does not go through an HTTP proxy)") }
    }

    Invoke-Check 'doctor https://github.com through this machine''s network' {
        $r = Invoke-Exe $script:Exe @('doctor', 'https://github.com') -TimeoutSec 180
        if ($r.Code -eq 0) { Pass (Tail $r.Out 1) } else { Fail (Tail ($r.Out + $r.Err) 25) }
    }

    Invoke-Check 'update replaces a running exe (a scratch copy, updated to the latest GitHub release)' {
        $dir = Join-Path $Work 'update'
        New-Item -ItemType Directory -Path $dir | Out-Null
        $copy = Join-Path $dir 'certapi.exe'
        Copy-Item $script:Exe $copy
        $before = (Get-Item $copy).Length
        $r = Invoke-Exe $copy @('update', '--force', '--no-app') -TimeoutSec 900 -WorkingDirectory $dir
        if ($r.Code -ne 0) { Fail ("update exited $($r.Code)`n" + (Tail ($r.Out + $r.Err))); return }
        if ($r.Out -notmatch 'replaced ') { Fail ("update exited 0 but did not say it replaced anything:`n" + (Tail ($r.Out + $r.Err))); return }
        $after = (Get-Item $copy).Length
        $leftovers = @(Get-ChildItem $dir -File | Where-Object { $_.Name -like 'certapi.exe.?*' } | ForEach-Object { $_.Name })
        $v = Invoke-Exe $copy @('--version') -WorkingDirectory $dir
        $note = "replaced ($before -> $after bytes); now $($v.Out.Trim()); left beside it: " +
            $(if ($leftovers.Count) { $leftovers -join ', ' } else { 'nothing' }) +
            "`n(the copy is now the published release, so what it cleans up on its next run is the release's code; the patched cleanup is checked above)"
        if ($v.Code -ne 0) { Fail ($note + "`nthe updated exe does not run: " + $v.Err) } else { Pass $note }
    }
}

Write-Host ""
Write-Host "== Your live workspace (-State)" -ForegroundColor White

if (-not $State) {
    Invoke-Check 'An unreadable workspace is kept, once' { Skip 'run with -State (uses your real state.json, backed up and restored)' }
} else {
    Invoke-Check 'An unreadable workspace is kept, once' {
        if (Get-Process -Name 'ApiTester.App' -ErrorAction SilentlyContinue) { Skip 'close the desktop app first: it saves over state.json'; return }
        $live = Join-Path (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'CertApiTester') 'state.json'
        $backup = Join-Path $Work 'state.json.original'
        $restored = Join-Path $Work 'state-restored.txt'
        $dir = Split-Path $live
        $hadDir = Test-Path $dir
        $had = Test-Path $live
        if ($had) { Copy-Item $live $backup }
        Write-Host "       (your state.json is copied to $backup until this check puts it back)"
        if (-not $hadDir) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $old = @(Get-ChildItem $dir -Filter 'state.json.unreadable-*.bak' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $broken = '{ this is not json'
        try {
            [IO.File]::WriteAllText($live, $broken)
            $r1 = Invoke-Exe $script:Exe @('send', "$Echo/")
            $savedOver = ([IO.File]::ReadAllText($live) -ne $broken)
            Start-Sleep -Milliseconds 1100      # the copy's name changes every second
            if ($savedOver) { [IO.File]::WriteAllText($live, $broken) }
            $r2 = Invoke-Exe $script:Exe @('send', "$Echo/")
            $new = @(Get-ChildItem $dir -Filter 'state.json.unreadable-*.bak' | Where-Object { $old -notcontains $_.Name })
            $warned = ($r1.Err -match 'could not read the live state') -and ($r1.Err -match 'kept as')
            $notes = $(if ($savedOver) { "`na plain send wrote state.json over the unreadable file (after keeping a copy)" } else { '' })
            if ($new.Count -eq 1 -and $warned -and ([IO.File]::ReadAllText($new[0].FullName) -eq $broken)) {
                Pass ("one copy ($($new[0].Name)) for two runs, and the warning says where it is" + $notes)
            } else {
                Fail ("copies made: $($new.Count); warning: " + (Tail $r1.Err 2) + "`nsecond run: " + (Tail $r2.Err 2) + $notes)
            }
        } finally {
            foreach ($f in @(Get-ChildItem $dir -Filter 'state.json.unreadable-*.bak' -ErrorAction SilentlyContinue | Where-Object { $old -notcontains $_.Name })) {
                Remove-Item $f.FullName -ErrorAction SilentlyContinue
            }
            try {
                if ($had) { Copy-Item $backup $live -Force }
                else {
                    Remove-Item $live -ErrorAction SilentlyContinue
                    if (-not $hadDir -and -not (Get-ChildItem $dir -Force)) { Remove-Item $dir }
                }
                [IO.File]::WriteAllText($restored, 'state.json was put back')
            } catch {
                Write-Host "YOUR state.json WAS NOT PUT BACK: $($_.Exception.Message)" -ForegroundColor Red
                Write-Host "Copy $backup to $live yourself." -ForegroundColor Red
                throw
            }
        }
    }
}

} catch {
    Add-Result 'The script stopped early' 'FAIL' ("line $($_.InvocationInfo.ScriptLineNumber): " + $_.Exception.Message)
} finally {
    foreach ($p in $script:Mocks) { try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { } }
    if ($script:Thumb) { Remove-TestCertificate $script:Thumb }
    foreach ($f in @('check.pfx', 'check.cer')) { Remove-Item (Join-Path $Work $f) -ErrorAction SilentlyContinue }
    # A certapi that was killed on a timeout cannot put the console back itself.
    if ($StartPages.Out -ne 0) {
        [void][CertapiChecks.Kernel32]::SetConsoleCP($StartPages.In)
        [void][CertapiChecks.Kernel32]::SetConsoleOutputCP($StartPages.Out)
    }
    if ($null -eq $ConfigBefore) { Remove-Item Env:\CERTAPI_CONFIG -ErrorAction SilentlyContinue } else { $env:CERTAPI_CONFIG = $ConfigBefore }
}

# ---------------------------------------------------------------------------------------------
# What only a person can check
# ---------------------------------------------------------------------------------------------

$app = Join-Path $PublishApp 'ApiTester.App.exe'
$manual = @"
By hand (none of this can be scripted). The app at $app
uses your real workspace, like the installed one.

  Desktop app
  - send from one tab, switch tabs before it answers: the answer stays with its own tab
  - Help > About > Check for updates
  - Capture session: Cancel keeps no tokens; Finish works once
  - middle-click closes a tab; a tab whose certificate the filter hides keeps its certificate
  - a saved request with no website, first 2xx: "Remembered website & certificate" appears
  - with the app closed, put "{ x" in %AppData%\CertApiTester\state.json and start it twice:
    one state.json.unreadable-*.bak, a note in the status line, the theme still loads
    (put your state.json back afterwards)

  Command line, at an interactive console
  - certapi ws <a WebSocket URL>, type a line with an accented letter: it arrives intact
  - Ctrl+C during a long send stops it; a second Ctrl+C ends certapi mcp
  - with a smart card: certapi send <url> --cert <card certificate> --cert-pin <pin>
    (does Windows still prompt for the PIN? if it does, --cert-pin is not reaching the card)
  - certapi serve <url> --port 8443 --tls --tls-trust (Windows asks to trust a root), open it in
    the browser, then certapi serve --tls-untrust removes it
"@

# ---------------------------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------------------------

$counts = $script:Results | Group-Object Result | ForEach-Object { "$($_.Count) $($_.Name)" }
$table = ($script:Results | ForEach-Object { "{0,-5} {1}" -f $_.Result, $_.Check }) -join "`r`n"
$details = ($script:Results | Where-Object { $_.Detail } | ForEach-Object {
    "[$($_.Result)] $($_.Check)`r`n    " + ($_.Detail -replace "`r?`n", "`r`n    ")
}) -join "`r`n`r`n"
$switches = @('Admin', 'Network', 'State', 'SkipTests') | Where-Object { (Get-Variable $_ -ValueOnly) } | ForEach-Object { "-$_" }
$commit = 'unknown'
try { $commit = (Invoke-Exe 'git' @('-C', $Repo, 'rev-parse', '--short', 'HEAD')).Out.Trim() } catch { }
$report = @"
certapi Windows checks, $(Get-Date -Format 'yyyy-MM-dd HH:mm')
clone: $Repo  (commit $commit)
switches: $(if ($switches) { $switches -join ' ' } else { 'none' })
$($counts -join ', ')

$table

Details
-------
$details

$manual
"@
$reportPath = Join-Path $Work 'results.txt'
[IO.File]::WriteAllText($reportPath, $report, (New-Object System.Text.UTF8Encoding($true)))

Write-Host ""
Write-Host $manual
Write-Host ""
Write-Host ($counts -join ', ') -ForegroundColor White
Write-Host "Results: $reportPath" -ForegroundColor White
$failures = @($script:Results | Where-Object { $_.Result -eq 'FAIL' }).Count
exit $failures
