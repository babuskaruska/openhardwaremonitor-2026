The final big update before a long soak test: everything in 0.10.0, plus the features below.

## Download

- **OpenHardwareMonitor.exe**: Windows 10 and 11, x64. One self-contained file with no installer and no .NET runtime to install. Download it and run it.
- **OpenHardwareMonitor-win-x64.zip**: the same executable with its licence file.
- **OpenHardwareMonitor-win-arm64-experimental.zip**: Windows on ARM. Untested.
- **SHA256SUMS.txt**: checksums for the files above.

Like the original, it asks for administrator permission when it starts, because SATA SMART data, CPU and motherboard temperatures, and fan control need it. The executable is not code-signed, so SmartScreen may say "Windows protected your PC"; choose **More info**, then **Run anyway**.

## New in 0.11.0

- **Full sensor access in a few clicks.** Settings › Hardware access › Full sensor access › **Set up** installs the PawnIO driver with winget, downloads its signed modules and restarts Open Hardware Monitor as administrator, asking for permission along the way.
- **Fan control, finished.** The Fans page now:
  - finds which speed sensor belongs to which fan;
  - calibrates the minimum speed of each motherboard fan;
  - edits curves inline: drag points, see a live marker, and see a band for speeds too slow to keep the fan turning;
  - switches Silent, Balanced and Performance profiles, also from the tray.

  Detection hands a fan back to automatic control on Cancel, above 80 °C, when a reading goes missing, or when the app closes.
- **Alerts.** Windows notifications when:
  - a processor nears its temperature limit;
  - a graphics card or drive runs hot;
  - a fan stops while hot;
  - a supply voltage leaves its range;
  - the CMOS battery is low;
  - SSD spare runs low;
  - a new drive media error appears.

  You can also add custom rules for any sensor.
- **24-hour sensor history.** Graphs from 10 minutes to 24 hours with lows, peaks and averages. Open them by clicking a card's trend line, double-clicking a sensor, or from ⋯ › Sensor history. History is saved every 10 minutes, so a crash loses little.
- **Ready for long runs:**
  - a daily update check with a notification; nothing is downloaded automatically;
  - crash reports saved to disk before anything is shown;
  - an application log;
  - **Report a problem**, which exports diagnostics and opens a prefilled GitHub issue.

## Fixes

- The installed PawnIO version is shown correctly (2.2.0 instead of 2.0.0).
- The application log records the real uptime when the app stops.

## Verified on real hardware

- The RTX 3070 fan was paired with its speed sensor.
- The ITE IT8689E CPU fan was paired and calibrated from 100 % down to 0 %.
- Cancelling part-way handed the fan back to automatic control.
- The window opens in under half a second, and idle CPU use is 1–5 % of one core.

## Known limitations

- Finding the restart speed of a fan that stops at low duty is covered by unit tests only, because the test PC's CPU fan never stops.
- Not yet checked on screen: Windows notifications, the history page, the alert and crash dialogs, and switching fan profiles.
- Untested, written from documentation: AMD CPUs and GPUs, Intel Arc, the Nuvoton NCT6799D and ARM64.

The README has full details, the verification status and build instructions.
