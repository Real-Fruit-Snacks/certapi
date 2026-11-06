<#
.SYNOPSIS
    Sets up certapi's offline dev kit: checks the toolchain, points dotnet and pip at the packages
    in offline\, and restores everything from them. Needs no network.

.DESCRIPTION
    Run it from anywhere, in Windows PowerShell 5.1 or PowerShell 7:

        powershell -ExecutionPolicy Bypass -File offline\setup.ps1

    It checks for the .NET SDK and the Python named in offline\toolchain.json and says what is
    missing and where to get it. Then it:

      - writes nuget.config beside ApiTester.sln with offline\nuget as the only package source, so
        every dotnet command in the kit restores from it and from nowhere else;
      - restores the solution, the self-contained win-x64 publish of the app and of certapi, and
        the local tools in .config\dotnet-tools.json, all from offline\nuget;
      - makes .venv with the packages docs\art\requirements.txt names, from offline\python, and a
        pip.ini in it that keeps pip off the network.

    Running it again is safe. It installs no toolchain and changes nothing outside the kit except
    the NuGet packages folder every restore fills (%USERPROFILE%\.nuget\packages, or
    NUGET_PACKAGES).

.PARAMETER Python
    The python.exe to make .venv with. Defaults to py -<version>, then python on PATH.
#>
[CmdletBinding()]
param(
    [string]$Python
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1

$Offline = $PSScriptRoot
$Kit = Split-Path -Parent $Offline
$Toolchain = [IO.File]::ReadAllText((Join-Path $Offline 'toolchain.json')) | ConvertFrom-Json
$Missing = New-Object System.Collections.Generic.List[string]
$Skipped = New-Object System.Collections.Generic.List[string]

function Say([string]$Status, [string]$Text) { Write-Host ('  {0,-8} {1}' -f $Status, $Text) }

function Invoke-Quiet {
    # A native command's output, without its stderr. Windows PowerShell turns redirected stderr
    # into errors, so this lowers $ErrorActionPreference for the call.
    param([string]$Exe, [string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $out = $null; $code = 1
    try { $out = & $Exe @Arguments 2>$null; $code = $LASTEXITCODE } catch { $code = 1 }
    [pscustomobject]@{ Output = @($out | ForEach-Object { "$_" }); ExitCode = $code }
}

function Invoke-Step([string]$What, [scriptblock]$Run) {
    Write-Host "> $What"
    & $Run | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE). Nothing in it needs the network; the message above says what was missing." }
}

$sdk = $Toolchain.dotnet.sdk
$runtime = $Toolchain.dotnet.runtime
Write-Host "certapi $($Toolchain.version) dev kit ($($Toolchain.commit.Substring(0, 12)), $($Toolchain.commitDate)) in $Kit"
Write-Host ''
Write-Host 'Toolchain'
Say 'ok' "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"

# --- .NET SDK --------------------------------------------------------------------------------
# global.json asks for a 9.0 SDK, 9.0.320 or later. The kit's runtime packs and its ILLink package
# are the runtime the kit's SDK bundles, and an SDK restores the ones for its own runtime, so the
# SDK must be one that bundles the same runtime: the kit's SDK, or another 9.0 SDK from the same
# release.
$dotnetOk = $false
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $Missing.Add(".NET SDK $sdk is not installed (dotnet is not on PATH). Install it: $($Toolchain.dotnet.installer)")
}
else {
    Push-Location -LiteralPath $Kit
    try {
        $r = Invoke-Quiet 'dotnet' @('--version')
        $sdks = (Invoke-Quiet 'dotnet' @('--list-sdks')).Output
    }
    finally { Pop-Location }
    if ($r.ExitCode -ne 0 -or -not $r.Output) {
        $have = if ($sdks) { ($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', ' } else { 'none' }
        $Missing.Add(".NET SDK $sdk is not installed, and no SDK here is one global.json accepts (installed: $have). Install it: $($Toolchain.dotnet.installer)")
    }
    else {
        $found = $r.Output[0].Trim()
        $line = $sdks | Where-Object { $_ -like "$found *" } | Select-Object -First 1
        $bundled = $null
        if ($line) {
            $props = Join-Path (Join-Path ($line -replace '^\S+ \[(.*)\]$', '$1') $found) 'Microsoft.NETCoreSdk.BundledVersions.props'
            if (Test-Path -LiteralPath $props) {
                $bundled = [regex]::Match([IO.File]::ReadAllText($props), '<BundledNETCoreAppPackageVersion>([^<]+)<').Groups[1].Value
            }
        }
        if ($bundled -eq $runtime) {
            $dotnetOk = $true
            if ($found -eq $sdk) { Say 'ok' ".NET SDK $found (runtime $runtime)" }
            else { Say 'ok' ".NET SDK $found (runtime $runtime; the kit was built with SDK $sdk, which bundles the same runtime)" }
        }
        else {
            $Missing.Add(".NET SDK $found is the one this folder uses, but it bundles .NET runtime $bundled and the kit's runtime packs are $runtime. Install SDK $sdk (it bundles $runtime): $($Toolchain.dotnet.installer)")
        }
    }
}
if (-not $dotnetOk) { Say 'MISSING' ".NET SDK $sdk" }

# --- Python ----------------------------------------------------------------------------------
# Only docs\art (the seal) needs it. The wheels are for one CPython minor version.
$pyWant = $Toolchain.python.version
$pyMinor = ($pyWant -split '\.')[0..1] -join '.'
$candidates = @()
if ($Python) { $candidates += @{ Exe = $Python; Args = @() } }
else {
    if (Get-Command py -ErrorAction SilentlyContinue) { $candidates += @{ Exe = 'py'; Args = @("-$pyMinor") } }
    if (Get-Command python -ErrorAction SilentlyContinue) { $candidates += @{ Exe = 'python'; Args = @() } }
}
$py = $null; $pyFound = $null; $pyOther = $null
foreach ($c in $candidates) {
    $r = Invoke-Quiet $c.Exe ($c.Args + @('-c', "import sys; print('%d.%d.%d' % sys.version_info[:3]); print(sys.executable)"))
    if ($r.ExitCode -ne 0 -or $r.Output.Count -lt 2) { continue }
    if ($r.Output[0] -like "$pyMinor.*") { $pyFound = $r.Output[0]; $py = $r.Output[1]; break }
    if (-not $pyOther) { $pyOther = $r.Output[0] }
}
if ($py) { Say 'ok' "Python $pyFound ($py)" }
else {
    $why = if ($pyOther) { "Python $pyOther is here, but the kit's wheels are for CPython $pyMinor" } else { "Python $pyMinor is not installed" }
    $Skipped.Add("$why; only the seal in docs\art needs it. Install Python ${pyWant}: $($Toolchain.python.installer)")
    Say 'skipped' "Python $pyWant (only for docs\art)"
}
Write-Host ''

if ($Missing.Count -gt 0) {
    Write-Host 'Missing:'
    $Missing | ForEach-Object { Write-Host "  - $_" }
    foreach ($s in $Skipped) { Write-Host "  - $s" }
    Write-Host ''
    Write-Host 'Install what is missing (offline\README.md has every installer), then run this again.'
    exit 1
}

# --- NuGet: the kit and nothing else ---------------------------------------------------------
$nugetConfig = Join-Path $Kit 'nuget.config'
$text = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- Written by offline\setup.ps1: packages come from the dev kit and from nowhere else. -->
<configuration>
  <packageSources>
    <clear />
    <add key="certapi-offline" value="offline\nuget" />
  </packageSources>
</configuration>
'@
[IO.File]::WriteAllText($nugetConfig, $text.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote $nugetConfig (offline\nuget is its only package source)"

# A package's signature is checked when it is first restored. Its certificates' revocation is
# checked online by default, which on a machine with no network waits for each lookup to fail.
$env:NUGET_CERT_REVOCATION_MODE = 'offline'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location -LiteralPath $Kit
try {
    foreach ($proj in 'src\ApiTester.App\ApiTester.App.csproj', 'src\ApiTester.Cli\ApiTester.Cli.csproj') {
        Invoke-Step "dotnet restore $proj (win-x64, self-contained, for dotnet publish)" { dotnet restore $proj -r win-x64 -p:SelfContained=true }
    }
    # Last, so the projects are left restored for an ordinary build.
    Invoke-Step 'dotnet restore ApiTester.sln' { dotnet restore ApiTester.sln }
    Invoke-Step 'dotnet tool restore (.config\dotnet-tools.json)' { dotnet tool restore }
}
finally { Pop-Location }

# --- Python: a venv for docs\art -------------------------------------------------------------
$venv = Join-Path $Kit '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'
if ($py) {
    if (-not (Test-Path -LiteralPath $venvPython)) {
        Invoke-Step "python -m venv .venv (Python $pyFound)" { & $py -m venv $venv }
    }
    $pipIni = "[global]`r`nno-index = true`r`nfind-links = $(Join-Path $Offline 'python')`r`ndisable-pip-version-check = true`r`n"
    [IO.File]::WriteAllText((Join-Path $venv 'pip.ini'), $pipIni, (New-Object System.Text.UTF8Encoding $false))
    # The pip.ini above is what keeps this off the network, and so it is tested here too.
    Invoke-Step 'pip install -r docs\art\requirements.txt (from offline\python)' {
        & $venvPython -m pip install -r (Join-Path $Kit 'docs\art\requirements.txt')
    }
}

Write-Host ''
if ($Skipped.Count -gt 0) {
    Write-Host 'Skipped:'
    $Skipped | ForEach-Object { Write-Host "  - $_" }
    Write-Host ''
}
Write-Host "Ready. Next, from $Kit :"
Write-Host ''
Write-Host '  dotnet build --configuration Release --no-restore -warnaserror'
Write-Host '  dotnet test --configuration Release --no-build --filter "Category!=StoreRoundTrip"'
Write-Host '  dotnet publish src\ApiTester.App -c Release -r win-x64 --self-contained -o publish'
Write-Host '  dotnet publish src\ApiTester.Cli -c Release -r win-x64 --self-contained -o publish-cli'
if ($py) { Write-Host '  .venv\Scripts\python docs\art\make-seal.py' }
Write-Host ''
Write-Host 'The filter leaves out the StoreRoundTrip tests, which add a certificate to your Windows store;'
Write-Host 'tools\windows-checks.ps1 does too. Mutation tests: dotnet stryker (stryker-config.json).'
