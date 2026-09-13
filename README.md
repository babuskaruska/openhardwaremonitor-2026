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

**Download:** get `OpenHardwareMonitor.exe` from the
[latest release](https://github.com/babuskaruska/openhardwaremonitor-2026/releases/latest).
It is a single file for 64-bit Windows 10 and 11, with nothing to install.

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

The quickest way to enable Deep tier is **Settings › Hardware access › Full
sensor access › Set up**. It installs PawnIO with winget, downloads the signed
modules and restarts Open Hardware Monitor as administrator; Windows asks for
permission along the way.

To enable Deep tier by hand:

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
  - **Fans:** every controllable fan with its speed, a choice of **Automatic**,
    **Fixed speed** or **Curve** with an inline curve editor, fan detection and
    calibration, and Silent / Balanced / Performance profiles (see
    [Fan control](#fan-control)).
  - **Settings:** every option on one page, grouped into General, Appearance,
    Sensors, Logging, Web server, Hardware access, and Diagnostics and about.

  The ⋯ button holds the less common actions: technical report, reset
  minimum and maximum, sensor history, desktop gadget, hidden sensors, rescan
  hardware, report a problem, about and exit. Light and dark themes, crisp at any display scaling. Motion
  is subtle, follows the Windows animation effects setting and can be turned
  off under Settings → Appearance:
  - cards ease in and lift under the pointer;
  - readings and bars move smoothly to new values;
  - pages cross-fade.
- **Fast and light:** sensors are read on a background thread, so the window
  never waits for hardware (see [Performance](#performance)).
- **Full sensor access in a few clicks** from Settings › Hardware access (see
  [Access tiers](#access-tiers)).
- **Alerts** with Windows notifications (see [Alerts](#alerts)).
- **24-hour sensor history** (see [Sensor history](#sensor-history)).
- **Ready for long runs:** update notifications, crash reports, an application
  log and Report a problem (see
  [Long runs and problem reports](#long-runs-and-problem-reports)).
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

## Fan control

Everything is on the **Fans** page. Each fan card shows the fan's speed and a
choice of **Automatic** (the motherboard or graphics driver decides),
**Fixed speed** or **Curve**.

**Detect and calibrate.** Open Hardware Monitor can find out which speed
sensor belongs to which fan output and how slowly each motherboard fan can
turn. It asks first, because fan speeds change for a short time:
- **Detect speed sensor** drives the output to a clearly different speed and
  watches which speed sensor follows. Graphics card fans only get this step;
  their driver decides how slowly they turn.
- **Detect and calibrate** then steps a motherboard fan down 5 % at a time.
  If the fan stops, it finds the lowest speed that starts it again. A fan stays
  stopped for at most about 6 seconds.
- **Detect and calibrate all** does every fan in turn.

Detection stops at once and hands the fan back to automatic control if you
choose **Cancel**, if any CPU or GPU temperature passes 80 °C, if a reading goes
missing, if a stopped fan does not restart, after 4 minutes on one fan, or when
the application closes. It does not start above 75 °C. Afterwards, curves and
fixed speeds never go below the speed that keeps the fan turning.

**Curve editor.** Choosing **Curve** opens the editor inside the card. Drag a
point, double-click to add one, and right-click or press Delete to remove one;
the arrow keys move the selected point. Any temperature sensor can be the
source. A marker shows the current temperature and the resulting speed, and a
shaded band marks speeds too slow to keep the fan turning.

**Profiles.** Silent, Balanced and Performance each hold a setting for every
fan. Switch profiles at the top of the Fans page or from the tray icon's
**Fan profile** menu. **Save current settings as…** stores the current fan
setup in a profile; a profile that was never saved starts from defaults based
on calibration.

How curves behave:

- **Interpolation:** the duty cycle is interpolated linearly between the
  points you enter.

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
- The recent alert log.
- The full technical report as an appendix.

Without the GUI:

```bash
dotnet run --project tools/SensorDump/SensorDump.csproj -c Release -- --export <folder> --seconds 10
```

## Alerts

**Settings › Alerts** turns alerts and their Windows notifications on or off,
rule by rule. A condition has to last for the time shown before it alerts.
After an alert, the same rule stays quiet for 15 minutes unless it becomes
critical, and it clears only once the reading is back past the limit by a
margin.

| Rule | Alerts when | Lasting |
|---|---|---|
| Processor near its temperature limit | a core comes within 15 °C of its TjMax | 30 s |
| Graphics card hot | the GPU core reaches 83 °C (critical at 90 °C) | 30 s |
| Drive hot | a drive reaches 70 °C | 60 s |
| Fan stopped while hot | a fan that has been turning stops while its hardware is above 60 °C | 30 s |
| New drive media errors | the error count rises above the last value seen | at once |
| SSD spare capacity low | the available spare falls below the drive's own warning threshold | at once |
| Supply voltage out of range | a supply rail is more than 5 % off its nominal voltage | 10 s |
| CMOS battery low | the battery reads below 2.8 V | 60 s |

Existing media errors do not alert on every start; only new ones do. **Custom
rules** (Settings › Alerts › Custom rules) watch any sensor above or below a
value for a chosen time. While an alert is active, the overview's status chip
shows it; click the chip for the recent alerts. Export for AI includes the last
100 alerts.

## Sensor history

Every sensor keeps 24 hours of readings. Open a sensor's history by clicking the
trend line on an overview card, by double-clicking a sensor on the Sensors page
(or right-click › Show history), or from ⋯ › Sensor history.

The history page shows 10 minutes, 1 hour, 6 hours or 24 hours: the range of
readings, the average, the lowest and highest values with the times they
happened, and the exact value under the pointer. **Compare** overlays a second
sensor of the same kind. Gaps mark times when Open Hardware Monitor was not
running.

History is saved every 10 minutes as well as on exit, so a crash during a long
run loses at most the last 10 minutes. A full day for 200 busy sensors takes
about 6 MB in the settings file.

## Long runs and problem reports

- **Updates:** once a day, Open Hardware Monitor checks GitHub for a newer
  release and shows a notification. It never downloads or installs anything.
  Settings › Updates has **Check now**, **Get the new version** (opens the
  release page) and **Skip this version**.
- **Crash reports:** an unexpected error is written to
  `%LOCALAPPDATA%\OpenHardwareMonitor\Crashes` before anything is shown. A dialog
  then offers to open the report or to report it on GitHub. The newest 20
  reports are kept.
- **Application log:** `%LOCALAPPDATA%\OpenHardwareMonitor\Logs\app.log` records
  starts and stops, the access tier, sensor errors, web server failures and fan
  fail-safe events. It is capped at 1 MB, with one previous file kept.
- **Report a problem** (Settings › Diagnostics and about, or ⋯) saves an Export
  for AI snapshot, copies it to the clipboard, shows it in Explorer and opens a
  new GitHub issue with the version, Windows version and access tier filled in.
  Nothing is sent until you submit the issue.

Crash reports, the log and issue links replace your user name, computer name and
profile folder with placeholders.

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
architectures, checks for driver files, and attaches the executable and zipped
builds with SHA-256 checksums to releases tagged `v*`. `packaging/winget` holds
winget manifests for the portable executable; `update-manifest.ps1` fills in a
release's download address and checksum before submission.

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
| Motherboard fan control through PawnIO: the ITE IT8689E CPU fan driven from 100 % down to 0 % in 5 % steps (1,378 → 370 RPM) and handed back to automatic | ✅ verified (requires administrator) |
| Fan detection: RTX 3070 fan paired with its speed sensor (2,621 RPM response); the CPU fan paired and calibrated; Cancel part-way returns the fan to automatic | ✅ verified (requires administrator) |
| A fan that stops at low speed: stall detection and restart | ⚠️ unit tests only (the CPU fan on the test PC keeps turning at 0 %) |
| Guided full sensor access setup: status and dialog against the real PawnIO 2.2.0 installation | ✅ verified (the winget install and module download were not run) |
| Update check against the real GitHub release | ✅ verified |
| Alert engine: debounce, cooldown, recovery, media-error baseline, custom rules | ✅ unit tests |
| Windows notifications, the history page, the alert and crash dialogs, Report a problem, fan profiles and the curve editor on screen | ⚠️ not yet tested on screen |
| Remote web access with token; Run On Windows Startup | ⚠️ not yet tested |
| AMD CPUs and GPUs, Intel Arc, NCT6799D, other Super I/O boards through PawnIO, ARM64 | ⚠️ written from documentation, untested |

Reports from other hardware are welcome. Please include the output of
`tools/SensorDump`.

## License

Mozilla Public License 2.0; see `License.html`. Open Hardware Monitor is
copyright © 2009–2020 Michael Möller and contributors.
