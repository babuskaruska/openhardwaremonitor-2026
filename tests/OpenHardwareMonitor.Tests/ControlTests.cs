/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System.Collections.Generic;
using OpenHardwareMonitor.Collections;
using OpenHardwareMonitor.Hardware;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// The fan control state machine. Hardware applies a level whenever the mode
  /// changes to Software or the value changes while in Software, so these tests
  /// record exactly the levels hardware would have been commanded.
  /// </summary>
  public class ControlTests {

    private sealed class MemorySettings : ISettings {
      private readonly Dictionary<string, string> values =
        new Dictionary<string, string>();

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

    /// <summary>Control only reads the sensor identifier.</summary>
    private sealed class StubSensor : ISensor {
      public IHardware Hardware { get { return null!; } }
      public SensorType SensorType { get { return SensorType.Control; } }
      public Identifier Identifier { get; } =
        new Identifier("test", "control", "0");
      public string Name { get; set; } = "Fan";
      public int Index { get { return 0; } }
      public bool IsDefaultHidden { get { return false; } }
      public IReadOnlyArray<IParameter> Parameters { get { return null!; } }
      public float? Value { get { return null; } }
      public float? Min { get { return null; } }
      public float? Max { get { return null; } }
      public void ResetMin() { }
      public void ResetMax() { }
      public IEnumerable<SensorValue> Values {
        get { return new SensorValue[0]; }
      }
      public IControl Control { get { return null!; } }
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }
    }

    private static Control Create(ISettings settings, List<float> applied) {
      Control control = new Control(new StubSensor(), settings, 30, 100);
      control.ControlModeChanged += c => {
        if (c.ControlMode == ControlMode.Software)
          applied.Add(c.SoftwareValue);
      };
      control.SoftwareControlValueChanged += c => {
        if (c.ControlMode == ControlMode.Software)
          applied.Add(c.SoftwareValue);
      };
      return control;
    }

    [Fact]
    public void FirstSwitchToManualAppliesOnlyTheRequestedLevel() {
      // Regression: the mode used to change first, briefly commanding the
      // previous value - 0 on a fresh control, which can stop a fan.
      List<float> applied = new List<float>();
      Control control = Create(new MemorySettings(), applied);

      control.SetSoftware(65);

      Assert.Equal(new[] { 65f }, applied);
    }

    [Fact]
    public void ChangingTheManualLevelAppliesItOnce() {
      List<float> applied = new List<float>();
      Control control = Create(new MemorySettings(), applied);
      control.SetSoftware(65);
      applied.Clear();

      control.SetSoftware(40);
      control.SetSoftware(40);

      Assert.Equal(new[] { 40f }, applied);
    }

    [Fact]
    public void ReturningToManualAppliesOnlyTheNewLevel() {
      List<float> applied = new List<float>();
      Control control = Create(new MemorySettings(), applied);
      control.SetSoftware(65);
      control.SetDefault();
      applied.Clear();

      control.SetSoftware(50);

      Assert.Equal(new[] { 50f }, applied);
      Assert.Equal(ControlMode.Software, control.ControlMode);
    }

    [Fact]
    public void PersistsModeAndLevel() {
      MemorySettings settings = new MemorySettings();
      Create(settings, new List<float>()).SetSoftware(55);

      Control reloaded = Create(settings, new List<float>());

      Assert.Equal(ControlMode.Software, reloaded.ControlMode);
      Assert.Equal(55f, reloaded.SoftwareValue);
    }
  }
}
