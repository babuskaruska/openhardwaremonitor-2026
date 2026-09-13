/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  /// <summary>Culture-invariant formatting shared by the analyzer and writers.</summary>
  internal static class DiagnosticFormat {

    public const string NoValue = "—";   // em dash

    public static string Unit(SensorType type) {
      switch (type) {
        case SensorType.Voltage: return "V";
        case SensorType.Clock: return "MHz";
        case SensorType.Temperature: return "°C";
        case SensorType.Load: return "%";
        case SensorType.Fan: return "RPM";
        case SensorType.Flow: return "L/h";
        case SensorType.Control: return "%";
        case SensorType.Level: return "%";
        case SensorType.Power: return "W";
        case SensorType.Data: return "GB";
        case SensorType.SmallData: return "MB";
        case SensorType.Throughput: return "MB/s";
        default: return "";
      }
    }

    public static string Number(double value, SensorType type) {
      string format;
      switch (type) {
        case SensorType.Voltage: format = "0.000"; break;
        case SensorType.Clock:
        case SensorType.Fan:
        case SensorType.SmallData: format = "0"; break;
        case SensorType.Data:
        case SensorType.Throughput: format = "0.00"; break;
        case SensorType.Factor:
          format = Math.Abs(value - Math.Round(value)) < 1e-6 ? "0" : "0.###";
          break;
        default: format = "0.0"; break;
      }
      return value.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string WithUnit(double? value, SensorType type) {
      if (!value.HasValue || !double.IsFinite(value.Value))
        return NoValue;
      string unit = Unit(type);
      string number = Number(value.Value, type);
      return unit.Length == 0 ? number : number + " " + unit;
    }

    public static string Plain(double value) {
      return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string Duration(TimeSpan span) {
      if (span < TimeSpan.Zero)
        span = TimeSpan.Zero;
      CultureInfo c = CultureInfo.InvariantCulture;
      if (span.TotalDays >= 1)
        return string.Format(c, "{0}d {1:00}h {2:00}m",
          (int)span.TotalDays, span.Hours, span.Minutes);
      if (span.TotalHours >= 1)
        return string.Format(c, "{0}h {1:00}m", span.Hours, span.Minutes);
      if (span.TotalMinutes >= 1)
        return string.Format(c, "{0}m {1:00}s", span.Minutes, span.Seconds);
      return string.Format(c, "{0}s", span.Seconds);
    }

    /// <summary>"2026-09-13 10:15:02 +02:00".</summary>
    public static string LocalTimestamp(DateTimeOffset time) {
      return time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>ISO 8601 with the local offset.</summary>
    public static string IsoLocal(DateTimeOffset time) {
      return time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz",
        CultureInfo.InvariantCulture);
    }

    /// <summary>ISO 8601 in UTC with a Z suffix.</summary>
    public static string IsoUtc(DateTimeOffset time) {
      return time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture);
    }

    public static string IsoUtc(DateTime utc) {
      return IsoUtc(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
    }

    /// <summary>Lower case with runs of whitespace collapsed, for name matching.</summary>
    public static string Normalize(string? name) {
      if (string.IsNullOrEmpty(name))
        return "";
      StringBuilder b = new StringBuilder(name.Length);
      bool space = false;
      foreach (char ch in name.Trim()) {
        if (char.IsWhiteSpace(ch) || ch == '_') {
          space = true;
          continue;
        }
        if (space && b.Length > 0)
          b.Append(' ');
        space = false;
        b.Append(char.ToLowerInvariant(ch));
      }
      return b.ToString();
    }

    /// <summary>Upper case without whitespace, underscores or hyphens: "+3.3 V" to "+3.3V".</summary>
    public static string Compact(string? name) {
      if (string.IsNullOrEmpty(name))
        return "";
      StringBuilder b = new StringBuilder(name.Length);
      foreach (char ch in name) {
        if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-')
          continue;
        b.Append(char.ToUpperInvariant(ch));
      }
      return b.ToString();
    }
  }
}
