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
using System.IO;
using System.Security.Principal;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.Tools.SensorDump {

  /// <summary>
  /// Opens the sensor engine, polls once, and prints every hardware node and
  /// sensor it found. Pass --report to also dump the full diagnostic report,
  /// or --export &lt;directory&gt; [--seconds N] to sample for N seconds and
  /// write the same AI-ready diagnostics snapshot as the desktop application.
  /// </summary>
  internal static class Program {

    private sealed class NoSettings : ISettings {
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

    private const int DefaultExportSeconds = 5;
    private const int MaxExportSeconds = 3600;

    private static int Main(string[] args) {
      bool wantReport = false;
      string? exportDirectory = null;
      int seconds = DefaultExportSeconds;

      for (int i = 0; i < args.Length; i++) {
        string arg = args[i];
        if (arg.Equals("--report", StringComparison.OrdinalIgnoreCase)) {
          wantReport = true;
        } else if (arg.Equals("--export", StringComparison.OrdinalIgnoreCase)) {
          if (i + 1 >= args.Length)
            return Usage("--export needs a directory.");
          exportDirectory = args[++i];
        } else if (arg.Equals("--seconds", StringComparison.OrdinalIgnoreCase)) {
          if (i + 1 >= args.Length || !int.TryParse(args[++i],
            NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) ||
            seconds < 1 || seconds > MaxExportSeconds)
            return Usage("--seconds needs a whole number from 1 to " +
              MaxExportSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        } else if (arg == "--help" || arg == "-h" || arg == "/?") {
          Usage(null);
          return 0;
        } else {
          return Usage("Unknown argument: " + arg);
        }
      }

      Console.OutputEncoding = System.Text.Encoding.UTF8;

      Computer computer = new Computer(new NoSettings()) {
        MainboardEnabled = true,
        CPUEnabled = true,
        RAMEnabled = true,
        GPUEnabled = true,
        HDDEnabled = true,
        FanControllerEnabled = true
      };

      computer.Open();
      try {
        PrintEnvironment();
        PrintTier();

        if (exportDirectory != null) {
          // Sample for a while so min/max and history mean something.
          Console.WriteLine();
          Console.WriteLine("== Sampling ==");
          Console.WriteLine("  Updating once per second for " +
            seconds.ToString(CultureInfo.InvariantCulture) + " s...");
          UpdateAll(computer);
          for (int s = 0; s < seconds; s++) {
            System.Threading.Thread.Sleep(1000);
            UpdateAll(computer);
          }
        } else {
          // One pass to populate, a second so rate-derived sensors (load,
          // energy-to-power deltas) have two samples to work from.
          UpdateAll(computer);
          System.Threading.Thread.Sleep(1200);
          UpdateAll(computer);
        }

        Console.WriteLine();
        Console.WriteLine("== Hardware ==");
        foreach (IHardware hardware in computer.Hardware)
          PrintHardware(hardware, 0);

        if (wantReport) {
          Console.WriteLine();
          Console.WriteLine("== Report ==");
          Console.WriteLine(computer.GetReport());
        }

        if (exportDirectory != null)
          return Export(computer, exportDirectory);
      } finally {
        computer.Close();
      }
      return 0;
    }

    private static int Usage(string? error) {
      if (error != null)
        Console.Error.WriteLine("SensorDump: " + error);
      Console.Error.WriteLine(
        "Usage: SensorDump [--report] [--export <directory> [--seconds N]]");
      Console.Error.WriteLine(
        "  --report           also print the full text report");
      Console.Error.WriteLine(
        "  --export <dir>     write an AI-ready diagnostics snapshot (.md and .json)");
      Console.Error.WriteLine(
        "  --seconds N        with --export: sample once per second for N seconds " +
        "first (default " + DefaultExportSeconds.ToString(CultureInfo.InvariantCulture) +
        ")");
      return error == null ? 0 : 2;
    }

    private static int Export(Computer computer, string directory) {
      Console.WriteLine();
      Console.WriteLine("== Diagnostics export ==");
      try {
        DiagnosticSnapshot snapshot = DiagnosticSnapshot.Capture(computer,
          new DiagnosticCaptureOptions { ApplicationName = "SensorDump" });
        DiagnosticExportResult result =
          DiagnosticExporter.WriteFiles(snapshot, directory);

        Console.WriteLine("  Markdown      : " + result.MarkdownPath);
        Console.WriteLine("  JSON          : " + result.JsonPath);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
          "  Findings      : {0} critical, {1} warning, {2} info",
          snapshot.CountFindings(DiagnosticSeverity.Critical),
          snapshot.CountFindings(DiagnosticSeverity.Warning),
          snapshot.CountFindings(DiagnosticSeverity.Info)));
        foreach (DiagnosticFinding finding in snapshot.Findings)
          Console.WriteLine("    [" + finding.Severity + "] " + finding.RuleId +
            ": " + finding.Title +
            (finding.HardwareName != null ? " (" + finding.HardwareName + ")" : ""));
        return 0;
      } catch (Exception ex) when (ex is IOException ||
        ex is UnauthorizedAccessException || ex is ArgumentException ||
        ex is NotSupportedException) {
        Console.Error.WriteLine("  Export failed: " + ex.Message);
        return 1;
      }
    }

    private static void UpdateAll(Computer computer) {
      foreach (IHardware hardware in computer.Hardware) {
        hardware.Update();
        foreach (IHardware sub in hardware.SubHardware)
          sub.Update();
      }
    }

    private static void PrintEnvironment() {
      Console.WriteLine("== Environment ==");
      Console.WriteLine("  OS            : " + Environment.OSVersion.VersionString);
      Console.WriteLine("  Runtime       : " + Environment.Version);
      Console.WriteLine("  Process       : " +
        (Environment.Is64BitProcess ? "64-bit" : "32-bit"));
      Console.WriteLine("  Logical CPUs  : " + Environment.ProcessorCount);
      Console.WriteLine("  Elevated      : " + IsElevated());
    }

    private static string IsElevated() {
      try {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) {
          return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator) ? "yes" : "no";
        }
      } catch (Exception) {
        return "unknown";
      }
    }

    private static void PrintTier() {
      Console.WriteLine();
      Console.WriteLine("== Low-level access ==");
      Console.WriteLine("  Backend       : " + HardwareAccess.BackendName);
      Console.WriteLine("  Tier          : " + HardwareAccess.Tier);
      Console.WriteLine("  MSR           : " +
        Yn(HardwareAccess.SupportsModelSpecificRegisters));
      Console.WriteLine("  I/O ports     : " + Yn(HardwareAccess.SupportsIoPort));
      Console.WriteLine("  PCI config    : " + Yn(HardwareAccess.SupportsPciConfig));
      string? reason = HardwareAccess.UnavailableReason;
      if (reason != null)
        Console.WriteLine("  Note          : " + reason);
    }

    private static string Yn(bool value) {
      return value ? "yes" : "no";
    }

    private static void PrintHardware(IHardware hardware, int depth) {
      string pad = new string(' ', 2 + depth * 2);
      Console.WriteLine();
      Console.WriteLine(pad + hardware.HardwareType + ": " + hardware.Name);

      SensorType? lastType = null;
      foreach (ISensor sensor in Sorted(hardware.Sensors)) {
        if (lastType != sensor.SensorType) {
          Console.WriteLine(pad + "  [" + sensor.SensorType + "]");
          lastType = sensor.SensorType;
        }
        string controllable = sensor.Control != null
          ? string.Format(CultureInfo.InvariantCulture,
            "  [controllable {0:0}-{1:0}%, mode {2}]",
            sensor.Control.MinSoftwareValue, sensor.Control.MaxSoftwareValue,
            sensor.Control.ControlMode)
          : "";
        Console.WriteLine(pad + "    " + sensor.Name.PadRight(30) +
          Format(sensor) + controllable);
      }

      foreach (IHardware sub in hardware.SubHardware)
        PrintHardware(sub, depth + 1);
    }

    private static IEnumerable<ISensor> Sorted(ISensor[] sensors) {
      List<ISensor> list = new List<ISensor>(sensors);
      list.Sort((a, b) => {
        int byType = a.SensorType.CompareTo(b.SensorType);
        return byType != 0 ? byType : a.Index.CompareTo(b.Index);
      });
      return list;
    }

    private static string Format(ISensor sensor) {
      if (!sensor.Value.HasValue)
        return "(no reading)";

      float value = sensor.Value.Value;
      switch (sensor.SensorType) {
        case SensorType.Temperature:
          return value.ToString("F1", CultureInfo.InvariantCulture) + " °C";
        case SensorType.Clock:
          return value.ToString("F0", CultureInfo.InvariantCulture) + " MHz";
        case SensorType.Load:
        case SensorType.Level:
        case SensorType.Control:
          return value.ToString("F1", CultureInfo.InvariantCulture) + " %";
        case SensorType.Voltage:
          return value.ToString("F3", CultureInfo.InvariantCulture) + " V";
        case SensorType.Fan:
          return value.ToString("F0", CultureInfo.InvariantCulture) + " RPM";
        case SensorType.Power:
          return value.ToString("F1", CultureInfo.InvariantCulture) + " W";
        case SensorType.Data:
          return value.ToString("F2", CultureInfo.InvariantCulture) + " GB";
        case SensorType.SmallData:
          return value.ToString("F0", CultureInfo.InvariantCulture) + " MB";
        case SensorType.Throughput:
          return value.ToString("F2", CultureInfo.InvariantCulture) + " MB/s";
        case SensorType.Flow:
          return value.ToString("F0", CultureInfo.InvariantCulture) + " L/h";
        default:
          return value.ToString("F2", CultureInfo.InvariantCulture);
      }
    }
  }
}
