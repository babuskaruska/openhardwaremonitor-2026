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
  /// Drives fan controls from temperature curves.
  ///
  /// Open Hardware Monitor could already set a fan to a fixed percentage; this
  /// is the part that was missing - a fan that follows a temperature.
  ///
  /// Safety, in order of importance:
  ///  1. The commanded duty is clamped to the control's own minimum and maximum,
  ///     so a curve can never stop a fan the hardware says must spin.
  ///  2. If the source sensor stops reporting for <see cref="LostSourceTicks"/>
  ///     consecutive updates, control is handed back to the hardware rather than
  ///     holding whatever duty the last reading produced.
  ///  3. <see cref="Release"/> hands every fan this instance drove back to the
  ///     hardware. The application calls it on close and on process exit, so a
  ///     crash does not leave a fan pinned at a fixed speed.
  /// </summary>
  public sealed class FanCurveController {

    public const int LostSourceTicks = 5;

    private readonly IComputer computer;
    private readonly ISettings settings;
    private readonly object sync = new object();

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
        entries[controlSensor.Identifier.ToString()] = new Entry(curve);
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

    /// <summary>Call after the sensors have been updated.</summary>
    public void Update() {
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
          duty = Math.Max(control.MinSoftwareValue,
            Math.Min(control.MaxSoftwareValue, duty));

          // Only write when something changes; every write is a driver call.
          if (control.ControlMode != ControlMode.Software ||
            Math.Abs(control.SoftwareValue - duty) >= 1f)
            control.SetSoftware(duty);

          entry.DrivenControl = control;
        }
      }
    }

    /// <summary>
    /// Hands every fan this instance has driven back to the hardware. Safe to
    /// call more than once and from any thread.
    /// </summary>
    public void Release() {
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
