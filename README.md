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
| **Deep** (optional) | [PawnIO](https://pawnio.eu), its signed modules, and administrator rights | Intel CPU core and package temperatures; CPU package and core power; motherboard Super I/O fans, voltages and temperatures. AMD CPU temperatures are not yet supported through PawnIO |

Base tier needs no driver. The CPU, NVIDIA, NVMe and memory sensors listed
above are also read without administrator rights. Like the original, the
application still asks for administrator permission when it starts, because
SATA SMART data, Deep tier, listening for remote web connections and changing
fan speeds need it. The NVIDIA driver rejects fan changes from programs
running without administrator rights.

To enable Deep tier:

1. Install PawnIO, a signed driver that is not on the blocklist and is used by
   LibreHardwareMonitor, FanControl and OpenRGB:

   ```bash
   winget install -e --id namazso.PawnIO
   ```

2. Download the signed modules from the
   [PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases)
   (`release_x_y_z.zip`) and copy the `.bin` files into
   `%LOCALAPPDATA%\OpenHardwareMonitor\PawnIOModules`. Intel CPU sensors
   need `IntelMSR.bin`; motherboard sensors need `LpcIO.bin`. The driver checks each module's signature before loading it.
3. Start Open Hardware Monitor as administrator. By default PawnIO refuses
   programs without administrator rights.

If Deep tier is still unavailable, `tools/SensorDump` prints the exact reason
under "Low-level access" (for example a missing module or missing
administrator rights).

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
- Super I/O through PawnIO:
  - An adapter maps the Super I/O code onto PawnIO's LpcIO module. It uses
    one module instance per chip slot and discovers each chip's I/O ranges
    during detection.
  - The ITE IT8689E is fully supported (10 voltages, 6 temperatures, 6 fans,
    6 fan outputs), with a mapping for the Gigabyte B760M GAMING PLUS WIFI
    DDR4.
  - Nuvoton NCT6799D was added but is untested.

**Application**
- **A new interface** with four pages along the top (Ctrl+1 to Ctrl+4):
  - **Overview:** one card each for the CPU, GPU, memory, motherboard and
    storage, with the readings that matter, a five-minute trend and colour
    cues when something runs warm or hot. **More info** on a card opens its
    sensors.
  - **Sensors:** the full sensor list, without grid or tree lines and with
    vector icons. Esc returns to the overview.
  - **Fans:** every controllable fan with its current speed and a choice of
    **Automatic**, **Fixed speed** (a slider) or **Curve** (see below).
  - **Settings:** every option on one page, grouped into General, Appearance,
    Sensors, Logging, Web server, Hardware access, and Diagnostics and about.

  The ⋯ button holds the less common actions: technical report, reset
  minimum and maximum, plot, desktop gadget, hidden sensors, rescan hardware,
  about and exit. Light and dark themes, crisp at any display scaling. Motion
  is subtle, follows the Windows animation effects setting and can be turned
  off under Settings → Appearance:
  - cards ease in and lift under the pointer;
  - readings and bars move smoothly to new values;
  - pages cross-fade.
- **Fast and light:** sensors are read on a background thread, so the window
  never waits for hardware (see [Performance](#performance)).
- **Export for AI:** a diagnostics snapshot in one click (see below).
- Settings are stored in `%LOCALAPPDATA%\OpenHardwareMonitor\`, not next to the
  executable. Logs are in `%LOCALAPPDATA%\OpenHardwareMonitor\Logs`
  (Settings → Logging → Log folder).
- Only one instance runs at a time. Settings → General → Start with Windows
  starts it when you sign in, and Settings → Hardware access → Restart as
  administrator enables the sensors and fan control that need it.
- The legacy WMI provider has been removed: its API does not exist on modern .NET.

## Performance

Hardware is read on a dedicated background thread; the window never waits for
it. Measured on the test PC described under Verification status, using the
published executable without administrator rights:

| Measurement | Result |
|---|---|
| Window visible after launch | 0.56 s; hardware detection continues in the background and the overview shows "Detecting hardware…" meanwhile |
| One update of every sensor | 5–30 ms, on the sensor thread |
| Idle CPU with the window open | 1–5 % of one core depending on the page, less when minimised |
| UI thread response while sensors update | median 0.3 ms, worst 1.5 ms |
| Moving the window | worst frame 2.8 ms, none over 16 ms |
| Closing | under 0.1 s |

What made the difference:
- Sensor updates moved off the UI thread. They used to block it for up to a
  few hundred milliseconds every second, which made dragging the window stutter.
- Per-core clocks are read with one PDH query instead of 28 .NET
  `PerformanceCounter` objects, which cost 0.4 s on every start and about 5 s
  after a cold boot.
- Drive letters come from each volume's disk extents instead of WMI (0.7 s).
- Hardware detection itself runs on the sensor thread.
- Overview cards cache their static artwork and repaint only what changed.
  Animations run only while something moves and pause while the window is
  being dragged.
- The published executable is precompiled (ReadyToRun).

## Fan curves

On the **Fans** page, choose **Curve** for a fan to drive it from any
temperature sensor. A curve maps the temperature to a fan duty cycle using
linear interpolation between the points you enter.

- **Hysteresis:** 3 °C by default, so a fan does not hunt up and down around a
  point.
- **Clamping:** the duty is always kept inside the range the hardware reports
  as valid, so a curve can never stop a fan the hardware does not allow to stop.
- **Fail-safe:** if the temperature source stops reporting for 5 consecutive
  updates, the fan is returned to automatic (hardware) control.
- **On exit, including after a crash**, every fan a curve was driving is
  handed back to automatic control.
- Choosing **Automatic** or **Fixed speed** for the fan removes the curve.

For NVIDIA GPUs, Open Hardware Monitor returns the fans to automatic control on
exit only if it changed them during that session. That way it does not
override another utility, such as MSI Afterburner, that is managing the fans.

## Export for AI

**Export for AI** (the button in the top bar, Ctrl+E, Settings → Diagnostics
and about, or the tray menu) saves a diagnostics snapshot in one click. It
writes two files to `Documents\OpenHardwareMonitor\Diagnostics`:
`OHM-diagnostics-<date>-<time>.md` and `.json`. The Markdown is copied to the
clipboard, ready to paste into an AI assistant, and Explorer opens with the
file selected.

The snapshot contains:
- Windows version, uptime, how long Open Hardware Monitor has been running,
  and the sensor access tier.
- Every sensor with its current value, its minimum and maximum since the app
  started, and statistics from its history.
- **Findings**: rule-based checks with a severity and a plain explanation, for
  example:
  - CPU temperatures close to TjMax, and hot GPUs or drives;
  - NVMe wear, low spare capacity and media errors;
  - voltage rails outside ±5 %, a weak CMOS battery;
  - fans stopped while hot, full memory or drives;
  - missing sensor access.
- The full technical report as an appendix.

Without the GUI:

```bash
dotnet run --project tools/SensorDump/SensorDump.csproj -c Release -- --export <folder> --seconds 10
```

## Web server and JSON API

Settings → Web server → Run the web server starts a local web server with a
live dashboard at `http://localhost:8085/`. The port can be changed under
Settings → Web server → Port and connection link.

| Endpoint | Description |
|---|---|
| `GET /api/v1/sensors` | Versioned JSON sensor tree, for Home Assistant, Grafana, Rainmeter and similar |
| `GET /data.json` | The 0.9.6 format, for existing integrations |
| `GET /api/v1/diagnostics` | The Export for AI snapshot as JSON |
| `GET /api/v1/diagnostics.md` | The same snapshot as Markdown |

Security defaults:
- The server listens on **localhost only**.
- **Remote access is opt-in** (Settings → Web server → Allow connections from
  other devices). Remote clients must present an access token (`?token=…`);
  the link containing the token is shown under Settings → Web server → Port
  and connection link. Listening on all interfaces requires running Open
  Hardware Monitor as administrator.
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
it runs on a PC without .NET installed and is precompiled (ReadyToRun), so it
starts quickly:

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
NVMe and Corsair DDR4-3200 memory on a Gigabyte B760M GAMING PLUS WIFI DDR4
board, running Windows 11 (build 26200). Everything except the Deep tier row
was tested with no driver installed:

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
| New interface (overview, sensor list, light and dark themes), rendered from the real sensors | ✅ verified |
| Overview, Sensors, Fans and Settings pages in the running application, light and dark | ✅ verified |
| Performance figures above, measured on the published executable | ✅ verified |
| Export for AI through SensorDump, including a real finding (NVMe media errors on a test drive) | ✅ verified |
| Web dashboard; localhost-only binding; JSON escaping | ✅ verified |
| Fan curve engine (interpolation, hysteresis, parsing) | ✅ unit tests |
| RTX 3070 fan control: Manual 65 % (0 → 1446 RPM), 40 % (→ 402 RPM), back to automatic | ✅ verified (requires administrator) |
| Deep tier with PawnIO 2.2.0 and the IntelMSR module, as administrator: P-cores 32–42 °C, E-cores 33 °C, package 40 °C, TjMax 100 °C read from the CPU, package power 37.9 W | ✅ verified |
| Motherboard sensors through PawnIO (ITE IT8689E, Gigabyte B760M GAMING PLUS WIFI DDR4): +12V 11.95 V, +5V 5.13 V, +3.3V 3.36 V, VBat 3.05 V, six temperatures, two fans | ✅ verified |
| Motherboard fan control (writing fan speeds) | ⚠️ not yet tested on hardware |
| Changing fan modes on the Fans page (no fan speeds were written while testing the page) | ⚠️ not yet tested on hardware |
| Remote web access with token; Run On Windows Startup | ⚠️ not yet tested |
| AMD CPUs and GPUs, Intel Arc, NCT6799D, other Super I/O boards through PawnIO, ARM64 | ⚠️ written from documentation, untested |

Reports from other hardware are welcome. Please include the output of
`tools/SensorDump`.

## License

Mozilla Public License 2.0; see `License.html`. Open Hardware Monitor is
copyright © 2009–2020 Michael Möller and contributors.
