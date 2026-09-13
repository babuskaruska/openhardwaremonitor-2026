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

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// What fan detection and calibration read. The application implements it
  /// over the computer's sensors; tests implement it over simulated fans. Fans
  /// are driven through <see cref="IControl"/>, which is already an interface.
  /// </summary>
  public interface IFanTuningHardware {

    /// <summary>A monotonic clock in seconds.</summary>
    double NowSeconds { get; }

    /// <summary>Every fan speed sensor that currently exists, in RPM.</summary>
    IReadOnlyList<FanSpeedReading> ReadFanSpeeds();

    /// <summary>
    /// The hottest processor or graphics card temperature in °C, or null
    /// when none can be read.
    /// </summary>
    float? ReadHighestTemperature();

    /// <summary>The duty a control currently reports, or null when unknown.</summary>
    float? ReadControlDuty(string controlIdentifier);
  }

  public readonly struct FanSpeedReading {

    public FanSpeedReading(string sensorIdentifier, float? rpm) {
      SensorIdentifier = sensorIdentifier;
      Rpm = rpm;
    }

    public string SensorIdentifier { get; }

    public float? Rpm { get; }
  }

  /// <summary>One fan output to detect and, optionally, calibrate.</summary>
  public sealed class FanTuningTarget {

    public FanTuningTarget(string identifier, string name, IControl control,
      bool calibrate) {
      if (string.IsNullOrEmpty(identifier))
        throw new ArgumentException("An identifier is required.", nameof(identifier));
      Identifier = identifier;
      Name = name ?? identifier;
      Control = control ?? throw new ArgumentNullException(nameof(control));
      Calibrate = calibrate;
    }

    /// <summary>The control sensor's identifier; results are stored under it.</summary>
    public string Identifier { get; }

    public string Name { get; }

    public IControl Control { get; }

    /// <summary>
    /// False for graphics card fans: their driver enforces its own minimum
    /// and stopping them is its decision, so they are only paired.
    /// </summary>
    public bool Calibrate { get; }
  }

  /// <summary>
  /// Timing and thresholds. The defaults suit ordinary PWM and DC fans read
  /// once or twice a second.
  /// </summary>
  public sealed class FanTuningOptions {

    /// <summary>Duty change between calibration steps, in percent.</summary>
    public float StepPercent { get; set; } = 5;

    /// <summary>Minimum wait after a duty change before a reading counts.</summary>
    public double SettleSeconds { get; set; } = 4;

    /// <summary>Minimum sensor updates after a duty change before a reading counts.</summary>
    public int SettleTicks { get; set; } = 2;

    /// <summary>After this long a reading counts even if it is still drifting.</summary>
    public double MaxSettleSeconds { get; set; } = 10;

    public int BaselineSamples { get; set; } = 3;

    public int ResponseSamples { get; set; } = 3;

    /// <summary>A sensor must change at least this much to count as responding.</summary>
    public float MinResponseRpm { get; set; } = 150;

    /// <summary>...and at least this fraction of its baseline speed.</summary>
    public float MinResponseFraction { get; set; } = 0.15f;

    /// <summary>Below this a fan counts as stopped.</summary>
    public float StallRpm { get; set; } = 100;

    /// <summary>At or above this a fan counts as started.</summary>
    public float StartedRpm { get; set; } = 150;

    /// <summary>How long one start attempt waits for the fan to spin up.</summary>
    public double StartWaitSeconds { get; set; } = 3;

    /// <summary>The longest a fan may stay stopped before it is brought back to full speed.</summary>
    public double MaxStallSeconds { get; set; } = 6;

    /// <summary>A stopped fan that does not start at full speed within this long aborts everything.</summary>
    public double RecoverySeconds { get; set; } = 8;

    /// <summary>How long to wait for a fan to stop again between start attempts.</summary>
    public double StopWaitSeconds { get; set; } = 12;

    /// <summary>Any processor or graphics temperature above this aborts.</summary>
    public float TemperatureLimit { get; set; } = 80;

    /// <summary>A session does not begin when the system is already this warm.</summary>
    public float StartTemperatureLimit { get; set; } = 75;

    /// <summary>Consecutive updates without a temperature before aborting.</summary>
    public int MissingTemperatureTicks { get; set; } = 3;

    /// <summary>Consecutive updates without the paired speed before aborting.</summary>
    public int MissingSpeedTicks { get; set; } = 3;

    /// <summary>The most time one fan may take; exceeding it aborts.</summary>
    public double BudgetSecondsPerFan { get; set; } = 240;

    /// <summary>Where the search for the minimum speed starts.</summary>
    public float CalibrationStartDuty { get; set; } = 60;
  }

  public enum FanTuningStep {
    Waiting,
    MeasuringBaseline,
    Pairing,
    Preparing,
    SeekingStall,
    SeekingStart,
    Recovering,
    Stopping,
    Finished
  }

  public enum FanTuningOutcome {
    Running,
    Completed,
    Cancelled,
    Overheated,
    StallNotRecovered,
    TimedOut,
    Failed,
    ApplicationExit,
    /// <summary>Something else changed the fan; it was left as that set it.</summary>
    Interrupted
  }

  public enum FanPairingConfidence {
    None,
    Low,
    Medium,
    High
  }

  /// <summary>Which fan speed sensor belongs to a control output.</summary>
  public sealed class FanPairing {

    public FanPairing(string? speedSensorIdentifier, FanPairingConfidence confidence,
      float responseRpm) {
      SpeedSensorIdentifier = confidence == FanPairingConfidence.None
        ? null : speedSensorIdentifier;
      Confidence = SpeedSensorIdentifier == null ? FanPairingConfidence.None : confidence;
      ResponseRpm = responseRpm;
    }

    /// <summary>Null when no sensor responded.</summary>
    public string? SpeedSensorIdentifier { get; }

    public FanPairingConfidence Confidence { get; }

    /// <summary>How far the chosen sensor moved when the duty changed.</summary>
    public float ResponseRpm { get; }

    public string Serialize() {
      return "sensor=" + (SpeedSensorIdentifier ?? "") +
        ";confidence=" + Confidence.ToString() +
        ";response=" + ResponseRpm.ToString("R", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? text, out FanPairing? pairing) {
      pairing = null;
      if (!FanTuningText.TrySplit(text, out Dictionary<string, string> values) ||
        !values.TryGetValue("sensor", out string? sensor) ||
        !values.TryGetValue("confidence", out string? confidenceText) ||
        !Enum.TryParse(confidenceText, false, out FanPairingConfidence confidence) ||
        !Enum.IsDefined(typeof(FanPairingConfidence), confidence))
        return false;
      float response = 0;
      if (values.TryGetValue("response", out string? responseText) &&
        !float.TryParse(responseText, NumberStyles.Float, CultureInfo.InvariantCulture,
          out response))
        return false;
      pairing = new FanPairing(sensor.Length == 0 ? null : sensor, confidence, response);
      return true;
    }
  }

  /// <summary>How slowly one fan can run, and how fast it must be driven to start.</summary>
  public sealed class FanCalibration {

    /// <summary>Used when the start duty could not be measured.</summary>
    public const float FallbackStartMargin = 15;

    public FanCalibration(float minRunningDuty, float? minStartDuty, bool stops) {
      if (!float.IsFinite(minRunningDuty))
        throw new ArgumentOutOfRangeException(nameof(minRunningDuty));
      if (minStartDuty.HasValue && !float.IsFinite(minStartDuty.Value))
        throw new ArgumentOutOfRangeException(nameof(minStartDuty));
      MinRunningDuty = Math.Clamp(minRunningDuty, 0, 100);
      MinStartDuty = minStartDuty.HasValue
        ? Math.Clamp(Math.Max(minStartDuty.Value, MinRunningDuty), 0, 100) : (float?)null;
      Stops = stops;
    }

    /// <summary>The lowest duty at which the fan kept spinning.</summary>
    public float MinRunningDuty { get; }

    /// <summary>The lowest duty that started the fan from a stop, or null when not measured.</summary>
    public float? MinStartDuty { get; }

    /// <summary>False when the fan kept spinning at every duty the control allows.</summary>
    public bool Stops { get; }

    /// <summary>The duty that reliably starts the fan, measured or estimated.</summary>
    public float StartDuty {
      get {
        return MinStartDuty ?? Math.Min(100, MinRunningDuty + FallbackStartMargin);
      }
    }

    public string Serialize() {
      return "minRunning=" + MinRunningDuty.ToString("R", CultureInfo.InvariantCulture) +
        ";minStart=" + (MinStartDuty.HasValue
          ? MinStartDuty.Value.ToString("R", CultureInfo.InvariantCulture) : "") +
        ";stops=" + (Stops ? "true" : "false");
    }

    public static bool TryParse(string? text, out FanCalibration? calibration) {
      calibration = null;
      if (!FanTuningText.TrySplit(text, out Dictionary<string, string> values) ||
        !values.TryGetValue("minRunning", out string? runningText) ||
        !float.TryParse(runningText, NumberStyles.Float, CultureInfo.InvariantCulture,
          out float running) || !float.IsFinite(running))
        return false;
      float? start = null;
      if (values.TryGetValue("minStart", out string? startText) && startText.Length > 0) {
        if (!float.TryParse(startText, NumberStyles.Float, CultureInfo.InvariantCulture,
          out float parsed) || !float.IsFinite(parsed))
          return false;
        start = parsed;
      }
      bool stops = !values.TryGetValue("stops", out string? stopsText) ||
        !string.Equals(stopsText, "false", StringComparison.OrdinalIgnoreCase);
      calibration = new FanCalibration(running, start, stops);
      return true;
    }
  }

  /// <summary>What happened to one fan in a session.</summary>
  public sealed class FanTuningResult {

    public FanTuningResult(string controlIdentifier, string name, FanTuningOutcome outcome,
      FanPairing? pairing, FanCalibration? calibration, string message) {
      ControlIdentifier = controlIdentifier;
      Name = name;
      Outcome = outcome;
      Pairing = pairing;
      Calibration = calibration;
      Message = message;
    }

    public string ControlIdentifier { get; }
    public string Name { get; }
    public FanTuningOutcome Outcome { get; }
    public FanPairing? Pairing { get; }
    public FanCalibration? Calibration { get; }
    public string Message { get; }
  }

  /// <summary>An immutable snapshot for the interface, safe to read from any thread.</summary>
  public sealed class FanTuningProgress {

    public FanTuningProgress(bool isRunning, FanTuningStep step, int fanIndex, int fanCount,
      string? controlIdentifier, string? name, float? duty, float? rpm, string description,
      float fraction, FanTuningOutcome outcome) {
      IsRunning = isRunning;
      Step = step;
      FanIndex = fanIndex;
      FanCount = fanCount;
      ControlIdentifier = controlIdentifier;
      Name = name;
      Duty = duty;
      Rpm = rpm;
      Description = description;
      Fraction = fraction;
      Outcome = outcome;
    }

    public bool IsRunning { get; }
    public FanTuningStep Step { get; }
    /// <summary>Zero-based index of the fan being worked on.</summary>
    public int FanIndex { get; }
    public int FanCount { get; }
    public string? ControlIdentifier { get; }
    public string? Name { get; }
    /// <summary>The duty the session is commanding, or null before it touches the fan.</summary>
    public float? Duty { get; }
    /// <summary>The paired sensor's speed, once known.</summary>
    public float? Rpm { get; }
    public string Description { get; }
    /// <summary>Rough overall progress, 0..1.</summary>
    public float Fraction { get; }
    public FanTuningOutcome Outcome { get; }
  }

  /// <summary>
  /// The limits every software-commanded duty goes through: a non-zero duty
  /// is never below what keeps the fan turning, and never outside what the
  /// control allows.
  /// </summary>
  public static class FanDutyLimits {

    /// <param name="running">True when the fan is known to be turning under
    /// software control already. A turning fan only needs the running minimum;
    /// a stopped one, or one in an unknown state, needs the start duty.</param>
    public static float Clamp(float duty, float minimum, float maximum,
      FanCalibration? calibration, bool running) {
      if (maximum < minimum)
        maximum = minimum;
      // An unreadable duty fails towards cooling.
      if (!float.IsFinite(duty))
        return maximum;
      if (calibration != null && duty > 0) {
        float floor = running ? calibration.MinRunningDuty : calibration.StartDuty;
        if (duty < floor)
          duty = floor;
      }
      return Math.Clamp(duty, minimum, maximum);
    }
  }

  internal static class FanTuningText {

    /// <summary>Splits "key=value;key=value", the format settings strings use.</summary>
    public static bool TrySplit(string? text, out Dictionary<string, string> values) {
      values = new Dictionary<string, string>(StringComparer.Ordinal);
      if (string.IsNullOrWhiteSpace(text))
        return false;
      foreach (string part in text.Split(';')) {
        int separator = part.IndexOf('=');
        if (separator <= 0)
          return false;
        values[part.Substring(0, separator).Trim()] = part.Substring(separator + 1).Trim();
      }
      return true;
    }
  }
}
