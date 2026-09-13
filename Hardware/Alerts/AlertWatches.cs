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
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.Hardware.Alerts {

  internal enum AlertCondition {
    /// <summary>No usable reading; neither confirms nor clears anything.</summary>
    Unknown,
    Clear,
    Warning,
    Critical
  }

  /// <summary>How far past its limit a reading must return before an active alert clears.</summary>
  internal static class AlertHysteresis {

    /// <summary>The margin fan curves use before stepping down, for the same reason.</summary>
    public const float Temperature = FanCurve.DefaultHysteresis;

    public const float Percent = 2f;

    /// <summary>Supply rails, as a fraction of nominal.</summary>
    public const float RailFraction = 0.01f;

    public const float CmosBattery = 0.05f;

    public static float For(SensorType type, float threshold) {
      float magnitude = Math.Abs(threshold);
      switch (type) {
        case SensorType.Temperature:
          return Temperature;
        case SensorType.Load:
        case SensorType.Level:
        case SensorType.Control:
          return Percent;
        case SensorType.Fan:
          return Math.Max(50f, magnitude * 0.05f);
        case SensorType.Voltage:
          return Math.Max(0.02f, magnitude * RailFraction);
        default:
          return magnitude * 0.02f;
      }
    }
  }

  /// <summary>Where one rule stands for one sensor or device.</summary>
  internal sealed class AlertState {
    public bool Active;
    public DiagnosticSeverity Severity;
    public bool Pending;
    public DateTime PendingSince;
    public bool Escalating;
    public DateTime EscalatingSince;
    public bool HasNotified;
    public DateTime LastNotified;

    /// <summary>For counters, which have no condition that clears.</summary>
    public DateTime ActiveUntil;

    /// <summary>
    /// Forgets the condition but not the last notification, so switching a
    /// rule off and on again cannot get around the cooldown.
    /// </summary>
    public void Reset() {
      Active = false;
      Pending = false;
      Escalating = false;
    }
  }

  /// <summary>Shared by every watch of a rule, so enabling is a field read per update.</summary>
  internal sealed class RuleSwitch {
    public RuleSwitch(bool enabled) {
      Enabled = enabled;
    }

    public bool Enabled;
  }

  internal sealed class AlertText {
    public AlertText(string title, string message, IHardware? hardware, ISensor? sensor) {
      Title = title;
      Message = message;
      HardwareName = hardware?.Name;
      SensorIdentifier = sensor?.Identifier.ToString();
    }

    public string Title { get; }
    public string Message { get; }
    public string? HardwareName { get; }
    public string? SensorIdentifier { get; }
  }

  /// <summary>
  /// One rule applied to one sensor or device. Watches are built when hardware
  /// changes; <see cref="Check"/> runs on every update and must not allocate.
  /// </summary>
  internal abstract class AlertWatch {

    protected AlertWatch(string ruleId, string key, TimeSpan duration,
      IHardware hardware) {
      RuleId = ruleId;
      Key = key;
      Duration = duration;
      Hardware = hardware;
    }

    public string RuleId { get; }

    /// <summary>Identifies the watch across rebuilds, so state survives hardware changes.</summary>
    public string Key { get; }

    public TimeSpan Duration { get; }

    public IHardware Hardware { get; }

    public AlertState State { get; set; } = new AlertState();

    public RuleSwitch Switch { get; set; } = null!;

    /// <param name="active">Whether the alert is active, which applies the
    /// clearing hysteresis.</param>
    public abstract AlertCondition Check(bool active);

    /// <summary>Title and message for the log, from the latest check.</summary>
    public abstract AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format);

    protected static bool TryRead(ISensor sensor, out float value) {
      float? reading = sensor.Value;
      value = reading.GetValueOrDefault();
      return reading.HasValue && float.IsFinite(value);
    }

    protected static string Reading(ISensor sensor,
      Func<float, SensorType, string> format) {
      return TryRead(sensor, out float value) ? format(value, sensor.SensorType) : "no value";
    }
  }

  /// <summary>
  /// The hottest of a device's temperature sensors against a warning and a
  /// critical limit. One watch per device, so a hot 20-core processor raises
  /// one alert rather than twenty.
  /// </summary>
  internal sealed class TemperatureWatch : AlertWatch {

    private readonly ISensor[] sensors;
    // Subtracted from each reading before comparing: TjMax for processors,
    // zero for absolute limits.
    private readonly float[] references;
    private readonly float warning;
    private readonly float critical;
    private int hottest;

    public TemperatureWatch(string ruleId, IHardware hardware, ISensor[] sensors,
      float[] references, float warning, float critical)
      : base(ruleId, ruleId + "|" + hardware.Identifier,
        AlertBuiltInRules.Duration(ruleId), hardware) {
      this.sensors = sensors;
      this.references = references;
      this.warning = warning;
      this.critical = critical;
    }

    public override AlertCondition Check(bool active) {
      int index = -1;
      float worst = float.NegativeInfinity;
      for (int i = 0; i < sensors.Length; i++) {
        if (TryRead(sensors[i], out float value) && value - references[i] > worst) {
          worst = value - references[i];
          index = i;
        }
      }
      if (index < 0)
        return AlertCondition.Unknown;
      hottest = index;
      if (worst >= critical)
        return AlertCondition.Critical;
      if (worst >= warning - (active ? AlertHysteresis.Temperature : 0))
        return AlertCondition.Warning;
      return AlertCondition.Clear;
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      ISensor sensor = sensors[hottest];
      bool clear = condition == AlertCondition.Clear;
      bool isCritical = condition == AlertCondition.Critical;
      string reading = Hardware.Name + ": " + sensor.Name + " is " + Reading(sensor, format);
      string title, message;
      switch (RuleId) {
        case DiagnosticRules.CpuTemperatureNearTjMax:
          title = clear ? "CPU temperature back to normal"
            : isCritical ? "CPU temperature at TjMax" : "CPU temperature near TjMax";
          message = clear ? reading + "." : reading + "; the processor throttles at " +
            format(references[hottest], SensorType.Temperature) + ".";
          break;
        case DiagnosticRules.GpuTemperatureHigh:
          title = clear ? "GPU temperature back to normal"
            : isCritical ? "GPU temperature very high" : "GPU temperature high";
          message = clear ? reading + "." : reading + " (limit " +
            format(isCritical ? critical : warning, SensorType.Temperature) + ").";
          break;
        default:
          title = clear ? "Drive temperature back to normal" : "Drive running hot";
          message = clear ? reading + "." : reading + " (limit " +
            format(warning, SensorType.Temperature) + ").";
          break;
      }
      return new AlertText(title, message, Hardware, sensor);
    }
  }

  /// <summary>
  /// A fan at 0 RPM while its device is hot. Motherboard headers only count
  /// once they have reported a speed, because an empty header reads 0 RPM
  /// forever; graphics cards always have their fans.
  /// </summary>
  internal sealed class FanStoppedWatch : AlertWatch {

    private readonly ISensor fan;
    private readonly ISensor[] temperatures;
    private readonly bool gpu;
    private int hottest;

    public FanStoppedWatch(IHardware hardware, ISensor fan, ISensor[] temperatures,
      bool gpu)
      : base(DiagnosticRules.FanStoppedWhileHot,
        DiagnosticRules.FanStoppedWhileHot + "|" + fan.Identifier,
        AlertBuiltInRules.Duration(DiagnosticRules.FanStoppedWhileHot), hardware) {
      this.fan = fan;
      this.temperatures = temperatures;
      this.gpu = gpu;
    }

    public override AlertCondition Check(bool active) {
      if (!TryRead(fan, out float rpm))
        return AlertCondition.Unknown;
      int index = -1;
      float max = float.NegativeInfinity;
      for (int i = 0; i < temperatures.Length; i++) {
        if (TryRead(temperatures[i], out float value) && value > max) {
          max = value;
          index = i;
        }
      }
      if (index < 0)
        return AlertCondition.Unknown;
      hottest = index;

      float? peak = fan.Max;
      bool stopped = rpm < DiagnosticThresholds.FanStoppedRpm &&
        (gpu || (peak.HasValue && peak.Value >= DiagnosticThresholds.FanStoppedRpm));
      float hot = DiagnosticThresholds.FanHotTemperature -
        (active ? AlertHysteresis.Temperature : 0);
      return stopped && max > hot ? AlertCondition.Warning : AlertCondition.Clear;
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      ISensor temperature = temperatures[hottest];
      string message = Hardware.Name + ": " + fan.Name + " reads " + Reading(fan, format);
      if (condition == AlertCondition.Clear)
        return new AlertText("Fan alert cleared", message + ".", Hardware, fan);
      return new AlertText("Fan stopped while hardware is hot",
        message + " while " + temperature.Name + " is " + Reading(temperature, format) + ".",
        Hardware, fan);
    }
  }

  /// <summary>
  /// A lifetime error counter, compared with the count last seen. That count
  /// is kept in the settings, so errors a drive already had do not alert again
  /// on every start. Only updated while the rule is on: switching it back on
  /// reports errors that appeared in the meantime.
  /// </summary>
  internal sealed class CounterWatch : AlertWatch {

    private const string SettingSuffix = "alertbaseline";

    private readonly ISensor sensor;
    private readonly ISettings settings;
    private readonly string settingKey;
    private float? baseline;

    public CounterWatch(IHardware hardware, ISensor sensor, ISettings settings)
      : base(DiagnosticRules.StorageMediaErrors,
        DiagnosticRules.StorageMediaErrors + "|" + sensor.Identifier,
        TimeSpan.Zero, hardware) {
      this.sensor = sensor;
      this.settings = settings;
      settingKey = BaselineKey(sensor);
      if (settings.Contains(settingKey) &&
        float.TryParse(settings.GetValue(settingKey, ""), NumberStyles.Float,
          CultureInfo.InvariantCulture, out float saved) && float.IsFinite(saved))
        baseline = saved;
    }

    public static string BaselineKey(ISensor sensor) {
      return new Identifier(sensor.Identifier, SettingSuffix).ToString();
    }

    public float Previous { get; private set; }

    public float Current { get; private set; }

    // Counters are polled, not checked against a limit.
    public override AlertCondition Check(bool active) {
      return AlertCondition.Unknown;
    }

    /// <summary>True when the count rose above the one last seen.</summary>
    public bool Poll() {
      if (!TryRead(sensor, out float count))
        return false;
      if (baseline.HasValue && count == baseline.Value)
        return false;
      // The first reading, or a lower count (another drive under the same
      // identifier), only becomes the new reference.
      bool increased = baseline.HasValue && count > baseline.Value;
      Previous = baseline ?? count;
      Current = count;
      baseline = count;
      settings.SetValue(settingKey, count.ToString("R", CultureInfo.InvariantCulture));
      return increased;
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      return new AlertText("New drive media errors",
        Hardware.Name + ": the drive now reports " + format(Current, sensor.SensorType) +
        " media errors, up from " + format(Previous, sensor.SensorType) +
        ". Back up important data.", Hardware, sensor);
    }
  }

  /// <summary>NVMe available spare, below the drive's own threshold or a fixed floor.</summary>
  internal sealed class SpareWatch : AlertWatch {

    private readonly ISensor spare;
    private readonly ISensor? threshold;

    public SpareWatch(IHardware hardware, ISensor spare, ISensor? threshold)
      : base(DiagnosticRules.StorageSpareLow,
        DiagnosticRules.StorageSpareLow + "|" + hardware.Identifier,
        AlertBuiltInRules.Duration(DiagnosticRules.StorageSpareLow), hardware) {
      this.spare = spare;
      this.threshold = threshold;
    }

    public override AlertCondition Check(bool active) {
      if (!TryRead(spare, out float value))
        return AlertCondition.Unknown;
      if (threshold != null && TryRead(threshold, out float limit) && limit > 0 &&
        value < limit)
        return AlertCondition.Critical;
      return value < DiagnosticThresholds.StorageSpareWarning +
        (active ? AlertHysteresis.Percent : 0)
        ? AlertCondition.Warning : AlertCondition.Clear;
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      string message = Hardware.Name + ": available spare is " + Reading(spare, format);
      if (threshold != null && TryRead(threshold, out float limit) && limit > 0)
        message += " (drive limit " + format(limit, threshold.SensorType) + ")";
      switch (condition) {
        case AlertCondition.Clear:
          return new AlertText("SSD spare capacity back to normal", message + ".",
            Hardware, spare);
        case AlertCondition.Critical:
          return new AlertText("SSD spare capacity below the drive's limit",
            message + ". Back up the data and plan a replacement.", Hardware, spare);
        default:
          return new AlertText("SSD spare capacity low",
            message + ". Back up the data and plan a replacement.", Hardware, spare);
      }
    }
  }

  /// <summary>A 3.3, 5 or 12 V supply rail outside the ATX tolerance.</summary>
  internal sealed class RailWatch : AlertWatch {

    private readonly ISensor sensor;
    private readonly float nominal;

    public RailWatch(IHardware hardware, ISensor sensor, float nominal)
      : base(DiagnosticRules.VoltageOutOfRange,
        DiagnosticRules.VoltageOutOfRange + "|" + sensor.Identifier,
        AlertBuiltInRules.Duration(DiagnosticRules.VoltageOutOfRange), hardware) {
      this.sensor = sensor;
      this.nominal = nominal;
    }

    public override AlertCondition Check(bool active) {
      if (!TryRead(sensor, out float value))
        return AlertCondition.Unknown;
      // Far outside the rail the input is unused or scaled for another board,
      // as the analyzer concludes; that is not a power problem.
      if (value < nominal * (1 - DiagnosticThresholds.RailImplausibleTolerance) ||
        value > nominal * (1 + DiagnosticThresholds.RailImplausibleTolerance))
        return AlertCondition.Unknown;
      float margin = active ? nominal * AlertHysteresis.RailFraction : 0;
      return value < Low + margin || value > High - margin
        ? AlertCondition.Warning : AlertCondition.Clear;
    }

    private float Low {
      get { return nominal * (1 - DiagnosticThresholds.RailTolerance); }
    }

    private float High {
      get { return nominal * (1 + DiagnosticThresholds.RailTolerance); }
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      string rail = DiagnosticFormat.Plain(nominal) + " V rail";
      string message = Hardware.Name + ": " + sensor.Name + " reads " + Reading(sensor, format);
      if (condition == AlertCondition.Clear)
        return new AlertText(rail + " back in range", message + ".", Hardware, sensor);
      return new AlertText(rail + " out of range", message + "; allowed " +
        format(Low, SensorType.Voltage) + " to " + format(High, SensorType.Voltage) + ".",
        Hardware, sensor);
    }
  }

  /// <summary>The motherboard coin cell.</summary>
  internal sealed class BatteryWatch : AlertWatch {

    private readonly ISensor sensor;

    public BatteryWatch(IHardware hardware, ISensor sensor)
      : base(DiagnosticRules.CmosBatteryLow,
        DiagnosticRules.CmosBatteryLow + "|" + sensor.Identifier,
        AlertBuiltInRules.Duration(DiagnosticRules.CmosBatteryLow), hardware) {
      this.sensor = sensor;
    }

    public override AlertCondition Check(bool active) {
      if (!TryRead(sensor, out float value))
        return AlertCondition.Unknown;
      // No coin cell produces this; the input is not monitored on the board.
      if (value < DiagnosticThresholds.CmosBatteryImplausibleBelow ||
        value > DiagnosticThresholds.CmosBatteryImplausibleAbove)
        return AlertCondition.Unknown;
      return value < DiagnosticThresholds.CmosBatteryLow +
        (active ? AlertHysteresis.CmosBattery : 0)
        ? AlertCondition.Warning : AlertCondition.Clear;
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      string message = Hardware.Name + ": " + sensor.Name + " reads " + Reading(sensor, format);
      if (condition == AlertCondition.Clear)
        return new AlertText("CMOS battery back to normal", message + ".", Hardware, sensor);
      return new AlertText("CMOS battery low", message + ", below " +
        format(DiagnosticThresholds.CmosBatteryLow, SensorType.Voltage) +
        ". The coin cell on the motherboard needs replacing soon.", Hardware, sensor);
    }
  }

  /// <summary>A <see cref="CustomAlertRule"/> bound to its sensor.</summary>
  internal sealed class CustomWatch : AlertWatch {

    private readonly ISensor sensor;
    private readonly CustomAlertRule rule;
    private readonly float hysteresis;

    public CustomWatch(CustomAlertRule rule, ISensor sensor)
      : base(AlertEngine.CustomRuleId, AlertEngine.CustomRuleId + "|" + rule.Id,
        TimeSpan.FromSeconds(rule.DurationSeconds), sensor.Hardware) {
      this.sensor = sensor;
      this.rule = rule;
      hysteresis = AlertHysteresis.For(sensor.SensorType, rule.Threshold);
    }

    public override AlertCondition Check(bool active) {
      if (!TryRead(sensor, out float value))
        return AlertCondition.Unknown;
      float margin = active ? hysteresis : 0;
      bool holds = rule.Direction == AlertDirection.Above
        ? value > rule.Threshold - margin
        : value < rule.Threshold + margin;
      return holds ? AlertCondition.Warning : AlertCondition.Clear;
    }

    public override AlertText Describe(AlertCondition condition,
      Func<float, SensorType, string> format) {
      string limit = (rule.Direction == AlertDirection.Above ? "above " : "below ") +
        format(rule.Threshold, sensor.SensorType);
      string message = Hardware.Name + ": " + sensor.Name + " is " + Reading(sensor, format);
      if (condition == AlertCondition.Clear)
        return new AlertText(sensor.Name + " back to normal", message + ".", Hardware, sensor);
      return new AlertText(sensor.Name + " " + limit, message + ", " + limit + ".",
        Hardware, sensor);
    }
  }

  /// <summary>
  /// Chooses the built-in watches for one device, matching sensors the way
  /// <see cref="DiagnosticAnalyzer"/> does so alerts and exported findings agree.
  /// </summary>
  internal static class AlertWatchFactory {

    public static void AddBuiltIn(IHardware hardware, ISettings settings,
      List<AlertWatch> watches) {
      ISensor[] sensors = hardware.Sensors;
      switch (hardware.HardwareType) {
        case HardwareType.CPU:
          AddCpu(hardware, sensors, watches);
          break;
        case HardwareType.GpuNvidia:
        case HardwareType.GpuAti:
          AddGpu(hardware, sensors, watches);
          break;
        case HardwareType.HDD:
          AddStorage(hardware, sensors, settings, watches);
          break;
        case HardwareType.Mainboard:
        case HardwareType.SuperIO:
          AddVoltages(hardware, sensors, watches);
          break;
      }
      AddFans(hardware, sensors, watches);
    }

    // Hidden-by-default sensors are inputs a board configuration marked as
    // unwired or wrongly scaled; a notification about one would be noise.
    private static bool IsTemperature(ISensor sensor) {
      return sensor.SensorType == SensorType.Temperature && !sensor.IsDefaultHidden;
    }

    private static void AddCpu(IHardware hardware, ISensor[] sensors,
      List<AlertWatch> watches) {
      List<ISensor> cores = new List<ISensor>();
      foreach (ISensor sensor in sensors)
        if (IsTemperature(sensor) && DiagnosticAnalyzer.IsCpuCoreOrPackage(sensor.Name))
          cores.Add(sensor);
      if (cores.Count == 0)
        return;
      float[] tjMax = new float[cores.Count];
      for (int i = 0; i < cores.Count; i++)
        tjMax[i] = DiagnosticAnalyzer.GetTjMax(cores[i]);
      watches.Add(new TemperatureWatch(DiagnosticRules.CpuTemperatureNearTjMax, hardware,
        cores.ToArray(), tjMax, -DiagnosticThresholds.CpuTjMaxWarningMargin,
        -DiagnosticThresholds.CpuTjMaxCriticalMargin));
    }

    private static void AddGpu(IHardware hardware, ISensor[] sensors,
      List<AlertWatch> watches) {
      ISensor[] cores = GpuCoreTemperatures(sensors, false);
      if (cores.Length == 0)
        return;
      watches.Add(new TemperatureWatch(DiagnosticRules.GpuTemperatureHigh, hardware,
        cores, new float[cores.Length], DiagnosticThresholds.GpuCoreWarning,
        DiagnosticThresholds.GpuCoreCritical));
    }

    private static ISensor[] GpuCoreTemperatures(ISensor[] sensors, bool fallBackToAll) {
      List<ISensor> cores = new List<ISensor>();
      List<ISensor> all = new List<ISensor>();
      foreach (ISensor sensor in sensors) {
        if (!IsTemperature(sensor))
          continue;
        all.Add(sensor);
        if (DiagnosticAnalyzer.IsGpuCoreTemperature(sensor.Name))
          cores.Add(sensor);
      }
      return (cores.Count > 0 || !fallBackToAll ? cores : all).ToArray();
    }

    private static void AddStorage(IHardware hardware, ISensor[] sensors,
      ISettings settings, List<AlertWatch> watches) {
      List<ISensor> temperatures = new List<ISensor>();
      ISensor? spare = null;
      ISensor? spareThreshold = null;
      foreach (ISensor sensor in sensors) {
        if (IsTemperature(sensor)) {
          temperatures.Add(sensor);
          continue;
        }
        string n = DiagnosticFormat.Normalize(sensor.Name);
        if (DiagnosticAnalyzer.IsSpareThreshold(n))
          spareThreshold ??= sensor;
        else if (DiagnosticAnalyzer.IsAvailableSpare(n))
          spare ??= sensor;
        else if (DiagnosticAnalyzer.IsMediaErrorCounter(n))
          watches.Add(new CounterWatch(hardware, sensor, settings));
      }
      if (temperatures.Count > 0)
        watches.Add(new TemperatureWatch(DiagnosticRules.StorageTemperatureHigh, hardware,
          temperatures.ToArray(), new float[temperatures.Count],
          DiagnosticThresholds.StorageTemperatureWarning, float.PositiveInfinity));
      if (spare != null)
        watches.Add(new SpareWatch(hardware, spare, spareThreshold));
    }

    private static void AddVoltages(IHardware hardware, ISensor[] sensors,
      List<AlertWatch> watches) {
      foreach (ISensor sensor in sensors) {
        if (sensor.SensorType != SensorType.Voltage || sensor.IsDefaultHidden)
          continue;
        float? nominal = DiagnosticAnalyzer.GetNominalRailVoltage(sensor.Name);
        if (nominal.HasValue)
          watches.Add(new RailWatch(hardware, sensor, nominal.Value));
        else if (DiagnosticAnalyzer.IsCmosBattery(sensor.Name))
          watches.Add(new BatteryWatch(hardware, sensor));
      }
    }

    private static void AddFans(IHardware hardware, ISensor[] sensors,
      List<AlertWatch> watches) {
      List<ISensor> fans = new List<ISensor>();
      foreach (ISensor sensor in sensors)
        if (sensor.SensorType == SensorType.Fan && !sensor.IsDefaultHidden)
          fans.Add(sensor);
      if (fans.Count == 0)
        return;

      bool gpu = DiagnosticAnalyzer.IsGpu(hardware.HardwareType);
      // On a graphics card the hot spot runs well above the core, and zero-RPM
      // cards keep their fans stopped until the core warms up; judging by the
      // hot spot would notify on every light load.
      ISensor[] temperatures = gpu
        ? GpuCoreTemperatures(sensors, true)
        : Array.FindAll(sensors, IsTemperature);
      if (temperatures.Length == 0)
        return;
      foreach (ISensor fan in fans)
        watches.Add(new FanStoppedWatch(hardware, fan, temperatures, gpu));
    }
  }
}
