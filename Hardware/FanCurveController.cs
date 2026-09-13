/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// Drives fan controls from temperature curves, and owns fan detection and
  /// calibration while one runs.
  ///
  /// Open Hardware Monitor could already set a fan to a fixed percentage; this
  /// is the part that was missing - a fan that follows a temperature.
  ///
  /// Safety, in order of importance:
  ///  1. The commanded duty is clamped to the control's own minimum and maximum,
  ///     and a non-zero duty is never below the calibrated minimum that keeps
  ///     the fan turning (or the duty that starts it, when it may be stopped).
  ///  2. If the source sensor stops reporting for <see cref="LostSourceTicks"/>
  ///     consecutive updates, control is handed back to the hardware rather than
  ///     holding whatever duty the last reading produced.
  ///  3. <see cref="Release"/> stops a running detection and hands every fan
  ///     this instance drove back to the hardware. The application calls it on
  ///     close and on process exit, so a crash does not leave a fan pinned at a
  ///     fixed speed.
  ///
  /// Detection runs through <see cref="Update"/> rather than beside it, so it is
  /// advanced on the sensor thread under the hardware lock, curves keep their
  /// hands off the fan it is testing, and every exit path that releases curves
  /// also stops it.
  /// </summary>
  public sealed class FanCurveController {

    public const int LostSourceTicks = 5;

    private readonly IComputer computer;
    private readonly ISettings settings;
    private readonly object sync = new object();
    private FanTuningSession? tuning;

    // Keyed by the control sensor's identifier.
    private readonly Dictionary<string, Entry> entries =
      new Dictionary<string, Entry>(StringComparer.Ordinal);

    private sealed class Entry {
      public Entry(FanCurve curve) {
        Curve = curve;
      }

      public FanCurve Curve;
      public readonly FanCurveState State = new FanCurveState();
      public int MissingTicks;
      public bool FailedSafe;
      public IControl? DrivenControl;
    }

    public FanCurveController(IComputer computer, ISettings settings) {
      this.computer = computer ?? throw new ArgumentNullException(nameof(computer));
      this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
      Store = new FanTuningStore(settings);
    }

    /// <summary>Detected pairings and calibrations.</summary>
    public FanTuningStore Store { get; }

    /// <summary>The running or most recent detection, if any.</summary>
    public FanTuningSession? Tuning {
      get {
        lock (sync)
          return tuning;
      }
    }

    /// <summary>
    /// Starts advancing <paramref name="session"/> on every <see cref="Update"/>.
    /// Call under the hardware lock.
    /// </summary>
    public void StartTuning(FanTuningSession session) {
      if (session == null)
        throw new ArgumentNullException(nameof(session));
      lock (sync) {
        if (tuning != null && tuning.IsRunning)
          throw new InvalidOperationException("Fan detection is already running.");
        tuning = session;
      }
    }

    private static string SettingKey(ISensor controlSensor) {
      return new Identifier(controlSensor.Identifier, "curve").ToString();
    }

    public bool HasCurve(ISensor controlSensor) {
      return GetCurve(controlSensor) != null;
    }

    public FanCurve? GetCurve(ISensor controlSensor) {
      lock (sync) {
        if (entries.TryGetValue(controlSensor.Identifier.ToString(), out Entry? entry))
          return entry.Curve;
      }
      string key = SettingKey(controlSensor);
      if (settings.Contains(key) &&
        FanCurve.TryParse(settings.GetValue(key, ""), out FanCurve? curve))
        return curve;
      return null;
    }

    public void SetCurve(ISensor controlSensor, FanCurve curve) {
      if (curve == null)
        throw new ArgumentNullException(nameof(curve));
      lock (sync) {
        string id = controlSensor.Identifier.ToString();
        Entry replacement = new Entry(curve);
        // Editing a curve must not make Release forget the fan it drives.
        if (entries.TryGetValue(id, out Entry? existing))
          replacement.DrivenControl = existing.DrivenControl;
        entries[id] = replacement;
        settings.SetValue(SettingKey(controlSensor), curve.Serialize());
      }
    }

    /// <summary>
    /// Stops following a curve. Deliberately does not change the fan: callers
    /// removing a curve are about to choose Default or a manual level themselves.
    /// </summary>
    public void RemoveCurve(ISensor controlSensor) {
      lock (sync) {
        entries.Remove(controlSensor.Identifier.ToString());
        settings.Remove(SettingKey(controlSensor));
      }
    }

    /// <summary>The duty a fixed speed or curve may actually command.</summary>
    public float LimitDuty(ISensor controlSensor, float duty, bool running) {
      IControl? control = controlSensor.Control;
      if (control == null)
        return duty;
      return FanDutyLimits.Clamp(duty, control.MinSoftwareValue, control.MaxSoftwareValue,
        Store.GetCalibration(controlSensor.Identifier.ToString()), running);
    }

    /// <summary>Call after the sensors have been updated.</summary>
    public void Update() {
      FanTuningSession? session;
      lock (sync)
        session = tuning;
      // Tick catches its own exceptions and restores the fan.
      session?.Tick();

      Dictionary<string, ISensor> sensors =
        new Dictionary<string, ISensor>(StringComparer.Ordinal);
      List<ISensor> controlSensors = new List<ISensor>();
      computer.Accept(new SensorVisitor(sensor => {
        sensors[sensor.Identifier.ToString()] = sensor;
        if (sensor.Control != null)
          controlSensors.Add(sensor);
      }));

      lock (sync) {
        // Pick up curves saved in an earlier session as their fans appear.
        foreach (ISensor controlSensor in controlSensors) {
          string id = controlSensor.Identifier.ToString();
          if (entries.ContainsKey(id))
            continue;
          string key = SettingKey(controlSensor);
          if (settings.Contains(key) &&
            FanCurve.TryParse(settings.GetValue(key, ""), out FanCurve? saved) &&
            saved != null)
            entries[id] = new Entry(saved);
        }

        foreach (KeyValuePair<string, Entry> pair in entries) {
          if (!sensors.TryGetValue(pair.Key, out ISensor? controlSensor))
            continue;
          IControl? control = controlSensor.Control;
          if (control == null)
            continue;

          Entry entry = pair.Value;
          if (session != null && session.IsBusy(pair.Key)) {
            // Detection owns this fan for now; start afresh when it hands back.
            entry.State.Reset();
            entry.MissingTicks = 0;
            continue;
          }

          float? temperature = null;
          if (sensors.TryGetValue(entry.Curve.SourceSensorIdentifier,
            out ISensor? source))
            temperature = source.Value;

          if (!temperature.HasValue || !float.IsFinite(temperature.Value)) {
            entry.MissingTicks++;
            if (entry.MissingTicks >= LostSourceTicks && !entry.FailedSafe) {
              control.SetDefault();
              entry.FailedSafe = true;
              entry.DrivenControl = null;
              entry.State.Reset();
            }
            continue;
          }

          entry.MissingTicks = 0;
          entry.FailedSafe = false;

          float duty = entry.State.Next(entry.Curve, temperature.Value);
          FanCalibration? calibration = Store.GetCalibration(pair.Key);
          // Turning under this curve since the last update: the running minimum
          // is enough. Otherwise the fan may be stopped and needs its start duty.
          bool running = entry.DrivenControl != null &&
            control.ControlMode == ControlMode.Software && calibration != null &&
            control.SoftwareValue > 0 && control.SoftwareValue >= calibration.MinRunningDuty;
          duty = FanDutyLimits.Clamp(duty, control.MinSoftwareValue,
            control.MaxSoftwareValue, calibration, running);

          // Only write when something changes; every write is a driver call.
          if (control.ControlMode != ControlMode.Software ||
            Math.Abs(control.SoftwareValue - duty) >= 1f)
            control.SetSoftware(duty);

          entry.DrivenControl = control;
        }
      }
    }

    /// <summary>
    /// Stops a running detection and hands every fan this instance has driven
    /// back to the hardware. Safe to call more than once and from any thread.
    /// </summary>
    public void Release() {
      FanTuningSession? session;
      lock (sync)
        session = tuning;
      try {
        session?.Release();
      } catch (Exception) {
        // Best effort during shutdown; the curves below must still be released.
      }

      lock (sync) {
        foreach (Entry entry in entries.Values) {
          IControl? control = entry.DrivenControl;
          entry.DrivenControl = null;
          entry.State.Reset();
          if (control == null)
            continue;
          try {
            control.SetDefault();
          } catch (Exception) {
            // Best effort during shutdown; one failure must not stop the rest.
          }
        }
      }
    }
  }
}
