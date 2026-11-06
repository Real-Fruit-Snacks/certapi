<#
.SYNOPSIS
    Builds the offline dev kit: certapi's source at HEAD and every package it needs, in one zip.

.DESCRIPTION
    Writes certapi-<version>-dev-windows-x64.zip. It unzips to one folder, certapi-<version>-dev\:
    the repository at HEAD as git archive writes it (no .git), plus offline\:

      nuget\           every NuGet package that a restore of the solution, a restore for the
                       self-contained win-x64 publish of the app and of certapi, and
                       `dotnet tool restore` fetch
      python\          the wheels docs\art\requirements.txt names, for CPython on win_amd64 at the
                       version in docs\art\.python-version
      licences\        the licence and notices of each of those packages
      setup.ps1        from tools\dev-kit: points dotnet and pip at the folders above
      README.md        from tools\dev-kit\offline-README.md: the toolchain and the steps
      toolchain.json   the exact .NET SDK, runtime and Python the kit was built with

    Every version comes from where CI reads it: the PackageReferences in the projects,
    .config\dotnet-tools.json, docs\art\requirements.txt and docs\art\.python-version. The .NET SDK
    and the Python that run this script are the ones the kit names, so run it with the SDK CI uses
    (the newest 9.0; global.json asks for 9.0.320 or later) and with Python 3.13.

    Needs the network: nuget.org, pypi.org, the .NET release metadata and the SPDX licence texts.
    The work happens in a temporary folder that is removed at the end; packages are fetched into
    it, not into your NuGet or pip cache.

.PARAMETER OutputDirectory
    Where the zip goes. Defaults to artifacts\dev-kit in the clone, which git ignores.

.PARAMETER Python
    The python.exe to use. Defaults to python on PATH, then the py launcher, at the version in
    docs\art\.python-version.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-dev-kit.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$Python
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$Repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $Repo 'artifacts\dev-kit' }

# Licences that let a package be copied into the kit as it is. A package under any other licence
# stops the build until someone has read its terms and added them here.
$Redistributable = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause')

# Packages that name a licence file or an old licence URL instead of an SPDX expression, and the
# licence that file or URL holds. Read the terms before adding a package here.
$ReviewedLicences = @{
    'Microsoft.Web.WebView2'     = 'BSD-3-Clause'   # LICENSE.txt: Microsoft's three-clause BSD terms
    'dotnet-stryker'             = 'Apache-2.0'     # LICENSE: the Apache License 2.0
    'System.Reflection.Metadata' = 'MIT'            # licenseUrl: github.com/dotnet/corefx/blob/master/LICENSE.TXT
    'xunit.abstractions'         = 'Apache-2.0'     # licenseUrl: raw.githubusercontent.com/xunit/xunit/master/license.txt
}

# The SPDX licence list release the licence texts in offline\licences\spdx come from.
$SpdxTag = 'v3.29.0'

$LicenceFileName = '^(licen[cs]e|notice|copying|third-?party-?notices)(\.(txt|md))?$'
$Utf8 = New-Object System.Text.UTF8Encoding $false

function Invoke-Native {
    # Runs a native command with its output on the console; stops when it fails.
    param([string]$What, [scriptblock]$Run)
    Write-Host "> $What"
    & $Run | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

function Invoke-Quiet {
    # Runs a native command for its output, dropping stderr. Windows PowerShell turns redirected
    # stderr into errors, so this lowers $ErrorActionPreference for the call.
    param([string]$Exe, [string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $out = $null; $code = 1
    try { $out = & $Exe @Arguments 2>$null; $code = $LASTEXITCODE } catch { $code = 1 }
    [pscustomobject]@{ Output = @($out | ForEach-Object { "$_" }); ExitCode = $code }
}

function Get-ProjectVersion([string]$Path) {
    $m = [regex]::Match([IO.File]::ReadAllText($Path), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "$Path has no <Version>" }
    $m.Groups[1].Value.Trim()
}

function Read-Entry($Entry) {
    $reader = New-Object System.IO.StreamReader($Entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Save-Entry($Entry, [string]$Directory) {
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($Entry, (Join-Path $Directory $Entry.Name), $true)
    $Entry.Name
}

function Get-SpdxIds([string]$Expression) {
    @([regex]::Matches($Expression, '[A-Za-z0-9.+-]+') | ForEach-Object { $_.Value } |
        Where-Object { $_ -notin 'AND', 'OR', 'WITH' })
}

function Test-Url([string]$Url) {
    try { $null = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing; $true } catch { $false }
}

function Write-Text([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n").Replace("`n", "`r`n"), $Utf8)
}

# --- What to build ---------------------------------------------------------------------------

$appVersion = Get-ProjectVersion (Join-Path $Repo 'src\ApiTester.App\ApiTester.App.csproj')
$version = Get-ProjectVersion (Join-Path $Repo 'src\ApiTester.Cli\ApiTester.Cli.csproj')
if ($appVersion -ne $version) { throw "ApiTester.App.csproj says $appVersion and ApiTester.Cli.csproj says $version; they ship together" }
$kitName = "certapi-$version-dev"
$zipName = "certapi-$version-dev-windows-x64.zip"

$pyMinor = ([IO.File]::ReadAllText((Join-Path $Repo 'docs\art\.python-version'))).Trim()
if ($pyMinor -notmatch '^3\.\d+$') { throw "docs\art\.python-version should hold a version like 3.13, not '$pyMinor'" }
$pyTag = 'cp' + ($pyMinor -replace '\.', '')

$commit = (Invoke-Quiet 'git' @('-C', $Repo, 'rev-parse', 'HEAD')).Output | Select-Object -First 1
if (-not $commit) { throw "$Repo is not a git clone; the kit is made with git archive" }
$commitDate = (Invoke-Quiet 'git' @('-C', $Repo, 'log', '-1', '--format=%cs', 'HEAD')).Output | Select-Object -First 1
if ((Invoke-Quiet 'git' @('-C', $Repo, 'status', '--porcelain', '--untracked-files=no')).Output.Count -gt 0) {
    Write-Warning 'The clone has uncommitted changes. The kit is made from HEAD and leaves them out.'
}

# The Python whose exact version the kit names.
$candidates = @()
if ($Python) { $candidates += @{ Exe = $Python; Args = @() } }
else {
    if (Get-Command python -ErrorAction SilentlyContinue) { $candidates += @{ Exe = 'python'; Args = @() } }
    if (Get-Command py -ErrorAction SilentlyContinue) { $candidates += @{ Exe = 'py'; Args = @("-$pyMinor") } }
}
$py = $null; $pyVersion = $null
foreach ($c in $candidates) {
    $r = Invoke-Quiet $c.Exe ($c.Args + @('-c', "import sys; print('%d.%d.%d' % sys.version_info[:3]); print(sys.executable)"))
    if ($r.ExitCode -eq 0 -and $r.Output.Count -ge 2 -and $r.Output[0] -like "$pyMinor.*") { $pyVersion = $r.Output[0]; $py = $r.Output[1]; break }
}
if (-not $py) { throw "Python $pyMinor (docs\art\.python-version) was not found; install it or pass -Python <path to python.exe>" }

$work = Join-Path ([IO.Path]::GetTempPath()) ('certapi-dev-kit-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$scratch = Join-Path $work 'scratch'
$packages = Join-Path $work 'packages'
$stage = Join-Path $work "kit\$kitName"
$offline = Join-Path $stage 'offline'
$saved = @{}
foreach ($name in 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'MSBUILDDISABLENODEREUSE') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}

Write-Host "Building $zipName from $commit"
try {
    New-Item -ItemType Directory -Force -Path $work, $OutputDirectory | Out-Null

    # --- The source -----------------------------------------------------------------------------
    $sourceZip = Join-Path $work 'source.zip'
    Invoke-Native 'git archive' { git -C $Repo archive --format=zip "--prefix=$kitName/" -o $sourceZip HEAD }
    [IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, (Join-Path $work 'kit'))
    [IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $scratch)
    $scratch = Join-Path $scratch $kitName
    foreach ($d in 'nuget', 'python', 'licences', 'licences\nuget', 'licences\python', 'licences\spdx') {
        New-Item -ItemType Directory -Force -Path (Join-Path $offline $d) | Out-Null
    }

    # --- NuGet packages: what CI restores, from nuget.org, into an empty folder ---------------------
    Write-Text (Join-Path $scratch 'nuget.config') @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@
    $env:NUGET_PACKAGES = $packages
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:MSBUILDDISABLENODEREUSE = '1'
    Push-Location -LiteralPath $scratch
    try {
        $r = Invoke-Quiet 'dotnet' @('--version')
        if ($r.ExitCode -ne 0) { throw 'no .NET SDK that global.json accepts (a 9.0 SDK, 9.0.320 or later) is installed' }
        $sdk = $r.Output[0].Trim()
        Invoke-Native 'dotnet restore (solution)' { dotnet restore ApiTester.sln }
        foreach ($proj in 'src\ApiTester.App\ApiTester.App.csproj', 'src\ApiTester.Cli\ApiTester.Cli.csproj') {
            Invoke-Native "dotnet restore $proj (win-x64, self-contained)" { dotnet restore $proj -r win-x64 -p:SelfContained=true }
        }
        Invoke-Native 'dotnet tool restore' { dotnet tool restore }
        $sdkLine = (Invoke-Quiet 'dotnet' @('--list-sdks')).Output | Where-Object { $_ -like "$sdk *" } | Select-Object -First 1
    }
    finally { Pop-Location }

    # The runtime the SDK bundles into a self-contained exe, and that its runtime packs are.
    $sdkDir = Join-Path ($sdkLine -replace '^\S+ \[(.*)\]$', '$1') $sdk
    $bundled = [IO.File]::ReadAllText((Join-Path $sdkDir 'Microsoft.NETCoreSdk.BundledVersions.props'))
    $runtime = [regex]::Match($bundled, '<BundledNETCoreAppPackageVersion>([^<]+)<').Groups[1].Value
    if (-not $runtime) { throw "could not read the runtime version SDK $sdk bundles" }

    $nupkgs = @(foreach ($idDir in Get-ChildItem -LiteralPath $packages -Directory) {
        foreach ($verDir in Get-ChildItem -LiteralPath $idDir.FullName -Directory) {
            $file = Join-Path $verDir.FullName ('{0}.{1}.nupkg' -f $idDir.Name, $verDir.Name)
            if (Test-Path -LiteralPath $file) { Get-Item -LiteralPath $file }
        }
    })
    if ($nupkgs.Count -eq 0) { throw 'the restore fetched no packages' }
    $nupkgs | Copy-Item -Destination (Join-Path $offline 'nuget')
    foreach ($pack in 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64') {
        if (-not (Test-Path -LiteralPath (Join-Path $offline "nuget\$pack.$runtime.nupkg"))) {
            throw "$pack $runtime is not among the restored packages, so the kit could not publish the exes"
        }
    }

    # --- Python wheels --------------------------------------------------------------------------
    Invoke-Native "pip download (CPython $pyMinor, win_amd64)" {
        & $py -m pip download --no-cache-dir --disable-pip-version-check --only-binary=:all: --platform win_amd64 `
            --python-version $pyMinor --implementation cp --dest (Join-Path $offline 'python') -r (Join-Path $scratch 'docs\art\requirements.txt')
    }
    $wheels = @(Get-ChildItem -LiteralPath (Join-Path $offline 'python') -Filter *.whl)

    # --- Licences -------------------------------------------------------------------------------
    $rows = New-Object System.Collections.Generic.List[object]
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($nupkg in $nupkgs) {
        $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
        try {
            $nuspec = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
            $xml = New-Object System.Xml.XmlDocument
            $xml.LoadXml((Read-Entry $nuspec))
            $meta = $xml.DocumentElement.SelectSingleNode("*[local-name()='metadata']")
            function Get-Meta([string]$Name) { $n = $meta.SelectSingleNode("*[local-name()='$Name']"); if ($n) { $n.InnerText.Trim() } else { '' } }
            $id = Get-Meta 'id'; $ver = Get-Meta 'version'
            $licNode = $meta.SelectSingleNode("*[local-name()='license']")
            $licType = if ($licNode) { $licNode.GetAttribute('type') } else { '' }
            $expr = $null
            if ($licType -eq 'expression') { $expr = $licNode.InnerText.Trim() }
            elseif ($ReviewedLicences.ContainsKey($id)) { $expr = $ReviewedLicences[$id] }
            else {
                $problems.Add("NuGet $id $ver names its licence as $(if ($licType) { "a $licType '$($licNode.InnerText)'" } else { "the URL '$(Get-Meta 'licenseUrl')'" }); read it, then add it to `$ReviewedLicences")
                continue
            }
            $ids = Get-SpdxIds $expr
            $bad = @($ids | Where-Object { $_ -notin $Redistributable })
            if ($bad.Count -gt 0) { $problems.Add("NuGet $id $ver is under $expr; $($bad -join ', ') is not in `$Redistributable"); continue }

            $dir = Join-Path $offline "licences\nuget\$id.$ver"
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $files = @($zip.Entries | Where-Object {
                    ($_.FullName -notmatch '/' -and $_.Name -match $LicenceFileName) -or
                    ($licType -eq 'file' -and $_.FullName -eq $licNode.InnerText.Trim().Replace('\', '/'))
                } | Sort-Object FullName -Unique | ForEach-Object { Save-Entry $_ $dir })
            Write-Text (Join-Path $dir 'package.txt') (@(
                    "Package:   $id $ver (nuget.org)",
                    "Licence:   $expr",
                    "Copyright: $(Get-Meta 'copyright')",
                    "Authors:   $(Get-Meta 'authors')",
                    "Project:   $(Get-Meta 'projectUrl')",
                    "Files:     $(if ($files.Count) { $files -join ', ' } else { 'none in the package; the licence text is in ..\..\spdx' })"
                ) -join "`n")
            $rows.Add([pscustomobject]@{ Kind = 'NuGet'; Id = $id; Version = $ver; Licence = $expr; Ids = $ids; Dir = "nuget/$id.$ver"; Files = $files })
        }
        finally { $zip.Dispose() }
    }
    foreach ($wheel in $wheels) {
        $zip = [IO.Compression.ZipFile]::OpenRead($wheel.FullName)
        try {
            $metaEntry = $zip.Entries | Where-Object { $_.FullName -match '^[^/]+\.dist-info/METADATA$' } | Select-Object -First 1
            $distInfo = $metaEntry.FullName.Substring(0, $metaEntry.FullName.IndexOf('/'))
            $headers = @{}
            foreach ($line in (Read-Entry $metaEntry) -split "`r?`n") {
                if ($line -eq '') { break }
                if ($line -match '^([A-Za-z-]+):\s*(.*)$' -and -not $headers.ContainsKey($Matches[1])) { $headers[$Matches[1]] = $Matches[2].Trim() }
            }
            $name = $headers['Name']; $ver = $headers['Version']
            $expr = $null
            if ($headers['License-Expression']) { $expr = $headers['License-Expression'] }
            elseif ($headers['License'] -and $headers['License'] -in $Redistributable) { $expr = $headers['License'] }
            elseif ($ReviewedLicences.ContainsKey($name)) { $expr = $ReviewedLicences[$name] }
            else { $problems.Add("Python $name $ver does not name an SPDX licence; read it, then add it to `$ReviewedLicences"); continue }
            $ids = Get-SpdxIds $expr
            $bad = @($ids | Where-Object { $_ -notin $Redistributable })
            if ($bad.Count -gt 0) { $problems.Add("Python $name $ver is under $expr; $($bad -join ', ') is not in `$Redistributable"); continue }

            $dir = Join-Path $offline "licences\python\$name-$ver"
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $files = @($zip.Entries | Where-Object {
                    $_.Name -and ($_.FullName.StartsWith("$distInfo/licenses/") -or
                        ($_.FullName -eq "$distInfo/$($_.Name)" -and $_.Name -match '^(licen[cs]e|notice|copying|authors)'))
                } | ForEach-Object { Save-Entry $_ $dir })
            Write-Text (Join-Path $dir 'package.txt') (@(
                    "Package:   $name $ver (pypi.org, $($wheel.Name))",
                    "Licence:   $expr",
                    "Project:   $(if ($headers['Home-page']) { $headers['Home-page'] } else { $headers['Project-URL'] })",
                    "Files:     $(if ($files.Count) { $files -join ', ' } else { 'none in the wheel; the licence text is in ..\..\spdx' })"
                ) -join "`n")
            $rows.Add([pscustomobject]@{ Kind = 'Python'; Id = $name; Version = $ver; Licence = $expr; Ids = $ids; Dir = "python/$name-$ver"; Files = $files })
        }
        finally { $zip.Dispose() }
    }
    if ($problems.Count -gt 0) { throw ("Licences to check before these can be bundled:`n  " + ($problems -join "`n  ")) }

    $spdx = @($rows | ForEach-Object { $_.Ids } | Sort-Object -Unique)
    foreach ($spdxId in $spdx) {
        $url = "https://raw.githubusercontent.com/spdx/license-list-data/$SpdxTag/text/$spdxId.txt"
        Invoke-WebRequest -Uri $url -OutFile (Join-Path $offline "licences\spdx\$spdxId.txt") -UseBasicParsing
    }
    $index = New-Object System.Collections.Generic.List[string]
    $index.Add('# Licences')
    $index.Add('')
    $index.Add('The licence and notices of every package in `offline\nuget` and `offline\python`, as each package')
    $index.Add('ships them, with its copyright line in `package.txt`, and in `spdx\` the text of each licence they')
    $index.Add("name (SPDX licence list $SpdxTag). certapi itself is MIT: ``LICENSE`` at the top of the kit. The fonts in")
    $index.Add('`docs\fonts` have their OFL texts beside them.')
    $index.Add('')
    $index.Add('| Package | Version | Licence | Files |')
    $index.Add('|---|---|---|---|')
    foreach ($row in $rows | Sort-Object Kind, Id, Version) {
        $links = @($row.Files | ForEach-Object { "[$_]($($row.Dir)/$_)" }) + @("[package.txt]($($row.Dir)/package.txt)")
        $licence = (@($row.Ids | ForEach-Object { "[$_](spdx/$_.txt)" }) -join ', ')
        if ($row.Licence -ne ($row.Ids -join ' ')) { $licence = "$($row.Licence): $licence" }
        $index.Add("| $($row.Id) ($($row.Kind)) | $($row.Version) | $licence | $($links -join ', ') |")
    }
    Write-Text (Join-Path $offline 'licences\README.md') (($index -join "`n") + "`n")

    # --- The toolchain, which the kit does not carry ---------------------------------------------
    $channel = ($sdk -split '\.')[0..1] -join '.'
    $releases = Invoke-RestMethod -Uri "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" -UseBasicParsing
    $sdkEntry = $releases.releases | ForEach-Object { $_.sdks } | Where-Object { $_ -and $_.version -eq $sdk } | Select-Object -First 1
    if (-not $sdkEntry) { throw ".NET SDK $sdk is not in the $channel release metadata" }
    $sdkUrl = ($sdkEntry.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1).url
    if (-not $sdkUrl) { throw ".NET SDK $sdk has no win-x64 installer in the release metadata" }

    $pyUrl = "https://www.python.org/ftp/python/$pyVersion/python-$pyVersion-amd64.exe"
    if (-not (Test-Url $pyUrl)) { throw "python.org has no Windows installer for Python $pyVersion ($pyUrl)" }

    $pwshVersion = $null
    if ($PSVersionTable.PSEdition -eq 'Core') { $pwshVersion = $PSVersionTable.PSVersion.ToString() }
    elseif (Get-Command pwsh -ErrorAction SilentlyContinue) {
        $pwshVersion = (Invoke-Quiet 'pwsh' @('-NoProfile', '-Command', '$PSVersionTable.PSVersion.ToString()')).Output | Select-Object -First 1
    }
    if ($pwshVersion -and $pwshVersion -match '^\d+\.\d+\.\d+$') {
        $pwshUrl = "https://github.com/PowerShell/PowerShell/releases/download/v$pwshVersion/PowerShell-$pwshVersion-win-x64.msi"
        if (-not (Test-Url $pwshUrl)) { throw "PowerShell $pwshVersion has no win-x64 MSI at $pwshUrl" }
    }
    else { $pwshVersion = '7 (any)'; $pwshUrl = 'https://github.com/PowerShell/PowerShell/releases/latest' }

    $webView2 = $null
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
                     'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.pv) { $webView2 = $item.pv; break }
    }
    if (-not $webView2) { $webView2 = 'any' }
    $webView2Url = 'https://go.microsoft.com/fwlink/?linkid=2124701'

    $toolchain = [ordered]@{
        kit        = $kitName
        version    = $version
        commit     = $commit
        commitDate = $commitDate
        dotnet     = [ordered]@{ sdk = $sdk; runtime = $runtime; installer = $sdkUrl }
        python     = [ordered]@{ version = $pyVersion; tag = $pyTag; installer = $pyUrl; requirements = 'docs/art/requirements.txt' }
        powershell = [ordered]@{ version = $pwshVersion; installer = $pwshUrl }
        webview2   = [ordered]@{ version = $webView2; installer = $webView2Url }
    }
    Write-Text (Join-Path $offline 'toolchain.json') (($toolchain | ConvertTo-Json -Depth 4) + "`n")

    $nugetBytes = ($nupkgs | Measure-Object Length -Sum).Sum
    $tokens = @{
        'version'        = $version
        'kit'            = $kitName
        'zip'            = $zipName
        'commit'         = $commit
        'commit-date'    = $commitDate
        'sdk'            = $sdk
        'sdk-url'        = $sdkUrl
        'runtime'        = $runtime
        'python'         = $pyVersion
        'python-minor'   = $pyMinor
        'python-url'     = $pyUrl
        'pwsh'           = $pwshVersion
        'pwsh-url'       = $pwshUrl
        'webview2'       = $webView2
        'webview2-url'   = $webView2Url
        'nuget-count'    = $nupkgs.Count
        'nuget-mb'       = [math]::Round($nugetBytes / 1MB)
        'wheels'         = (@($wheels | ForEach-Object { '`' + $_.Name + '`' }) -join ', ')
        'licence-count'  = $rows.Count
    }
    $readme = [IO.File]::ReadAllText((Join-Path $Repo 'tools\dev-kit\offline-README.md'))
    foreach ($k in $tokens.Keys) { $readme = $readme.Replace("{{$k}}", [string]$tokens[$k]) }
    if ($readme -match '\{\{[a-z-]+\}\}') { throw "tools\dev-kit\offline-README.md has a token this script does not fill: $($Matches[0])" }
    Write-Text (Join-Path $offline 'README.md') $readme
    Copy-Item -LiteralPath (Join-Path $Repo 'tools\dev-kit\setup.ps1') -Destination (Join-Path $offline 'setup.ps1')

    # --- The zip --------------------------------------------------------------------------------
    # Entry by entry: ZipFile.CreateFromDirectory in Windows PowerShell writes backslashes into
    # the names. Packages and images are compressed already.
    $zipPath = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
    $base = (Split-Path -Parent $stage).Length + 1
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File -Force | Sort-Object FullName) {
            $level = if ($file.Extension -in '.nupkg', '.whl', '.png', '.ico', '.woff2') { 'NoCompression' } else { 'Optimal' }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.FullName.Substring($base).Replace('\', '/'), $level)
        }
    }
    finally { $zip.Dispose() }

    Write-Host ''
    Write-Host ("{0}  {1:N1} MB" -f $zipPath, ((Get-Item -LiteralPath $zipPath).Length / 1MB))
    Write-Host ("  {0} NuGet packages ({1} MB), {2} wheels for CPython {3}, licences for all {4}" -f $nupkgs.Count, $tokens['nuget-mb'], $wheels.Count, $pyMinor, $rows.Count)
    Write-Host "  .NET SDK $sdk (runtime $runtime), Python $pyVersion, PowerShell $pwshVersion"
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
