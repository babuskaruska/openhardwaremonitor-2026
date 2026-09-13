/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Globalization;
using OpenHardwareMonitor.Hardware;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  public class FanCurveTests {

    private const string Source = "/intelcpu/0/temperature/0";

    private static FanCurve Curve(params (float t, float d)[] points) {
      FanCurvePoint[] converted = Array.ConvertAll(points,
        p => new FanCurvePoint(p.t, p.d));
      return new FanCurve(Source, converted);
    }

    [Fact]
    public void HoldsFirstDutyBelowTheCurve() {
      Assert.Equal(30f, Curve((40, 30), (80, 90)).Evaluate(10));
    }

    [Fact]
    public void HoldsLastDutyAboveTheCurve() {
      Assert.Equal(90f, Curve((40, 30), (80, 90)).Evaluate(105));
    }

    [Fact]
    public void InterpolatesBetweenPoints() {
      Assert.Equal(60f, Curve((40, 30), (80, 90)).Evaluate(60), 3);
    }

    [Fact]
    public void InterpolatesWithinTheCorrectSegment() {
      FanCurve curve = Curve((30, 20), (50, 40), (70, 100));
      Assert.Equal(30f, curve.Evaluate(40), 3);
      Assert.Equal(70f, curve.Evaluate(60), 3);
    }

    [Fact]
    public void SortsPointsGivenOutOfOrder() {
      FanCurve curve = Curve((80, 90), (40, 30));
      Assert.Equal(40f, curve.Points[0].Temperature);
      Assert.Equal(60f, curve.Evaluate(60), 3);
    }

    [Fact]
    public void ClampsDutyToAValidPercentage() {
      FanCurve curve = Curve((40, -10), (80, 150));
      Assert.Equal(0f, curve.Points[0].Duty);
      Assert.Equal(100f, curve.Points[1].Duty);
    }

    [Fact]
    public void UnreadableTemperatureFailsTowardsCooling() {
      Assert.Equal(90f, Curve((40, 30), (80, 90)).Evaluate(float.NaN));
    }

    [Fact]
    public void RejectsASinglePoint() {
      Assert.Throws<ArgumentException>(() => Curve((40, 30)));
    }

    [Fact]
    public void RejectsDuplicateTemperatures() {
      Assert.Throws<ArgumentException>(() => Curve((40, 30), (40, 50)));
    }

    [Fact]
    public void RejectsMissingSource() {
      Assert.Throws<ArgumentException>(() => new FanCurve(" ",
        new[] { new FanCurvePoint(40, 30), new FanCurvePoint(80, 90) }));
    }

    [Fact]
    public void RoundTripsThroughSettings() {
      FanCurve original = new FanCurve(Source, new[] {
        new FanCurvePoint(32.5f, 25f), new FanCurvePoint(71f, 87.5f)
      }, 2.5f);

      Assert.True(FanCurve.TryParse(original.Serialize(), out FanCurve? parsed));
      Assert.NotNull(parsed);
      Assert.Equal(Source, parsed!.SourceSensorIdentifier);
      Assert.Equal(2.5f, parsed.Hysteresis);
      Assert.Equal(32.5f, parsed.Points[0].Temperature);
      Assert.Equal(87.5f, parsed.Points[1].Duty);
    }

    [Fact]
    public void SerializationIgnoresTheCurrentCulture() {
      CultureInfo previous = CultureInfo.CurrentCulture;
      try {
        // German writes 32,5 - which would collide with the point separator.
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        FanCurve curve = new FanCurve(Source, new[] {
          new FanCurvePoint(32.5f, 25f), new FanCurvePoint(71f, 87.5f)
        });
        string text = curve.Serialize();
        Assert.Contains("32.5:25", text);
        Assert.True(FanCurve.TryParse(text, out FanCurve? parsed));
        Assert.Equal(87.5f, parsed!.Points[1].Duty);
      } finally {
        CultureInfo.CurrentCulture = previous;
      }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("source=/x;points=40:30")]
    [InlineData("source=/x;points=40-30,80:90")]
    [InlineData("points=40:30,80:90")]
    [InlineData("source=/x;points=40:30,80:90;hysteresis=abc")]
    public void RejectsMalformedSettings(string? text) {
      Assert.False(FanCurve.TryParse(text, out FanCurve? curve));
      Assert.Null(curve);
    }

    [Fact]
    public void RisingTemperatureTakesEffectImmediately() {
      FanCurve curve = Curve((40, 30), (80, 90));
      FanCurveState state = new FanCurveState();
      Assert.Equal(60f, state.Next(curve, 60), 3);
      Assert.Equal(67.5f, state.Next(curve, 65), 3);
    }

    [Fact]
    public void SmallFallsAreHeldByHysteresis() {
      FanCurve curve = Curve((40, 30), (80, 90));   // default hysteresis is 3
      FanCurveState state = new FanCurveState();
      float atSixty = state.Next(curve, 60);
      Assert.Equal(atSixty, state.Next(curve, 58.5f), 3);
      Assert.Equal(atSixty, state.Next(curve, 57.1f), 3);
    }

    [Fact]
    public void FallsBeyondHysteresisLowerTheFan() {
      FanCurve curve = Curve((40, 30), (80, 90));
      FanCurveState state = new FanCurveState();
      state.Next(curve, 60);
      Assert.Equal(55.5f, state.Next(curve, 57), 3);
    }

    [Fact]
    public void ResetForgetsTheGoverningTemperature() {
      FanCurve curve = Curve((40, 30), (80, 90));
      FanCurveState state = new FanCurveState();
      state.Next(curve, 60);
      state.Reset();
      Assert.Equal(57f, state.Next(curve, 58), 3);
    }
  }
}
