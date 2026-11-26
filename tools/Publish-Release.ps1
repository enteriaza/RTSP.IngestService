#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes RTSP.IngestService as Native AOT release packages for Windows x64 and Linux x64.

.DESCRIPTION
    Resolves the repository from this script's location, so it can be run from any directory, with Windows
    PowerShell 5.1 or PowerShell 7. One invocation produces:

      Releases/windows-x64/   RTSP.IngestService.exe, appsettings.json
      Releases/linux-x64/     RTSP.IngestService, appsettings.json,
                              systemd/rtsp-ingest-service.service (from deploy/linux/)

    Both targets are published with dotnet publish -c Release --self-contained true -p:PublishAot=true:
      - win-x64 links with the host MSVC toolchain.
      - linux-x64 is cross-compiled on this Windows host by the project's StuDev.AotAnywhere MSBuild SDK (Zig linker
        and sysroot from NuGet). Nothing is built in WSL, and there is no framework-dependent or non-AOT fallback.

    Publish output and packages are staged under .artifacts/release-staging/ (publish logs in its logs/ folder).
    Release directories are replaced only after every selected target has published and validated, so a publish or
    validation failure exits 1 with every release directory untouched. Each replacement keeps the previous directory
    until the new one validates in place, and restores it otherwise. Other content under Releases/ is never touched.

    Packages contain only allow-listed files. Symbols (.pdb, .dbg) and the XML documentation file are not shipped;
    any other unexpected publish output fails the run, so new runtime assets are never dropped silently.
    appsettings.Development.json and appsettings.Production.json (local, credential-bearing) are never packaged.

.PARAMETER Platform
    win-x64, linux-x64, or all (default: all).
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet('win-x64', 'linux-x64', 'all')]
    [string] $Platform = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Project = Join-Path $RepoRoot 'src\RTSP.IngestService\RTSP.IngestService.csproj'
$GlobalJson = Join-Path $RepoRoot 'global.json'
$SystemdUnit = Join-Path $RepoRoot 'deploy\linux\rtsp-ingest-service.service'
$ReleaseRoot = Join-Path $RepoRoot 'Releases'
$StagingParent = Join-Path $RepoRoot '.artifacts\release-staging'
# The executable's base name is the project's AssemblyName, the one source of the application name.
$ExeBaseName = if (Test-Path -LiteralPath $Project -PathType Leaf) {
    $node = ([xml](Get-Content -LiteralPath $Project -Raw)).SelectSingleNode('/Project/PropertyGroup/AssemblyName')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) { throw "$Project does not define AssemblyName." }
    $node.InnerText.Trim()
}
$AotAnywhereSdk = 'StuDev.AotAnywhere'

# Files published but deliberately not shipped. Anything else not allow-listed fails the run.
$ExcludedPublishFiles = @("$ExeBaseName.pdb", "$ExeBaseName.dbg", "$ExeBaseName.xml")
$ForbiddenFiles = @('appsettings.Development.json', 'appsettings.Production.json')

$Targets = @{
    'win-x64'   = @{ Rid = 'win-x64'; Folder = 'windows-x64'; Exe = "$ExeBaseName.exe"; Toolchain = 'host MSVC toolchain' }
    'linux-x64' = @{ Rid = 'linux-x64'; Folder = 'linux-x64'; Exe = $ExeBaseName; Toolchain = "$AotAnywhereSdk (Zig cross-link on this Windows host)" }
}

function Fail {
    param([string] $Message)
    throw $Message
}

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "==> $Message"
}

function Assert-UnderDirectory {
    param([string] $CandidatePath, [string] $Root)

    $full = [System.IO.Path]::GetFullPath($CandidatePath).TrimEnd('\', '/')
    $prefix = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "Refusing to modify a path outside ${Root}: $full"
    }
}

function Assert-Prerequisites {
    param([string[]] $Selected)

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail 'dotnet was not found on PATH.'
    }

    foreach ($path in @($Project, $GlobalJson)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Fail "Required file was not found: $path"
        }
    }

    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        Fail 'dotnet --version failed.'
    }

    Write-Host "dotnet SDK $sdk (global.json: $(((Get-Content -LiteralPath $GlobalJson -Raw) | ConvertFrom-Json).sdk.version))"

    if ($Selected -contains 'linux-x64') {
        if (-not (Test-Path -LiteralPath $SystemdUnit -PathType Leaf)) {
            Fail "systemd unit was not found: $SystemdUnit"
        }

        $projectXml = Get-Content -LiteralPath $Project -Raw
        if ($projectXml -notmatch ('<Sdk\s+Name="' + [regex]::Escape($AotAnywhereSdk) + '"\s+Version="[^"]+"\s*/>')) {
            Fail "$Project does not reference the $AotAnywhereSdk SDK, which linux-x64 Native AOT from Windows requires."
        }
    }
}

function Invoke-AotPublish {
    param([hashtable] $Target, [string] $OutputDir, [string] $LogPath)

    $arguments = @(
        'publish', $Project,
        '-c', 'Release',
        '-r', $Target.Rid,
        '--self-contained', 'true',
        '-p:PublishAot=true',
        '-o', $OutputDir,
        '--nologo'
    )
    Write-Host "Toolchain: $($Target.Toolchain)"
    Write-Host ('dotnet ' + ($arguments -join ' '))

    # Native output is captured and checked for warnings; stderr must not become a terminating error in 5.1.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& dotnet @arguments 2>&1 | ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    [System.IO.File]::WriteAllLines($LogPath, [string[]] $output)
    $output | ForEach-Object { Write-Host "    $_" }

    if ($exitCode -ne 0) {
        Fail "dotnet publish ($($Target.Rid)) failed with exit code $exitCode. Log: $LogPath"
    }

    $warnings = @($output | Where-Object { $_ -match '(?i)\bwarning\b\s*[A-Z]{2,}\d+' })
    if ($warnings.Count -gt 0) {
        Fail "dotnet publish ($($Target.Rid)) reported $($warnings.Count) warning(s); releases require zero. Log: $LogPath"
    }

    if ($Target.Rid -eq 'linux-x64' -and -not ($output -match 'AotAnywhere: linking')) {
        Fail "dotnet publish (linux-x64) did not link with $AotAnywhereSdk. Log: $LogPath"
    }
}

function Test-NativeAotBinary {
    param([string] $Path, [string] $Rid)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($Rid -eq 'win-x64') {
        if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            Fail "Expected a Windows PE executable: $Path"
        }

        $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or ($peOffset + 256) -ge $bytes.Length -or $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
            Fail "Missing PE signature: $Path"
        }

        $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x8664) {
            Fail ('Expected x64 (0x8664) and found machine 0x{0:X4}: {1}' -f $machine, $Path)
        }

        $optional = $peOffset + 24
        $magic = [System.BitConverter]::ToUInt16($bytes, $optional)
        if ($magic -ne 0x20B) {
            Fail ('Expected PE32+ and found magic 0x{0:X4}: {1}' -f $magic, $Path)
        }

        # The CLR runtime header is data directory 14 of the PE32+ optional header; Native AOT has none.
        $clrSize = [System.BitConverter]::ToUInt32($bytes, $optional + 112 + (14 * 8) + 4)
        if ($clrSize -ne 0) {
            Fail "Executable has a CLR runtime header, so it is not Native AOT: $Path"
        }

        return 'PE32+ x86-64 (machine 0x8664), no CLR header (Native AOT)'
    }

    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        Fail "Expected a Linux ELF executable: $Path"
    }

    if ($bytes[4] -ne 2 -or $bytes[5] -ne 1) {
        Fail "Expected a 64-bit little-endian ELF executable: $Path"
    }

    $elfType = [System.BitConverter]::ToUInt16($bytes, 16)
    if ($elfType -ne 2 -and $elfType -ne 3) {
        Fail "Expected an ELF executable (ET_EXEC or ET_DYN) and found type ${elfType}: $Path"
    }

    $elfMachine = [System.BitConverter]::ToUInt16($bytes, 18)
    if ($elfMachine -ne 62) {
        Fail "Expected ELF x86-64 (machine 62) and found machine ${elfMachine}: $Path"
    }

    return 'ELF64 little-endian x86-64 (machine 62)'
}

function Test-SystemdUnit {
    param([string] $Path)

    $text = Get-Content -LiteralPath $Path -Raw
    foreach ($section in @('[Unit]', '[Service]', '[Install]')) {
        if (-not $text.Contains($section)) {
            Fail "systemd unit is missing its $section section: $Path"
        }
    }

    $execStart = [regex]::Match($text, '(?m)^ExecStart=(\S+)').Groups[1].Value
    $workingDirectory = [regex]::Match($text, '(?m)^WorkingDirectory=(\S+)').Groups[1].Value
    if ($execStart -notmatch "^/.+/$([regex]::Escape($ExeBaseName))$") {
        Fail "systemd ExecStart must be an absolute path to $ExeBaseName; found '$execStart'."
    }

    if ($execStart -ne "$workingDirectory/$ExeBaseName") {
        Fail "systemd ExecStart '$execStart' is not $ExeBaseName in WorkingDirectory '$workingDirectory'."
    }

    if ($text -match '(?im)^\s*Environment(File)?=.*(password|secret|keyhash|streamkey|token)') {
        Fail "systemd unit appears to contain a credential setting: $Path"
    }

    return "ExecStart=$execStart, WorkingDirectory=$workingDirectory"
}

function New-Package {
    param([hashtable] $Target, [string] $PublishDir, [string] $PackageDir)

    New-Item -ItemType Directory -Path $PackageDir | Out-Null
    $shipped = @($Target.Exe, 'appsettings.json')
    foreach ($file in Get-ChildItem -LiteralPath $PublishDir -Force) {
        if ($ForbiddenFiles -contains $file.Name) {
            Fail "Publish output contains local configuration that must never ship: $($file.Name)"
        }

        if ($shipped -contains $file.Name) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $PackageDir $file.Name)
        }
        elseif ($ExcludedPublishFiles -contains $file.Name) {
            Write-Host "Not shipped: $($file.Name)"
        }
        else {
            Fail "Unexpected publish output '$($file.Name)' for $($Target.Rid); add it to the package allow-list or the exclusions deliberately."
        }
    }

    if ($Target.Rid -eq 'linux-x64') {
        $systemdDir = Join-Path $PackageDir 'systemd'
        New-Item -ItemType Directory -Path $systemdDir | Out-Null
        Copy-Item -LiteralPath $SystemdUnit -Destination (Join-Path $systemdDir (Split-Path $SystemdUnit -Leaf))
    }
}

function Test-Package {
    param([hashtable] $Target, [string] $PackageDir)

    $exePath = Join-Path $PackageDir $Target.Exe
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf) -or (Get-Item -LiteralPath $exePath).Length -le 0) {
        Fail "Missing or empty release executable: $exePath"
    }

    $identity = Test-NativeAotBinary -Path $exePath -Rid $Target.Rid

    $appsettings = Join-Path $PackageDir 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appsettings -PathType Leaf)) {
        Fail "Missing appsettings.json: $appsettings"
    }

    $null = (Get-Content -LiteralPath $appsettings -Raw) | ConvertFrom-Json

    $expected = @($Target.Exe, 'appsettings.json')
    if ($Target.Rid -eq 'linux-x64') {
        $expected += 'systemd/rtsp-ingest-service.service'
    }

    $root = $PackageDir.TrimEnd('\') + '\'
    $actual = @(Get-ChildItem -LiteralPath $PackageDir -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($root.Length).Replace('\', '/') })
    $missing = @($expected | Where-Object { $actual -notcontains $_ })
    $extra = @($actual | Where-Object { $expected -notcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        Fail "Package $PackageDir does not match its manifest. Missing: [$($missing -join ', ')]; unexpected: [$($extra -join ', ')]"
    }

    $unit = $null
    if ($Target.Rid -eq 'linux-x64') {
        $unit = Test-SystemdUnit -Path (Join-Path $PackageDir 'systemd\rtsp-ingest-service.service')
    }

    return @{ Exe = $exePath; Identity = $identity; Unit = $unit }
}

function Install-Package {
    param([hashtable] $Target, [string] $PackageDir)

    $releaseDir = Join-Path $ReleaseRoot $Target.Folder
    $previous = "$releaseDir.previous"
    Assert-UnderDirectory -CandidatePath $releaseDir -Root $ReleaseRoot
    Assert-UnderDirectory -CandidatePath $previous -Root $ReleaseRoot

    if (Test-Path -LiteralPath $previous) {
        Remove-Item -LiteralPath $previous -Recurse -Force
    }

    $hadPrevious = Test-Path -LiteralPath $releaseDir
    if ($hadPrevious) {
        Move-Item -LiteralPath $releaseDir -Destination $previous
    }

    try {
        Move-Item -LiteralPath $PackageDir -Destination $releaseDir
        $result = Test-Package -Target $Target -PackageDir $releaseDir
    }
    catch {
        if (Test-Path -LiteralPath $releaseDir) {
            Remove-Item -LiteralPath $releaseDir -Recurse -Force
        }

        if ($hadPrevious) {
            Move-Item -LiteralPath $previous -Destination $releaseDir
        }

        throw
    }

    if ($hadPrevious) {
        Remove-Item -LiteralPath $previous -Recurse -Force
    }

    return $result
}

$selected = if ($Platform -eq 'all') { @('win-x64', 'linux-x64') } else { @($Platform) }
$stagingRoot = Join-Path $StagingParent ([System.Guid]::NewGuid().ToString('N'))
$logDir = Join-Path $StagingParent 'logs'
$current = $null
$installed = @()
try {
    Write-Step "$ExeBaseName Native AOT release: $($selected -join ', ')"
    Write-Host "Repository: $RepoRoot"
    Assert-Prerequisites -Selected $selected
    New-Item -ItemType Directory -Path $stagingRoot, $logDir -Force | Out-Null

    $staged = [ordered]@{}
    foreach ($name in $selected) {
        $current = $name
        $target = $Targets[$name]
        Write-Step "[$name] Publishing Native AOT"
        $publishDir = Join-Path $stagingRoot "publish-$name"
        Invoke-AotPublish -Target $target -OutputDir $publishDir -LogPath (Join-Path $logDir "publish-$name.log")

        Write-Step "[$name] Assembling and validating the package"
        $packageDir = Join-Path $stagingRoot "package-$name"
        New-Package -Target $target -PublishDir $publishDir -PackageDir $packageDir
        $check = Test-Package -Target $target -PackageDir $packageDir
        Write-Host "Executable: $($check.Identity)"
        if ($check.Unit) {
            Write-Host "systemd unit: $($check.Unit)"
        }

        $staged[$name] = $packageDir
    }

    New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
    $summary = @()
    foreach ($name in $staged.Keys) {
        $current = $name
        $target = $Targets[$name]
        Write-Step "[$name] Installing into Releases\$($target.Folder)"
        $result = Install-Package -Target $target -PackageDir $staged[$name]
        $installed += $name
        $summary += [pscustomobject]@{ Target = $name; Directory = (Split-Path $result.Exe); Executable = $result.Exe; Identity = $result.Identity }
    }

    $current = $null
    Write-Step 'Publish-Release completed successfully'
    foreach ($entry in $summary) {
        Write-Host ''
        Write-Host "[$($entry.Target)] $($entry.Directory)"
        Write-Host "    executable: $($entry.Executable)"
        Write-Host "    identity:   $($entry.Identity)"
        Get-ChildItem -LiteralPath $entry.Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
            Write-Host ('    {0,12}  {1}' -f $_.Length, $_.FullName.Substring($entry.Directory.Length + 1))
        }
    }

    if ($selected -contains 'linux-x64') {
        Write-Host ''
        Write-Host "Linux install: copy $ExeBaseName and appsettings.json to /opt/rtsp-ingest-service/ (chmod 0755 the"
        Write-Host 'executable) and systemd/rtsp-ingest-service.service to /etc/systemd/system/; see docs/deployment/linux.md.'
    }
}
catch {
    $where = if ($current) { "[$current] " } else { '' }
    [Console]::Error.WriteLine("error: $where$($_.Exception.Message)")
    if ($installed.Count -gt 0) {
        [Console]::Error.WriteLine("Already replaced and validated before the failure: $($installed -join ', '). Every other release directory is unchanged.")
    }
    else {
        [Console]::Error.WriteLine('No release directory was replaced; existing releases are unchanged.')
    }

    exit 1
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
