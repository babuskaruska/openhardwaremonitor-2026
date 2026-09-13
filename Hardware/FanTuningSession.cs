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

  /// <summary>
  /// Finds which speed sensor belongs to each fan output (pairing) and how
  /// slowly each fan can run (calibration), one fan at a time.
  ///
  /// It is a state machine advanced by <see cref="Tick"/> after every sensor
  /// update, on the thread that owns the hardware and under its lock. It
  /// never sleeps and never starts a thread, so it cannot race the sensor
  /// update or outlive the application.
  ///
  /// Safety:
  ///  - Any processor or graphics temperature above the limit, a temperature
  ///    or fan speed that stops reporting, a stopped fan that does not start
  ///    again at full speed, an exception, an exceeded time budget,
  ///    <see cref="Cancel"/> and <see cref="Release"/> all abort at once and
  ///    put the fan being worked on back on automatic control.
  ///  - A fan is kept stopped for at most <see cref="FanTuningOptions.MaxStallSeconds"/>
  ///    (plus one sensor update) before it is driven to full speed.
  ///  - Stall seeking needs a pairing of at least medium confidence: watching
  ///    the wrong sensor would hide a stopped fan.
  ///  - A fan that finished normally gets its previous mode back: its previous
  ///    software duty, or automatic control.
  ///  - If anything else changes the fan meanwhile, the session stops touching
  ///    it and leaves the new setting alone.
  /// </summary>
  public sealed class FanTuningSession {

    private readonly IFanTuningHardware hardware;
    private readonly FanTuningTarget[] targets;
    private readonly FanTuningOptions options;
    private readonly List<FanTuningResult> results = new List<FanTuningResult>();
    private readonly object resultsLock = new object();

    private volatile FanTuningProgress progress;
    private volatile bool running = true;
    private bool started;
    private int index = -1;
    private FanTuningOutcome outcome = FanTuningOutcome.Running;
    private string message = "Waiting for the next sensor update";

    // The fan being worked on.
    private FanTuningTarget? target;
    private FanTuningStep step = FanTuningStep.Waiting;
    private double targetStart;
    private double phaseStart;
    private int phaseTicks;
    private bool touched;
    private ControlMode previousMode;
    private float previousValue;
    private float commanded;
    private double notBefore;
    private int samples;
    private readonly Dictionary<string, List<float>> baseline =
      new Dictionary<string, List<float>>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<float>> response =
      new Dictionary<string, List<float>>(StringComparer.Ordinal);
    private bool expectIncrease;
    private FanPairing? pairing;
    private FanCalibration? calibration;
    private float? lastRpm;
    private float? previousReading;
    private int missingSpeedTicks;
    private int missingTemperatureTicks;
    private float searchStart;
    private float lastRunning;
    private float stallDuty;
    private float candidate;
    private float nextCandidate;
    private double stallSince;
    private double? stoppedAt;
    private bool finishAfterRecovery;

    public FanTuningSession(IFanTuningHardware hardware, IEnumerable<FanTuningTarget> targets,
      FanTuningOptions? options = null) {
      this.hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
      if (targets == null)
        throw new ArgumentNullException(nameof(targets));
      this.targets = targets.ToArray();
      if (this.targets.Select(t => t.Identifier).Distinct(StringComparer.Ordinal).Count() !=
        this.targets.Length)
        throw new ArgumentException("Each fan may appear only once.", nameof(targets));
      this.options = options ?? new FanTuningOptions();
      if (this.options.StepPercent <= 0 ||
        this.options.MaxStallSeconds < this.options.StartWaitSeconds)
        throw new ArgumentException("The step must be positive, and a fan must be allowed " +
          "to stay stopped for at least one start attempt.", nameof(options));
      progress = BuildProgress();
    }

    /// <summary>Raised on the ticking thread when one fan is done, successfully or not.</summary>
    public event Action<FanTuningResult>? FanFinished;

    /// <summary>Raised on the ticking thread once, when the whole session ends.</summary>
    public event Action<FanTuningSession>? Finished;

    public bool IsRunning {
      get { return running; }
    }

    public FanTuningOutcome Outcome {
      get { return outcome; }
    }

    public FanTuningProgress Progress {
      get { return progress; }
    }

    public IReadOnlyList<FanTuningTarget> Targets {
      get { return targets; }
    }

    public IReadOnlyList<FanTuningResult> Results {
      get {
        lock (resultsLock)
          return results.ToArray();
      }
    }

    /// <summary>
    /// True while the session is driving this control, so nothing else - fan
    /// curves in particular - writes to it.
    /// </summary>
    public bool IsBusy(string controlIdentifier) {
      FanTuningTarget? current = target;
      return running && current != null &&
        string.Equals(current.Identifier, controlIdentifier, StringComparison.Ordinal);
    }

    /// <summary>Call after every sensor update, under the hardware lock.</summary>
    public void Tick() {
      if (!running)
        return;
      try {
        TickCore();
      } catch (Exception ex) {
        Abort(FanTuningOutcome.Failed, "Stopped because of an error: " + ex.Message +
          " The fan is back on automatic control.");
      }
      progress = BuildProgress();
    }

    /// <summary>Stops now and puts the fan back on automatic control. Call under the hardware lock.</summary>
    public void Cancel() {
      Abort(FanTuningOutcome.Cancelled, "Cancelled. The fan is back on automatic control.");
    }

    /// <summary>For application exit: stops now and puts the fan back on automatic control.</summary>
    public void Release() {
      Abort(FanTuningOutcome.ApplicationExit,
        "Stopped because Open Hardware Monitor is closing.");
    }

    // ---- the state machine ------------------------------------------------------

    private void TickCore() {
      double now = hardware.NowSeconds;

      if (!started) {
        started = true;
        float? initial = hardware.ReadHighestTemperature();
        if (!initial.HasValue || !float.IsFinite(initial.Value)) {
          Abort(FanTuningOutcome.Failed, "No processor or graphics card temperature can " +
            "be read, so overheating could not be noticed. Nothing was changed.");
          return;
        }
        if (initial.Value > options.StartTemperatureLimit) {
          Abort(FanTuningOutcome.Overheated, string.Format(CultureInfo.CurrentCulture,
            "Not started: the system is already at {0:0} °C. Try again when it is idle.",
            initial.Value));
          return;
        }
        if (targets.Length == 0) {
          Complete();
          return;
        }
        BeginTarget(0, now, false);
      }

      // Safety checks come first, every update, in every phase.
      float? temperature = hardware.ReadHighestTemperature();
      if (!temperature.HasValue || !float.IsFinite(temperature.Value)) {
        if (++missingTemperatureTicks >= options.MissingTemperatureTicks) {
          Abort(FanTuningOutcome.Failed, "Temperatures stopped reporting. " +
            "The fan is back on automatic control.");
          return;
        }
      } else {
        missingTemperatureTicks = 0;
        if (temperature.Value > options.TemperatureLimit) {
          Abort(FanTuningOutcome.Overheated, string.Format(CultureInfo.CurrentCulture,
            "Stopped at {0:0} °C to keep the system cool. The fan is back on automatic control.",
            temperature.Value));
          return;
        }
      }
      if (now - targetStart > options.BudgetSecondsPerFan) {
        Abort(FanTuningOutcome.TimedOut, "Took too long. The fan is back on automatic control.");
        return;
      }
      if (touched && ChangedElsewhere()) {
        Interrupt();
        return;
      }

      IReadOnlyList<FanSpeedReading> speeds = hardware.ReadFanSpeeds();
      phaseTicks++;

      switch (step) {
        case FanTuningStep.MeasuringBaseline: MeasureBaseline(now, speeds); break;
        case FanTuningStep.Pairing: MeasureResponse(now, speeds); break;
        case FanTuningStep.Preparing: Prepare(now, speeds); break;
        case FanTuningStep.SeekingStall: SeekStall(now, speeds); break;
        case FanTuningStep.SeekingStart: SeekStart(now, speeds); break;
        case FanTuningStep.Recovering: Recover(now, speeds); break;
        case FanTuningStep.Stopping: Stop(now, speeds); break;
      }
    }

    private IControl Control {
      get { return target!.Control; }
    }

    private void BeginTarget(int newIndex, double now, bool afterAnother) {
      index = newIndex;
      target = targets[newIndex];
      targetStart = now;
      touched = false;
      previousMode = target.Control.ControlMode;
      previousValue = target.Control.SoftwareValue;
      baseline.Clear();
      response.Clear();
      samples = 0;
      pairing = null;
      calibration = null;
      lastRpm = null;
      missingSpeedTicks = 0;
      // The previous fan is still slowing down or speeding up; measuring now
      // would credit its change to this one.
      notBefore = afterAnother ? now + options.SettleSeconds : now;
      Enter(FanTuningStep.MeasuringBaseline, now);
    }

    private void Enter(FanTuningStep next, double now) {
      step = next;
      phaseStart = now;
      phaseTicks = 0;
      previousReading = null;
      stoppedAt = null;
    }

    private void Command(float duty) {
      IControl control = Control;
      duty = Math.Clamp(duty, control.MinSoftwareValue,
        Math.Max(control.MinSoftwareValue, control.MaxSoftwareValue));
      commanded = duty;
      touched = true;
      control.SetSoftware(duty);
    }

    private bool ChangedElsewhere() {
      IControl control = Control;
      return control.ControlMode != ControlMode.Software ||
        Math.Abs(control.SoftwareValue - commanded) > 0.5f;
    }

    private bool Settled(double now) {
      return now - phaseStart >= options.SettleSeconds && phaseTicks >= options.SettleTicks;
    }

    /// <summary>Settled, and the speed has stopped drifting (or waited long enough).</summary>
    private bool SettledAndSteady(double now, float rpm) {
      float? previous = previousReading;
      previousReading = rpm;
      if (!Settled(now))
        return false;
      if (now - phaseStart >= options.MaxSettleSeconds)
        return true;
      return previous.HasValue &&
        Math.Abs(rpm - previous.Value) <= Math.Max(30f, 0.04f * rpm);
    }

    // ---- pairing ------------------------------------------------------------------

    private static void Accumulate(Dictionary<string, List<float>> into,
      IReadOnlyList<FanSpeedReading> speeds) {
      foreach (FanSpeedReading reading in speeds) {
        if (!reading.Rpm.HasValue || !float.IsFinite(reading.Rpm.Value))
          continue;
        if (!into.TryGetValue(reading.SensorIdentifier, out List<float>? list)) {
          list = new List<float>();
          into[reading.SensorIdentifier] = list;
        }
        list.Add(reading.Rpm.Value);
      }
    }

    private void MeasureBaseline(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (now < notBefore) {
        phaseTicks = 0;
        return;
      }
      Accumulate(baseline, speeds);
      if (++samples < options.BaselineSamples)
        return;

      IControl control = Control;
      float minimum = control.MinSoftwareValue;
      float maximum = Math.Max(minimum, control.MaxSoftwareValue);
      float? current = previousMode == ControlMode.Software
        ? previousValue : hardware.ReadControlDuty(target!.Identifier);
      float known = current.HasValue && float.IsFinite(current.Value) ? current.Value : minimum;

      // Up to full speed where there is room: it is the clearest change and
      // cannot stop a fan. A fan already near full speed is lowered instead.
      float test;
      if (maximum - known >= 30) {
        test = maximum;
        expectIncrease = true;
      } else {
        test = Math.Max(minimum, Math.Min(50, known - 30));
        expectIncrease = false;
      }
      if (Math.Abs(test - known) < 15) {
        FinishTarget(now, new FanPairing(null, FanPairingConfidence.None, 0),
          "The control's range is too narrow to tell which sensor responds.");
        return;
      }

      samples = 0;
      Command(test);
      Enter(FanTuningStep.Pairing, now);
    }

    private void MeasureResponse(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (!Settled(now))
        return;
      Accumulate(response, speeds);
      if (++samples < options.ResponseSamples)
        return;

      pairing = EvaluatePairing();
      switch (pairing.Confidence) {
        case FanPairingConfidence.None:
          FinishTarget(now, pairing, "No speed sensor responded. This fan may not report its speed.");
          return;
        case FanPairingConfidence.Low:
          FinishTarget(now, pairing, "More than one speed sensor responded, so the match " +
            "is uncertain and calibration was skipped.");
          return;
      }
      if (!target!.Calibrate) {
        FinishTarget(now, pairing, "Speed sensor found. The graphics driver keeps its own minimum speed.");
        return;
      }
      if (Math.Abs(commanded - Control.MaxSoftwareValue) > 0.5f)
        Command(Control.MaxSoftwareValue);
      Enter(FanTuningStep.Preparing, now);
    }

    /// <summary>
    /// The sensor whose every sample moved the expected way by more than the
    /// threshold, with the largest average movement. Confidence reflects how
    /// far ahead of every other sensor it is.
    /// </summary>
    internal FanPairing EvaluatePairing() {
      string? best = null;
      float bestScore = 0, runnerUp = 0;
      foreach (KeyValuePair<string, List<float>> entry in response) {
        float basis = baseline.TryGetValue(entry.Key, out List<float>? before) && before.Count > 0
          ? before.Average() : 0;   // a sensor that appeared only now was not turning
        float threshold = Math.Max(options.MinResponseRpm, options.MinResponseFraction * basis);
        bool consistent = entry.Value.Count >= options.ResponseSamples;
        float total = 0;
        foreach (float value in entry.Value) {
          float change = expectIncrease ? value - basis : basis - value;
          if (change < threshold)
            consistent = false;
          total += Math.Max(0, change);
        }
        float score = entry.Value.Count > 0 ? total / entry.Value.Count : 0;
        if (consistent && score > bestScore) {
          runnerUp = Math.Max(runnerUp, bestScore);
          best = entry.Key;
          bestScore = score;
        } else {
          runnerUp = Math.Max(runnerUp, score);
        }
      }

      if (best == null)
        return new FanPairing(null, FanPairingConfidence.None, 0);
      float ratio = runnerUp / bestScore;
      FanPairingConfidence confidence = ratio <= 0.25f ? FanPairingConfidence.High
        : ratio <= 0.6f ? FanPairingConfidence.Medium : FanPairingConfidence.Low;
      return new FanPairing(best, confidence, bestScore);
    }

    // ---- calibration ------------------------------------------------------------

    private bool TryReadPairedSpeed(IReadOnlyList<FanSpeedReading> speeds, out float rpm) {
      string id = pairing!.SpeedSensorIdentifier!;
      foreach (FanSpeedReading reading in speeds) {
        if (reading.SensorIdentifier == id && reading.Rpm.HasValue &&
          float.IsFinite(reading.Rpm.Value)) {
          rpm = reading.Rpm.Value;
          lastRpm = rpm;
          missingSpeedTicks = 0;
          return true;
        }
      }
      rpm = 0;
      if (++missingSpeedTicks >= options.MissingSpeedTicks)
        Abort(FanTuningOutcome.Failed, "The fan speed stopped reporting. " +
          "The fan is back on automatic control.");
      return false;
    }

    private float Step {
      get { return options.StepPercent; }
    }

    private void Prepare(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (!TryReadPairedSpeed(speeds, out float rpm))
        return;
      if (Settled(now) && rpm >= options.StartedRpm) {
        IControl control = Control;
        lastRunning = commanded;
        float start = (float)Math.Floor(Math.Min(control.MaxSoftwareValue,
          options.CalibrationStartDuty) / Step) * Step;
        searchStart = Math.Max(control.MinSoftwareValue, start);
        BeginStallStep(searchStart, now);
        return;
      }
      if (now - phaseStart >= options.RecoverySeconds && phaseTicks >= options.SettleTicks)
        Abort(FanTuningOutcome.StallNotRecovered, "The fan did not spin at full speed. " +
          "It is back on automatic control.");
    }

    private void BeginStallStep(float duty, double now) {
      Command(duty);
      Enter(FanTuningStep.SeekingStall, now);
    }

    private void SeekStall(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (!TryReadPairedSpeed(speeds, out float rpm))
        return;

      if (rpm < options.StallRpm) {
        // Stopped: react at once instead of waiting for the step to settle.
        // One more update confirms a standstill, so a fan still coasting is
        // not mistaken for one that started.
        stallDuty = commanded;
        nextCandidate = lastRunning;
        Enter(FanTuningStep.Stopping, now);
        stoppedAt = now;
        return;
      }
      if (!SettledAndSteady(now, rpm))
        return;

      lastRunning = commanded;
      float next = commanded - Step;
      if (next < Control.MinSoftwareValue - 0.001f) {
        calibration = new FanCalibration(lastRunning, lastRunning, false);
        FinishTarget(now, pairing, "Calibrated. This fan keeps turning at every speed.");
        return;
      }
      BeginStallStep(next, now);
    }

    private void BeginStartAttempt(float duty, double now) {
      candidate = Math.Min(duty, Control.MaxSoftwareValue);
      Command(candidate);
      Enter(FanTuningStep.SeekingStart, now);
    }

    private void SeekStart(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (!TryReadPairedSpeed(speeds, out float rpm))
        return;

      if (rpm >= options.StartedRpm) {
        calibration = new FanCalibration(lastRunning, candidate, true);
        FinishCalibrated(now);
        return;
      }

      bool attempted = now - phaseStart >= options.StartWaitSeconds && phaseTicks >= 1;
      bool stallLimit = now - stallSince >= options.MaxStallSeconds;
      if (!attempted && !stallLimit)
        return;

      if (attempted) {
        float maximum = Control.MaxSoftwareValue;
        if (candidate >= maximum - 0.001f) {
          BeginRecovery(now, true);
          return;
        }
        float next = Math.Min(maximum, candidate + Step);
        if (now - stallSince + options.StartWaitSeconds <= options.MaxStallSeconds) {
          BeginStartAttempt(next, now);
          return;
        }
        nextCandidate = next;
      } else {
        // The stall limit cut this attempt short; try the same duty again.
        nextCandidate = candidate;
      }
      BeginRecovery(now, false);
    }

    private void BeginRecovery(double now, bool finishAfter) {
      finishAfterRecovery = finishAfter;
      Command(Control.MaxSoftwareValue);
      Enter(FanTuningStep.Recovering, now);
    }

    private void Recover(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (!TryReadPairedSpeed(speeds, out float rpm))
        return;
      if (rpm >= options.StartedRpm) {
        if (finishAfterRecovery) {
          calibration = new FanCalibration(lastRunning, Control.MaxSoftwareValue, true);
          FinishCalibrated(now);
        } else {
          // Stop it again, so the next attempt starts from a standstill.
          Command(stallDuty);
          Enter(FanTuningStep.Stopping, now);
        }
        return;
      }
      if (now - phaseStart >= options.RecoverySeconds && phaseTicks >= 1)
        Abort(FanTuningOutcome.StallNotRecovered, "The fan did not start again at full " +
          "speed. It is back on automatic control; check that it can turn freely.");
    }

    private void Stop(double now, IReadOnlyList<FanSpeedReading> speeds) {
      if (!TryReadPairedSpeed(speeds, out float rpm))
        return;
      if (rpm < options.StallRpm) {
        if (!stoppedAt.HasValue) {
          stoppedAt = now;
        } else if (now > stoppedAt.Value) {
          // The stall limit counts from the first stopped reading.
          stallSince = stoppedAt.Value;
          BeginStartAttempt(nextCandidate, now);
        }
        return;
      }
      stoppedAt = null;
      if (now - phaseStart >= options.StopWaitSeconds) {
        // It keeps turning where it stopped before; the running minimum is
        // still safe, the start duty is simply not known.
        calibration = new FanCalibration(lastRunning, null, true);
        FinishCalibrated(now);
      }
    }

    private void FinishCalibrated(double now) {
      FanCalibration result = calibration!;
      FinishTarget(now, pairing, string.Format(CultureInfo.CurrentCulture,
        "Calibrated. Keeps turning down to {0:0} %{1}.", result.MinRunningDuty,
        result.MinStartDuty.HasValue
          ? string.Format(CultureInfo.CurrentCulture, " and starts at {0:0} %", result.MinStartDuty.Value)
          : ""));
    }

    // ---- endings ----------------------------------------------------------------

    private void RestorePrevious() {
      if (!touched)
        return;
      touched = false;
      IControl control = Control;
      if (previousMode == ControlMode.Software) {
        float value = previousValue;
        // The fan is turning now; never hand back a duty just learned to stop it.
        if (value > 0 && calibration != null && value < calibration.MinRunningDuty)
          value = calibration.MinRunningDuty;
        control.SetSoftware(Math.Clamp(value, control.MinSoftwareValue,
          Math.Max(control.MinSoftwareValue, control.MaxSoftwareValue)));
      } else {
        control.SetDefault();
      }
    }

    private void FinishTarget(double now, FanPairing? found, string text) {
      pairing = found;
      try {
        RestorePrevious();
      } catch (Exception ex) {
        Abort(FanTuningOutcome.Failed, "The fan's previous setting could not be restored: " +
          ex.Message + " It is on automatic control.");
        return;
      }
      FanTuningResult result = new FanTuningResult(target!.Identifier, target.Name,
        FanTuningOutcome.Completed, found, calibration, text);
      AddResult(result);

      if (index + 1 < targets.Length)
        BeginTarget(index + 1, now, true);
      else
        Complete();
    }

    private void AddResult(FanTuningResult result) {
      lock (resultsLock)
        results.Add(result);
      try {
        FanFinished?.Invoke(result);
      } catch (Exception) {
        // A listener must not keep a fan in software mode.
      }
    }

    private void Complete() {
      running = false;
      outcome = FanTuningOutcome.Completed;
      message = targets.Length == 1 ? "Done." : "Done. All fans are back to their previous settings.";
      step = FanTuningStep.Finished;
      target = null;
      RaiseFinished();
    }

    private void Interrupt() {
      running = false;
      touched = false;
      outcome = FanTuningOutcome.Interrupted;
      message = "Stopped because the fan was changed elsewhere. Its new setting was kept.";
      if (target != null)
        AddResult(new FanTuningResult(target.Identifier, target.Name, outcome, pairing, null, message));
      step = FanTuningStep.Finished;
      target = null;
      RaiseFinished();
    }

    private void Abort(FanTuningOutcome reason, string text) {
      if (!running)
        return;
      running = false;
      FanTuningTarget? current = target;
      if (current != null && touched) {
        touched = false;
        try {
          current.Control.SetDefault();
        } catch (Exception) {
          // Nothing more can be done here; the application's exit path and
          // the hardware's own close try again.
        }
      }
      outcome = reason;
      message = text;
      step = FanTuningStep.Finished;
      if (current != null)
        AddResult(new FanTuningResult(current.Identifier, current.Name, reason,
          pairing != null && pairing.Confidence != FanPairingConfidence.None ? pairing : null,
          null, text));
      target = null;
      progress = BuildProgress();
      RaiseFinished();
    }

    private void RaiseFinished() {
      try {
        Finished?.Invoke(this);
      } catch (Exception) {
        // See AddResult.
      }
    }

    // ---- progress ---------------------------------------------------------------

    private FanTuningProgress BuildProgress() {
      FanTuningTarget? current = target;
      return new FanTuningProgress(running, step, Math.Max(0, index), targets.Length,
        current?.Identifier, current?.Name, touched ? commanded : (float?)null,
        pairing?.SpeedSensorIdentifier != null ? lastRpm : null, Describe(), Fraction(),
        outcome);
    }

    private string Describe() {
      switch (step) {
        case FanTuningStep.MeasuringBaseline: return "Measuring current fan speeds";
        case FanTuningStep.Pairing: return "Checking which speed sensor responds";
        case FanTuningStep.Preparing: return "Running the fan at full speed";
        case FanTuningStep.SeekingStall: return "Lowering the speed to find the minimum";
        case FanTuningStep.SeekingStart: return "Finding the speed that starts the fan";
        case FanTuningStep.Recovering: return "Bringing the fan back up to speed";
        case FanTuningStep.Stopping: return "Letting the fan stop for the next test";
        default: return message;
      }
    }

    private float Fraction() {
      if (targets.Length == 0 || step == FanTuningStep.Finished)
        return 1;
      bool calibrating = target?.Calibrate == true;
      float within;
      switch (step) {
        case FanTuningStep.MeasuringBaseline: within = 0.03f; break;
        case FanTuningStep.Pairing: within = calibrating ? 0.12f : 0.5f; break;
        case FanTuningStep.Preparing: within = 0.2f; break;
        case FanTuningStep.SeekingStall:
          float span = Math.Max(Step, searchStart - (target?.Control.MinSoftwareValue ?? 0));
          within = 0.25f + 0.45f * Math.Clamp((searchStart - commanded) / span, 0, 1);
          break;
        case FanTuningStep.SeekingStart:
        case FanTuningStep.Recovering:
        case FanTuningStep.Stopping:
          within = 0.8f;
          break;
        default: within = 0; break;
      }
      return Math.Clamp((Math.Max(0, index) + within) / targets.Length, 0, 1);
    }
  }
}
