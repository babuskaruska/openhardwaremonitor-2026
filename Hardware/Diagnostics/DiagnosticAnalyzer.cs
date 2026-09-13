/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  public enum DiagnosticSeverity {
    Info,
    Warning,
    Critical
  }

  /// <summary>One reading that supports a finding.</summary>
  public sealed class DiagnosticEvidence {

    internal DiagnosticEvidence() { }

    public string? SensorIdentifier { get; internal set; }
    public string? SensorName { get; internal set; }
    public string? HardwareName { get; internal set; }
    public SensorType? Type { get; internal set; }
    public float? Value { get; internal set; }
    public float? Min { get; internal set; }
    public float? Max { get; internal set; }

    /// <summary>The limit the reading was compared against, in the sensor's unit.</summary>
    public float? Threshold { get; internal set; }

    public string? Note { get; internal set; }

    public string Unit {
      get { return Type.HasValue ? DiagnosticFormat.Unit(Type.Value) : ""; }
    }
  }

  public sealed class DiagnosticFinding {

    internal DiagnosticFinding() { }

    public DiagnosticSeverity Severity { get; internal set; }

    /// <summary>Stable identifier of the rule; see <see cref="DiagnosticRules"/>.</summary>
    public string RuleId { get; internal set; } = "";

    public string Title { get; internal set; } = "";

    /// <summary>Plain-language explanation of what it means and what to check.</summary>
    public string Explanation { get; internal set; } = "";

    public string? HardwareName { get; internal set; }
    public string? HardwareIdentifier { get; internal set; }

    public IReadOnlyList<DiagnosticEvidence> Evidence { get; internal set; } =
      Array.Empty<DiagnosticEvidence>();
  }

  /// <summary>Rule identifiers. These are part of the export format; do not rename.</summary>
  public static class DiagnosticRules {
    public const string CpuTemperatureNearTjMax = "cpu-temperature-near-tjmax";
    public const string GpuTemperatureHigh = "gpu-temperature-high";
    public const string StorageTemperatureHigh = "storage-temperature-high";
    public const string StorageEnduranceUsed = "storage-endurance-used";
    public const string StorageSpareLow = "storage-spare-low";
    public const string StorageMediaErrors = "storage-media-errors";
    public const string StorageUnsafeShutdowns = "storage-unsafe-shutdowns";
    public const string DriveSpaceLow = "drive-space-low";
    public const string FanStoppedWhileHot = "fan-stopped-while-hot";
    public const string GpuFanZeroRpmIdle = "gpu-fan-zero-rpm-idle";
    public const string VoltageOutOfRange = "voltage-out-of-range";
    public const string VoltageExcursion = "voltage-excursion";
    public const string VoltageImplausible = "voltage-implausible";
    public const string CmosBatteryLow = "cmos-battery-low";
    public const string MemoryLoadHigh = "memory-load-high";
    public const string VirtualMemoryLoadHigh = "virtual-memory-load-high";
    public const string SensorNoValue = "sensor-no-value";
    public const string AccessTierLimited = "access-tier-limited";
    public const string CaptureIncomplete = "capture-incomplete";
  }

  /// <summary>Every limit the analyzer uses, in the sensor's canonical unit.</summary>
  public static class DiagnosticThresholds {

    /// <summary>Assumed when a CPU temperature sensor has no TjMax parameter.</summary>
    public const float CpuDefaultTjMax = 100f;
    public const float CpuTjMaxWarningMargin = 15f;
    public const float CpuTjMaxCriticalMargin = 5f;

    public const float GpuCoreWarning = 83f;
    public const float GpuCoreCritical = 90f;

    public const float StorageTemperatureWarning = 70f;

    /// <summary>NVMe "percentage used" (100 minus remaining life).</summary>
    public const float StorageEnduranceUsedWarning = 90f;
    public const float StorageEnduranceUsedCritical = 100f;

    /// <summary>Available spare below this is low even without a reported threshold.</summary>
    public const float StorageSpareWarning = 20f;

    public const float StorageUnsafeShutdownsInfo = 50f;

    /// <summary>Unsafe shutdowns as a share of power cycles, when cycles are known.</summary>
    public const float StorageUnsafeShutdownRatio = 0.1f;

    public const float DriveUsedSpaceWarning = 95f;

    /// <summary>Readings below this count as a stopped fan.</summary>
    public const float FanStoppedRpm = 1f;
    public const float FanHotTemperature = 60f;

    /// <summary>ATX rail tolerance, as a fraction of nominal.</summary>
    public const float RailTolerance = 0.05f;

    /// <summary>
    /// Beyond this fraction of nominal the reading is almost certainly an
    /// unused input or wrong scaling for the board, not a real rail voltage.
    /// </summary>
    public const float RailImplausibleTolerance = 0.5f;

    public const float CmosBatteryLow = 2.8f;
    public const float CmosBatteryImplausibleBelow = 1.0f;
    public const float CmosBatteryImplausibleAbove = 4.0f;

    public const float MemoryLoadWarning = 90f;
    public const float VirtualMemoryLoadWarning = 90f;
  }

  /// <summary>
  /// Rule-based checks over a <see cref="DiagnosticSnapshot"/>. Rules match
  /// sensors by type and by forgiving name patterns, because names differ by
  /// vendor, board configuration and can be renamed by the user.
  /// </summary>
  public static class DiagnosticAnalyzer {

    public static IReadOnlyList<DiagnosticFinding> Analyze(
      DiagnosticSnapshot snapshot) {
      if (snapshot == null)
        throw new ArgumentNullException(nameof(snapshot));

      List<DiagnosticFinding> findings = new List<DiagnosticFinding>();
      foreach (DiagnosticHardware hardware in snapshot.AllHardware()) {
        CheckCpuTemperatures(hardware, findings);
        CheckGpuTemperatures(hardware, findings);
        CheckStorage(hardware, findings);
        CheckFans(hardware, findings);
        CheckVoltages(hardware, findings);
        CheckMemory(hardware, findings);
        CheckMissingValues(hardware, findings);
      }
      CheckAccess(snapshot, findings);
      CheckCaptureErrors(snapshot, findings);

      // OrderBy is stable, so rules keep their evaluation order within a severity.
      return findings.OrderByDescending(f => f.Severity).ToList();
    }

    // ---- CPU ------------------------------------------------------------------

    internal static bool IsCpuCoreOrPackage(string name) {
      string n = DiagnosticFormat.Normalize(name);
      if (n.Contains("distance") || n.Contains("tjmax"))
        return false;
      return n.Contains("core") || n.Contains("package") || n.Contains("ccd") ||
        n.Contains("tctl") || n.Contains("tdie") || n.Contains("die");
    }

    /// <summary>The sensor's TjMax parameter, or the default when absent or invalid.</summary>
    internal static float GetTjMax(DiagnosticSensor sensor, out bool fromParameter) {
      DiagnosticParameter? parameter = sensor.FindParameter("TjMax");
      fromParameter = parameter != null && float.IsFinite(parameter.Value) &&
        parameter.Value > 0;
      return fromParameter ? parameter!.Value : DiagnosticThresholds.CpuDefaultTjMax;
    }

    private static DiagnosticSeverity? TjMaxSeverity(float? value, float tjMax) {
      if (!value.HasValue)
        return null;
      float margin = tjMax - value.Value;
      if (margin <= DiagnosticThresholds.CpuTjMaxCriticalMargin)
        return DiagnosticSeverity.Critical;
      if (margin <= DiagnosticThresholds.CpuTjMaxWarningMargin)
        return DiagnosticSeverity.Warning;
      return null;
    }

    private static void CheckCpuTemperatures(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      if (hardware.Type != HardwareType.CPU)
        return;

      List<DiagnosticEvidence> evidence = new List<DiagnosticEvidence>();
      DiagnosticSeverity? worst = null;
      bool hotNow = false;
      bool anyDefaultTjMax = false;

      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        if (sensor.Type != SensorType.Temperature ||
          !IsCpuCoreOrPackage(sensor.Name))
          continue;

        float tjMax = GetTjMax(sensor, out bool fromParameter);
        DiagnosticSeverity? current = TjMaxSeverity(sensor.Value, tjMax);
        DiagnosticSeverity? peak = TjMaxSeverity(sensor.Max, tjMax);
        DiagnosticSeverity? severity = Worst(current, peak);
        if (!severity.HasValue)
          continue;

        hotNow |= current.HasValue;
        anyDefaultTjMax |= !fromParameter;
        worst = Worst(worst, severity);

        string note = "TjMax " + Plain(tjMax) + " °C (" +
          (fromParameter ? "from sensor parameter" : "assumed default") + "); " +
          (current.HasValue
            ? "current reading is " + Plain(tjMax - sensor.Value!.Value) +
              " °C below TjMax"
            : "only the peak since app start came within " +
              Plain(tjMax - sensor.Max!.Value) + " °C of TjMax");
        evidence.Add(Evidence(sensor, tjMax, note));
      }

      if (!worst.HasValue)
        return;

      string explanation =
        "TjMax is the temperature at which the processor protects itself by " +
        "throttling. Readings within " +
        Plain(DiagnosticThresholds.CpuTjMaxWarningMargin) + " °C of TjMax " +
        "are flagged as a warning, within " +
        Plain(DiagnosticThresholds.CpuTjMaxCriticalMargin) + " °C as critical. " +
        (hotNow
          ? "At least one sensor is in this range right now. "
          : "Only the peak since the application started reached this range and " +
            "current readings are lower, which points to heat under load (a game, " +
            "render or stress test) rather than constant overheating. ") +
        "Common causes: a clogged or badly mounted cooler, dried thermal paste, a " +
        "failed pump or fan, aggressive overclocking or voltage settings, or poor " +
        "case airflow. Some recent desktop CPUs are designed to run close to TjMax " +
        "under full load, so compare against idle behaviour and check whether the " +
        "CPU clock drops (throttling) at the same time." +
        (anyDefaultTjMax
          ? " Some sensors had no TjMax parameter, so " +
            Plain(DiagnosticThresholds.CpuDefaultTjMax) + " °C was assumed."
          : "");

      findings.Add(Finding(worst.Value, DiagnosticRules.CpuTemperatureNearTjMax,
        worst == DiagnosticSeverity.Critical
          ? "CPU temperature at or very near TjMax"
          : "CPU temperature close to TjMax",
        explanation, hardware, evidence));
    }

    // ---- GPU ------------------------------------------------------------------

    internal static bool IsGpuCoreTemperature(string name) {
      string n = DiagnosticFormat.Normalize(name);
      if (n.Contains("hot spot") || n.Contains("hotspot") ||
        n.Contains("junction") || n.Contains("memory") || n.Contains("vrm"))
        return false;
      return n.Contains("core") || n == "gpu" || n == "gpu temperature" ||
        n == "gpu temp" || n == "temperature";
    }

    private static bool IsGpu(DiagnosticHardware hardware) {
      return hardware.Type == HardwareType.GpuNvidia ||
        hardware.Type == HardwareType.GpuAti;
    }

    private static DiagnosticSeverity? GpuSeverity(float? value) {
      if (!value.HasValue)
        return null;
      if (value.Value >= DiagnosticThresholds.GpuCoreCritical)
        return DiagnosticSeverity.Critical;
      if (value.Value >= DiagnosticThresholds.GpuCoreWarning)
        return DiagnosticSeverity.Warning;
      return null;
    }

    private static void CheckGpuTemperatures(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      if (!IsGpu(hardware))
        return;

      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        if (sensor.Type != SensorType.Temperature ||
          !IsGpuCoreTemperature(sensor.Name))
          continue;

        DiagnosticSeverity? current = GpuSeverity(sensor.Value);
        DiagnosticSeverity? severity = Worst(current, GpuSeverity(sensor.Max));
        if (!severity.HasValue)
          continue;

        float threshold = severity == DiagnosticSeverity.Critical
          ? DiagnosticThresholds.GpuCoreCritical
          : DiagnosticThresholds.GpuCoreWarning;
        string explanation =
          "The GPU core reached " + Plain(threshold) + " °C or more (warning from " +
          Plain(DiagnosticThresholds.GpuCoreWarning) + " °C, critical from " +
          Plain(DiagnosticThresholds.GpuCoreCritical) + " °C). " +
          (current.HasValue
            ? "It is this hot right now. "
            : "Only the peak since the application started was this hot; it is " +
              "cooler now, so this happens under load. ") +
          "Most GPUs start reducing performance around their thermal limit " +
          "(typically 83-95 °C). Check the GPU fan curve and fan speed at the " +
          "same time, dust in the heatsink, case airflow, and any overclock or " +
          "raised power limit.";

        findings.Add(Finding(severity.Value, DiagnosticRules.GpuTemperatureHigh,
          "GPU core temperature high", explanation, hardware,
          new[] { Evidence(sensor, threshold, null) }));
      }
    }

    // ---- storage --------------------------------------------------------------

    private static void CheckStorage(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      if (hardware.Type != HardwareType.HDD)
        return;

      // Temperature, grouped per drive.
      List<DiagnosticEvidence> hot = new List<DiagnosticEvidence>();
      bool hotNow = false;
      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        if (sensor.Type != SensorType.Temperature)
          continue;
        bool current = AtLeast(sensor.Value,
          DiagnosticThresholds.StorageTemperatureWarning);
        if (current || AtLeast(sensor.Max,
          DiagnosticThresholds.StorageTemperatureWarning)) {
          hotNow |= current;
          hot.Add(Evidence(sensor, DiagnosticThresholds.StorageTemperatureWarning,
            current ? null : "peak since app start"));
        }
      }
      if (hot.Count > 0) {
        findings.Add(Finding(DiagnosticSeverity.Warning,
          DiagnosticRules.StorageTemperatureHigh, "Storage drive running hot",
          "A drive temperature reached " +
          Plain(DiagnosticThresholds.StorageTemperatureWarning) + " °C or more" +
          (hotNow ? " and is still that hot. " : " at some point since the application started. ") +
          "NVMe SSDs throttle to protect themselves at around 70-80 °C, which " +
          "shows up as slow transfers or stutter during large writes. Hard disks " +
          "prefer to stay below about 50 °C. Consider a heatsink on the M.2 " +
          "drive, better airflow over it, or moving it away from the GPU exhaust. " +
          "Secondary NVMe sensors often measure the controller, which normally runs " +
          "hotter than the flash.",
          hardware, hot));
      }

      DiagnosticSensor? spare = null;
      DiagnosticSensor? spareThreshold = null;
      DiagnosticSensor? unsafeShutdowns = null;
      DiagnosticSensor? powerCycles = null;

      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        string n = DiagnosticFormat.Normalize(sensor.Name);

        if (n.Contains("spare")) {
          if (n.Contains("threshold"))
            spareThreshold ??= sensor;
          else if (n.Contains("available"))
            spare ??= sensor;
          continue;
        }
        if (n.Contains("unsafe shutdown")) {
          unsafeShutdowns ??= sensor;
          continue;
        }
        if (n.Contains("power cycle")) {
          powerCycles ??= sensor;
          continue;
        }

        CheckEndurance(hardware, sensor, n, findings);
        CheckMediaErrors(hardware, sensor, n, findings);
        CheckDriveSpace(hardware, sensor, n, findings);
      }

      CheckSpare(hardware, spare, spareThreshold, findings);
      CheckUnsafeShutdowns(hardware, unsafeShutdowns, powerCycles, findings);
    }

    private static void CheckEndurance(DiagnosticHardware hardware,
      DiagnosticSensor sensor, string n, List<DiagnosticFinding> findings) {
      if (!sensor.Value.HasValue)
        return;

      float used;
      string note;
      if (n.Contains("percentage used") || n.Contains("percent used")) {
        used = sensor.Value.Value;
        note = "percentage used";
      } else if (sensor.Type == SensorType.Level && (n.Contains("remaining life") ||
        n.Contains("life remaining") || n.Contains("endurance remaining") ||
        n.Contains("life left"))) {
        used = 100 - sensor.Value.Value;
        note = "percentage used = 100 - remaining life = " + Plain(used) + " %";
      } else {
        return;
      }

      if (used < DiagnosticThresholds.StorageEnduranceUsedWarning)
        return;

      DiagnosticSeverity severity =
        used >= DiagnosticThresholds.StorageEnduranceUsedCritical
          ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;
      findings.Add(Finding(severity, DiagnosticRules.StorageEnduranceUsed,
        "SSD rated endurance nearly or fully used",
        "The drive reports " + Plain(used) + " % of its rated write endurance used " +
        "(flagged from " + Plain(DiagnosticThresholds.StorageEnduranceUsedWarning) +
        " %). This is the manufacturer's wear estimate, not a failure prediction: " +
        "many drives keep working well past 100 %, but the risk of failure rises and " +
        "the warranty may no longer apply. Make sure backups are current and plan a " +
        "replacement.",
        hardware, new[] { Evidence(sensor, null, note) }));
    }

    private static void CheckSpare(DiagnosticHardware hardware,
      DiagnosticSensor? spare, DiagnosticSensor? threshold,
      List<DiagnosticFinding> findings) {
      if (spare == null || !spare.Value.HasValue)
        return;

      float value = spare.Value.Value;
      float? limit = threshold?.Value;
      bool belowThreshold = limit.HasValue && limit.Value > 0 && value < limit.Value;
      bool low = value < DiagnosticThresholds.StorageSpareWarning;
      if (!belowThreshold && !low)
        return;

      List<DiagnosticEvidence> evidence = new List<DiagnosticEvidence> {
        Evidence(spare, belowThreshold ? limit : DiagnosticThresholds.StorageSpareWarning,
          belowThreshold ? "below the drive's own threshold" : null)
      };
      if (threshold != null)
        evidence.Add(Evidence(threshold, null, "threshold reported by the drive"));

      findings.Add(Finding(
        belowThreshold ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning,
        DiagnosticRules.StorageSpareLow, "SSD spare capacity low",
        "Available spare is the reserve of flash blocks the SSD uses to replace worn " +
        "or failed ones. It normally stays at or near 100 %. " +
        (belowThreshold
          ? "It has dropped below the threshold set by the manufacturer, which the " +
            "drive itself treats as a critical health warning. "
          : "It is below " + Plain(DiagnosticThresholds.StorageSpareWarning) + " %. ") +
        "Back up the data now and plan to replace the drive.",
        hardware, evidence));
    }

    private static void CheckMediaErrors(DiagnosticHardware hardware,
      DiagnosticSensor sensor, string n, List<DiagnosticFinding> findings) {
      if (!(n.Contains("media error") || n.Contains("integrity error") ||
        n.Contains("uncorrectable error")))
        return;
      if (!sensor.Value.HasValue || sensor.Value.Value <= 0)
        return;

      findings.Add(Finding(DiagnosticSeverity.Warning,
        DiagnosticRules.StorageMediaErrors, "Drive reports media or data integrity errors",
        "The drive has recorded " + Plain(sensor.Value.Value) + " unrecovered media " +
        "or data integrity error(s) over its lifetime. Each one is a read or write " +
        "the drive could not complete correctly, which can cause corrupted files, " +
        "failed updates or crashes when affected data is used. A small, stable count " +
        "can be old, but a rising count means the drive is failing. Back up " +
        "important data, run the manufacturer's diagnostic tool, and watch whether " +
        "the number grows.",
        hardware, new[] { Evidence(sensor, 0, null) }));
    }

    private static void CheckUnsafeShutdowns(DiagnosticHardware hardware,
      DiagnosticSensor? unsafeShutdowns, DiagnosticSensor? powerCycles,
      List<DiagnosticFinding> findings) {
      if (unsafeShutdowns == null || !unsafeShutdowns.Value.HasValue)
        return;

      float count = unsafeShutdowns.Value.Value;
      if (count < DiagnosticThresholds.StorageUnsafeShutdownsInfo)
        return;

      float? cycles = powerCycles?.Value;
      if (cycles.HasValue && cycles.Value > 0 &&
        count / cycles.Value < DiagnosticThresholds.StorageUnsafeShutdownRatio)
        return;

      List<DiagnosticEvidence> evidence = new List<DiagnosticEvidence> {
        Evidence(unsafeShutdowns, DiagnosticThresholds.StorageUnsafeShutdownsInfo,
          cycles.HasValue && cycles.Value > 0
            ? Plain(100 * count / cycles.Value) + " % of power cycles"
            : null)
      };
      if (powerCycles != null)
        evidence.Add(Evidence(powerCycles, null, null));

      findings.Add(Finding(DiagnosticSeverity.Info,
        DiagnosticRules.StorageUnsafeShutdowns, "Many unsafe shutdowns recorded",
        "The drive lost power " + Plain(count) + " times without being told to " +
        "shut down first. This counter is lifetime and cumulative: forced power-offs " +
        "(holding the power button), power cuts, system freezes and some " +
        "sleep/hibernate or Fast Startup setups all add to it. On its own it does not " +
        "mean the drive is damaged, but a high count fits a history of hard crashes " +
        "or power problems and is worth mentioning when investigating instability.",
        hardware, evidence));
    }

    private static void CheckDriveSpace(DiagnosticHardware hardware,
      DiagnosticSensor sensor, string n, List<DiagnosticFinding> findings) {
      if (sensor.Type != SensorType.Load ||
        !(n.Contains("used space") || n.Contains("space used")))
        return;
      if (!AtLeast(sensor.Value, DiagnosticThresholds.DriveUsedSpaceWarning))
        return;

      findings.Add(Finding(DiagnosticSeverity.Warning, DiagnosticRules.DriveSpaceLow,
        "Drive almost full",
        "The volumes on this drive are " + Plain(sensor.Value!.Value) + " % full. " +
        "Windows needs free space for updates, the page file, hibernation and " +
        "temporary files; running out causes failed updates, application crashes " +
        "and slowdowns, and SSDs also get slower when nearly full. Free up space or " +
        "move data to another drive.",
        hardware, new[] { Evidence(sensor, DiagnosticThresholds.DriveUsedSpaceWarning, null) }));
    }

    // ---- fans -----------------------------------------------------------------

    private static void CheckFans(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      List<DiagnosticSensor> stopped = new List<DiagnosticSensor>();
      DiagnosticSensor? hottest = null;
      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        if (sensor.Type == SensorType.Fan && sensor.Value.HasValue &&
          sensor.Value.Value < DiagnosticThresholds.FanStoppedRpm)
          stopped.Add(sensor);
        else if (sensor.Type == SensorType.Temperature && sensor.Value.HasValue &&
          (hottest == null || sensor.Value.Value > hottest.Value!.Value))
          hottest = sensor;
      }
      if (stopped.Count == 0 || hottest == null)
        return;

      bool gpu = IsGpu(hardware);
      float temperature = hottest.Value!.Value;

      if (temperature <= DiagnosticThresholds.FanHotTemperature) {
        if (!gpu)
          return;
        List<DiagnosticEvidence> idle = stopped.Select(s => Evidence(s, null, null)).ToList();
        idle.Add(Evidence(hottest, DiagnosticThresholds.FanHotTemperature,
          "hottest temperature on this GPU"));
        findings.Add(Finding(DiagnosticSeverity.Info, DiagnosticRules.GpuFanZeroRpmIdle,
          "GPU fan stopped at low temperature (normal zero-RPM mode)",
          "The GPU fan reads 0 RPM while the GPU is at " + Plain(temperature) +
          " °C. Most modern graphics cards stop their fans below roughly " +
          "50-60 °C to run silently and spin them up under load. This is " +
          "expected behaviour and not a fault. It only becomes a concern if the fan " +
          "stays at 0 RPM once the GPU is above about " +
          Plain(DiagnosticThresholds.FanHotTemperature) + " °C.",
          hardware, idle));
        return;
      }

      // Hot. A GPU fan, or a motherboard fan that has spun since the app
      // started, is a real concern. A header that has never reported any speed
      // is more likely empty, so it is only mentioned.
      List<DiagnosticSensor> spun = stopped
        .Where(s => gpu || (s.Max.HasValue && s.Max.Value >= DiagnosticThresholds.FanStoppedRpm))
        .ToList();
      List<DiagnosticSensor> neverSpun = stopped.Except(spun).ToList();

      if (spun.Count > 0) {
        List<DiagnosticEvidence> evidence = spun.Select(s => Evidence(s, null,
          gpu ? null : "was spinning earlier (max since app start above 0)")).ToList();
        evidence.Add(Evidence(hottest, DiagnosticThresholds.FanHotTemperature,
          "hottest temperature on the same hardware"));
        findings.Add(Finding(DiagnosticSeverity.Warning, DiagnosticRules.FanStoppedWhileHot,
          "Fan stopped while hardware is hot",
          "A fan reads 0 RPM while the same device reports " + Plain(temperature) +
          " °C, above " + Plain(DiagnosticThresholds.FanHotTemperature) + " °C. " +
          (gpu
            ? "GPU zero-RPM modes normally spin the fans up well before this " +
              "temperature, so the fan may be stuck, disconnected, failed, or held at " +
              "0 % by a custom fan curve or tuning tool. On some laptops the GPU fan " +
              "is not reported and reads 0 regardless. "
            : "The fan was spinning earlier in this session, so it has stopped, " +
              "stalled, or been set to 0 % by a fan curve or BIOS setting. ") +
          "Check the fan physically and review fan control settings.",
          hardware, evidence));
      }

      if (neverSpun.Count > 0) {
        List<DiagnosticEvidence> evidence = neverSpun.Select(s => Evidence(s, null,
          "no speed reported since app start")).ToList();
        evidence.Add(Evidence(hottest, DiagnosticThresholds.FanHotTemperature,
          "hottest temperature on the same hardware"));
        findings.Add(Finding(DiagnosticSeverity.Info, DiagnosticRules.FanStoppedWhileHot,
          "Fan header at 0 RPM while hardware is warm",
          "A fan input reads 0 RPM and has not reported any speed since the " +
          "application started, while the same controller reports " +
          Plain(temperature) + " °C. Most often the header simply has nothing " +
          "connected, or the fan is plugged into a hub or another header. If a fan " +
          "that should be on this header (for example the CPU cooler fan) is " +
          "missing, check that it is connected and spinning.",
          hardware, evidence));
      }
    }

    // ---- voltages -------------------------------------------------------------

    private static readonly Regex Rail12 = new Regex(
      @"^\+?12(\.0)?V(IN|RAIL)?$|^VIN12$|^12VCC$",
      RegexOptions.CultureInvariant);
    private static readonly Regex Rail5 = new Regex(
      @"^\+?5(\.0)?V(IN|RAIL|SB|DUAL|CC)?$|^VCC5$",
      RegexOptions.CultureInvariant);
    private static readonly Regex Rail33 = new Regex(
      @"^\+?3\.3V(IN|RAIL|SB|CC)?$|^\+?3V3$|^\+?3VSB$|^VSB3$|^\+?3VCC$|^VCC3$|^AVCC3?$|^AVSB$",
      RegexOptions.CultureInvariant);

    /// <summary>Nominal voltage of a named ATX or chipset supply rail, or null.</summary>
    internal static float? GetNominalRailVoltage(string name) {
      string c = DiagnosticFormat.Compact(name);
      if (Rail12.IsMatch(c))
        return 12f;
      if (Rail5.IsMatch(c))
        return 5f;
      if (Rail33.IsMatch(c))
        return 3.3f;
      return null;
    }

    internal static bool IsCmosBattery(string name) {
      string c = DiagnosticFormat.Compact(name);
      return c.StartsWith("VBAT", StringComparison.Ordinal) ||
        c.Contains("BATTERY") || c == "BAT" || c == "CMOS";
    }

    private static void CheckVoltages(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      if (hardware.Type != HardwareType.SuperIO &&
        hardware.Type != HardwareType.Mainboard)
        return;

      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        if (sensor.Type != SensorType.Voltage || !sensor.Value.HasValue)
          continue;
        float value = sensor.Value.Value;

        float? nominal = GetNominalRailVoltage(sensor.Name);
        if (nominal.HasValue) {
          CheckRail(hardware, sensor, value, nominal.Value, findings);
          continue;
        }

        if (!IsCmosBattery(sensor.Name))
          continue;

        if (value < DiagnosticThresholds.CmosBatteryImplausibleBelow ||
          value > DiagnosticThresholds.CmosBatteryImplausibleAbove) {
          findings.Add(Finding(DiagnosticSeverity.Info,
            DiagnosticRules.VoltageImplausible, "Implausible CMOS battery reading",
            "The battery input reads " + Plain(value) + " V, outside anything a " +
            "3 V coin cell produces. The input is most likely not monitored on this " +
            "board or needs a different scaling, so the value should be ignored.",
            hardware, new[] { Evidence(sensor, null, "expected about 3.0 V") }));
        } else if (value < DiagnosticThresholds.CmosBatteryLow) {
          findings.Add(Finding(DiagnosticSeverity.Warning,
            DiagnosticRules.CmosBatteryLow, "CMOS battery low",
            "The motherboard coin cell (CR2032, nominally 3.0 V) reads " + Plain(value) +
            " V, below " + Plain(DiagnosticThresholds.CmosBatteryLow) + " V. A weak " +
            "battery causes lost BIOS settings and a wrong clock after the PC has " +
            "been unplugged, which can also break certificate checks and sign-ins. " +
            "Replacing it is cheap.",
            hardware, new[] { Evidence(sensor, DiagnosticThresholds.CmosBatteryLow, null) }));
        }
      }
    }

    private static void CheckRail(DiagnosticHardware hardware,
      DiagnosticSensor sensor, float value, float nominal,
      List<DiagnosticFinding> findings) {
      float low = nominal * (1 - DiagnosticThresholds.RailTolerance);
      float high = nominal * (1 + DiagnosticThresholds.RailTolerance);
      float implausibleLow = nominal * (1 - DiagnosticThresholds.RailImplausibleTolerance);
      float implausibleHigh = nominal * (1 + DiagnosticThresholds.RailImplausibleTolerance);
      string range = Plain(low) + "-" + Plain(high) + " V";
      string railName = Plain(nominal) + " V";

      if (value < implausibleLow || value > implausibleHigh) {
        findings.Add(Finding(DiagnosticSeverity.Info, DiagnosticRules.VoltageImplausible,
          "Implausible " + railName + " rail reading",
          "'" + sensor.Name + "' reads " + Plain(value) + " V for a nominal " + railName +
          " rail. A real deviation this large would stop the PC from running, so the " +
          "input is almost certainly unused or scaled differently on this board. " +
          "Ignore this value rather than suspecting the power supply.",
          hardware, new[] { Evidence(sensor, nominal, "nominal " + railName) }));
        return;
      }

      if (value < low || value > high) {
        findings.Add(Finding(DiagnosticSeverity.Warning, DiagnosticRules.VoltageOutOfRange,
          railName + " rail outside ±" +
          Plain(DiagnosticThresholds.RailTolerance * 100) + " %",
          "'" + sensor.Name + "' reads " + Plain(value) + " V; the ATX specification " +
          "allows " + range + ". Out-of-spec rails can cause random reboots, crashes " +
          "under load and USB or drive dropouts, and point to a failing or overloaded " +
          "power supply. Motherboard monitoring chips are only moderately accurate, so " +
          "confirm with the BIOS hardware monitor or a multimeter before replacing " +
          "parts.",
          hardware, new[] { Evidence(sensor, value < low ? low : high, "allowed " + range) }));
        return;
      }

      bool minOut = sensor.Min.HasValue && sensor.Min.Value < low &&
        sensor.Min.Value >= implausibleLow;
      bool maxOut = sensor.Max.HasValue && sensor.Max.Value > high &&
        sensor.Max.Value <= implausibleHigh;
      if (minOut || maxOut) {
        findings.Add(Finding(DiagnosticSeverity.Info, DiagnosticRules.VoltageExcursion,
          railName + " rail left its tolerance earlier",
          "'" + sensor.Name + "' is within " + range + " now, but its " +
          (minOut ? "minimum" : "maximum") + " since the application started was " +
          Plain(minOut ? sensor.Min!.Value : sensor.Max!.Value) + " V. A brief dip under " +
          "heavy load can be a sign of a weak power supply; a single odd sample can " +
          "also be a sensor glitch. Worth correlating with when crashes happen.",
          hardware, new[] { Evidence(sensor, minOut ? low : high, "allowed " + range) }));
      }
    }

    // ---- memory ---------------------------------------------------------------

    private static void CheckMemory(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      if (hardware.Type != HardwareType.RAM)
        return;

      foreach (DiagnosticSensor sensor in hardware.Sensors) {
        if (sensor.Type != SensorType.Load || !sensor.Value.HasValue)
          continue;
        string n = DiagnosticFormat.Normalize(sensor.Name);

        if (n.Contains("virtual") || n.Contains("commit")) {
          if (sensor.Value.Value < DiagnosticThresholds.VirtualMemoryLoadWarning)
            continue;
          findings.Add(Finding(DiagnosticSeverity.Warning,
            DiagnosticRules.VirtualMemoryLoadHigh, "Virtual memory (commit) nearly exhausted",
            "Virtual memory is " + Plain(sensor.Value.Value) + " % used. This is RAM " +
            "plus the page file. When it runs out, Windows shows low-memory warnings " +
            "and applications or games crash or fail to start. Look for a process " +
            "leaking memory, close heavy applications, or let Windows manage a larger " +
            "page file.",
            hardware, new[] { Evidence(sensor, DiagnosticThresholds.VirtualMemoryLoadWarning, null) }));
        } else if (n.Contains("memory") || n == "load" || n == "ram") {
          if (sensor.Value.Value < DiagnosticThresholds.MemoryLoadWarning)
            continue;
          findings.Add(Finding(DiagnosticSeverity.Warning,
            DiagnosticRules.MemoryLoadHigh, "Physical memory nearly full",
            "RAM is " + Plain(sensor.Value.Value) + " % used. Windows is paging to " +
            "disk, which causes stutter, slow application switching and long load " +
            "times. Check which processes use the most memory; if this is normal for " +
            "the workload, more RAM would help.",
            hardware, new[] { Evidence(sensor, DiagnosticThresholds.MemoryLoadWarning, null) }));
        }
      }
    }

    // ---- missing values, access, capture --------------------------------------

    private static void CheckMissingValues(DiagnosticHardware hardware,
      List<DiagnosticFinding> findings) {
      List<DiagnosticEvidence> missing = hardware.Sensors
        .Where(s => !s.Value.HasValue)
        .Select(s => Evidence(s, null, null))
        .ToList();
      if (missing.Count == 0)
        return;

      findings.Add(Finding(DiagnosticSeverity.Info, DiagnosticRules.SensorNoValue,
        missing.Count.ToString(CultureInfo.InvariantCulture) +
          (missing.Count == 1 ? " sensor reports" : " sensors report") + " no value",
        "These sensors exist but had no reading at export time. That is usually " +
        "harmless: a header with nothing connected, a feature the device or driver " +
        "does not expose, or a reading that is only refreshed periodically. It only " +
        "matters if a value you would expect, such as a CPU fan speed, is among them.",
        hardware, missing));
    }

    private static void CheckAccess(DiagnosticSnapshot snapshot,
      List<DiagnosticFinding> findings) {
      DiagnosticAccessInfo access = snapshot.Access;
      string state = "backend: " +
        (string.IsNullOrEmpty(access.BackendName) ? "none" : access.BackendName) +
        "; MSR: " + YesNo(access.SupportsModelSpecificRegisters) +
        "; I/O ports: " + YesNo(access.SupportsIoPort) +
        "; PCI config: " + YesNo(access.SupportsPciConfig);
      string reason = string.IsNullOrWhiteSpace(access.UnavailableReason)
        ? "" : " Reason given by the application: " + access.UnavailableReason.Trim();

      if (access.Tier == AccessTier.Base) {
        findings.Add(new DiagnosticFinding {
          Severity = DiagnosticSeverity.Info,
          RuleId = DiagnosticRules.AccessTierLimited,
          Title = "Low-level sensor access unavailable (Base tier)",
          Explanation =
            "Open Hardware Monitor is running without its low-level hardware access " +
            "driver, so processor core temperatures, CPU package power, and " +
            "motherboard (Super I/O) fan speeds, fan controls and voltages cannot be " +
            "read and are absent from this snapshot. Their absence is expected and is " +
            "not a hardware fault; do not conclude that fans or temperature sensors " +
            "are broken. GPU sensors, storage health, memory, and CPU load and clocks " +
            "are unaffected." + reason,
          Evidence = new[] { new DiagnosticEvidence { Note = "tier: Base; " + state } }
        });
      } else if (!string.IsNullOrWhiteSpace(access.UnavailableReason)) {
        findings.Add(new DiagnosticFinding {
          Severity = DiagnosticSeverity.Info,
          RuleId = DiagnosticRules.AccessTierLimited,
          Title = "Some low-level sensors unavailable",
          Explanation =
            "Low-level access is active (Deep tier) but not every capability is " +
            "available, so some sensors are missing from this snapshot. Their " +
            "absence is expected and is not a hardware fault." + reason,
          Evidence = new[] { new DiagnosticEvidence { Note = "tier: Deep; " + state } }
        });
      }
    }

    private static void CheckCaptureErrors(DiagnosticSnapshot snapshot,
      List<DiagnosticFinding> findings) {
      if (snapshot.CaptureErrors.Count == 0)
        return;
      findings.Add(new DiagnosticFinding {
        Severity = DiagnosticSeverity.Info,
        RuleId = DiagnosticRules.CaptureIncomplete,
        Title = "Parts of the snapshot could not be read",
        Explanation =
          "Reading some hardware or sensors failed while exporting, so those parts " +
          "are missing. This is a limitation of the export, not necessarily a " +
          "hardware problem.",
        Evidence = snapshot.CaptureErrors
          .Select(e => new DiagnosticEvidence { Note = e }).ToList()
      });
    }

    // ---- helpers --------------------------------------------------------------

    private static DiagnosticSeverity? Worst(DiagnosticSeverity? a,
      DiagnosticSeverity? b) {
      if (!a.HasValue)
        return b;
      if (!b.HasValue)
        return a;
      return a.Value >= b.Value ? a : b;
    }

    private static bool AtLeast(float? value, float threshold) {
      return value.HasValue && value.Value >= threshold;
    }

    private static string YesNo(bool value) {
      return value ? "yes" : "no";
    }

    private static string Plain(double value) {
      return DiagnosticFormat.Plain(value);
    }

    private static DiagnosticEvidence Evidence(DiagnosticSensor sensor,
      float? threshold, string? note) {
      return new DiagnosticEvidence {
        SensorIdentifier = sensor.Identifier,
        SensorName = sensor.Name,
        HardwareName = sensor.Hardware?.Name,
        Type = sensor.Type,
        Value = sensor.Value,
        Min = sensor.Min,
        Max = sensor.Max,
        Threshold = threshold,
        Note = note
      };
    }

    private static DiagnosticFinding Finding(DiagnosticSeverity severity,
      string ruleId, string title, string explanation,
      DiagnosticHardware hardware, IReadOnlyList<DiagnosticEvidence> evidence) {
      return new DiagnosticFinding {
        Severity = severity,
        RuleId = ruleId,
        Title = title,
        Explanation = explanation,
        HardwareName = hardware.Path,
        HardwareIdentifier = hardware.Identifier,
        Evidence = evidence
      };
    }
  }
}
