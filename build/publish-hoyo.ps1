<#
.SYNOPSIS
Builds a self-contained Windows x64 HoYo fork zip without redistributing Oodle.
The unpacked launcher selects a separate data folder; this is not a Velopack install or update feed.
#>
param([string]$Version = '1.4.5-hoyo.1', [switch]$NoZip)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This package targets Windows x64.' }
if ($Version -notmatch '^\d+\.\d+\.\d+-hoyo\.\d+$') { throw 'Use a fork prerelease version, e.g. 1.4.5-hoyo.1.' }
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = 'dotnet'
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
& $dotnet test (Join-Path $repo 'tests\SCSKiller.Tests\SCSKiller.Tests.csproj') -c Release "-p:Version=$Version" --filter 'Needs!=Game'
if ($LASTEXITCODE -ne 0) { throw 'Reader regression tests failed; no package was built.' }

# Keep the upstream native/runtime/license checks as the single packaging implementation.
& (Join-Path $PSScriptRoot 'publish.ps1') -NoOodle -NoZip -Version $Version
$dist = Join-Path $repo 'dist'
$out = Join-Path $dist 'SCSKiller'
Copy-Item (Join-Path $PSScriptRoot 'hoyo\Start-SCSKiller.cmd'), (Join-Path $PSScriptRoot 'hoyo\SCSKiller-CLI.cmd') $out
Copy-Item (Join-Path $repo 'docs\distribution.md') (Join-Path $out 'README.md')
$sha = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
$dirty = @(git -C $repo status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source worktree state.' }
$info = [ordered]@{ repository = 'https://github.com/BedrockLian/SCSKiller'; commit = $sha; modified = $dirty.Count -gt 0; version = $Version; platform = 'win-x64'; oodleBundled = $false }
$info | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $out 'build-info.json')

# Both entry points must run with their shipped .NET runtime, without touching a user's app data.
foreach ($entry in 'SCSKiller.exe', 'cli\scskiller.exe') {
    $stdout = Join-Path $dist 'version-check.txt'
    $stderr = Join-Path $dist 'version-check.err'
    $process = Start-Process -FilePath (Join-Path $out $entry) -ArgumentList '--version' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $got = [IO.File]::ReadAllText($stdout).Trim()
    if ($process.ExitCode -ne 0 -or $got -ne $Version) { throw "Wrong version or failed startup: $entry ($got); $([IO.File]::ReadAllText($stderr))" }
}
$zipName = "SCSKiller-HoYo-$Version-win-x64.zip"
if (-not $NoZip) {
    $zip = Join-Path $dist $zipName
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $dist "$zipName.sha256"), "$hash  $zipName`n")
    Write-Host "Built $zip and its SHA-256 checksum."
}
