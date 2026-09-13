# winget package

These are the manifests for publishing Open Hardware Monitor 2026 to the
[Windows Package Manager](https://learn.microsoft.com/windows/package-manager/)
as `babuskaruska.OpenHardwareMonitor2026`. They are prepared here and have
**not** been submitted.

```
manifests/b/babuskaruska/OpenHardwareMonitor2026/<version>/
  babuskaruska.OpenHardwareMonitor2026.yaml               version manifest
  babuskaruska.OpenHardwareMonitor2026.installer.yaml     installer: portable x64 exe
  babuskaruska.OpenHardwareMonitor2026.locale.en-US.yaml  name, description, licence
```

The folder layout matches the
[winget-pkgs](https://github.com/microsoft/winget-pkgs) repository, so a
version folder can be copied there unchanged.

The package installs `OpenHardwareMonitor.exe` from the GitHub release as a
*portable* app: winget copies the executable and adds the command
`openhardwaremonitor` to the path. There is no installer, and uninstalling
removes the file. Settings, logs and crash reports in
`%LOCALAPPDATA%\OpenHardwareMonitor` stay.

## 1. Fill in the hash

The 0.10.0 installer manifest contains a placeholder instead of
`InstallerSha256`. `winget validate` rejects it, so it cannot be submitted
by accident.

After a release has been published by the release workflow, run from this
folder:

```powershell
./update-manifest.ps1 -Version 0.10.0
```

The script downloads that release's `SHA256SUMS.txt` (not the executable),
takes the hash of `OpenHardwareMonitor.exe` and writes it into the manifest.
For a new version, for example `-Version 0.11.0`, it copies the newest
existing version folder and updates the version, download URL, hash and
release notes link. Check the description and tags by hand if the release
changed what the app does.

## 2. Validate

With winget installed (it ships with Windows 10 and 11 as App Installer):

```powershell
winget validate --manifest manifests\b\babuskaruska\OpenHardwareMonitor2026\0.10.0
```

To try the package locally before submitting, allow local manifests once
(this needs an administrator terminal) and install from the folder:

```powershell
winget settings --enable LocalManifestFiles
winget install --manifest manifests\b\babuskaruska\OpenHardwareMonitor2026\0.10.0
openhardwaremonitor
winget uninstall babuskaruska.OpenHardwareMonitor2026
```

The application asks for administrator permission when it starts, and
SmartScreen may warn because the executable is not code-signed. Both are
expected.

## 3. Submit

The simplest way is [wingetcreate](https://github.com/microsoft/winget-create)
(`winget install Microsoft.WingetCreate`). It forks winget-pkgs and opens the
pull request with a GitHub token that has the `public_repo` scope:

```powershell
wingetcreate submit --token <github-token> manifests\b\babuskaruska\OpenHardwareMonitor2026\0.10.0
```

Alternatively, copy the version folder to the same path in a fork of
`microsoft/winget-pkgs` and open a pull request yourself.

A bot validates the pull request and installs the package in a sandbox; a
moderator then merges it. For later releases, either run
`update-manifest.ps1` and submit again, or let wingetcreate update the
published package directly:

```powershell
wingetcreate update babuskaruska.OpenHardwareMonitor2026 --version 0.11.0 --urls https://github.com/babuskaruska/openhardwaremonitor-2026/releases/download/v0.11.0/OpenHardwareMonitor.exe --submit --token <github-token>
```
