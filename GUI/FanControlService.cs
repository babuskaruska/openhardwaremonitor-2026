/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Fan detection, calibration and profiles for the Fans page and the tray
  /// menu. The logic lives in the hardware library; this class only takes the
  /// hardware lock, picks sensible sensors and tells the interface when
  /// something changed.
  /// </summary>
  internal sealed class FanControlService {

    // A stopped fan is noticed within one sensor update, so detection reads
    // at least twice a second however slow the chosen interval is.
    private const int TuningIntervalMilliseconds = 500;

    private readonly Control owner;
    private readonly IComputer computer;
    private readonly FanCurveController curves;
    private readonly SensorPoller poller;
    private int restoreInterval;

    public FanControlService(Control owner, IComputer computer, ISettings settings,
      FanCurveController curves, SensorPoller poller) {
      this.owner = owner;
      this.computer = computer;
      this.curves = curves;
      this.poller = poller;
      Profiles = new FanProfileStore(settings);
    }

    /// <summary>Raised on the UI thread after a profile change or when detection ends.</summary>
    public event EventHandler? StateChanged;

    public FanProfileStore Profiles { get; }

    public FanTuningStore Store {
      get { return curves.Store; }
    }

    public static bool IsGraphicsCard(ISensor control) {
      HardwareType? type = control.Hardware?.HardwareType;
      return type == HardwareType.GpuNvidia || type == HardwareType.GpuAti;
    }

    /// <summary>Every fan output that can be driven. Call under the hardware lock or during a UI refresh.</summary>
    public List<ISensor> GetControls() {
      List<ISensor> controls = new List<ISensor>();
      computer.Accept(new SensorVisitor(sensor => {
        if (sensor.SensorType == SensorType.Control && sensor.Control != null)
          controls.Add(sensor);
      }));
      return controls;
    }

    public List<ISensor> GetTemperatureSensors() {
      List<ISensor> temperatures = new List<ISensor>();
      computer.Accept(new SensorVisitor(sensor => {
        if (sensor.SensorType == SensorType.Temperature)
          temperatures.Add(sensor);
      }));
      return temperatures;
    }

    public FanMode GetMode(ISensor control) {
      if (curves.HasCurve(control))
        return FanMode.Curve;
      return control.Control != null && control.Control.ControlMode == ControlMode.Software
        ? FanMode.Fixed : FanMode.Automatic;
    }

    /// <summary>
    /// The temperature a new curve follows: the graphics core for a graphics
    /// card fan, the processor package for everything else.
    /// </summary>
    public string? DefaultSource(ISensor control) {
      List<ISensor> temperatures = GetTemperatureSensors();
      ISensor? pick;
      if (IsGraphicsCard(control)) {
        pick = temperatures.FirstOrDefault(t => t.Hardware == control.Hardware && t.Name == "GPU Core") ??
          temperatures.FirstOrDefault(t => t.Hardware == control.Hardware);
      } else {
        List<ISensor> processor = temperatures.Where(
          t => t.Hardware?.HardwareType == HardwareType.CPU).ToList();
        pick = processor.FirstOrDefault(t => t.Name == "CPU Package") ?? processor.FirstOrDefault() ??
          temperatures.FirstOrDefault(t => t.Hardware == control.Hardware);
      }
      return (pick ?? temperatures.FirstOrDefault())?.Identifier.ToString();
    }

    public FanCurve? CreateDefaultCurve(ISensor control) {
      IControl? hardware = control.Control;
      string? source = DefaultSource(control);
      if (hardware == null || source == null)
        return null;
      return FanProfileDefaults.Create(FanProfileKind.Balanced, false, source,
        hardware.MinSoftwareValue, hardware.MaxSoftwareValue,
        Store.GetCalibration(control.Identifier.ToString())).Curve;
    }

    private FanProfileEntry DefaultEntry(FanProfileKind kind, ISensor control) {
      IControl hardware = control.Control;
      return FanProfileDefaults.Create(kind, IsGraphicsCard(control), DefaultSource(control),
        hardware.MinSoftwareValue, hardware.MaxSoftwareValue,
        Store.GetCalibration(control.Identifier.ToString()));
    }

    public void SetCurve(ISensor control, FanCurve curve) {
      poller.RunLocked(() => curves.SetCurve(control, curve));
    }

    // ---- detection ----------------------------------------------------------------

    public FanTuningState GetTuningState(ISensor control) {
      FanTuningSession? session = curves.Tuning;
      if (session == null || !session.IsRunning)
        return FanTuningState.Idle;
      string id = control.Identifier.ToString();
      if (session.Progress.ControlIdentifier == id)
        return FanTuningState.Active;
      if (session.Targets.Any(t => t.Identifier == id) &&
        !session.Results.Any(r => r.ControlIdentifier == id))
        return FanTuningState.Waiting;
      return FanTuningState.Idle;
    }

    /// <summary>What the most recent detection reported for this fan.</summary>
    public FanTuningResult? GetResult(ISensor control) {
      string id = control.Identifier.ToString();
      return curves.Tuning?.Results.LastOrDefault(r => r.ControlIdentifier == id);
    }

    public bool StartTuning(IEnumerable<ISensor> controls) {
      List<FanTuningTarget> targets = controls.Where(c => c.Control != null)
        .Select(c => new FanTuningTarget(c.Identifier.ToString(), FanCurveEditor.SensorName(c),
          c.Control, !IsGraphicsCard(c)))
        .ToList();
      if (targets.Count == 0)
        return false;

      FanTuningSession session = new FanTuningSession(
        new ComputerFanTuningHardware(computer), targets);
      session.FanFinished += Store.Save;
      session.Finished += OnTuningFinished;

      bool started = poller.RunLocked(() => {
        FanTuningSession? current = curves.Tuning;
        if (current != null && current.IsRunning)
          return false;
        // Under the lock, so the session cannot finish before this is noted.
        int interval = poller.IntervalMilliseconds;
        if (interval > TuningIntervalMilliseconds) {
          restoreInterval = interval;
          poller.IntervalMilliseconds = TuningIntervalMilliseconds;
        }
        curves.StartTuning(session);
        return true;
      });
      if (started)
        RaiseStateChanged();
      return started;
    }

    public void CancelTuning() {
      poller.RunLocked(() => curves.Tuning?.Cancel());
    }

    private void OnTuningFinished(FanTuningSession session) {
      int interval = Interlocked.Exchange(ref restoreInterval, 0);
      // Unless the user picked another interval meanwhile.
      if (interval > 0 && poller.IntervalMilliseconds == TuningIntervalMilliseconds)
        poller.IntervalMilliseconds = interval;
      RaiseStateChanged();
    }

    // ---- profiles -------------------------------------------------------------------

    /// <summary>
    /// Applies a profile to every fan: its saved settings, or defaults built
    /// from calibration. Stops a running detection first.
    /// </summary>
    public void ApplyProfile(FanProfileKind kind) {
      poller.RunLocked(() => {
        FanTuningSession? session = curves.Tuning;
        if (session != null && session.IsRunning)
          session.Cancel();
        foreach (ISensor control in GetControls()) {
          IControl hardware = control.Control;
          FanProfileEntry entry = Profiles.GetSaved(kind, control.Identifier.ToString()) ??
            DefaultEntry(kind, control);
          try {
            switch (entry.Mode) {
              case FanMode.Curve:
                curves.SetCurve(control, entry.Curve!);
                break;
              case FanMode.Fixed:
                curves.RemoveCurve(control);
                hardware.SetSoftware(curves.LimitDuty(control, entry.FixedDuty, false));
                break;
              default:
                curves.RemoveCurve(control);
                hardware.SetDefault();
                break;
            }
          } catch (Exception) {
            // One fan failing must not keep the others on the old profile.
          }
        }
      });
      Profiles.Active = kind;
      RaiseStateChanged();
    }

    /// <summary>Saves every fan's current mode, fixed duty and curve as a profile.</summary>
    public void SaveProfile(FanProfileKind kind) {
      poller.RunLocked(() => {
        foreach (ISensor control in GetControls()) {
          FanMode mode = GetMode(control);
          FanCurve? curve = mode == FanMode.Curve ? curves.GetCurve(control) : null;
          if (mode == FanMode.Curve && curve == null)
            mode = FanMode.Automatic;
          Profiles.Save(kind, control.Identifier.ToString(),
            new FanProfileEntry(mode, control.Control.SoftwareValue, curve));
        }
      });
      Profiles.Active = kind;
      RaiseStateChanged();
    }

    private void RaiseStateChanged() {
      // Always later, on the UI thread: callers may hold the hardware lock or
      // be in the middle of a click on a card that the refresh rebuilds.
      try {
        if (!owner.IsDisposed && owner.IsHandleCreated)
          owner.BeginInvoke((Action)(() => StateChanged?.Invoke(this, EventArgs.Empty)));
      } catch (InvalidOperationException) {
        // The window is closing.
      }
    }
  }
}
