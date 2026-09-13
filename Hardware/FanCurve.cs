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

  public readonly struct FanCurvePoint {

    public FanCurvePoint(float temperature, float duty) {
      Temperature = temperature;
      Duty = duty;
    }

    /// <summary>Temperature in degrees Celsius.</summary>
    public float Temperature { get; }

    /// <summary>Fan duty in percent.</summary>
    public float Duty { get; }
  }

  /// <summary>
  /// A piecewise-linear mapping from temperature to fan duty. Between two
  /// points the duty is interpolated; beyond the first or last point it is held
  /// at that point's duty.
  /// </summary>
  public sealed class FanCurve {

    public const float DefaultHysteresis = 3f;

    private readonly FanCurvePoint[] points;

    public FanCurve(string sourceSensorIdentifier,
      IEnumerable<FanCurvePoint> points, float hysteresis = DefaultHysteresis) {

      if (string.IsNullOrWhiteSpace(sourceSensorIdentifier))
        throw new ArgumentException("A source sensor is required.",
          nameof(sourceSensorIdentifier));
      if (points == null)
        throw new ArgumentNullException(nameof(points));
      if (!float.IsFinite(hysteresis) || hysteresis < 0)
        throw new ArgumentOutOfRangeException(nameof(hysteresis),
          "Hysteresis must be a non-negative number.");

      List<FanCurvePoint> sorted = new List<FanCurvePoint>();
      foreach (FanCurvePoint point in points) {
        if (!float.IsFinite(point.Temperature) || !float.IsFinite(point.Duty))
          throw new ArgumentException("Curve points must be finite numbers.",
            nameof(points));
        sorted.Add(new FanCurvePoint(point.Temperature,
          Math.Clamp(point.Duty, 0f, 100f)));
      }

      if (sorted.Count < 2)
        throw new ArgumentException("A curve needs at least two points.",
          nameof(points));

      sorted.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
      for (int i = 1; i < sorted.Count; i++) {
        if (sorted[i].Temperature == sorted[i - 1].Temperature)
          throw new ArgumentException(
            "Each temperature may appear only once.", nameof(points));
      }

      SourceSensorIdentifier = sourceSensorIdentifier.Trim();
      Hysteresis = hysteresis;
      this.points = sorted.ToArray();
    }

    /// <summary>Identifier of the temperature sensor that drives the curve.</summary>
    public string SourceSensorIdentifier { get; }

    /// <summary>Points in ascending temperature order.</summary>
    public IReadOnlyList<FanCurvePoint> Points {
      get { return points; }
    }

    /// <summary>
    /// How many degrees a temperature must fall below the one that set the
    /// current duty before the duty is lowered. See <see cref="FanCurveState"/>.
    /// </summary>
    public float Hysteresis { get; }

    public float Evaluate(float temperature) {
      // An unreadable temperature is treated as hot. The controller never
      // passes one, but a curve should fail towards cooling, not silence.
      if (float.IsNaN(temperature))
        return points[points.Length - 1].Duty;

      if (temperature <= points[0].Temperature)
        return points[0].Duty;

      for (int i = 1; i < points.Length; i++) {
        if (temperature <= points[i].Temperature) {
          FanCurvePoint low = points[i - 1];
          FanCurvePoint high = points[i];
          float fraction = (temperature - low.Temperature) /
            (high.Temperature - low.Temperature);
          return low.Duty + (high.Duty - low.Duty) * fraction;
        }
      }

      return points[points.Length - 1].Duty;
    }

    /// <summary>
    /// A settings string such as
    /// "source=/intelcpu/0/temperature/8;hysteresis=3;points=30:30,70:80".
    /// Always culture-invariant, so a settings file survives a locale change.
    /// </summary>
    public string Serialize() {
      return "source=" + SourceSensorIdentifier +
        ";hysteresis=" + Hysteresis.ToString("R", CultureInfo.InvariantCulture) +
        ";points=" + string.Join(",", points.Select(p =>
          p.Temperature.ToString("R", CultureInfo.InvariantCulture) + ":" +
          p.Duty.ToString("R", CultureInfo.InvariantCulture)));
    }

    public static bool TryParse(string? text, out FanCurve? curve) {
      curve = null;
      if (string.IsNullOrWhiteSpace(text))
        return false;

      string? source = null;
      string? pointList = null;
      float hysteresis = DefaultHysteresis;

      foreach (string part in text.Split(';')) {
        int separator = part.IndexOf('=');
        if (separator <= 0)
          return false;
        string key = part.Substring(0, separator).Trim();
        string value = part.Substring(separator + 1).Trim();
        switch (key) {
          case "source":
            source = value;
            break;
          case "points":
            pointList = value;
            break;
          case "hysteresis":
            if (!float.TryParse(value, NumberStyles.Float,
              CultureInfo.InvariantCulture, out hysteresis))
              return false;
            break;
        }
      }

      if (source == null || pointList == null)
        return false;

      List<FanCurvePoint> parsed = new List<FanCurvePoint>();
      foreach (string pair in pointList.Split(',')) {
        string[] values = pair.Split(':');
        if (values.Length != 2 ||
          !float.TryParse(values[0], NumberStyles.Float,
            CultureInfo.InvariantCulture, out float temperature) ||
          !float.TryParse(values[1], NumberStyles.Float,
            CultureInfo.InvariantCulture, out float duty))
          return false;
        parsed.Add(new FanCurvePoint(temperature, duty));
      }

      try {
        curve = new FanCurve(source, parsed, hysteresis);
        return true;
      } catch (ArgumentException) {
        return false;
      }
    }
  }

  /// <summary>
  /// Applies a curve with hysteresis. A rising temperature takes effect
  /// immediately; a falling one only lowers the duty once it has dropped
  /// <see cref="FanCurve.Hysteresis"/> degrees below the temperature that set
  /// the current duty. Without this, a temperature hovering around a curve point
  /// makes the fan audibly hunt up and down.
  /// </summary>
  public sealed class FanCurveState {

    private float governingTemperature;
    private float duty;
    private bool hasValue;

    public float Next(FanCurve curve, float temperature) {
      if (!hasValue || temperature >= governingTemperature ||
        temperature <= governingTemperature - curve.Hysteresis) {
        governingTemperature = temperature;
        duty = curve.Evaluate(temperature);
        hasValue = true;
      }
      return duty;
    }

    public void Reset() {
      hasValue = false;
    }
  }
}
