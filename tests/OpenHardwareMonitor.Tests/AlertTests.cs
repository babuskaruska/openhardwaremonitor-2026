/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using OpenHardwareMonitor.Collections;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Alerts;
using OpenHardwareMonitor.Hardware.Diagnostics;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Alerts: debounce, cooldown, hysteresis and recovery, the media error
  /// baseline, the built-in rules, custom rules, switches, hardware that
  /// appears at runtime, and the alert log in the diagnostics export.
  /// </summary>
  public class AlertTests {

    // ---- stubs ----------------------------------------------------------------

    private sealed class MemorySettings : ISettings {
      public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();
      public bool Contains(string name) { return Values.ContainsKey(name); }
      public void SetValue(string name, string value) { Values[name] = value; }
      public string GetValue(string name, string value) {
        return Values.TryGetValue(name, out string? result) ? result : value;
      }
      public void Remove(string name) { Values.Remove(name); }
    }

    private sealed class StubParameter : IParameter {
      public StubParameter(ISensor sensor, string name, float value) {
        Sensor = sensor;
        Name = name;
        Value = value;
        DefaultValue = value;
      }
      public ISensor Sensor { get; }
      public Identifier Identifier {
        get { return new Identifier(Sensor.Identifier, "parameter"); }
      }
      public string Name { get; }
      public string Description { get { return ""; } }
      public float Value { get; set; }
      public float DefaultValue { get; }
      public bool IsDefault { get; set; } = true;
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }
    }

    private sealed class StubSensor : ISensor {
      private readonly StubHardware hardware;
      private readonly List<IParameter> parameters = new List<IParameter>();

      public StubSensor(StubHardware hardware, string name, SensorType type,
        int index, float? value) {
        this.hardware = hardware;
        Name = name;
        SensorType = type;
        Index = index;
        Value = value;
        Min = value;
        Max = value;
      }

      public IHardware Hardware { get { return hardware; } }
      public SensorType SensorType { get; }
      public Identifier Identifier {
        get {
          return new Identifier(hardware.Identifier,
            SensorType.ToString().ToLowerInvariant(),
            Index.ToString(CultureInfo.InvariantCulture));
        }
      }
      public string Name { get; set; }
      public int Index { get; }
      public bool IsDefaultHidden { get; set; }
      public IReadOnlyArray<IParameter> Parameters {
        get { return new ReadOnlyArray<IParameter>(parameters.ToArray()); }
      }
      public float? Value { get; set; }
      public float? Min { get; set; }
      public float? Max { get; set; }
      public void ResetMin() { }
      public void ResetMax() { }
      public IEnumerable<SensorValue> Values { get { return Array.Empty<SensorValue>(); } }
      public IControl Control { get { return null!; } }
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }

      public StubSensor WithParameter(string name, float value) {
        parameters.Add(new StubParameter(this, name, value));
        return this;
      }

      public StubSensor WithRange(float? min, float? max) {
        Min = min;
        Max = max;
        return this;
      }
    }

    private sealed class StubHardware : IHardware {
      private readonly List<StubSensor> sensors = new List<StubSensor>();
      private readonly List<StubHardware> children = new List<StubHardware>();

      public StubHardware(string name, HardwareType type, params string[] id) {
        Name = name;
        HardwareType = type;
        Identifier = new Identifier(id);
      }

      public string Name { get; set; }
      public Identifier Identifier { get; }
      public HardwareType HardwareType { get; }
      public string GetReport() { return ""; }
      public void Update() { }
      public IHardware[] SubHardware { get { return children.ToArray(); } }
      public IHardware Parent { get; private set; } = null!;
      public ISensor[] Sensors { get { return sensors.ToArray(); } }
      public event SensorEventHandler? SensorAdded;
      public event SensorEventHandler? SensorRemoved;
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }

      public StubSensor Add(string name, SensorType type, float? value) {
        int index = sensors.Count(s => s.SensorType == type);
        StubSensor sensor = new StubSensor(this, name, type, index, value);
        sensors.Add(sensor);
        SensorAdded?.Invoke(sensor);
        return sensor;
      }

      public void Remove(StubSensor sensor) {
        sensors.Remove(sensor);
        SensorRemoved?.Invoke(sensor);
      }

      public StubHardware AddChild(StubHardware child) {
        child.Parent = this;
        children.Add(child);
        return child;
      }
    }

    private sealed class StubComputer : IComputer {
      private readonly List<IHardware> hardware = new List<IHardware>();
      public IHardware[] Hardware { get { return hardware.ToArray(); } }
      public bool MainboardEnabled { get { return true; } }
      public bool CPUEnabled { get { return true; } }
      public bool RAMEnabled { get { return true; } }
      public bool GPUEnabled { get { return true; } }
      public bool FanControllerEnabled { get { return true; } }
      public bool HDDEnabled { get { return true; } }
      public string GetReport() { return "Open Hardware Monitor Report"; }
      public event HardwareEventHandler? HardwareAdded;
      public event HardwareEventHandler? HardwareRemoved;
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }

      public StubHardware Add(string name, HardwareType type, params string[] id) {
        StubHardware added = new StubHardware(name, type, id);
        hardware.Add(added);
        HardwareAdded?.Invoke(added);
        return added;
      }

      public void Remove(StubHardware removed) {
        hardware.Remove(removed);
        HardwareRemoved?.Invoke(removed);
      }
    }

    // ---- helpers --------------------------------------------------------------

    private static readonly DateTime Start =
      new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Harness {
      public Harness(MemorySettings? settings = null) {
        Settings = settings ?? new MemorySettings();
        Engine = new AlertEngine(Computer, Settings);
        Engine.AlertRaised += (sender, record) => Raised.Add(record);
      }

      public MemorySettings Settings { get; }
      public StubComputer Computer { get; } = new StubComputer();
      public AlertEngine Engine { get; }
      public List<AlertRecord> Raised { get; } = new List<AlertRecord>();

      /// <summary>One update, the given number of seconds after the start.</summary>
      public void At(int seconds) {
        Engine.Update(Start.AddSeconds(seconds));
      }

      /// <summary>One update per second, both ends included.</summary>
      public void Run(int from, int to) {
        for (int seconds = from; seconds <= to; seconds++)
          At(seconds);
      }
    }

    private static StubSensor AddGpu(Harness h, float core) {
      StubHardware gpu = h.Computer.Add("RTX 3070", HardwareType.GpuNvidia, "nvidiagpu", "0");
      gpu.Add("GPU Hot Spot", SensorType.Temperature, core + 12);
      return gpu.Add("GPU Core", SensorType.Temperature, core);
    }

    // ---- behaviour ------------------------------------------------------------

    [Fact]
    public void AnAlertWaitsForItsRuleDuration() {
      Harness h = new Harness();
      AddGpu(h, 91);

      h.Run(0, 29);
      Assert.Empty(h.Raised);
      Assert.Equal(0, h.Engine.ActiveCount);

      h.At(30);
      AlertRecord alert = Assert.Single(h.Raised);
      Assert.Equal(AlertKind.Raised, alert.Kind);
      Assert.Equal(DiagnosticRules.GpuTemperatureHigh, alert.RuleId);
      Assert.Equal(DiagnosticSeverity.Critical, alert.Severity);
      Assert.True(alert.Notify);
      Assert.Equal("GPU temperature very high", alert.Title);
      Assert.Equal("RTX 3070: GPU Core is 91.0 °C (limit 90.0 °C).", alert.Message);
      Assert.Equal("/nvidiagpu/0/temperature/1", alert.SensorIdentifier);
      Assert.Equal(1, h.Engine.ActiveCount);
      Assert.Equal(DiagnosticSeverity.Critical, h.Engine.HighestActiveSeverity);
    }

    [Fact]
    public void AnInterruptionStartsTheWaitAgain() {
      Harness h = new Harness();
      StubSensor drive = h.Computer.Add("990 PRO", HardwareType.HDD, "nvme", "0")
        .Add("Temperature", SensorType.Temperature, 75);

      h.Run(0, 50);
      drive.Value = 60;
      h.At(51);
      drive.Value = 75;
      h.Run(52, 111);
      Assert.Empty(h.Raised);

      h.At(112);
      Assert.Equal(DiagnosticRules.StorageTemperatureHigh, Assert.Single(h.Raised).RuleId);
    }

    [Fact]
    public void HysteresisKeepsAnAlertFromFlapping() {
      Harness h = new Harness();
      StubSensor core = AddGpu(h, 84);
      h.Run(0, 30);
      Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(h.Raised).Severity);

      // Hovering around the 83 °C limit, within the 3 °C margin.
      float[] hovering = { 82.5f, 83.5f, 81f, 80.5f };
      for (int i = 0; i < hovering.Length; i++) {
        core.Value = hovering[i];
        h.At(31 + i);
      }
      Assert.Single(h.Raised);
      Assert.Equal(1, h.Engine.ActiveCount);

      core.Value = 79.9f;
      h.At(40);
      Assert.Equal(2, h.Raised.Count);
      AlertRecord recovered = h.Raised[1];
      Assert.Equal(AlertKind.Recovered, recovered.Kind);
      Assert.Equal(DiagnosticSeverity.Info, recovered.Severity);
      Assert.False(recovered.Notify);
      Assert.Equal(0, h.Engine.ActiveCount);
      Assert.Null(h.Engine.HighestActiveSeverity);
    }

    [Fact]
    public void CooldownSuppressesRepeatedNotificationsButLogsThem() {
      Harness h = new Harness();
      StubSensor core = AddGpu(h, 91);
      h.Run(0, 30);
      core.Value = 70;
      h.At(31);
      core.Value = 91;
      h.Run(32, 62);

      Assert.Equal(3, h.Raised.Count);
      Assert.Equal(AlertKind.Raised, h.Raised[2].Kind);
      Assert.False(h.Raised[2].Notify);
      Assert.Equal(3, h.Engine.GetRecentAlerts().Count);

      core.Value = 70;
      h.At(63);
      core.Value = 91;
      h.Run(930, 960);   // 15.5 minutes after the first notification
      Assert.Equal(5, h.Raised.Count);
      Assert.True(h.Raised[4].Notify);
    }

    [Fact]
    public void GettingWorseNotifiesAfterTheDuration() {
      Harness h = new Harness();
      StubSensor package = h.Computer.Add("Core i7-14700KF", HardwareType.CPU, "intelcpu", "0")
        .Add("CPU Package", SensorType.Temperature, 86).WithParameter("TjMax [°C]", 100);
      h.Run(0, 30);
      Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(h.Raised).Severity);

      package.Value = 97;
      h.Run(31, 60);
      Assert.Single(h.Raised);
      h.At(61);
      Assert.Equal(2, h.Raised.Count);
      AlertRecord worse = h.Raised[1];
      Assert.Equal(AlertKind.Escalated, worse.Kind);
      Assert.Equal(DiagnosticSeverity.Critical, worse.Severity);
      Assert.True(worse.Notify);

      h.Run(62, 100);
      Assert.Equal(2, h.Raised.Count);
    }

    [Fact]
    public void AnUnknownReadingDoesNotClearAnAlert() {
      Harness h = new Harness();
      StubSensor core = AddGpu(h, 91);
      h.Run(0, 30);
      core.Value = null;
      h.Run(31, 40);
      Assert.Single(h.Raised);
      Assert.Equal(1, h.Engine.ActiveCount);
    }

    // ---- built-in rules ---------------------------------------------------------

    [Fact]
    public void ProcessorUsesTjMaxAndRaisesOneAlertForAllCores() {
      Harness h = new Harness();
      StubHardware cpu = h.Computer.Add("CPU", HardwareType.CPU, "intelcpu", "0");
      for (int i = 1; i <= 8; i++)
        cpu.Add("P-Core #" + i, SensorType.Temperature, 70 + i).WithParameter("TjMax [°C]", 90);
      cpu.Add("Distance to TjMax #1", SensorType.Temperature, 99);

      h.Run(0, 30);
      // 78 °C is 12 °C below this TjMax; the default of 100 would not alert.
      AlertRecord alert = Assert.Single(h.Raised);
      Assert.Equal(DiagnosticRules.CpuTemperatureNearTjMax, alert.RuleId);
      Assert.Equal(DiagnosticSeverity.Warning, alert.Severity);
      Assert.Equal("CPU: P-Core #8 is 78.0 °C; the processor throttles at 90.0 °C.", alert.Message);
    }

    [Fact]
    public void MediaErrorsAlertOnlyWhenTheCountRises() {
      const string Key = "/nvme/0/factor/0/alertbaseline";
      MemorySettings settings = new MemorySettings();

      Harness first = new Harness(settings);
      first.Computer.Add("990 PRO", HardwareType.HDD, "nvme", "0")
        .Add("Media Errors", SensorType.Factor, 15);
      first.Run(0, 5);
      Assert.Empty(first.Raised);
      Assert.Equal("15", settings.Values[Key]);

      // After a restart the drive still reports the same 15 errors.
      Harness second = new Harness(settings);
      StubSensor errors = second.Computer.Add("990 PRO", HardwareType.HDD, "nvme", "0")
        .Add("Media Errors", SensorType.Factor, 15);
      second.Run(0, 5);
      Assert.Empty(second.Raised);

      errors.Value = 16;
      second.At(6);
      AlertRecord alert = Assert.Single(second.Raised);
      Assert.Equal(DiagnosticRules.StorageMediaErrors, alert.RuleId);
      Assert.Equal(DiagnosticSeverity.Warning, alert.Severity);
      Assert.True(alert.Notify);
      Assert.Contains("16 media errors, up from 15", alert.Message);
      Assert.Equal("16", settings.Values[Key]);
      Assert.Equal(1, second.Engine.ActiveCount);

      second.Run(7, 20);
      Assert.Single(second.Raised);
      second.At(6 + 15 * 60);
      Assert.Equal(0, second.Engine.ActiveCount);

      // A lower count (another drive under the same identifier) is only remembered.
      errors.Value = 0;
      second.At(1000);
      Assert.Single(second.Raised);
      Assert.Equal("0", settings.Values[Key]);
    }

    [Fact]
    public void SpareBelowTheDrivesOwnLimitIsCritical() {
      Harness h = new Harness();
      StubHardware ssd = h.Computer.Add("SSD", HardwareType.HDD, "nvme", "0");
      StubSensor spare = ssd.Add("Available Spare", SensorType.Level, 100);
      ssd.Add("Available Spare Threshold", SensorType.Level, 10).IsDefaultHidden = true;

      h.At(0);
      Assert.Empty(h.Raised);
      spare.Value = 15;
      h.At(1);
      Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(h.Raised).Severity);
      spare.Value = 8;
      h.At(2);
      Assert.Equal(2, h.Raised.Count);
      Assert.Equal(AlertKind.Escalated, h.Raised[1].Kind);
      Assert.Equal(DiagnosticSeverity.Critical, h.Raised[1].Severity);
      Assert.Contains("(drive limit 10.0 %)", h.Raised[1].Message);
    }

    [Fact]
    public void FanStoppedWhileHotNeedsAFanThatHasSpun() {
      Harness h = new Harness();
      StubHardware board = h.Computer.Add("B760M", HardwareType.Mainboard, "mainboard");
      StubHardware superIo = board.AddChild(
        new StubHardware("IT8689E", HardwareType.SuperIO, "lpc", "it8689e"));
      superIo.Add("System", SensorType.Temperature, 70);
      superIo.Add("Fan #3", SensorType.Fan, 0);
      StubSensor cpuFan = superIo.Add("Fan #1", SensorType.Fan, 0).WithRange(0, 950);

      h.Run(0, 60);
      AlertRecord alert = Assert.Single(h.Raised);
      Assert.Equal(DiagnosticRules.FanStoppedWhileHot, alert.RuleId);
      Assert.Equal("/lpc/it8689e/fan/1", alert.SensorIdentifier);

      cpuFan.Value = 900;
      h.At(61);
      Assert.Equal(2, h.Raised.Count);
      Assert.Equal(AlertKind.Recovered, h.Raised[1].Kind);
    }

    [Fact]
    public void GraphicsFanStopJudgesTheCoreNotTheHotSpot() {
      Harness h = new Harness();
      StubHardware gpu = h.Computer.Add("RTX 3070", HardwareType.GpuNvidia, "nvidiagpu", "0");
      StubSensor core = gpu.Add("GPU Core", SensorType.Temperature, 52);
      gpu.Add("GPU Hot Spot", SensorType.Temperature, 64);
      gpu.Add("GPU Fan", SensorType.Fan, 0);

      h.Run(0, 60);
      Assert.Empty(h.Raised);

      core.Value = 65;
      h.Run(61, 91);
      Assert.Equal(DiagnosticRules.FanStoppedWhileHot, Assert.Single(h.Raised).RuleId);
    }

    [Fact]
    public void VoltageRulesSkipImplausibleAndHiddenInputs() {
      Harness h = new Harness();
      StubHardware superIo = h.Computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "nct6798d");
      superIo.Add("+12V", SensorType.Voltage, 11.2f);
      superIo.Add("+5V", SensorType.Voltage, 1.0f);
      superIo.Add("AVCC3", SensorType.Voltage, 3.0f).IsDefaultHidden = true;
      superIo.Add("VBAT", SensorType.Voltage, 2.7f);
      superIo.Add("Vcore", SensorType.Voltage, 1.1f);

      h.Run(0, 60);
      Assert.Equal(2, h.Raised.Count);
      Assert.Equal(DiagnosticRules.VoltageOutOfRange, h.Raised[0].RuleId);
      Assert.Equal("12 V rail out of range", h.Raised[0].Title);
      Assert.Contains("allowed 11.400 V to 12.600 V", h.Raised[0].Message);
      Assert.Equal(DiagnosticRules.CmosBatteryLow, h.Raised[1].RuleId);
    }

    // ---- custom rules -----------------------------------------------------------

    [Fact]
    public void CustomRulesFireClearAndPersist() {
      MemorySettings settings = new MemorySettings();
      Harness h = new Harness(settings);
      StubSensor fan = h.Computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0")
        .Add("Fan #2", SensorType.Fan, 1200);
      CustomAlertRule rule = new CustomAlertRule(fan.Identifier.ToString(),
        AlertDirection.Below, 1000, 10);
      h.Engine.AddCustomRule(rule);

      h.Run(0, 20);
      Assert.Empty(h.Raised);
      fan.Value = 800;
      h.Run(21, 31);
      AlertRecord alert = Assert.Single(h.Raised);
      Assert.Equal(AlertEngine.CustomRuleId, alert.RuleId);
      Assert.Equal("Fan #2 below 1000 RPM", alert.Title);

      // Within the 50 RPM margin the alert stays; beyond it, it clears.
      fan.Value = 1030;
      h.At(32);
      Assert.Single(h.Raised);
      fan.Value = 1060;
      h.At(33);
      Assert.Equal(AlertKind.Recovered, h.Raised[1].Kind);

      Harness restarted = new Harness(settings);
      CustomAlertRule saved = Assert.Single(restarted.Engine.CustomRules);
      Assert.Equal(rule.Id, saved.Id);
      Assert.Equal("/lpc/0/fan/0", saved.SensorIdentifier);
      Assert.Equal(AlertDirection.Below, saved.Direction);
      Assert.Equal(1000f, saved.Threshold);
      Assert.Equal(10, saved.DurationSeconds);

      Assert.True(restarted.Engine.RemoveCustomRule(rule.Id));
      Assert.False(restarted.Engine.RemoveCustomRule(rule.Id));
      Assert.Empty(restarted.Engine.CustomRules);
      Assert.DoesNotContain(settings.Values.Keys,
        key => key.StartsWith("alerts.custom.", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomRulesRoundTripThroughTheirSettingsText() {
      CustomAlertRule rule = new CustomAlertRule("/intelcpu/0/temperature/8",
        AlertDirection.Above, 85.5f, 30);
      Assert.True(CustomAlertRule.TryParse(rule.Serialize(), out CustomAlertRule? parsed));
      Assert.NotNull(parsed);
      Assert.Equal(rule.Id, parsed!.Id);
      Assert.Equal(rule.SensorIdentifier, parsed.SensorIdentifier);
      Assert.Equal(AlertDirection.Above, parsed.Direction);
      Assert.Equal(85.5f, parsed.Threshold);
      Assert.Equal(30, parsed.DurationSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("sensor=/a/0;direction=above;threshold=1;duration=0")]
    [InlineData("id=abc;sensor=/a/0;direction=sideways;threshold=1;duration=0")]
    [InlineData("id=abc;sensor=/a/0;direction=above;threshold=hot;duration=0")]
    [InlineData("id=abc;sensor=/a/0;direction=above;threshold=NaN;duration=0")]
    [InlineData("id=abc;sensor=/a/0;direction=above;threshold=1;duration=-5")]
    [InlineData("id=a-b;sensor=/a/0;direction=above;threshold=1;duration=0")]
    public void MalformedCustomRulesAreRejected(string? text) {
      Assert.False(CustomAlertRule.TryParse(text, out CustomAlertRule? rule));
      Assert.Null(rule);
    }

    [Fact]
    public void RecentAlertLogKeepsTheNewestHundred() {
      Harness h = new Harness();
      StubSensor load = h.Computer.Add("CPU", HardwareType.CPU, "intelcpu", "0")
        .Add("CPU Total", SensorType.Load, 0);
      h.Engine.AddCustomRule(new CustomAlertRule(load.Identifier.ToString(),
        AlertDirection.Above, 50, 0));

      for (int i = 0; i < 120; i++) {
        load.Value = i % 2 == 0 ? 90 : 10;
        h.At(i);
      }

      IReadOnlyList<AlertRecord> recent = h.Engine.GetRecentAlerts();
      Assert.Equal(120, h.Raised.Count);
      Assert.Equal(AlertEngine.MaxRecentAlerts, recent.Count);
      Assert.Same(h.Raised[119], recent[0]);
      Assert.Same(h.Raised[20], recent[99]);
      Assert.Same(h.Raised[119], h.Engine.LatestAlert);
    }

    // ---- switches and hardware changes ------------------------------------------

    [Fact]
    public void RuleSwitchesPersistAndClearActiveAlerts() {
      MemorySettings settings = new MemorySettings();
      Harness h = new Harness(settings);
      StubSensor core = AddGpu(h, 91);
      h.Run(0, 30);
      Assert.Equal(1, h.Engine.ActiveCount);

      h.Engine.SetRuleEnabled(DiagnosticRules.GpuTemperatureHigh, false);
      Assert.Equal("false", settings.Values["alerts.gpu-temperature-high.enabled"]);
      Assert.Equal(0, h.Engine.ActiveCount);
      h.Run(31, 100);
      Assert.Single(h.Raised);
      Assert.False(new AlertEngine(new StubComputer(), settings)
        .IsRuleEnabled(DiagnosticRules.GpuTemperatureHigh));

      // Switching a rule off and on again does not get around the cooldown.
      h.Engine.SetRuleEnabled(DiagnosticRules.GpuTemperatureHigh, true);
      h.Run(101, 131);
      Assert.Equal(2, h.Raised.Count);
      Assert.False(h.Raised[1].Notify);

      h.Engine.Enabled = false;
      Assert.Equal("false", settings.Values["alerts.enabled"]);
      Assert.Equal(0, h.Engine.ActiveCount);
      core.Value = 70;
      h.Run(132, 200);
      Assert.Equal(2, h.Raised.Count);
      Assert.False(new AlertEngine(new StubComputer(), settings).Enabled);
    }

    [Fact]
    public void HardwareAndSensorsThatAppearLaterAreWatched() {
      Harness h = new Harness();
      h.Run(0, 5);

      AddGpu(h, 91);
      h.Run(6, 36);
      Assert.Equal(DiagnosticRules.GpuTemperatureHigh, Assert.Single(h.Raised).RuleId);

      StubHardware drive = h.Computer.Add("SSD", HardwareType.HDD, "nvme", "0");
      h.At(37);
      drive.Add("Temperature", SensorType.Temperature, 80);
      h.Run(38, 98);
      Assert.Equal(2, h.Raised.Count);
      Assert.Equal(DiagnosticRules.StorageTemperatureHigh, h.Raised[1].RuleId);
      Assert.Equal(2, h.Engine.ActiveCount);

      // Removed hardware is no longer watched, and its alert goes with it.
      h.Computer.Remove(drive);
      h.At(99);
      Assert.Equal(1, h.Engine.ActiveCount);
    }

    // ---- export -------------------------------------------------------------------

    private static DiagnosticSnapshot Capture(StubComputer computer,
      IReadOnlyList<AlertRecord>? alerts) {
      DateTimeOffset now = new DateTimeOffset(2026, 9, 13, 10, 15, 2, TimeSpan.FromHours(2));
      return DiagnosticSnapshot.Capture(computer, new DiagnosticCaptureOptions {
        Now = now,
        RecentAlerts = alerts,
        ApplicationInfo = new DiagnosticApplicationInfo {
          Name = "Test", Version = "1.2.3", ProcessStart = now.AddMinutes(-90)
        },
        SystemInfo = new DiagnosticSystemInfo {
          OsProductName = "Windows 11 Pro", OsDescription = "Microsoft Windows 10.0.26100",
          OsArchitecture = "X64", ProcessArchitecture = "X64",
          Uptime = TimeSpan.FromHours(50), LogicalProcessors = 16,
          IsElevated = false, DotNetRuntime = ".NET 10.0.0"
        },
        AccessInfo = new DiagnosticAccessInfo { Tier = AccessTier.Base }
      });
    }

    [Fact]
    public void ExportIncludesTheAlertLogWhenGiven() {
      Harness h = new Harness();
      AddGpu(h, 91);
      h.Run(0, 30);

      DiagnosticSnapshot snapshot = Capture(h.Computer, h.Engine.GetRecentAlerts());
      string markdown = DiagnosticMarkdownWriter.ToMarkdown(snapshot);
      Assert.Contains("## Recent alerts", markdown);
      Assert.Contains("| GPU temperature very high |", markdown);
      using (JsonDocument json = JsonDocument.Parse(DiagnosticJsonWriter.ToJson(snapshot))) {
        JsonElement alert = json.RootElement.GetProperty("recentAlerts")[0];
        Assert.Equal("raised", alert.GetProperty("kind").GetString());
        Assert.Equal("critical", alert.GetProperty("severity").GetString());
        Assert.Equal(DiagnosticRules.GpuTemperatureHigh, alert.GetProperty("ruleId").GetString());
        Assert.Equal("/nvidiagpu/0/temperature/1", alert.GetProperty("sensorId").GetString());
        Assert.True(alert.GetProperty("notified").GetBoolean());
      }

      DiagnosticSnapshot without = Capture(h.Computer, null);
      Assert.DoesNotContain("## Recent alerts", DiagnosticMarkdownWriter.ToMarkdown(without));
      using (JsonDocument json = JsonDocument.Parse(DiagnosticJsonWriter.ToJson(without)))
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("recentAlerts").ValueKind);

      DiagnosticSnapshot empty = Capture(h.Computer, Array.Empty<AlertRecord>());
      Assert.Contains("None since the application started.",
        DiagnosticMarkdownWriter.ToMarkdown(empty));
    }
  }
}
