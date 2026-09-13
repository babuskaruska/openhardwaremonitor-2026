The first release of the modernised fork of Open Hardware Monitor 0.9.6.

## Download

- **OpenHardwareMonitor.exe**: Windows 10 and 11, x64. One self-contained file with no installer and no .NET runtime to install. Download it and run it.
- **OpenHardwareMonitor-win-x64.zip**: the same executable with its licence file.
- **OpenHardwareMonitor-win-arm64-experimental.zip**: Windows on ARM. Untested.
- **SHA256SUMS.txt**: checksums for the files above.

Like the original, it asks for administrator permission when it starts, because SATA SMART data, CPU and motherboard temperatures, and fan control need it. The executable is not code-signed, so SmartScreen may say "Windows protected your PC"; choose **More info**, then **Run anyway**.

## Highlights

- **No kernel driver and no machine code generated at runtime.** Those were the two reasons antivirus products flagged the original (WinRing0, CVE-2020-14979).
- **2026 hardware:** hybrid Intel CPUs grouped into P-cores and E-cores (Alder Lake through Panther Lake), AMD Zen 5, NVMe health, current NVIDIA GPUs including fan control, and DDR5 module details.
- **Optional deep sensors through [PawnIO](https://pawnio.eu)**, a signed driver that is not on Microsoft's blocklist: CPU core and package temperatures and power, and motherboard fans, voltages and temperatures. The README explains the setup.
- **A new interface** with Overview, Sensors, Fans and Settings pages, light and dark themes, and smooth animations.
- **Fans page:** Automatic, Fixed speed or a temperature curve for each fan, with fail-safes that hand fans back to automatic control.
- **Export for AI:** a one-click diagnostics snapshot with automatic findings.
- **Web dashboard and JSON API**, listening on localhost only unless you allow remote access.
- **Fast:** the window opens in about half a second, sensors update on a background thread, and idle CPU use is 1–5 % of one core.

## Known limitations

- Changing motherboard fan speeds, and changing fan modes on the Fans page, have not yet been tested on real hardware.
- AMD CPUs and GPUs, Intel Arc, the Nuvoton NCT6799D and ARM64 were written from documentation and are untested.

The README has full details, the verification status and build instructions.
