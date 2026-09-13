<#
.SYNOPSIS
  Prepares the winget manifests for a published GitHub release.

.DESCRIPTION
  Downloads SHA256SUMS.txt from the release v<Version> (the release workflow,
  .github/workflows/build.yml, publishes it next to OpenHardwareMonitor.exe),
  takes the hash of OpenHardwareMonitor.exe, and writes the three manifest
  files to manifests/b/babuskaruska/OpenHardwareMonitor2026/<Version>.

  If that folder already exists (for example 0.10.0 with its placeholder
  hash), it is updated in place. Otherwise the newest existing version is
  copied and the version, installer URL, hash and release notes URL are
  replaced.

  The script downloads only the small checksum file, never the executable.
  It does not validate or submit anything; see README.md for those steps.

.PARAMETER Version
  The release version without the leading "v", for example 0.11.0.

.EXAMPLE
  ./update-manifest.ps1 -Version 0.10.0
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
  [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = 'babuskaruska/openhardwaremonitor-2026'
$identifier = 'babuskaruska.OpenHardwareMonitor2026'
$asset = 'OpenHardwareMonitor.exe'
$packageRoot = Join-Path $PSScriptRoot 'manifests/b/babuskaruska/OpenHardwareMonitor2026'

$tag = "v$Version"
$sumsUrl = "https://github.com/$repository/releases/download/$tag/SHA256SUMS.txt"
$installerUrl = "https://github.com/$repository/releases/download/$tag/$asset"
$releaseNotesUrl = "https://github.com/$repository/releases/tag/$tag"

# ---- the hash ---------------------------------------------------------------

Write-Host "Downloading $sumsUrl"
$response = Invoke-WebRequest -Uri $sumsUrl -UseBasicParsing
if ($response.Content -is [byte[]]) {
  $sums = [System.Text.Encoding]::UTF8.GetString($response.Content)
} else {
  $sums = [string] $response.Content
}
$sums = $sums.TrimStart([char] 0xFEFF)

# Lines look like "<64 hex digits>  OpenHardwareMonitor.exe".
$hash = $null
foreach ($line in ($sums -split "`r?`n")) {
  if ($line -match '^\s*([0-9A-Fa-f]{64})\s+\*?(\S+)\s*$' -and $Matches[2] -eq $asset) {
    $hash = $Matches[1].ToUpperInvariant()
  }
}
if (-not $hash) {
  throw "SHA256SUMS.txt of $tag has no entry for $asset."
}
Write-Host "SHA-256 of ${asset}: $hash"

# ---- the manifests ------------------------------------------------------------

$target = Join-Path $packageRoot $Version
if (Test-Path $target) {
  $source = $target
} else {
  $newest = Get-ChildItem -Path $packageRoot -Directory |
    Sort-Object { [version] (($_.Name -split '-')[0]) } -Descending |
    Select-Object -First 1
  if (-not $newest) {
    throw "There is no existing manifest folder in $packageRoot to start from."
  }
  $source = $newest.FullName
  New-Item -ItemType Directory -Path $target | Out-Null
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
foreach ($suffix in @('', '.installer', '.locale.en-US')) {
  $name = "$identifier$suffix.yaml"
  $text = [System.IO.File]::ReadAllText((Join-Path $source $name))

  # [^\r\n]* rather than .* so Windows line endings survive.
  $text = $text -replace '(?m)^PackageVersion:[^\r\n]*', "PackageVersion: $Version"
  $text = $text -replace '(?m)^(\s*InstallerUrl:)[^\r\n]*', "`$1 $installerUrl"
  $text = $text -replace '(?m)^(\s*InstallerSha256:)[^\r\n]*', "`$1 $hash"
  $text = $text -replace '(?m)^ReleaseNotesUrl:[^\r\n]*', "ReleaseNotesUrl: $releaseNotesUrl"
  # The placeholder explanation no longer applies.
  $text = $text -replace '(?m)^[ \t]*# (PLACEHOLDER|Until then)[^\r\n]*\r?\n', ''

  [System.IO.File]::WriteAllText((Join-Path $target $name), $text, $utf8NoBom)
  Write-Host "Wrote $(Join-Path $target $name)"
}

Write-Host ""
Write-Host "Next: winget validate --manifest `"$target`""
