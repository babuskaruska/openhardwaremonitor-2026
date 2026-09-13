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

namespace OpenHardwareMonitor.Hardware {

  public enum FanMode {
    Automatic,
    Fixed,
    Curve
  }

  public enum FanProfileKind {
    Silent,
    Balanced,
    Performance
  }

  /// <summary>One fan's settings within a profile.</summary>
  public sealed class FanProfileEntry {

    public FanProfileEntry(FanMode mode, float fixedDuty, FanCurve? curve) {
      if (mode == FanMode.Curve && curve == null)
        throw new ArgumentException("A curve entry needs a curve.", nameof(curve));
      Mode = mode;
      FixedDuty = float.IsFinite(fixedDuty) ? Math.Clamp(fixedDuty, 0, 100) : 100;
      Curve = curve;
    }

    public FanMode Mode { get; }

    public float FixedDuty { get; }

    public FanCurve? Curve { get; }

    /// <summary>"mode=Fixed;duty=40", or the mode and duty followed by the curve's own string.</summary>
    public string Serialize() {
      string text = "mode=" + Mode.ToString() +
        ";duty=" + FixedDuty.ToString("R", CultureInfo.InvariantCulture);
      if (Curve != null)
        text += ";" + Curve.Serialize();
      return text;
    }

    public static bool TryParse(string? text, out FanProfileEntry? entry) {
      entry = null;
      if (string.IsNullOrWhiteSpace(text))
        return false;
      FanMode? mode = null;
      float duty = 100;
      List<string> curveParts = new List<string>();
      foreach (string part in text.Split(';')) {
        int separator = part.IndexOf('=');
        if (separator <= 0)
          return false;
        string key = part.Substring(0, separator).Trim();
        string value = part.Substring(separator + 1).Trim();
        if (key == "mode") {
          if (!Enum.TryParse(value, false, out FanMode parsed) ||
            !Enum.IsDefined(typeof(FanMode), parsed))
            return false;
          mode = parsed;
        } else if (key == "duty") {
          if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duty))
            return false;
        } else {
          curveParts.Add(part);
        }
      }
      if (!mode.HasValue)
        return false;
      FanCurve? curve = null;
      if (curveParts.Count > 0 && !FanCurve.TryParse(string.Join(";", curveParts), out curve))
        return false;
      if (mode == FanMode.Curve && curve == null)
        return false;
      entry = new FanProfileEntry(mode.Value, duty, curve);
      return true;
    }
  }

  /// <summary>Saved profiles and which one is active.</summary>
  public sealed class FanProfileStore {

    private const string ActiveKey = "fanProfile.active";

    private readonly ISettings settings;

    public FanProfileStore(ISettings settings) {
      this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public static IReadOnlyList<FanProfileKind> All { get; } = new[] {
      FanProfileKind.Silent, FanProfileKind.Balanced, FanProfileKind.Performance
    };

    public static string DisplayName(FanProfileKind kind) {
      return kind.ToString();
    }

    private static string Key(FanProfileKind kind, string controlIdentifier) {
      return controlIdentifier + "/fanprofile/" + kind.ToString().ToLowerInvariant();
    }

    /// <summary>Null when no profile has been chosen.</summary>
    public FanProfileKind? Active {
      get {
        if (!settings.Contains(ActiveKey))
          return null;
        return Enum.TryParse(settings.GetValue(ActiveKey, ""), false, out FanProfileKind kind) &&
          Enum.IsDefined(typeof(FanProfileKind), kind) ? kind : null;
      }
      set {
        if (value.HasValue)
          settings.SetValue(ActiveKey, value.Value.ToString());
        else
          settings.Remove(ActiveKey);
      }
    }

    public FanProfileEntry? GetSaved(FanProfileKind kind, string controlIdentifier) {
      string key = Key(kind, controlIdentifier);
      return settings.Contains(key) &&
        FanProfileEntry.TryParse(settings.GetValue(key, ""), out FanProfileEntry? entry)
        ? entry : null;
    }

    public void Save(FanProfileKind kind, string controlIdentifier, FanProfileEntry entry) {
      if (entry == null)
        throw new ArgumentNullException(nameof(entry));
      settings.SetValue(Key(kind, controlIdentifier), entry.Serialize());
    }

    public void Forget(FanProfileKind kind, string controlIdentifier) {
      settings.Remove(Key(kind, controlIdentifier));
    }
  }

  /// <summary>
  /// Built-in profiles for fans the user has not saved one for. Curves start
  /// from what calibration measured - just above the lowest speed that keeps
  /// the fan turning - so Silent really is as quiet as that fan allows.
  /// </summary>
  public static class FanProfileDefaults {

    /// <summary>Floor for a fan that has not been calibrated.</summary>
    public const float UncalibratedFloor = 30;

    /// <param name="isGraphicsCard">Graphics card fans stay with the driver
    /// except in Performance: their own curves are usually already quiet.</param>
    /// <param name="temperatureSource">The sensor a curve follows; without one
    /// the fan stays on automatic control.</param>
    public static FanProfileEntry Create(FanProfileKind kind, bool isGraphicsCard,
      string? temperatureSource, float minimum, float maximum, FanCalibration? calibration) {

      maximum = Math.Clamp(maximum, 0, 100);
      minimum = Math.Clamp(minimum, 0, maximum);
      if (string.IsNullOrWhiteSpace(temperatureSource) ||
        (isGraphicsCard && kind != FanProfileKind.Performance) || maximum - minimum < 1)
        return new FanProfileEntry(FanMode.Automatic, maximum, null);

      float margin, hysteresis;
      float[] temperatures, shares;
      switch (kind) {
        case FanProfileKind.Silent:
          margin = 5;
          hysteresis = 4;
          temperatures = new float[] { 45, 62, 76, 88 };
          shares = new float[] { 0, 0.2f, 0.55f, 1 };
          break;
        case FanProfileKind.Performance:
          margin = 20;
          hysteresis = 2;
          temperatures = new float[] { 35, 50, 65, 75 };
          shares = new float[] { 0, 0.5f, 0.85f, 1 };
          break;
        default:
          margin = 10;
          hysteresis = 3;
          temperatures = new float[] { 40, 58, 72, 84 };
          shares = new float[] { 0, 0.35f, 0.7f, 1 };
          break;
      }

      float floor = calibration != null
        ? Math.Max(calibration.MinRunningDuty, 15) + margin
        : UncalibratedFloor + margin;
      floor = Math.Clamp(floor, minimum, maximum);

      FanCurvePoint[] points = temperatures.Select((t, i) =>
        new FanCurvePoint(t, (float)Math.Round(floor + (maximum - floor) * shares[i]))).ToArray();
      return new FanProfileEntry(FanMode.Curve, floor,
        new FanCurve(temperatureSource, points, hysteresis));
    }
  }
}
