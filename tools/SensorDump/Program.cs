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
using System.Security.Principal;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.Tools.SensorDump {

  /// <summary>
  /// Opens the sensor engine, polls once, and prints every hardware node and
  /// sensor it found. Pass --report to also dump the full diagnostic report.
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

    private static int Main(string[] args) {
      bool wantReport = Array.Exists(args,
        a => a.Equals("--report", StringComparison.OrdinalIgnoreCase));

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

        // One pass to populate, a second so rate-derived sensors (load,
        // energy-to-power deltas) have two samples to work from.
        UpdateAll(computer);
        System.Threading.Thread.Sleep(1200);
        UpdateAll(computer);

        Console.WriteLine();
        Console.WriteLine("== Hardware ==");
        foreach (IHardware hardware in computer.Hardware)
          PrintHardware(hardware, 0);

        if (wantReport) {
          Console.WriteLine();
          Console.WriteLine("== Report ==");
          Console.WriteLine(computer.GetReport());
        }
      } finally {
        computer.Close();
      }
      return 0;
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
        Console.WriteLine(pad + "    " + sensor.Name.PadRight(30) +
          Format(sensor));
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
          return value.ToString("F0", CultureInfo.InvariantCulture) + " B/s";
        case SensorType.Flow:
          return value.ToString("F0", CultureInfo.InvariantCulture) + " L/h";
        default:
          return value.ToString("F2", CultureInfo.InvariantCulture);
      }
    }
  }
}
