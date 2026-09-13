/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Globalization;
using System.Linq;
using OpenHardwareMonitor.Hardware;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Duty limits, stored detection results, profiles, and how the curve
  /// controller applies calibration and shares fans with detection.
  /// </summary>
  public class FanSettingsTests {

    private const string Control = "/sim/control/a";

    // ---- duty limits ----------------------------------------------------------------

    [Fact]
    public void ZeroStaysZero() {
      Assert.Equal(0f, FanDutyLimits.Clamp(0, 0, 100, new FanCalibration(25, 40, true), false));
    }

    [Fact]
    public void AStoppedFanGetsItsStartDuty() {
      Assert.Equal(40f, FanDutyLimits.Clamp(10, 0, 100, new FanCalibration(25, 40, true), false));
    }

    [Fact]
    public void ATurningFanOnlyNeedsItsRunningMinimum() {
      Assert.Equal(25f, FanDutyLimits.Clamp(10, 0, 100, new FanCalibration(25, 40, true), true));
      Assert.Equal(33f, FanDutyLimits.Clamp(33, 0, 100, new FanCalibration(25, 40, true), true));
    }

    [Fact]
    public void TheControlsOwnRangeAlwaysApplies() {
      Assert.Equal(30f, FanDutyLimits.Clamp(10, 30, 90, null, false));
      Assert.Equal(90f, FanDutyLimits.Clamp(100, 30, 90, new FanCalibration(25, 40, true), true));
      Assert.Equal(90f, FanDutyLimits.Clamp(float.NaN, 30, 90, null, false));
    }

    [Fact]
    public void AnUnmeasuredStartDutyIsEstimatedConservatively() {
      FanCalibration calibration = new FanCalibration(25, null, true);
      Assert.Equal(25 + FanCalibration.FallbackStartMargin, calibration.StartDuty);
    }

    // ---- stored results -------------------------------------------------------------

    [Fact]
    public void PairingAndCalibrationRoundTripThroughSettings() {
      CultureInfo previous = CultureInfo.CurrentCulture;
      try {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        FanTuningStore store = new FanTuningStore(new FanTestSettings());
        store.SetPairing(Control, new FanPairing("/lpc/it8689e/fan/1", FanPairingConfidence.High, 812.5f));
        store.SetCalibration(Control, new FanCalibration(22.5f, 37.5f, true));

        FanPairing pairing = store.GetPairing(Control)!;
        FanCalibration calibration = store.GetCalibration(Control)!;
        Assert.Equal("/lpc/it8689e/fan/1", pairing.SpeedSensorIdentifier);
        Assert.Equal(FanPairingConfidence.High, pairing.Confidence);
        Assert.Equal(812.5f, pairing.ResponseRpm);
        Assert.Equal(22.5f, calibration.MinRunningDuty);
        Assert.Equal(37.5f, calibration.MinStartDuty);
        Assert.True(calibration.Stops);
      } finally {
        CultureInfo.CurrentCulture = previous;
      }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("sensor=/x;confidence=Certain")]
    [InlineData("sensor=/x;confidence=7")]
    [InlineData("sensor=/x")]
    public void RejectsMalformedPairings(string? text) {
      Assert.False(FanPairing.TryParse(text, out FanPairing? pairing));
      Assert.Null(pairing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("minStart=30")]
    [InlineData("minRunning=abc")]
    [InlineData("minRunning=20;minStart=x")]
    [InlineData("minRunning=NaN")]
    public void RejectsMalformedCalibrations(string? text) {
      Assert.False(FanCalibration.TryParse(text, out FanCalibration? calibration));
      Assert.Null(calibration);
    }

    [Fact]
    public void AnUncertainPairingDoesNotReplaceAConfidentOne() {
      FanTuningStore store = new FanTuningStore(new FanTestSettings());
      store.Save(new FanTuningResult(Control, "a", FanTuningOutcome.Completed,
        new FanPairing("/fan/1", FanPairingConfidence.High, 900), null, ""));
      store.Save(new FanTuningResult(Control, "a", FanTuningOutcome.Completed,
        new FanPairing("/fan/2", FanPairingConfidence.Low, 300), null, ""));

      Assert.Equal("/fan/1", store.GetPairing(Control)!.SpeedSensorIdentifier);
    }

    [Fact]
    public void OnlyACompletedRunStoresACalibration() {
      FanTuningStore store = new FanTuningStore(new FanTestSettings());
      store.Save(new FanTuningResult(Control, "a", FanTuningOutcome.Cancelled,
        new FanPairing("/fan/1", FanPairingConfidence.High, 900),
        new FanCalibration(20, 30, true), ""));

      Assert.NotNull(store.GetPairing(Control));
      Assert.Null(store.GetCalibration(Control));
    }

    [Fact]
    public void ADetectionRunPersistsWhatItFound() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = rig.Add(new SimulatedFan("a", 20, 35));
      FanTestSettings settings = new FanTestSettings();
      FanTuningStore store = new FanTuningStore(settings);
      FanTuningSession session = new FanTuningSession(rig, new[] { SimulatedRig.Target(fan) });
      session.FanFinished += store.Save;

      rig.Run(session);

      string id = fan.Identifier.ToString();
      Assert.Equal(fan.SensorIdentifier, new FanTuningStore(settings).GetPairing(id)!.SpeedSensorIdentifier);
      Assert.InRange(new FanTuningStore(settings).GetCalibration(id)!.MinRunningDuty, 20, 25);
    }

    // ---- profiles ---------------------------------------------------------------------

    private const string Source = "/intelcpu/0/temperature/8";

    [Fact]
    public void ProfileEntriesRoundTrip() {
      FanProfileEntry fixedEntry = new FanProfileEntry(FanMode.Fixed, 42.5f, null);
      Assert.True(FanProfileEntry.TryParse(fixedEntry.Serialize(), out FanProfileEntry? parsedFixed));
      Assert.Equal(FanMode.Fixed, parsedFixed!.Mode);
      Assert.Equal(42.5f, parsedFixed.FixedDuty);
      Assert.Null(parsedFixed.Curve);

      FanCurve curve = new FanCurve(Source, new[] {
        new FanCurvePoint(40, 30), new FanCurvePoint(80, 90) }, 2.5f);
      FanProfileEntry curveEntry = new FanProfileEntry(FanMode.Curve, 30, curve);
      Assert.True(FanProfileEntry.TryParse(curveEntry.Serialize(), out FanProfileEntry? parsedCurve));
      Assert.Equal(FanMode.Curve, parsedCurve!.Mode);
      Assert.Equal(Source, parsedCurve.Curve!.SourceSensorIdentifier);
      Assert.Equal(2.5f, parsedCurve.Curve.Hysteresis);
      Assert.Equal(90f, parsedCurve.Curve.Points[1].Duty);
    }

    [Theory]
    [InlineData("duty=40")]
    [InlineData("mode=Loud;duty=40")]
    [InlineData("mode=Curve;duty=40")]
    [InlineData("mode=Fixed;duty=abc")]
    public void RejectsMalformedProfileEntries(string text) {
      Assert.False(FanProfileEntry.TryParse(text, out FanProfileEntry? entry));
      Assert.Null(entry);
    }

    [Fact]
    public void TheActiveProfileIsRemembered() {
      FanTestSettings settings = new FanTestSettings();
      Assert.Null(new FanProfileStore(settings).Active);
      new FanProfileStore(settings).Active = FanProfileKind.Performance;
      Assert.Equal(FanProfileKind.Performance, new FanProfileStore(settings).Active);
      new FanProfileStore(settings).Active = null;
      Assert.Null(new FanProfileStore(settings).Active);
    }

    [Fact]
    public void SavedProfilesAreKeptPerFan() {
      FanProfileStore store = new FanProfileStore(new FanTestSettings());
      store.Save(FanProfileKind.Silent, Control, new FanProfileEntry(FanMode.Fixed, 35, null));

      Assert.Equal(35f, store.GetSaved(FanProfileKind.Silent, Control)!.FixedDuty);
      Assert.Null(store.GetSaved(FanProfileKind.Balanced, Control));
      Assert.Null(store.GetSaved(FanProfileKind.Silent, "/sim/control/b"));
    }

    [Fact]
    public void DefaultProfilesGetLouderFromSilentToPerformance() {
      FanCalibration calibration = new FanCalibration(20, 35, true);
      float[] at60 = FanProfileStore.All.Select(kind =>
        FanProfileDefaults.Create(kind, false, Source, 0, 100, calibration).Curve!.Evaluate(60)).ToArray();

      Assert.True(at60[0] < at60[1] && at60[1] < at60[2], string.Join(", ", at60));
    }

    [Fact]
    public void DefaultCurvesStartAboveTheCalibratedMinimumAndStayInRange() {
      FanCalibration calibration = new FanCalibration(28, 40, true);
      foreach (FanProfileKind kind in FanProfileStore.All) {
        FanCurve curve = FanProfileDefaults.Create(kind, false, Source, 10, 90, calibration).Curve!;
        Assert.True(curve.Points[0].Duty > calibration.MinRunningDuty);
        Assert.All(curve.Points, p => Assert.InRange(p.Duty, 10, 90));
        for (int i = 1; i < curve.Points.Count; i++)
          Assert.True(curve.Points[i].Duty >= curve.Points[i - 1].Duty);
        Assert.Equal(90f, curve.Points[curve.Points.Count - 1].Duty);
      }
    }

    [Fact]
    public void GraphicsCardsKeepTheirDriverExceptInPerformance() {
      Assert.Equal(FanMode.Automatic,
        FanProfileDefaults.Create(FanProfileKind.Silent, true, Source, 30, 100, null).Mode);
      Assert.Equal(FanMode.Automatic,
        FanProfileDefaults.Create(FanProfileKind.Balanced, true, Source, 30, 100, null).Mode);
      Assert.Equal(FanMode.Curve,
        FanProfileDefaults.Create(FanProfileKind.Performance, true, Source, 30, 100, null).Mode);
    }

    [Fact]
    public void WithoutATemperatureAFanStaysAutomatic() {
      Assert.Equal(FanMode.Automatic,
        FanProfileDefaults.Create(FanProfileKind.Balanced, false, null, 0, 100, null).Mode);
    }

    // ---- curve controller ---------------------------------------------------------------

    private static (FanTestComputer, FanTestSensor, FanTestSensor) Computer(SimulatedFan fan) {
      FanTestComputer computer = new FanTestComputer();
      FanTestSensor temperature = new FanTestSensor("/sim/temperature/0", SensorType.Temperature) { Value = 30 };
      FanTestSensor control = new FanTestSensor(fan.Identifier.ToString().Replace("/control/", "/c/"),
        SensorType.Control, fan);
      computer.Sensors.Add(temperature);
      computer.Sensors.Add(control);
      return (computer, temperature, control);
    }

    private static FanCurve LowCurve() {
      return new FanCurve("/sim/temperature/0", new[] {
        new FanCurvePoint(30, 10), new FanCurvePoint(80, 100) }, 0);
    }

    [Fact]
    public void CurvesStartAFanAtItsStartDutyThenHoldItsRunningMinimum() {
      SimulatedFan fan = new SimulatedFan("a", 22, 38);
      (FanTestComputer computer, _, FanTestSensor control) = Computer(fan);
      FanCurveController controller = new FanCurveController(computer, new FanTestSettings());
      controller.Store.SetCalibration(control.Identifier.ToString(), new FanCalibration(25, 40, true));
      controller.SetCurve(control, LowCurve());

      controller.Update();
      Assert.Equal(40f, fan.SoftwareValue);

      controller.Update();
      Assert.Equal(25f, fan.SoftwareValue);
      Assert.DoesNotContain(fan.Writes, duty => duty > 0 && duty < 25);
    }

    [Fact]
    public void CurvesRespectTheControlsRange() {
      SimulatedFan fan = new SimulatedFan("a", 22, 38, minimum: 30, maximum: 90);
      (FanTestComputer computer, FanTestSensor temperature, FanTestSensor control) = Computer(fan);
      FanCurveController controller = new FanCurveController(computer, new FanTestSettings());
      controller.SetCurve(control, LowCurve());

      controller.Update();
      Assert.Equal(30f, fan.SoftwareValue);

      temperature.Value = 95;
      controller.Update();
      Assert.Equal(90f, fan.SoftwareValue);
    }

    [Fact]
    public void CurvesLeaveAFanAloneWhileDetectionTestsIt() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = rig.Add(new SimulatedFan("a", 22, 38));
      (FanTestComputer computer, _, FanTestSensor control) = Computer(fan);
      FanCurveController controller = new FanCurveController(computer, new FanTestSettings());
      controller.SetCurve(control, LowCurve());
      FanTuningSession session = new FanTuningSession(rig,
        new[] { new FanTuningTarget(control.Identifier.ToString(), "a", fan, true) });
      controller.StartTuning(session);

      while (session.IsRunning && rig.Now < 3000) {
        rig.Step(controller.Update);
        if (session.IsRunning && session.Progress.Duty.HasValue)
          Assert.Equal(session.Progress.Duty.Value, fan.SoftwareValue);
      }

      Assert.Equal(FanTuningOutcome.Completed, session.Outcome);
      // The curve takes the fan back once detection is done.
      rig.Step(controller.Update);
      Assert.Equal(ControlMode.Software, fan.ControlMode);
    }

    [Fact]
    public void ReleaseStopsDetectionAndHandsFansBack() {
      SimulatedRig rig = new SimulatedRig();
      SimulatedFan fan = rig.Add(new SimulatedFan("a", 22, 38));
      (FanTestComputer computer, _, FanTestSensor control) = Computer(fan);
      FanCurveController controller = new FanCurveController(computer, new FanTestSettings());
      FanTuningSession session = new FanTuningSession(rig,
        new[] { new FanTuningTarget(control.Identifier.ToString(), "a", fan, true) });
      controller.StartTuning(session);
      while (session.Progress.Step != FanTuningStep.SeekingStall && rig.Now < 3000)
        rig.Step(controller.Update);

      Assert.Throws<InvalidOperationException>(() => controller.StartTuning(
        new FanTuningSession(rig, Array.Empty<FanTuningTarget>())));

      controller.Release();

      Assert.Equal(FanTuningOutcome.ApplicationExit, session.Outcome);
      Assert.Equal(ControlMode.Default, fan.ControlMode);
    }
  }
}
