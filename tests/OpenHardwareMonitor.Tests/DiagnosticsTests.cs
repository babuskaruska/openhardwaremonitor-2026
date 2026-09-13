/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenHardwareMonitor.Collections;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Diagnostics;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// The "Export for AI" diagnostics: capture from stub hardware, every
  /// analyzer rule on both sides of its threshold, and the JSON and Markdown
  /// output.
  /// </summary>
  public class DiagnosticsTests {

    // ---- stubs ----------------------------------------------------------------

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

    private sealed class StubControl : IControl {
      public Identifier Identifier { get; } = new Identifier("test", "control");
      public ControlMode ControlMode { get; set; } = ControlMode.Software;
      public float SoftwareValue { get; set; } = 45;
      public float MinSoftwareValue { get { return 30; } }
      public float MaxSoftwareValue { get { return 100; } }
      public void SetDefault() { }
      public void SetSoftware(float value) { }
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
      public bool ThrowOnValues { get; set; }
      public void ResetMin() { }
      public void ResetMax() { }
      public List<SensorValue> History { get; } = new List<SensorValue>();
      public IEnumerable<SensorValue> Values {
        get {
          if (ThrowOnValues)
            throw new InvalidOperationException("broken sensor");
          return History;
        }
      }
      public IControl Control { get; set; } = null!;
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
      public IHardware[] SubHardware {
        get { return children.ToArray(); }
      }
      public IHardware Parent { get; private set; } = null!;
      public ISensor[] Sensors {
        get { return sensors.ToArray(); }
      }
      public event SensorEventHandler SensorAdded { add { } remove { } }
      public event SensorEventHandler SensorRemoved { add { } remove { } }
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }

      public StubSensor Add(string name, SensorType type, float? value) {
        int index = sensors.Count(s => s.SensorType == type);
        StubSensor sensor = new StubSensor(this, name, type, index, value);
        sensors.Add(sensor);
        return sensor;
      }

      public StubHardware AddChild(StubHardware child) {
        child.Parent = this;
        children.Add(child);
        return child;
      }
    }

    private sealed class StubComputer : IComputer {
      public List<IHardware> List { get; } = new List<IHardware>();
      public IHardware[] Hardware { get { return List.ToArray(); } }
      public bool MainboardEnabled { get { return true; } }
      public bool CPUEnabled { get { return true; } }
      public bool RAMEnabled { get { return true; } }
      public bool GPUEnabled { get { return true; } }
      public bool FanControllerEnabled { get { return true; } }
      public bool HDDEnabled { get { return true; } }
      public string Report { get; set; } = "Open Hardware Monitor Report\nStub body";
      public string GetReport() { return Report; }
      public event HardwareEventHandler HardwareAdded { add { } remove { } }
      public event HardwareEventHandler HardwareRemoved { add { } remove { } }
      public void Accept(IVisitor visitor) { }
      public void Traverse(IVisitor visitor) { }

      public StubHardware Add(string name, HardwareType type, params string[] id) {
        StubHardware hardware = new StubHardware(name, type, id);
        List.Add(hardware);
        return hardware;
      }
    }

    // ---- helpers --------------------------------------------------------------

    private static readonly DateTimeOffset ExportTime =
      new DateTimeOffset(2026, 9, 13, 10, 15, 2, TimeSpan.FromHours(2));

    private static DiagnosticSnapshot Capture(StubComputer computer,
      AccessTier tier = AccessTier.Deep, string? reason = null) {
      return DiagnosticSnapshot.Capture(computer, new DiagnosticCaptureOptions {
        Now = ExportTime,
        ApplicationInfo = new DiagnosticApplicationInfo {
          Name = "Test", Version = "1.2.3", ProcessStart = ExportTime.AddMinutes(-90)
        },
        SystemInfo = new DiagnosticSystemInfo {
          OsProductName = "Windows 11 Pro", OsDisplayVersion = "24H2",
          OsBuild = "26100.4061", OsDescription = "Microsoft Windows 10.0.26100",
          OsArchitecture = "X64", ProcessArchitecture = "X64",
          Uptime = TimeSpan.FromHours(50), LogicalProcessors = 16,
          IsElevated = false, DotNetRuntime = ".NET 10.0.0"
        },
        AccessInfo = new DiagnosticAccessInfo {
          Tier = tier, BackendName = tier == AccessTier.Deep ? "PawnIO" : "",
          SupportsModelSpecificRegisters = tier == AccessTier.Deep,
          SupportsIoPort = tier == AccessTier.Deep,
          SupportsPciConfig = tier == AccessTier.Deep,
          UnavailableReason = reason
        }
      });
    }

    private static List<DiagnosticFinding> Findings(DiagnosticSnapshot snapshot,
      string ruleId) {
      return snapshot.Findings.Where(f => f.RuleId == ruleId).ToList();
    }

    private static DiagnosticFinding Single(DiagnosticSnapshot snapshot,
      string ruleId, DiagnosticSeverity severity) {
      DiagnosticFinding finding = Assert.Single(Findings(snapshot, ruleId));
      Assert.Equal(severity, finding.Severity);
      return finding;
    }

    private static void None(DiagnosticSnapshot snapshot, string ruleId) {
      Assert.Empty(Findings(snapshot, ruleId));
    }

    // ---- CPU ------------------------------------------------------------------

    [Fact]
    public void CpuUsesTheTjMaxParameterWhenPresent() {
      StubComputer computer = new StubComputer();
      StubHardware cpu = computer.Add("CPU", HardwareType.CPU, "intelcpu", "0");
      // 14 °C below a TjMax of 90: warning. Against the default 100 it would be 24.
      cpu.Add("P-Core #1", SensorType.Temperature, 76).WithParameter("TjMax [°C]", 90);

      DiagnosticFinding finding = Single(Capture(computer),
        DiagnosticRules.CpuTemperatureNearTjMax, DiagnosticSeverity.Warning);
      DiagnosticEvidence evidence = Assert.Single(finding.Evidence);
      Assert.Equal(90f, evidence.Threshold);
      Assert.Contains("from sensor parameter", evidence.Note);
      Assert.Equal("/intelcpu/0/temperature/0", evidence.SensorIdentifier);
    }

    [Fact]
    public void CpuFallsBackToTjMax100WithoutAParameter() {
      StubComputer computer = new StubComputer();
      StubHardware cpu = computer.Add("CPU", HardwareType.CPU, "intelcpu", "0");
      cpu.Add("CPU Core #1", SensorType.Temperature, 76);   // 24 below 100
      None(Capture(computer), DiagnosticRules.CpuTemperatureNearTjMax);

      cpu.Add("CPU Package", SensorType.Temperature, 96);   // 4 below 100
      DiagnosticFinding finding = Single(Capture(computer),
        DiagnosticRules.CpuTemperatureNearTjMax, DiagnosticSeverity.Critical);
      Assert.Equal(100f, Assert.Single(finding.Evidence).Threshold);
      Assert.Contains("assumed", finding.Explanation);
    }

    [Theory]
    [InlineData(84.9f, null)]
    [InlineData(85f, DiagnosticSeverity.Warning)]
    [InlineData(95f, DiagnosticSeverity.Critical)]
    public void CpuMarginBoundaries(float value, DiagnosticSeverity? expected) {
      StubComputer computer = new StubComputer();
      computer.Add("CPU", HardwareType.CPU, "amdcpu", "0")
        .Add("CPU Package", SensorType.Temperature, value);

      List<DiagnosticFinding> found =
        Findings(Capture(computer), DiagnosticRules.CpuTemperatureNearTjMax);
      if (expected == null)
        Assert.Empty(found);
      else
        Assert.Equal(expected, Assert.Single(found).Severity);
    }

    [Fact]
    public void CpuPeakSinceStartTriggersWhenCurrentIsCool() {
      StubComputer computer = new StubComputer();
      computer.Add("CPU", HardwareType.CPU, "intelcpu", "0")
        .Add("CPU Package", SensorType.Temperature, 45).WithRange(30, 92);

      DiagnosticFinding finding = Single(Capture(computer),
        DiagnosticRules.CpuTemperatureNearTjMax, DiagnosticSeverity.Warning);
      Assert.Contains("Only the peak", finding.Explanation);
    }

    [Theory]
    [InlineData("CPU Package", true)]
    [InlineData("P-Core #3", true)]
    [InlineData("E-Core #12", true)]
    [InlineData("Core #1 - #8", true)]
    [InlineData("CPU CCD #2", true)]
    [InlineData("Tdie", true)]
    [InlineData("Distance to TjMax #1", false)]
    [InlineData("Motherboard", false)]
    public void CpuSensorNameMatching(string name, bool expected) {
      Assert.Equal(expected, DiagnosticAnalyzer.IsCpuCoreOrPackage(name));
    }

    // ---- GPU ------------------------------------------------------------------

    [Theory]
    [InlineData(82.9f, null)]
    [InlineData(83f, DiagnosticSeverity.Warning)]
    [InlineData(89.9f, DiagnosticSeverity.Warning)]
    [InlineData(90f, DiagnosticSeverity.Critical)]
    public void GpuCoreThresholds(float value, DiagnosticSeverity? expected) {
      StubComputer computer = new StubComputer();
      StubHardware gpu = computer.Add("GPU", HardwareType.GpuNvidia, "nvidiagpu", "0");
      gpu.Add("GPU Core", SensorType.Temperature, value);
      gpu.Add("GPU Hot Spot", SensorType.Temperature, 99);   // not the core

      List<DiagnosticFinding> found =
        Findings(Capture(computer), DiagnosticRules.GpuTemperatureHigh);
      if (expected == null) {
        Assert.Empty(found);
      } else {
        DiagnosticFinding finding = Assert.Single(found);
        Assert.Equal(expected, finding.Severity);
        Assert.Equal("GPU Core", Assert.Single(finding.Evidence).SensorName);
      }
    }

    // ---- storage --------------------------------------------------------------

    [Theory]
    [InlineData(69.9f, false)]
    [InlineData(70f, true)]
    public void StorageTemperature(float value, bool expected) {
      StubComputer computer = new StubComputer();
      computer.Add("SSD", HardwareType.HDD, "nvme", "0")
        .Add("Temperature", SensorType.Temperature, value);
      Assert.Equal(expected,
        Findings(Capture(computer), DiagnosticRules.StorageTemperatureHigh).Count == 1);
    }

    [Theory]
    [InlineData(11f, null)]
    [InlineData(10f, DiagnosticSeverity.Warning)]
    [InlineData(0f, DiagnosticSeverity.Critical)]
    public void StorageEnduranceFromRemainingLife(float remaining,
      DiagnosticSeverity? expected) {
      StubComputer computer = new StubComputer();
      computer.Add("SSD", HardwareType.HDD, "nvme", "0")
        .Add("Remaining Life", SensorType.Level, remaining);

      List<DiagnosticFinding> found =
        Findings(Capture(computer), DiagnosticRules.StorageEnduranceUsed);
      if (expected == null)
        Assert.Empty(found);
      else
        Assert.Equal(expected, Assert.Single(found).Severity);
    }

    [Fact]
    public void StorageEnduranceFromPercentageUsed() {
      StubComputer computer = new StubComputer();
      computer.Add("SSD", HardwareType.HDD, "nvme", "0")
        .Add("Percentage Used", SensorType.Level, 93);
      Single(Capture(computer), DiagnosticRules.StorageEnduranceUsed,
        DiagnosticSeverity.Warning);
    }

    [Fact]
    public void StorageSpareBelowReportedThresholdIsCritical() {
      StubComputer computer = new StubComputer();
      StubHardware ssd = computer.Add("SSD", HardwareType.HDD, "nvme", "0");
      ssd.Add("Available Spare", SensorType.Level, 8);
      ssd.Add("Available Spare Threshold", SensorType.Level, 10);
      Single(Capture(computer), DiagnosticRules.StorageSpareLow,
        DiagnosticSeverity.Critical);
    }

    [Fact]
    public void StorageSpareLowWithoutThresholdIsWarning() {
      StubComputer computer = new StubComputer();
      computer.Add("SSD", HardwareType.HDD, "nvme", "0")
        .Add("Available Spare", SensorType.Level, 15);
      Single(Capture(computer), DiagnosticRules.StorageSpareLow,
        DiagnosticSeverity.Warning);
    }

    [Fact]
    public void StorageSpareHealthyIsQuiet() {
      StubComputer computer = new StubComputer();
      StubHardware ssd = computer.Add("SSD", HardwareType.HDD, "nvme", "0");
      ssd.Add("Available Spare", SensorType.Level, 100);
      ssd.Add("Available Spare Threshold", SensorType.Level, 10);
      None(Capture(computer), DiagnosticRules.StorageSpareLow);
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(1f, true)]
    public void StorageMediaErrors(float count, bool expected) {
      StubComputer computer = new StubComputer();
      computer.Add("SSD", HardwareType.HDD, "nvme", "0")
        .Add("Media Errors", SensorType.Factor, count);
      Assert.Equal(expected,
        Findings(Capture(computer), DiagnosticRules.StorageMediaErrors).Count == 1);
    }

    [Theory]
    [InlineData(60f, 100f, true)]     // 60 % of power cycles
    [InlineData(60f, 2000f, false)]   // 3 %: normal for the drive's age
    [InlineData(10f, null, false)]    // below the count threshold
    [InlineData(80f, null, true)]     // cycles unknown
    public void StorageUnsafeShutdowns(float count, float? cycles, bool expected) {
      StubComputer computer = new StubComputer();
      StubHardware ssd = computer.Add("SSD", HardwareType.HDD, "nvme", "0");
      ssd.Add("Unsafe Shutdowns", SensorType.Factor, count);
      if (cycles.HasValue)
        ssd.Add("Power Cycles", SensorType.Factor, cycles);

      List<DiagnosticFinding> found =
        Findings(Capture(computer), DiagnosticRules.StorageUnsafeShutdowns);
      Assert.Equal(expected, found.Count == 1);
      if (expected)
        Assert.Equal(DiagnosticSeverity.Info, found[0].Severity);
    }

    [Theory]
    [InlineData(94.9f, false)]
    [InlineData(95f, true)]
    public void DriveUsedSpace(float used, bool expected) {
      StubComputer computer = new StubComputer();
      computer.Add("HDD", HardwareType.HDD, "hdd", "0")
        .Add("Used Space", SensorType.Load, used);
      Assert.Equal(expected,
        Findings(Capture(computer), DiagnosticRules.DriveSpaceLow).Count == 1);
    }

    // ---- fans -----------------------------------------------------------------

    [Fact]
    public void MotherboardFanThatStoppedWhileHotIsWarning() {
      StubComputer computer = new StubComputer();
      StubHardware board = computer.Add("Board", HardwareType.Mainboard, "mainboard");
      StubHardware superIo = board.AddChild(
        new StubHardware("NCT6798D", HardwareType.SuperIO, "lpc", "nct6798d"));
      superIo.Add("CPU", SensorType.Temperature, 72);
      superIo.Add("Fan #1", SensorType.Fan, 0).WithRange(0, 950);

      DiagnosticFinding finding = Single(Capture(computer),
        DiagnosticRules.FanStoppedWhileHot, DiagnosticSeverity.Warning);
      Assert.Equal("Board / NCT6798D", finding.HardwareName);
      Assert.Contains(finding.Evidence, e => e.SensorName == "Fan #1");
    }

    [Fact]
    public void MotherboardHeaderThatNeverSpunIsOnlyInfo() {
      StubComputer computer = new StubComputer();
      StubHardware superIo = computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0");
      superIo.Add("CPU", SensorType.Temperature, 72);
      superIo.Add("Fan #4", SensorType.Fan, 0);

      Single(Capture(computer), DiagnosticRules.FanStoppedWhileHot,
        DiagnosticSeverity.Info);
    }

    [Fact]
    public void StoppedFanOnCoolHardwareIsQuiet() {
      StubComputer computer = new StubComputer();
      StubHardware superIo = computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0");
      superIo.Add("System", SensorType.Temperature, 60);   // not above 60
      superIo.Add("Fan #1", SensorType.Fan, 0).WithRange(0, 800);
      superIo.Add("Fan #2", SensorType.Fan, 1100);

      DiagnosticSnapshot snapshot = Capture(computer);
      None(snapshot, DiagnosticRules.FanStoppedWhileHot);
      None(snapshot, DiagnosticRules.GpuFanZeroRpmIdle);
    }

    [Fact]
    public void GpuZeroRpmAtIdleIsExplainedAsNormal() {
      StubComputer computer = new StubComputer();
      StubHardware gpu = computer.Add("GPU", HardwareType.GpuNvidia, "nvidiagpu", "0");
      gpu.Add("GPU Core", SensorType.Temperature, 45);
      gpu.Add("GPU", SensorType.Fan, 0);

      DiagnosticSnapshot snapshot = Capture(computer);
      None(snapshot, DiagnosticRules.FanStoppedWhileHot);
      DiagnosticFinding finding = Single(snapshot, DiagnosticRules.GpuFanZeroRpmIdle,
        DiagnosticSeverity.Info);
      Assert.Contains("not a fault", finding.Explanation);
    }

    [Fact]
    public void GpuFanStoppedWhileHotIsWarning() {
      StubComputer computer = new StubComputer();
      StubHardware gpu = computer.Add("GPU", HardwareType.GpuAti, "atigpu", "0");
      gpu.Add("GPU Core", SensorType.Temperature, 71);
      gpu.Add("GPU Fan", SensorType.Fan, 0);

      DiagnosticSnapshot snapshot = Capture(computer);
      Single(snapshot, DiagnosticRules.FanStoppedWhileHot, DiagnosticSeverity.Warning);
      None(snapshot, DiagnosticRules.GpuFanZeroRpmIdle);
    }

    [Fact]
    public void SpinningFanOnHotHardwareIsQuiet() {
      StubComputer computer = new StubComputer();
      StubHardware gpu = computer.Add("GPU", HardwareType.GpuNvidia, "nvidiagpu", "0");
      gpu.Add("GPU Core", SensorType.Temperature, 80);
      gpu.Add("GPU", SensorType.Fan, 1500);
      None(Capture(computer), DiagnosticRules.FanStoppedWhileHot);
    }

    // ---- voltages -------------------------------------------------------------

    [Theory]
    [InlineData("+12V", 12f)]
    [InlineData("12V", 12f)]
    [InlineData("+12 V", 12f)]
    [InlineData("+5V", 5f)]
    [InlineData("5VSB", 5f)]
    [InlineData("+3.3V", 3.3f)]
    [InlineData("3.3 V", 3.3f)]
    [InlineData("3VSB", 3.3f)]
    [InlineData("3VCC", 3.3f)]
    [InlineData("AVCC", 3.3f)]
    [InlineData("CPU VCore", null)]
    [InlineData("VBat", null)]
    [InlineData("VTT", null)]
    public void RailNameMatching(string name, float? nominal) {
      Assert.Equal(nominal, DiagnosticAnalyzer.GetNominalRailVoltage(name));
    }

    [Theory]
    [InlineData("+12V", 11.3f, true)]    // below 11.4
    [InlineData("+12V", 12.1f, false)]
    [InlineData("+5V", 5.3f, true)]      // above 5.25
    [InlineData("+5V", 4.9f, false)]
    [InlineData("+3.3V", 3.2f, false)]   // 3.135 - 3.465
    [InlineData("AVCC", 3.1f, true)]
    [InlineData("3VSB", 3.5f, true)]
    public void RailTolerance(string name, float value, bool expected) {
      StubComputer computer = new StubComputer();
      computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0")
        .Add(name, SensorType.Voltage, value);

      DiagnosticSnapshot snapshot = Capture(computer);
      Assert.Equal(expected,
        Findings(snapshot, DiagnosticRules.VoltageOutOfRange).Count == 1);
      if (expected)
        Assert.Equal(DiagnosticSeverity.Warning,
          Findings(snapshot, DiagnosticRules.VoltageOutOfRange)[0].Severity);
    }

    [Fact]
    public void WildlyWrongRailIsReportedAsImplausibleNotAsAFault() {
      StubComputer computer = new StubComputer();
      computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0")
        .Add("+12V", SensorType.Voltage, 2.04f);

      DiagnosticSnapshot snapshot = Capture(computer);
      None(snapshot, DiagnosticRules.VoltageOutOfRange);
      Single(snapshot, DiagnosticRules.VoltageImplausible, DiagnosticSeverity.Info);
    }

    [Fact]
    public void RailDipSinceStartIsAnExcursion() {
      StubComputer computer = new StubComputer();
      computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0")
        .Add("+12V", SensorType.Voltage, 12.0f).WithRange(11.2f, 12.1f);

      DiagnosticSnapshot snapshot = Capture(computer);
      None(snapshot, DiagnosticRules.VoltageOutOfRange);
      Single(snapshot, DiagnosticRules.VoltageExcursion, DiagnosticSeverity.Info);
    }

    [Fact]
    public void RailsAreOnlyCheckedOnMotherboardHardware() {
      StubComputer computer = new StubComputer();
      computer.Add("GPU", HardwareType.GpuAti, "atigpu", "0")
        .Add("+12V", SensorType.Voltage, 10f);
      None(Capture(computer), DiagnosticRules.VoltageOutOfRange);
    }

    [Theory]
    [InlineData(2.7f, DiagnosticRules.CmosBatteryLow)]
    [InlineData(3.05f, null)]
    [InlineData(0.2f, DiagnosticRules.VoltageImplausible)]
    public void CmosBattery(float value, string? expectedRule) {
      StubComputer computer = new StubComputer();
      computer.Add("IT8686E", HardwareType.SuperIO, "lpc", "0")
        .Add("VBat", SensorType.Voltage, value);

      DiagnosticSnapshot snapshot = Capture(computer);
      List<DiagnosticFinding> voltage = snapshot.Findings
        .Where(f => f.RuleId == DiagnosticRules.CmosBatteryLow ||
          f.RuleId == DiagnosticRules.VoltageImplausible).ToList();
      if (expectedRule == null)
        Assert.Empty(voltage);
      else
        Assert.Equal(expectedRule, Assert.Single(voltage).RuleId);
    }

    // ---- memory ---------------------------------------------------------------

    [Theory]
    [InlineData("Memory", 89.9f, null)]
    [InlineData("Memory", 90f, DiagnosticRules.MemoryLoadHigh)]
    [InlineData("Virtual Memory", 89f, null)]
    [InlineData("Virtual Memory", 95f, DiagnosticRules.VirtualMemoryLoadHigh)]
    public void MemoryLoad(string name, float value, string? expectedRule) {
      StubComputer computer = new StubComputer();
      computer.Add("Generic Memory", HardwareType.RAM, "ram")
        .Add(name, SensorType.Load, value);

      List<DiagnosticFinding> memory = Capture(computer).Findings
        .Where(f => f.RuleId == DiagnosticRules.MemoryLoadHigh ||
          f.RuleId == DiagnosticRules.VirtualMemoryLoadHigh).ToList();
      if (expectedRule == null) {
        Assert.Empty(memory);
      } else {
        DiagnosticFinding finding = Assert.Single(memory);
        Assert.Equal(expectedRule, finding.RuleId);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
      }
    }

    // ---- missing values, access, capture --------------------------------------

    [Fact]
    public void SensorsWithoutValueAreListed() {
      StubComputer computer = new StubComputer();
      StubHardware superIo = computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0");
      superIo.Add("Fan #3", SensorType.Fan, null);
      superIo.Add("Fan #1", SensorType.Fan, 900);

      DiagnosticFinding finding = Single(Capture(computer),
        DiagnosticRules.SensorNoValue, DiagnosticSeverity.Info);
      DiagnosticEvidence evidence = Assert.Single(finding.Evidence);
      Assert.Equal("Fan #3", evidence.SensorName);
    }

    [Fact]
    public void FanControlsInAutomaticModeAreNotListedAsMissing() {
      // A fan output has no reading while the hardware drives it (automatic
      // mode), as on the IT8689E. Only the unplugged fan header counts.
      StubComputer computer = new StubComputer();
      StubHardware superIo = computer.Add("ITE IT8689E", HardwareType.SuperIO, "lpc", "0");
      superIo.Add("CPU Fan", SensorType.Control, null);
      superIo.Add("System Fan #2", SensorType.Control, null);
      superIo.Add("System Fan #2", SensorType.Fan, null);

      DiagnosticFinding finding = Single(Capture(computer),
        DiagnosticRules.SensorNoValue, DiagnosticSeverity.Info);
      DiagnosticEvidence evidence = Assert.Single(finding.Evidence);
      Assert.Equal(SensorType.Fan, evidence.Type);
    }

    [Fact]
    public void OnlyFanControlsWithoutValueRaiseNothing() {
      StubComputer computer = new StubComputer();
      computer.Add("ITE IT8689E", HardwareType.SuperIO, "lpc", "0")
        .Add("CPU Fan", SensorType.Control, null);
      None(Capture(computer), DiagnosticRules.SensorNoValue);
    }

    [Fact]
    public void HiddenVoltageInputIsNotCheckedAsARail() {
      // Board configurations hide unwired or unscaled inputs, such as AVCC3
      // on the IT8689E reading a flat placeholder. The same reading on a
      // visible input is out of tolerance (see RailTolerance).
      StubComputer computer = new StubComputer();
      StubSensor avcc = computer.Add("ITE IT8689E", HardwareType.SuperIO, "lpc", "0")
        .Add("AVCC", SensorType.Voltage, 3.1f);
      avcc.IsDefaultHidden = true;

      DiagnosticSnapshot snapshot = Capture(computer);
      None(snapshot, DiagnosticRules.VoltageOutOfRange);
      None(snapshot, DiagnosticRules.VoltageExcursion);
      None(snapshot, DiagnosticRules.VoltageImplausible);
    }

    [Fact]
    public void NaNCountsAsNoValue() {
      StubComputer computer = new StubComputer();
      computer.Add("CPU", HardwareType.CPU, "intelcpu", "0")
        .Add("CPU Package", SensorType.Temperature, float.NaN);
      DiagnosticSnapshot snapshot = Capture(computer);
      Single(snapshot, DiagnosticRules.SensorNoValue, DiagnosticSeverity.Info);
      Assert.Null(snapshot.AllSensors().Single().Value);
    }

    [Fact]
    public void BaseTierIsExplainedWithTheReason() {
      StubComputer computer = new StubComputer();
      const string reason = "Install PawnIO to enable them.";

      DiagnosticFinding finding = Single(Capture(computer, AccessTier.Base, reason),
        DiagnosticRules.AccessTierLimited, DiagnosticSeverity.Info);
      Assert.Contains(reason, finding.Explanation);
      Assert.Contains("core temperatures", finding.Explanation);
    }

    [Fact]
    public void DeepTierIsQuietUnlessSomethingIsMissing() {
      StubComputer computer = new StubComputer();
      None(Capture(computer, AccessTier.Deep, null), DiagnosticRules.AccessTierLimited);
      DiagnosticFinding partial = Single(
        Capture(computer, AccessTier.Deep, "No I/O ports."),
        DiagnosticRules.AccessTierLimited, DiagnosticSeverity.Info);
      Assert.Contains("No I/O ports.", partial.Explanation);
    }

    [Fact]
    public void BrokenSensorDoesNotAbortTheCapture() {
      StubComputer computer = new StubComputer();
      StubHardware cpu = computer.Add("CPU", HardwareType.CPU, "intelcpu", "0");
      cpu.Add("CPU Total", SensorType.Load, 10).ThrowOnValues = true;
      cpu.Add("CPU Package", SensorType.Temperature, 50);

      DiagnosticSnapshot snapshot = Capture(computer);
      Assert.Single(snapshot.CaptureErrors);
      Assert.Equal("CPU Package", Assert.Single(snapshot.AllSensors()).Name);
      Single(snapshot, DiagnosticRules.CaptureIncomplete, DiagnosticSeverity.Info);
    }

    [Fact]
    public void FindingsAreOrderedBySeverity() {
      StubComputer computer = new StubComputer();
      StubHardware superIo = computer.Add("NCT6798D", HardwareType.SuperIO, "lpc", "0");
      superIo.Add("Fan #3", SensorType.Fan, null);                  // info
      superIo.Add("+12V", SensorType.Voltage, 11f);                  // warning
      computer.Add("CPU", HardwareType.CPU, "intelcpu", "0")
        .Add("CPU Package", SensorType.Temperature, 99);             // critical

      List<DiagnosticSeverity> order = Capture(computer).Findings
        .Select(f => f.Severity).ToList();
      Assert.Equal(order.OrderByDescending(s => s).ToList(), order);
      Assert.Equal(DiagnosticSeverity.Critical, order[0]);
    }

    // ---- history --------------------------------------------------------------

    [Fact]
    public void HistoryStatisticsSkipGapMarkersAndKeepTheNewestSamples() {
      DateTime t0 = new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);
      List<SensorValue> values = new List<SensorValue> {
        new SensorValue(40, t0),
        new SensorValue(60, t0.AddSeconds(4)),
        new SensorValue(float.NaN, t0.AddSeconds(6)),   // session gap
        new SensorValue(50, t0.AddSeconds(8)),
        new SensorValue(30, t0.AddSeconds(12))
      };

      DiagnosticHistory? history = DiagnosticSnapshot.CaptureHistory(values, 3);

      Assert.NotNull(history);
      Assert.Equal(4, history!.SampleCount);
      Assert.Equal(45.0, history.Average, 6);
      Assert.Equal(30f, history.Minimum);
      Assert.Equal(60f, history.Maximum);
      Assert.Equal(t0.AddSeconds(4), history.MaximumTimeUtc);
      Assert.Equal(t0, history.WindowStartUtc);
      Assert.Equal(t0.AddSeconds(12), history.WindowEndUtc);
      Assert.Equal(new[] { 60f, 50f, 30f },
        history.RecentSamples.Select(s => s.Value).ToArray());
    }

    [Fact]
    public void EmptyHistoryIsNull() {
      Assert.Null(DiagnosticSnapshot.CaptureHistory(new SensorValue[0], 10));
      Assert.Null(DiagnosticSnapshot.CaptureHistory(
        new[] { new SensorValue(float.NaN, DateTime.UtcNow) }, 10));
    }

    // ---- JSON -----------------------------------------------------------------

    private static StubComputer SampleComputer() {
      StubComputer computer = new StubComputer();
      StubHardware board = computer.Add("ASUS PRIME Z790-P", HardwareType.Mainboard, "mainboard");
      StubHardware superIo = board.AddChild(
        new StubHardware("Nuvoton NCT6798D", HardwareType.SuperIO, "lpc", "nct6798d"));
      superIo.Add("+12V", SensorType.Voltage, 12.096f).WithRange(12.0f, 12.2f);
      StubSensor fanControl = superIo.Add("Fan Control #1", SensorType.Control, 45);
      fanControl.Control = new StubControl();

      StubHardware cpu = computer.Add("Intel Core i7-14700KF", HardwareType.CPU, "intelcpu", "0");
      StubSensor package = cpu.Add("CPU Package", SensorType.Temperature, 45.5f)
        .WithRange(38.25f, 91f).WithParameter("TjMax [°C]", 100);
      DateTime t0 = new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);
      package.History.Add(new SensorValue(40, t0));
      package.History.Add(new SensorValue(91, t0.AddSeconds(4)));
      package.History.Add(new SensorValue(46, t0.AddSeconds(8)));
      StubSensor renamed = cpu.Add("Core | with `pipe`", SensorType.Load, 12.5f);
      renamed.IsDefaultHidden = true;
      cpu.Add("CPU Core #2", SensorType.Load, null);
      return computer;
    }

    [Fact]
    public void JsonIsCompleteAndMachineReadable() {
      DiagnosticSnapshot snapshot = Capture(SampleComputer());
      using JsonDocument document = JsonDocument.Parse(
        DiagnosticJsonWriter.ToUtf8Bytes(snapshot));
      JsonElement root = document.RootElement;

      Assert.Equal(DiagnosticSnapshot.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
      Assert.Equal("2026-09-13T10:15:02.000+02:00",
        root.GetProperty("exportTime").GetProperty("local").GetString());
      Assert.Equal("2026-09-13T08:15:02.000Z",
        root.GetProperty("exportTime").GetProperty("utc").GetString());
      Assert.Equal(5400, root.GetProperty("application").GetProperty("runtimeSeconds").GetDouble());
      Assert.Equal("Windows 11 Pro", root.GetProperty("system").GetProperty("osProductName").GetString());
      Assert.Equal(180000, root.GetProperty("system").GetProperty("uptimeSeconds").GetDouble());
      Assert.Equal("Deep", root.GetProperty("access").GetProperty("tier").GetString());
      Assert.Equal(JsonValueKind.Null,
        root.GetProperty("access").GetProperty("unavailableReason").ValueKind);

      // Nested hardware.
      JsonElement board = root.GetProperty("hardware")[0];
      Assert.Equal("Mainboard", board.GetProperty("type").GetString());
      JsonElement superIo = board.GetProperty("subHardware")[0];
      Assert.Equal("/mainboard", superIo.GetProperty("parentId").GetString());
      JsonElement rail = superIo.GetProperty("sensors")[0];
      Assert.Equal("V", rail.GetProperty("unit").GetString());
      Assert.Equal(12.096, rail.GetProperty("value").GetDouble(), 3);
      Assert.Equal(12.0, rail.GetProperty("minSinceAppStart").GetDouble(), 3);
      Assert.Equal(12.2, rail.GetProperty("maxSinceAppStart").GetDouble(), 3);
      JsonElement control = superIo.GetProperty("sensors")[1].GetProperty("control");
      Assert.Equal("Software", control.GetProperty("mode").GetString());
      Assert.Equal(30, control.GetProperty("minSoftwareValue").GetDouble());

      JsonElement package = root.GetProperty("hardware")[1].GetProperty("sensors")
        .EnumerateArray().Single(s => s.GetProperty("name").GetString() == "CPU Package");
      Assert.Equal("°C", package.GetProperty("unit").GetString());
      Assert.Equal(100, package.GetProperty("parameters")[0].GetProperty("value").GetDouble());
      JsonElement history = package.GetProperty("history");
      Assert.Equal(3, history.GetProperty("sampleCount").GetInt32());
      Assert.Equal(59, history.GetProperty("average").GetDouble(), 3);
      Assert.Equal("2026-09-13T08:00:04.000Z", history.GetProperty("maxTimeUtc").GetString());
      JsonElement firstSample = history.GetProperty("recentSamples")[0];
      Assert.Equal(new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        firstSample[0].GetInt64());
      Assert.Equal(40, firstSample[1].GetDouble());

      JsonElement missing = root.GetProperty("hardware")[1].GetProperty("sensors")
        .EnumerateArray().Single(s => s.GetProperty("name").GetString() == "CPU Core #2");
      Assert.Equal(JsonValueKind.Null, missing.GetProperty("value").ValueKind);
      Assert.Equal(JsonValueKind.Null, missing.GetProperty("history").ValueKind);

      JsonElement finding = root.GetProperty("findings").EnumerateArray()
        .Single(f => f.GetProperty("ruleId").GetString() == DiagnosticRules.CpuTemperatureNearTjMax);
      Assert.Equal("warning", finding.GetProperty("severity").GetString());
      Assert.Equal("/intelcpu/0/temperature/0",
        finding.GetProperty("evidence")[0].GetProperty("sensorId").GetString());
      Assert.Equal(1, root.GetProperty("summary").GetProperty("findings").GetProperty("warning").GetInt32());

      Assert.StartsWith("Open Hardware Monitor Report", root.GetProperty("report").GetString());
      AssertCamelCase(root);
    }

    private static void AssertCamelCase(JsonElement element) {
      switch (element.ValueKind) {
        case JsonValueKind.Object:
          foreach (JsonProperty property in element.EnumerateObject()) {
            Assert.True(char.IsLower(property.Name[0]),
              "Property is not camelCase: " + property.Name);
            Assert.DoesNotContain("_", property.Name);
            AssertCamelCase(property.Value);
          }
          break;
        case JsonValueKind.Array:
          foreach (JsonElement item in element.EnumerateArray())
            AssertCamelCase(item);
          break;
      }
    }

    [Fact]
    public void OutputIsCultureInvariant() {
      CultureInfo previous = CultureInfo.CurrentCulture;
      try {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        DiagnosticSnapshot snapshot = Capture(SampleComputer());
        string json = DiagnosticJsonWriter.ToJson(snapshot);
        string markdown = DiagnosticMarkdownWriter.ToMarkdown(snapshot);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Contains("45.5", json);
        Assert.Contains("| 45.5 °C |", markdown);
        Assert.Contains("| 12.096 V |", markdown);
        Assert.DoesNotContain("45,5", markdown);
      } finally {
        CultureInfo.CurrentCulture = previous;
      }
    }

    // ---- Markdown -------------------------------------------------------------

    [Fact]
    public void MarkdownHasTheSectionsInOrder() {
      string md = DiagnosticMarkdownWriter.ToMarkdown(Capture(SampleComputer()));

      int preamble = md.IndexOf("For the AI assistant", StringComparison.Ordinal);
      int summary = md.IndexOf("## System summary", StringComparison.Ordinal);
      int findings = md.IndexOf("## Findings", StringComparison.Ordinal);
      int hardware = md.IndexOf("## Hardware and sensors", StringComparison.Ordinal);
      int appendix = md.IndexOf("## Appendix", StringComparison.Ordinal);
      Assert.True(preamble >= 0);
      Assert.True(preamble < summary && summary < findings && findings < hardware &&
        hardware < appendix, "sections out of order");

      Assert.Contains("since the application started", md);
      Assert.Contains("| Windows | Windows 11 Pro 24H2, build 26100.4061 (X64) |", md);
      Assert.Contains("1h 30m", md);
      Assert.Contains("`" + DiagnosticRules.CpuTemperatureNearTjMax + "`", md);
      Assert.Contains("### CPU: Intel Core i7-14700KF", md);
      Assert.Contains("### SuperIO: ASUS PRIME Z790-P / Nuvoton NCT6798D", md);
      Assert.Contains("| Sensor | Type | Current | Min | Max | Avg |", md);
      Assert.Contains("TjMax [°C] = 100", md);
      Assert.Contains("control: manual 45 %, range 30-100 %", md);
    }

    [Fact]
    public void MarkdownSummarisesHistoryInsteadOfListingSamples() {
      string md = DiagnosticMarkdownWriter.ToMarkdown(Capture(SampleComputer()));
      string row = md.Split('\n').Single(l => l.StartsWith("| CPU Package |", StringComparison.Ordinal));

      Assert.Contains("| 45.5 °C | 38.3 °C | 91.0 °C | 59.0 °C |", row);   // current, min, max, avg
      Assert.Contains("| 10:00:04 |", row);                                 // peak, local time
      Assert.Contains("| 3 over 8s |", row);
      long firstSampleMs = new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();
      Assert.DoesNotContain(firstSampleMs.ToString(CultureInfo.InvariantCulture), md);
    }

    [Fact]
    public void MarkdownEscapesUserEditableNames() {
      string md = DiagnosticMarkdownWriter.ToMarkdown(Capture(SampleComputer()));
      string row = md.Split('\n').Single(l => l.Contains("with `pipe`") || l.Contains("with 'pipe'")
        || l.Contains("Core \\| with"));
      Assert.StartsWith("| Core \\| with `pipe` |", row);
      Assert.Contains("hidden", row);
      Assert.Contains("| " + DiagnosticFormat.NoValue + " |",
        md.Split('\n').Single(l => l.StartsWith("| CPU Core #2 |", StringComparison.Ordinal)));
    }

    [Fact]
    public void MarkdownAppendixFenceSurvivesBackticksInTheReport() {
      StubComputer computer = SampleComputer();
      computer.Report = "line one\n```\nline three";
      string md = DiagnosticMarkdownWriter.ToMarkdown(Capture(computer));

      int start = md.IndexOf("````text\nline one\n```\nline three\n````",
        StringComparison.Ordinal);
      Assert.True(start > md.IndexOf("## Appendix", StringComparison.Ordinal));
    }

    [Fact]
    public void MarkdownSaysSoWhenNothingWasFound() {
      StubComputer computer = new StubComputer();
      computer.Add("Generic Memory", HardwareType.RAM, "ram")
        .Add("Memory", SensorType.Load, 40);
      string md = DiagnosticMarkdownWriter.ToMarkdown(Capture(computer));
      Assert.Contains("No rule matched.", md);
    }

    // ---- files ----------------------------------------------------------------

    [Fact]
    public void ExporterWritesBothFilesWithoutOverwriting() {
      string directory = Path.Combine(Path.GetTempPath(),
        "ohm-diagnostics-tests-" + Guid.NewGuid().ToString("N"), "nested");
      try {
        DiagnosticSnapshot snapshot = Capture(SampleComputer());

        DiagnosticExportResult first = DiagnosticExporter.WriteFiles(snapshot, directory);
        DiagnosticExportResult second = DiagnosticExporter.WriteFiles(snapshot, directory);

        Assert.Equal("OHM-diagnostics-20260913-101502.md", Path.GetFileName(first.MarkdownPath));
        Assert.Equal("OHM-diagnostics-20260913-101502.json", Path.GetFileName(first.JsonPath));
        Assert.Equal("OHM-diagnostics-20260913-101502-2.md", Path.GetFileName(second.MarkdownPath));
        Assert.Equal(first.Markdown, File.ReadAllText(first.MarkdownPath));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(first.JsonPath));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());

        byte[] markdownBytes = File.ReadAllBytes(first.MarkdownPath);
        Assert.False(markdownBytes.Length >= 3 && markdownBytes[0] == 0xEF,
          "Markdown should be written without a byte order mark");
      } finally {
        string root = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(root))
          Directory.Delete(root, true);
      }
    }
  }
}
