/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenHardwareMonitor.Hardware.RAM {
  internal class RAMGroup : IGroup {

    private readonly Hardware[] hardware;
    private readonly SMBIOS.MemoryDevice[] modules;

    public RAMGroup(SMBIOS smbios, ISettings settings) {

      // No implementation for RAM on Unix systems
      if (OperatingSystem.IsUnix) {
        hardware = new Hardware[0];
        modules = new SMBIOS.MemoryDevice[0];
        return;
      }

      // The parsed SMBIOS table was always handed in here and then ignored,
      // so every machine showed "Generic Memory". Name the node from the
      // modules actually installed.
      modules = smbios?.MemoryDevices?.Where(m => m.IsInstalled).ToArray()
        ?? new SMBIOS.MemoryDevice[0];

      hardware = new Hardware[] {
        new GenericRAM(BuildName(modules), settings)
      };
    }

    /// <summary>
    /// A short description such as "Corsair 32 GB DDR4-3200". Manufacturer,
    /// technology and speed are only included when every module agrees, so a
    /// mixed kit is never described inaccurately.
    /// </summary>
    internal static string BuildName(SMBIOS.MemoryDevice[] modules) {
      if (modules == null || modules.Length == 0)
        return "Generic Memory";

      long totalMegabytes = modules.Sum(m => m.SizeMegabytes);
      string[] types = modules.Select(m => m.MemoryType).Distinct().ToArray();
      int[] speeds = modules
        .Select(m => m.ConfiguredSpeed > 0 ? m.ConfiguredSpeed : m.Speed)
        .Distinct().ToArray();
      string[] manufacturers = modules
        .Select(m => m.ManufacturerName)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

      StringBuilder name = new StringBuilder();
      if (manufacturers.Length == 1)
        name.Append(manufacturers[0]).Append(' ');
      name.Append((totalMegabytes / 1024.0).ToString("0.#",
        CultureInfo.InvariantCulture)).Append(" GB");
      if (types.Length == 1 && types[0].Length > 0) {
        name.Append(' ').Append(types[0]);
        if (speeds.Length == 1 && speeds[0] > 0)
          name.Append('-').Append(speeds[0].ToString(CultureInfo.InvariantCulture));
      }
      return name.ToString();
    }

    public string GetReport() {
      if (modules.Length == 0)
        return null;

      StringBuilder r = new StringBuilder();
      r.AppendLine("Memory Modules");
      r.AppendLine();
      foreach (SMBIOS.MemoryDevice m in modules) {
        r.Append("Slot: ").Append(m.DeviceLocator);
        if (!string.IsNullOrEmpty(m.BankLocator))
          r.Append(" / ").Append(m.BankLocator);
        r.AppendLine();
        r.Append("  Module: ").Append(m.ManufacturerName).Append(' ')
          .AppendLine(m.PartNumber);
        r.Append("  Type: ").Append(m.MemoryType).Append(", ")
          .Append(m.SizeMegabytes).Append(" MB, rank ").Append(m.Rank)
          .AppendLine();
        r.Append("  Speed: rated ").Append(m.Speed)
          .Append(" MT/s, configured ").Append(m.ConfiguredSpeed)
          .AppendLine(" MT/s");
        if (m.ConfiguredVoltage > 0) {
          r.Append("  Voltage: ").Append((m.ConfiguredVoltage / 1000.0)
            .ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(" V");
        }
        r.AppendLine();
      }
      return r.ToString();
    }

    public IHardware[] Hardware {
      get {
        return hardware;
      }
    }

    public void Close() {
      foreach (Hardware ram in hardware)
        ram.Close();
    }
  }
}
