/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using OpenHardwareMonitor.Collections;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// A fan on a PWM header. It stops when the duty falls below the stall
  /// threshold and, once stopped, needs the higher start threshold to turn
  /// again. The speed follows the duty with a first-order lag, and readings
  /// carry noise. Automatic control drives it at <see cref="AutomaticDuty"/>.
  /// </summary>
  internal sealed class SimulatedFan : IControl {

    private readonly Random random;

    public SimulatedFan(string name, float stallDuty, float startDuty, float maxRpm = 1800,
      double lagSeconds = 1.0, float noise = 0.02f, int seed = 1,
      float minimum = 0, float maximum = 100) {
      Name = name;
      Identifier = new Identifier("sim", "control", name);
      SensorIdentifier = "/sim/fan/" + name;
      StallDuty = stallDuty;
      StartDuty = startDuty;
      MaxRpm = maxRpm;
      LagSeconds = lagSeconds;
      Noise = noise;
      MinSoftwareValue = minimum;
      MaxSoftwareValue = maximum;
      random = new Random(seed);
      Spinning = true;
      Rpm = TargetRpm(AutomaticDuty);
    }

    public string Name { get; }
    public string SensorIdentifier { get; set; }
    public float StallDuty { get; }
    public float StartDuty { get; }
    public float MaxRpm { get; }
    public double LagSeconds { get; }
    public float Noise { get; }
    public float AutomaticDuty { get; set; } = 40;

    /// <summary>A seized fan never turns, whatever the duty.</summary>
    public bool Seized { get; set; }

    /// <summary>When true the sensor stops reporting.</summary>
    public bool SensorMissing { get; set; }

    public bool Spinning { get; private set; }
    public float Rpm { get; private set; }

    /// <summary>Every software duty written, in order.</summary>
    public List<float> Writes { get; } = new List<float>();
    public int DefaultCalls { get; private set; }

    /// <summary>The longest continuous time a reading showed the fan stopped while under software control.</summary>
    public double LongestSoftwareStallSeconds { get; private set; }
    private double currentStall;

    public Identifier Identifier { get; }
    public ControlMode ControlMode { get; private set; } = ControlMode.Default;
    public float SoftwareValue { get; private set; }
    public float MinSoftwareValue { get; }
    public float MaxSoftwareValue { get; }

    public void SetDefault() {
      ControlMode = ControlMode.Default;
      DefaultCalls++;
    }

    public void SetSoftware(float value) {
      SoftwareValue = value;
      ControlMode = ControlMode.Software;
      Writes.Add(value);
    }

    public float EffectiveDuty {
      get { return ControlMode == ControlMode.Software ? SoftwareValue : AutomaticDuty; }
    }

    private float TargetRpm(float duty) {
      return MaxRpm * (0.25f + 0.75f * duty / 100f);
    }

    public void Advance(double seconds) {
      float duty = EffectiveDuty;
      if (Spinning && duty < StallDuty)
        Spinning = false;
      else if (!Spinning && duty >= StartDuty)
        Spinning = true;
      if (Seized)
        Spinning = false;

      float target = Spinning ? TargetRpm(duty) : 0;
      Rpm += (float)((target - Rpm) * (1 - Math.Exp(-seconds / LagSeconds)));

      if (ControlMode == ControlMode.Software && Reading() is float rpm && rpm < 100) {
        currentStall += seconds;
        LongestSoftwareStallSeconds = Math.Max(LongestSoftwareStallSeconds, currentStall);
      } else {
        currentStall = 0;
      }
    }

    private float? cached;

    /// <summary>Like a tachometer: noisy, and zero below what it can resolve.</summary>
    public float? Reading() {
      if (SensorMissing)
        return null;
      return cached ??= Rpm < 60 ? 0 : Rpm * (1 + Noise * (float)(random.NextDouble() * 2 - 1));
    }

    public void ClearReading() {
      cached = null;
    }
  }

  /// <summary>A computer with simulated fans, a clock and a temperature.</summary>
  internal sealed class SimulatedRig : IFanTuningHardware {

    public const double TickSeconds = 0.5;

    private readonly Random random = new Random(7);

    public List<SimulatedFan> Fans { get; } = new List<SimulatedFan>();

    /// <summary>Sensors no control drives: a fixed speed that wanders a little.</summary>
    public Dictionary<string, float> Unrelated { get; } = new Dictionary<string, float>();

    public double Now { get; private set; } = 1000;
    public float? Temperature { get; set; } = 45;
    public bool ThrowOnRead { get; set; }

    public double NowSeconds {
      get { return Now; }
    }

    public IReadOnlyList<FanSpeedReading> ReadFanSpeeds() {
      if (ThrowOnRead)
        throw new InvalidOperationException("Simulated driver failure");
      List<FanSpeedReading> readings = new List<FanSpeedReading>();
      foreach (SimulatedFan fan in Fans)
        readings.Add(new FanSpeedReading(fan.SensorIdentifier, fan.Reading()));
      foreach (KeyValuePair<string, float> sensor in Unrelated)
        readings.Add(new FanSpeedReading(sensor.Key,
          sensor.Value * (1 + 0.05f * (float)(random.NextDouble() * 2 - 1))));
      foreach (KeyValuePair<string, SimulatedFan> mirror in Mirrors)
        readings.Add(new FanSpeedReading(mirror.Key, mirror.Value.Reading()));
      return readings;
    }

    /// <summary>Extra sensors reporting the same fan, as boards with linked headers do.</summary>
    public Dictionary<string, SimulatedFan> Mirrors { get; } = new Dictionary<string, SimulatedFan>();

    public float? ReadHighestTemperature() {
      return Temperature;
    }

    public float? ReadControlDuty(string controlIdentifier) {
      foreach (SimulatedFan fan in Fans)
        if (fan.Identifier.ToString() == controlIdentifier)
          return fan.EffectiveDuty;
      return null;
    }

    public SimulatedFan Add(SimulatedFan fan) {
      Fans.Add(fan);
      return fan;
    }

    public static FanTuningTarget Target(SimulatedFan fan, bool calibrate = true) {
      return new FanTuningTarget(fan.Identifier.ToString(), fan.Name, fan, calibrate);
    }

    /// <summary>One sensor update: physics, then the session.</summary>
    public void Step(Action? afterUpdate) {
      Now += TickSeconds;
      foreach (SimulatedFan fan in Fans) {
        fan.ClearReading();
        fan.Advance(TickSeconds);
      }
      afterUpdate?.Invoke();
    }

    /// <summary>Ticks until the session ends or <paramref name="seconds"/> pass.</summary>
    public void Run(FanTuningSession session, double seconds = 1200,
      Func<FanTuningSession, bool>? stopWhen = null) {
      double end = Now + seconds;
      while (session.IsRunning && Now < end) {
        Step(session.Tick);
        if (stopWhen != null && stopWhen(session))
          return;
      }
    }

    /// <summary>Keeps the physics running with nothing driving the fans.</summary>
    public void Idle(double seconds) {
      double end = Now + seconds;
      while (Now < end)
        Step(null);
    }
  }

  internal sealed class FanTestSettings : ISettings {
    private readonly Dictionary<string, string> values = new Dictionary<string, string>();

    public bool Contains(string name) {
      return values.ContainsKey(name);
    }

    public void SetValue(string name, string value) {
      values[name] = value;
    }

    public string GetValue(string name, string value) {
      return values.TryGetValue(name, out string? stored) ? stored : value;
    }

    public void Remove(string name) {
      values.Remove(name);
    }
  }

  /// <summary>A sensor for the fan curve controller, which reads identifiers, values and controls.</summary>
  internal sealed class FanTestSensor : ISensor {

    public FanTestSensor(string identifier, SensorType type, IControl? control = null) {
      string[] parts = identifier.Trim('/').Split('/');
      Identifier = new Identifier(parts);
      SensorType = type;
      Control = control!;
    }

    public IHardware Hardware { get { return null!; } }
    public SensorType SensorType { get; }
    public Identifier Identifier { get; }
    public string Name { get; set; } = "Sensor";
    public int Index { get { return 0; } }
    public bool IsDefaultHidden { get { return false; } }
    public IReadOnlyArray<IParameter> Parameters { get { return null!; } }
    public float? Value { get; set; }
    public float? Min { get { return null; } }
    public float? Max { get { return null; } }
    public void ResetMin() { }
    public void ResetMax() { }
    public IEnumerable<SensorValue> Values { get { return new SensorValue[0]; } }
    public IControl Control { get; }
    public void Accept(IVisitor visitor) { visitor.VisitSensor(this); }
    public void Traverse(IVisitor visitor) { }
  }

  /// <summary>A computer that is just a list of sensors.</summary>
  internal sealed class FanTestComputer : IComputer {

    public List<ISensor> Sensors { get; } = new List<ISensor>();

    public IHardware[] Hardware { get { return new IHardware[0]; } }
    public bool MainboardEnabled { get { return true; } }
    public bool CPUEnabled { get { return true; } }
    public bool RAMEnabled { get { return true; } }
    public bool GPUEnabled { get { return true; } }
    public bool FanControllerEnabled { get { return true; } }
    public bool HDDEnabled { get { return true; } }
    public string GetReport() { return ""; }
#pragma warning disable CS0067
    public event HardwareEventHandler? HardwareAdded;
    public event HardwareEventHandler? HardwareRemoved;
#pragma warning restore CS0067

    public void Accept(IVisitor visitor) {
      visitor.VisitComputer(this);
    }

    public void Traverse(IVisitor visitor) {
      foreach (ISensor sensor in Sensors)
        sensor.Accept(visitor);
    }
  }
}
