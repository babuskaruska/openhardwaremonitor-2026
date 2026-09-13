# Open Hardware Monitor — 2026 edition

A modernised fork of [Open Hardware Monitor](https://openhardwaremonitor.org) 0.9.6
(last released December 2020). It shows temperatures, fan speeds, voltages,
loads, clock speeds and drive health on Windows. This fork:

- **ships no kernel driver and generates no machine code at runtime**, which
  were the two reasons antivirus products flagged the original;
- runs on **.NET 10** as a single self-contained executable, with no separate
  runtime to install;
- understands **2026-era hardware**: hybrid Intel CPUs, NVMe drives, current
  NVIDIA GPUs and DDR5 memory.

## Why the original was flagged as malware

The detections were **not false positives**.

1. **WinRing0.** Open Hardware Monitor extracted `WinRing0.sys` / `WinRing0x64.sys`
   to disk and installed it as a kernel service. WinRing0 lets *any* program
   on the machine read and write CPU MSRs, I/O ports and physical memory
   ([CVE-2020-14979](https://nvd.nist.gov/vuln/detail/CVE-2020-14979)). It is on
   Microsoft's vulnerable driver blocklist, and Defender reports it as
   `VulnerableDriver:WinNT/Winring0`. On a PC with the blocklist enabled, which is
   the Windows 11 default, the driver is refused anyway, so the original
   triggered the warning *and* read nothing through it.
2. **Executable memory.** `Opcode.cs` allocated read-write-execute memory, copied
   raw x86 machine code into it and called it. That is a classic heuristic
   for malicious code.

Both are gone. CPUID is read through the .NET `X86Base.CpuId` intrinsic, and all
low-level hardware access goes through a pluggable backend that is disabled
unless you install PawnIO (see below). CI fails the build if a `.sys`, `.inf` or
`.cat` file ever reappears in the published output.

## Access tiers

| Tier | Requires | Provides |
|---|---|---|
| **Base** (default) | nothing | CPU name, hybrid topology, per-core load and clock speeds; NVIDIA GPU temperature, load, clocks, memory, power, fan speed and fan control; NVMe health (temperature, wear, spare, errors, data written); per-module memory information; memory usage |
| **Deep** (optional) | [PawnIO](https://pawnio.eu) | CPU core and package temperatures, CPU package power, motherboard Super I/O sensors (fans, voltages, temperatures) and motherboard fan control |

Base tier needs no driver. The CPU, NVIDIA, NVMe and memory sensors listed
above are also read without administrator rights. Like the original, the
application still asks for administrator permission when it starts, because
SATA SMART data, Deep tier, listening for remote web connections and changing
fan speeds need it. The NVIDIA driver rejects fan changes from programs
running without administrator rights.

To enable Deep tier, install PawnIO, a signed driver that is not on the
blocklist and is used by LibreHardwareMonitor, FanControl and OpenRGB, then
restart Open Hardware Monitor:

```bash
winget install -e --id namazso.PawnIO
```

> **Status:** the PawnIO backend is written against PawnIO's documented
> interface but has **not yet been tested with PawnIO installed**. Treat Deep
> tier as experimental.

## What is new compared with 0.9.6

**Hardware**
- Intel: model table through Alder Lake, Raptor Lake, Meteor Lake, Arrow Lake,
  Lunar Lake and Panther Lake. Hybrid CPUs are grouped correctly into
  **P-cores and E-cores** using CPUID leaves 0x1A and 0x1F (0.9.6 put the
  threads under the wrong cores). Per-core clock speeds come from Windows
  performance counters, so they work without a driver.
- AMD: family 0x1A (Zen 5) recognised.
- Storage: a new **NVMe** path (Identify plus the SMART/Health log, read without
  administrator rights). In 0.9.6 NVMe drives appeared as "Generic Hard Disk".
- NVIDIA: clock speeds read with `GetAllClockFrequencies`; the old call returned
  garbage on current GPUs. **Fan control on Turing and later** through the
  client fan cooler interface; the old interface returns "not supported" there,
  so fan control had silently disappeared.
- Memory: SMBIOS is read directly from firmware, with no WMI. Size, type
  (DDR4/DDR5), speed, rank and voltage per module, including the SMBIOS 3.3
  extended fields. The memory node is named after the kit, for example
  "Corsair 32 GB DDR4-3200".
- Super I/O: Nuvoton NCT6799D and ITE IT8689E added. **Untested**; they need
  Deep tier.

**Application**
- Menus ported to current WinForms controls; per-monitor DPI awareness; a
  **dark theme** (Options → Theme: Follow Windows, Light or Dark; takes effect
  on the next start).
- **Fan curves:** right-click a controllable fan sensor, then Control →
  Curve… to drive it from any temperature sensor (details below).
- Settings are stored in `%LOCALAPPDATA%\OpenHardwareMonitor\`, not next to the
  executable. Logs are in `%LOCALAPPDATA%\OpenHardwareMonitor\Logs`
  (Options → Open Log Folder).
- Only one instance runs at a time; Options → Run On Windows Startup starts it
  with Windows.
- The legacy WMI provider has been removed: its API does not exist on modern .NET.

## Fan curves

A curve maps a temperature to a fan duty cycle using linear interpolation
between the points you enter.

- **Hysteresis:** 3 °C by default, so a fan does not hunt up and down around a
  point.
- **Clamping:** the duty is always kept inside the range the hardware reports
  as valid, so a curve can never stop a fan the hardware does not allow to stop.
- **Fail-safe:** if the temperature source stops reporting for 5 consecutive
  updates, the fan is returned to automatic (hardware) control.
- **On exit, including after a crash**, every fan a curve was driving is
  handed back to automatic control.
- Choosing Control → Default or a fixed Manual level removes the curve.

For NVIDIA GPUs, Open Hardware Monitor returns the fans to automatic control on
exit only if it changed them during that session. That way it does not
override another utility, such as MSI Afterburner, that is managing the fans.

## Web server and JSON API

Options → Web Server → Run starts a local web server with a live dashboard at
`http://localhost:8085/`. The port can be changed under Web Server → Port.

| Endpoint | Description |
|---|---|
| `GET /api/v1/sensors` | Versioned JSON sensor tree, for Home Assistant, Grafana, Rainmeter and similar |
| `GET /data.json` | The 0.9.6 format, for existing integrations |

Security defaults:
- The server listens on **localhost only**.
- **Remote access is opt-in** (Web Server → Allow Remote Connections). Remote
  clients must present an access token (`?token=…`); the link containing the
  token is shown under Web Server → Port. Listening on all interfaces requires
  running Open Hardware Monitor as administrator.
- JSON is generated with a real JSON writer, so sensor names cannot inject
  script. The dashboard is served with a strict Content Security Policy and
  only from a fixed list of embedded files.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build OpenHardwareMonitor.slnx -c Release
```

```bash
dotnet test tests/OpenHardwareMonitor.Tests/OpenHardwareMonitor.Tests.csproj -c Release
```

Self-contained single-file build. The output goes to `artifacts/publish/win-x64/`;
it runs on a PC without .NET installed:

```bash
dotnet publish OpenHardwareMonitor.csproj -c Release -p:PublishProfile=SelfContained-win-x64
```

A `SelfContained-win-arm64` profile also exists. It is **experimental and
untested** on ARM hardware.

`tools/SensorDump` is a console tool that prints every detected sensor. It is
useful for bug reports and for checking hardware support without the GUI:

```bash
dotnet run --project tools/SensorDump/SensorDump.csproj -c Release
```

GitHub Actions (`.github/workflows/build.yml`) builds, tests and publishes both
architectures, checks for driver files, and attaches zipped builds with SHA-256
checksums to releases tagged `v*`.

## Verification status

Tested on an Intel Core i7-14700KF, NVIDIA GeForce RTX 3070, Samsung 990 PRO
NVMe and Corsair DDR4-3200 memory, running Windows 11 (build 26200), with no
driver installed:

| Area | Status |
|---|---|
| No driver installed or loaded; no `.sys`, `.inf` or `.cat` in the published output | ✅ verified |
| No detection by the installed antivirus (Malwarebytes; Microsoft Defender was disabled on the test PC, so it was not tested) | ✅ verified |
| Self-contained executable starts without .NET installed | ✅ verified |
| Base-tier sensors read by `SensorDump` without administrator rights | ✅ verified |
| 8 P-cores + 12 E-cores grouped and labelled; per-core load and clocks | ✅ verified |
| NVMe health on two drives | ✅ verified |
| RTX 3070 temperature, load, clocks, memory, power, fan speed | ✅ verified |
| Memory module names and details from SMBIOS | ✅ verified |
| Dark theme; web dashboard; localhost-only binding; JSON escaping | ✅ verified |
| Fan curve engine (interpolation, hysteresis, parsing) | ✅ unit tests |
| RTX 3070 fan control: Manual 65 % (0 → 1446 RPM), 40 % (→ 402 RPM), back to automatic | ✅ verified (requires administrator) |
| Deep tier with PawnIO (CPU temperatures, Super I/O, motherboard fans) | ⚠️ not yet tested |
| Remote web access with token; Run On Windows Startup | ⚠️ not yet tested |
| AMD CPUs and GPUs, Intel Arc, NCT6799D, IT8689E, ARM64 | ⚠️ written from documentation, untested |

Reports from other hardware are welcome. Please include the output of
`tools/SensorDump`.

## License

Mozilla Public License 2.0; see `License.html`. Open Hardware Monitor is
copyright © 2009–2020 Michael Möller and contributors.
