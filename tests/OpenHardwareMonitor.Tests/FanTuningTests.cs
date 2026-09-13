/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Linq;
using OpenHardwareMonitor.Hardware;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Fan pairing and calibration against simulated fans: the right sensor is
  /// found, the thresholds are measured, and every way a session can end
  /// leaves the fan turning under automatic or its previous control.
  /// </summary>
  public class FanTuningTests {

    private const float Stall = 22;
    private const float Start = 38;

    private static SimulatedFan BoardFan(SimulatedRig rig, string name = "board", int seed = 1) {
      return rig.Add(new SimulatedFan(name, Stall, Start, seed: seed));
    }

    private static FanTuningSession Session(SimulatedRig rig, params FanTuningTarget[] targets) {
      return new FanTuningSession(rig, targets);
    }

    private static void RunUntil(SimulatedRig rig, FanTuningSession session, FanTuningStep step) {
      rig.Run(session, stopWhen: s => s.Progress.Step == step);
      Assert.True(session.IsRunning, "The session ended before reaching " + step);
      Assert.Equal(step, session.Progress.Step);
    }

    // ---- pairing ----------------------------------------------------------------

    [Fact]
    public void PairsEachControlWithTheSensorThatResponds() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan a = rig.Add(new SimulatedFan("a", 20, 35, maxRpm: 1500, seed: 1));
      SimulatedFan b = rig.Add(new SimulatedFan("b", 25, 40, maxRpm: 1100, seed: 2));
      SimulatedFan c = rig.Add(new SimulatedFan("c", 15, 30, maxRpm: 2400, seed: 3));
      // Sensors are not numbered like their controls.
      a.SensorIdentifier = "/sim/fan/3";
      b.SensorIdentifier = "/sim/fan/1";
      c.SensorIdentifier = "/sim/fan/2";
      rig.Unrelated["/sim/fan/9"] = 900;

      FanTuningSession session = Session(rig, SimulatedRig.Target(a, false),
        SimulatedRig.Target(b, false), SimulatedRig.Target(c, false));
      rig.Run(session);

      Assert.Equal(FanTuningOutcome.Completed, session.Outcome);
      Assert.Equal(3, session.Results.Count);
      foreach (SimulatedFan fan in new[] { a, b, c }) {
        FanTuningResult result = session.Results.Single(
          r => r.ControlIdentifier == fan.Identifier.ToString());
        Assert.Equal(fan.SensorIdentifier, result.Pairing!.SpeedSensorIdentifier);
        Assert.Equal(FanPairingConfidence.High, result.Pairing.Confidence);
        Assert.Equal(ControlMode.Default, fan.ControlMode);
      }
    }

    [Fact]
    public void LowersAFanThatIsAlreadyNearFullSpeed() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      fan.SetSoftware(90);
      rig.Idle(10);
      fan.Writes.Clear();

      FanTuningSession session = Session(rig, SimulatedRig.Target(fan, false));
      rig.Run(session);

      FanTuningResult result = Assert.Single(session.Results);
      Assert.Equal(fan.SensorIdentifier, result.Pairing!.SpeedSensorIdentifier);
      Assert.True(fan.Writes[0] < 90 && fan.Writes[0] >= Start,
        "The test duty should be lower, but still keep the fan turning.");
    }

    [Fact]
    public void RestoresAPreviousSoftwareDutyExactly() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      fan.SetSoftware(55);

      FanTuningSession session = Session(rig, SimulatedRig.Target(fan, false));
      rig.Run(session);

      Assert.Equal(FanTuningOutcome.Completed, session.Outcome);
      Assert.Equal(ControlMode.Software, fan.ControlMode);
      Assert.Equal(55f, fan.SoftwareValue);
    }

    [Fact]
    public void ReportsNoPairingWhenNoSensorResponds() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      fan.SensorMissing = true;
      rig.Unrelated["/sim/fan/9"] = 1200;

      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      rig.Run(session);

      FanTuningResult result = Assert.Single(session.Results);
      Assert.Equal(FanTuningOutcome.Completed, result.Outcome);
      Assert.Equal(FanPairingConfidence.None, result.Pairing!.Confidence);
      Assert.Null(result.Pairing.SpeedSensorIdentifier);
      Assert.Null(result.Calibration);
      Assert.Equal(ControlMode.Default, fan.ControlMode);
    }

    [Fact]
    public void AnUncertainPairingIsNeverUsedToSeekAStall() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      rig.Mirrors["/sim/fan/linked"] = fan;

      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      rig.Run(session);

      FanTuningResult result = Assert.Single(session.Results);
      Assert.Equal(FanPairingConfidence.Low, result.Pairing!.Confidence);
      Assert.Null(result.Calibration);
      Assert.All(fan.Writes, duty => Assert.Equal(100f, duty));
      Assert.Equal(ControlMode.Default, fan.ControlMode);
    }

    [Fact]
    public void GraphicsCardFansAreOnlyPaired() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan gpu = rig.Add(new SimulatedFan("gpu", 30, 30, minimum: 30, maximum: 100));

      FanTuningSession session = Session(rig, SimulatedRig.Target(gpu, calibrate: false));
      rig.Run(session);

      FanTuningResult result = Assert.Single(session.Results);
      Assert.Equal(gpu.SensorIdentifier, result.Pairing!.SpeedSensorIdentifier);
      Assert.Null(result.Calibration);
      Assert.Equal(new[] { 100f }, gpu.Writes);
      Assert.Equal(ControlMode.Default, gpu.ControlMode);
    }

    // ---- calibration --------------------------------------------------------------

    [Fact]
    public void CalibrationFindsTheStallAndStartThresholds() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningOptions options = new FanTuningOptions();

      FanTuningSession session = new FanTuningSession(rig,
        new[] { SimulatedRig.Target(fan) }, options);
      rig.Run(session);

      Assert.Equal(FanTuningOutcome.Completed, session.Outcome);
      FanCalibration calibration = Assert.Single(session.Results).Calibration!;
      Assert.InRange(calibration.MinRunningDuty, Stall, Stall + options.StepPercent);
      Assert.NotNull(calibration.MinStartDuty);
      Assert.InRange(calibration.MinStartDuty!.Value, Start, Start + options.StepPercent);
      Assert.True(calibration.Stops);

      Assert.Equal(ControlMode.Default, fan.ControlMode);
      Assert.True(fan.LongestSoftwareStallSeconds <= options.MaxStallSeconds + 2 * SimulatedRig.TickSeconds,
        "Stopped for " + fan.LongestSoftwareStallSeconds + " s");
      rig.Idle(5);
      Assert.True(fan.Spinning);
    }

    [Fact]
    public void AFanThatNeedsFullSpeedToStartIsStillBoundedAndRestored() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = rig.Add(new SimulatedFan("stiff", 30, 97));
      FanTuningOptions options = new FanTuningOptions();

      FanTuningSession session = new FanTuningSession(rig,
        new[] { SimulatedRig.Target(fan) }, options);
      rig.Run(session);

      Assert.Equal(FanTuningOutcome.Completed, session.Outcome);
      FanCalibration calibration = Assert.Single(session.Results).Calibration!;
      Assert.Equal(100f, calibration.StartDuty);
      Assert.True(fan.LongestSoftwareStallSeconds <= options.MaxStallSeconds + 2 * SimulatedRig.TickSeconds,
        "Stopped for " + fan.LongestSoftwareStallSeconds + " s");
      Assert.Equal(ControlMode.Default, fan.ControlMode);
    }

    [Fact]
    public void AFanThatNeverStopsReportsTheControlsMinimum() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = rig.Add(new SimulatedFan("floor", 10, 15, minimum: 20));

      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      rig.Run(session);

      FanCalibration calibration = Assert.Single(session.Results).Calibration!;
      Assert.Equal(20f, calibration.MinRunningDuty);
      Assert.False(calibration.Stops);
      Assert.Equal(ControlMode.Default, fan.ControlMode);
    }

    [Fact]
    public void CalibratesSeveralFansOneAfterAnother() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan a = rig.Add(new SimulatedFan("a", 20, 35, maxRpm: 1500, seed: 4));
      SimulatedFan b = rig.Add(new SimulatedFan("b", 30, 45, maxRpm: 2000, seed: 5));

      FanTuningSession session = Session(rig, SimulatedRig.Target(a), SimulatedRig.Target(b));
      rig.Run(session);

      Assert.Equal(FanTuningOutcome.Completed, session.Outcome);
      FanTuningResult first = session.Results[0], second = session.Results[1];
      Assert.Equal(a.SensorIdentifier, first.Pairing!.SpeedSensorIdentifier);
      Assert.Equal(b.SensorIdentifier, second.Pairing!.SpeedSensorIdentifier);
      Assert.InRange(first.Calibration!.MinRunningDuty, 20, 25);
      Assert.InRange(second.Calibration!.MinRunningDuty, 30, 35);
      Assert.Equal(ControlMode.Default, a.ControlMode);
      Assert.Equal(ControlMode.Default, b.ControlMode);
    }

    [Fact]
    public void NeverHandsBackADutyThatWouldStopTheFan() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      fan.SetSoftware(12);   // below the stall threshold
      rig.Idle(10);

      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      rig.Run(session);

      FanCalibration calibration = Assert.Single(session.Results).Calibration!;
      Assert.Equal(ControlMode.Software, fan.ControlMode);
      Assert.Equal(calibration.MinRunningDuty, fan.SoftwareValue);
      rig.Idle(10);
      Assert.True(fan.Spinning);
    }

    [Fact]
    public void ReportsProgressWhileWorking() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));

      RunUntil(rig, session, FanTuningStep.SeekingStall);

      FanTuningProgress progress = session.Progress;
      Assert.True(progress.IsRunning);
      Assert.Equal(fan.Identifier.ToString(), progress.ControlIdentifier);
      Assert.Equal(fan.SoftwareValue, progress.Duty);
      Assert.NotNull(progress.Rpm);
      Assert.InRange(progress.Fraction, 0.01f, 0.99f);
      Assert.True(session.IsBusy(fan.Identifier.ToString()));
    }

    // ---- every abort restores automatic control ---------------------------------------

    private static void AssertAbortedToAutomatic(SimulatedRig rig, FanTuningSession session,
      SimulatedFan fan, FanTuningOutcome expected) {
      Assert.False(session.IsRunning);
      Assert.Equal(expected, session.Outcome);
      Assert.Equal(ControlMode.Default, fan.ControlMode);
      Assert.Equal(expected, session.Results.Last().Outcome);
      Assert.False(session.IsBusy(fan.Identifier.ToString()));

      // Nothing more is written, and the fan turns again.
      int writes = fan.Writes.Count;
      for (int i = 0; i < 20; i++)
        rig.Step(session.Tick);
      Assert.Equal(writes, fan.Writes.Count);
      Assert.True(fan.Seized || fan.Spinning);
    }

    [Fact]
    public void CancelRestoresAutomaticControl() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStart);

      session.Cancel();

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.Cancelled);
    }

    [Fact]
    public void AbortGoesToAutomaticEvenFromAPreviousFixedSpeed() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      fan.SetSoftware(55);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStall);

      session.Cancel();

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.Cancelled);
    }

    [Fact]
    public void OverheatingAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStall);

      rig.Temperature = 81;
      rig.Step(session.Tick);

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.Overheated);
    }

    [Fact]
    public void LosingTheTemperatureAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStall);

      rig.Temperature = null;
      rig.Run(session, 5);

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.Failed);
    }

    [Fact]
    public void LosingTheFanSpeedAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStall);

      fan.SensorMissing = true;
      rig.Run(session, 5);
      fan.SensorMissing = false;

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.Failed);
    }

    [Fact]
    public void AStallThatDoesNotRecoverAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningOptions options = new FanTuningOptions();
      FanTuningSession session = new FanTuningSession(rig,
        new[] { SimulatedRig.Target(fan) }, options);
      RunUntil(rig, session, FanTuningStep.SeekingStart);

      fan.Seized = true;
      double seizedAt = rig.Now;
      rig.Run(session, 60);

      Assert.True(rig.Now - seizedAt <= options.MaxStallSeconds + options.StartWaitSeconds +
        options.RecoverySeconds + 2, "Took " + (rig.Now - seizedAt) + " s to give up");
      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.StallNotRecovered);
    }

    [Fact]
    public void AnExceptionAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStall);

      rig.ThrowOnRead = true;
      rig.Step(session.Tick);
      rig.ThrowOnRead = false;

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.Failed);
    }

    [Fact]
    public void ExceedingTheTimeBudgetAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = new FanTuningSession(rig, new[] { SimulatedRig.Target(fan) },
        new FanTuningOptions { BudgetSecondsPerFan = 25 });

      rig.Run(session, 60);

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.TimedOut);
    }

    [Fact]
    public void ReleaseOnExitAborts() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.Stopping);

      session.Release();

      AssertAbortedToAutomatic(rig, session, fan, FanTuningOutcome.ApplicationExit);
    }

    [Fact]
    public void AFanChangedElsewhereIsLeftAsItWasSet() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));
      RunUntil(rig, session, FanTuningStep.SeekingStall);

      fan.SetSoftware(70);   // the user, from the sensor list
      int writes = fan.Writes.Count;
      rig.Step(session.Tick);

      Assert.Equal(FanTuningOutcome.Interrupted, session.Outcome);
      Assert.Equal(ControlMode.Software, fan.ControlMode);
      Assert.Equal(70f, fan.SoftwareValue);
      Assert.Equal(writes, fan.Writes.Count);
    }

    [Fact]
    public void DoesNotStartWhenTheSystemIsAlreadyHot() {
      SimulatedRig rig = new SimulatedRig { Temperature = 78 };
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));

      rig.Run(session, 10);

      Assert.Equal(FanTuningOutcome.Overheated, session.Outcome);
      Assert.Empty(fan.Writes);
      Assert.Equal(0, fan.DefaultCalls);
    }

    [Fact]
    public void DoesNotStartWithoutATemperatureToWatch() {
      SimulatedRig rig = new SimulatedRig { Temperature = null };
      SimulatedFan fan = BoardFan(rig);
      FanTuningSession session = Session(rig, SimulatedRig.Target(fan));

      rig.Run(session, 10);

      Assert.Equal(FanTuningOutcome.Failed, session.Outcome);
      Assert.Empty(fan.Writes);
      Assert.Equal(0, fan.DefaultCalls);
    }

    [Fact]
    public void RejectsTheSameFanTwice() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = BoardFan(rig);
      Assert.Throws<ArgumentException>(() =>
        Session(rig, SimulatedRig.Target(fan), SimulatedRig.Target(fan)));
    }

    [Fact]
    public void RaisesEventsForEachFanAndTheEnd() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan a = rig.Add(new SimulatedFan("a", 20, 35, seed: 8));
      SimulatedFan b = rig.Add(new SimulatedFan("b", 20, 35, seed: 9));
      FanTuningSession session = Session(rig, SimulatedRig.Target(a, false),
        SimulatedRig.Target(b, false));
      int fans = 0, finished = 0;
      session.FanFinished += delegate { fans++; };
      session.Finished += delegate { finished++; };

      rig.Run(session);

      Assert.Equal(2, fans);
      Assert.Equal(1, finished);
    }
  }
}
