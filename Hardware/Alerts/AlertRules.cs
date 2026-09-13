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

  public enum AlertKind {
    /// <summary>A condition held for its rule's duration, or a counter went up.</summary>
    Raised,
    /// <summary>An active alert became more severe.</summary>
    Escalated,
    /// <summary>An active condition cleared.</summary>
    Recovered
  }

  /// <summary>One entry in the recent-alert log.</summary>
  public sealed class AlertRecord {

    internal AlertRecord() { }

    /// <summary>Local time, including the UTC offset.</summary>
    public DateTimeOffset Time { get; internal set; }

    public AlertKind Kind { get; internal set; }

    /// <summary>Warning or critical; info for a recovery.</summary>
    public DiagnosticSeverity Severity { get; internal set; }

    /// <summary>
    /// A <see cref="DiagnosticRules"/> identifier, or
    /// <see cref="AlertEngine.CustomRuleId"/> for user-defined rules.
    /// </summary>
    public string RuleId { get; internal set; } = "";

    public string Title { get; internal set; } = "";

    public string Message { get; internal set; } = "";

    public string? HardwareName { get; internal set; }

    public string? SensorIdentifier { get; internal set; }

    /// <summary>
    /// Whether this entry deserves a notification: raised or escalated, and
    /// outside the cooldown of an earlier notification for the same rule and
    /// sensor. Repeats inside the cooldown are still logged.
    /// </summary>
    public bool Notify { get; internal set; }
  }

  /// <summary>A built-in rule, as offered in settings.</summary>
  public sealed class AlertRuleInfo {

    internal AlertRuleInfo(string id, string title, TimeSpan duration) {
      Id = id;
      Title = title;
      Duration = duration;
    }

    /// <summary>The matching <see cref="DiagnosticRules"/> identifier.</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>How long the condition must hold before the alert fires.</summary>
    public TimeSpan Duration { get; }
  }

  /// <summary>
  /// The built-in rules. They reuse the analyzer's rule identifiers and
  /// thresholds; the durations are what alerts add, since a single reading
  /// over a limit (a load spike, one noisy sample) is not worth a notification.
  /// </summary>
  internal static class AlertBuiltInRules {

    public static readonly IReadOnlyList<AlertRuleInfo> All = Array.AsReadOnly(new[] {
      new AlertRuleInfo(DiagnosticRules.CpuTemperatureNearTjMax,
        "Processor near its temperature limit", TimeSpan.FromSeconds(30)),
      new AlertRuleInfo(DiagnosticRules.GpuTemperatureHigh,
        "Graphics card hot", TimeSpan.FromSeconds(30)),
      new AlertRuleInfo(DiagnosticRules.StorageTemperatureHigh,
        "Drive hot", TimeSpan.FromSeconds(60)),
      new AlertRuleInfo(DiagnosticRules.FanStoppedWhileHot,
        "Fan stopped while hot", TimeSpan.FromSeconds(30)),
      new AlertRuleInfo(DiagnosticRules.StorageMediaErrors,
        "New drive media errors", TimeSpan.Zero),
      new AlertRuleInfo(DiagnosticRules.StorageSpareLow,
        "SSD spare capacity low", TimeSpan.Zero),
      new AlertRuleInfo(DiagnosticRules.VoltageOutOfRange,
        "Supply voltage out of range", TimeSpan.FromSeconds(10)),
      new AlertRuleInfo(DiagnosticRules.CmosBatteryLow,
        "CMOS battery low", TimeSpan.FromSeconds(60))
    });

    public static TimeSpan Duration(string ruleId) {
      foreach (AlertRuleInfo rule in All)
        if (rule.Id == ruleId)
          return rule.Duration;
      return TimeSpan.Zero;
    }
  }

  public enum AlertDirection {
    Above,
    Below
  }

  /// <summary>
  /// A user-defined alert: one sensor above or below a value for a while. The
  /// threshold is in the sensor's canonical unit (°C for temperatures), so a
  /// rule survives switching the display to °F.
  /// </summary>
  public sealed class CustomAlertRule {

    public const int MaxDurationSeconds = 24 * 60 * 60;

    public CustomAlertRule(string sensorIdentifier, AlertDirection direction,
      float threshold, int durationSeconds)
      : this(Guid.NewGuid().ToString("N").Substring(0, 12), sensorIdentifier,
        direction, threshold, durationSeconds) { }

    internal CustomAlertRule(string id, string sensorIdentifier,
      AlertDirection direction, float threshold, int durationSeconds) {
      if (string.IsNullOrEmpty(id) || !IsLettersAndDigits(id))
        throw new ArgumentException("The id must be letters and digits.", nameof(id));
      // ';' separates fields in the settings string.
      if (string.IsNullOrWhiteSpace(sensorIdentifier) || sensorIdentifier.IndexOf(';') >= 0)
        throw new ArgumentException("A sensor identifier is required.",
          nameof(sensorIdentifier));
      if (direction != AlertDirection.Above && direction != AlertDirection.Below)
        throw new ArgumentOutOfRangeException(nameof(direction));
      if (!float.IsFinite(threshold))
        throw new ArgumentOutOfRangeException(nameof(threshold));
      if (durationSeconds < 0 || durationSeconds > MaxDurationSeconds)
        throw new ArgumentOutOfRangeException(nameof(durationSeconds));

      Id = id;
      SensorIdentifier = sensorIdentifier;
      Direction = direction;
      Threshold = threshold;
      DurationSeconds = durationSeconds;
    }

    public string Id { get; }

    public string SensorIdentifier { get; }

    public AlertDirection Direction { get; }

    public float Threshold { get; }

    public int DurationSeconds { get; }

    private static bool IsLettersAndDigits(string text) {
      foreach (char ch in text)
        if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')))
          return false;
      return true;
    }

    /// <summary>
    /// A settings string such as
    /// "id=3f2a9c1b7d40;sensor=/intelcpu/0/temperature/8;direction=above;threshold=85;duration=30".
    /// Always culture-invariant, so a settings file survives a locale change.
    /// </summary>
    public string Serialize() {
      return "id=" + Id +
        ";sensor=" + SensorIdentifier +
        ";direction=" + (Direction == AlertDirection.Above ? "above" : "below") +
        ";threshold=" + Threshold.ToString("R", CultureInfo.InvariantCulture) +
        ";duration=" + DurationSeconds.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? text, out CustomAlertRule? rule) {
      rule = null;
      if (string.IsNullOrWhiteSpace(text))
        return false;

      string? id = null, sensor = null, direction = null;
      float threshold = float.NaN;
      int duration = -1;

      foreach (string part in text.Split(';')) {
        int separator = part.IndexOf('=');
        if (separator <= 0)
          return false;
        string key = part.Substring(0, separator).Trim();
        string value = part.Substring(separator + 1).Trim();
        switch (key) {
          case "id":
            id = value;
            break;
          case "sensor":
            sensor = value;
            break;
          case "direction":
            direction = value;
            break;
          case "threshold":
            if (!float.TryParse(value, NumberStyles.Float,
              CultureInfo.InvariantCulture, out threshold))
              return false;
            break;
          case "duration":
            if (!int.TryParse(value, NumberStyles.Integer,
              CultureInfo.InvariantCulture, out duration))
              return false;
            break;
        }
      }

      if (id == null || sensor == null ||
        (direction != "above" && direction != "below"))
        return false;

      try {
        rule = new CustomAlertRule(id, sensor,
          direction == "above" ? AlertDirection.Above : AlertDirection.Below,
          threshold, duration);
        return true;
      } catch (ArgumentException) {
        return false;
      }
    }
  }
}
