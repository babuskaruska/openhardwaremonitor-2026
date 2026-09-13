/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// Detected pairings and calibrations, one settings key each per control
  /// sensor, next to the control's own mode, value and curve keys.
  /// </summary>
  public sealed class FanTuningStore {

    private readonly ISettings settings;

    public FanTuningStore(ISettings settings) {
      this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    private static string PairingKey(string controlIdentifier) {
      return controlIdentifier + "/fanpairing";
    }

    private static string CalibrationKey(string controlIdentifier) {
      return controlIdentifier + "/fancalibration";
    }

    public FanPairing? GetPairing(string controlIdentifier) {
      string key = PairingKey(controlIdentifier);
      return settings.Contains(key) &&
        FanPairing.TryParse(settings.GetValue(key, ""), out FanPairing? pairing)
        ? pairing : null;
    }

    public void SetPairing(string controlIdentifier, FanPairing pairing) {
      if (pairing == null)
        throw new ArgumentNullException(nameof(pairing));
      settings.SetValue(PairingKey(controlIdentifier), pairing.Serialize());
    }

    public FanCalibration? GetCalibration(string controlIdentifier) {
      string key = CalibrationKey(controlIdentifier);
      return settings.Contains(key) &&
        FanCalibration.TryParse(settings.GetValue(key, ""), out FanCalibration? calibration)
        ? calibration : null;
    }

    public void SetCalibration(string controlIdentifier, FanCalibration calibration) {
      if (calibration == null)
        throw new ArgumentNullException(nameof(calibration));
      settings.SetValue(CalibrationKey(controlIdentifier), calibration.Serialize());
    }

    public void Clear(string controlIdentifier) {
      settings.Remove(PairingKey(controlIdentifier));
      settings.Remove(CalibrationKey(controlIdentifier));
    }

    /// <summary>
    /// Keeps what a session learned. A pairing is kept whatever ended the
    /// session, since it was complete when found; a calibration only from a
    /// fan that finished normally. An uncertain pairing never replaces a
    /// confident one.
    /// </summary>
    public void Save(FanTuningResult result) {
      if (result == null)
        throw new ArgumentNullException(nameof(result));
      FanPairing? pairing = result.Pairing;
      if (pairing != null && pairing.Confidence != FanPairingConfidence.None) {
        FanPairing? existing = GetPairing(result.ControlIdentifier);
        if (existing == null || pairing.Confidence >= existing.Confidence ||
          existing.Confidence < FanPairingConfidence.Medium)
          SetPairing(result.ControlIdentifier, pairing);
      }
      if (result.Outcome == FanTuningOutcome.Completed && result.Calibration != null)
        SetCalibration(result.ControlIdentifier, result.Calibration);
    }
  }

  /// <summary>Fan detection's view of the real computer.</summary>
  public sealed class ComputerFanTuningHardware : IFanTuningHardware {

    private readonly IComputer computer;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public ComputerFanTuningHardware(IComputer computer) {
      this.computer = computer ?? throw new ArgumentNullException(nameof(computer));
    }

    public double NowSeconds {
      get { return clock.Elapsed.TotalSeconds; }
    }

    public IReadOnlyList<FanSpeedReading> ReadFanSpeeds() {
      List<FanSpeedReading> readings = new List<FanSpeedReading>();
      computer.Accept(new SensorVisitor(sensor => {
        if (sensor.SensorType == SensorType.Fan)
          readings.Add(new FanSpeedReading(sensor.Identifier.ToString(), sensor.Value));
      }));
      return readings;
    }

    public float? ReadHighestTemperature() {
      float? highest = null;
      computer.Accept(new SensorVisitor(sensor => {
        if (sensor.SensorType != SensorType.Temperature || !IsWatched(sensor.Hardware))
          return;
        float? value = sensor.Value;
        if (value.HasValue && float.IsFinite(value.Value) &&
          (!highest.HasValue || value.Value > highest.Value))
          highest = value.Value;
      }));
      return highest;
    }

    private static bool IsWatched(IHardware? hardware) {
      if (hardware == null)
        return false;
      switch (hardware.HardwareType) {
        case HardwareType.CPU:
        case HardwareType.GpuNvidia:
        case HardwareType.GpuAti:
          return true;
        default:
          return false;
      }
    }

    public float? ReadControlDuty(string controlIdentifier) {
      float? duty = null;
      computer.Accept(new SensorVisitor(sensor => {
        if (sensor.SensorType == SensorType.Control &&
          sensor.Identifier.ToString() == controlIdentifier)
          duty = sensor.Value;
      }));
      return duty;
    }
  }
}
