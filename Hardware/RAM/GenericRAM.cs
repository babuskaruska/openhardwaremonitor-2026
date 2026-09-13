/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System.Runtime.InteropServices;

namespace OpenHardwareMonitor.Hardware.RAM {
  internal class GenericRAM : Hardware {

    private const float BytesPerGigabyte = 1024f * 1024f * 1024f;

    private readonly Sensor loadSensor;
    private readonly Sensor usedMemory;
    private readonly Sensor availableMemory;
    private readonly Sensor virtualLoadSensor;
    private readonly Sensor usedVirtualMemory;
    private readonly Sensor availableVirtualMemory;

    public GenericRAM(string name, ISettings settings)
      : base(name, new Identifier("ram"), settings)
    {
      loadSensor = new Sensor("Memory", 0, SensorType.Load, this, settings);
      ActivateSensor(loadSensor);

      usedMemory = new Sensor("Used Memory", 0, SensorType.Data, this,
        settings);
      ActivateSensor(usedMemory);

      availableMemory = new Sensor("Available Memory", 1, SensorType.Data, this,
        settings);
      ActivateSensor(availableMemory);

      // Commit charge against the commit limit (physical memory plus page
      // files). This, not physical usage, is what running out of memory
      // actually means on Windows: allocations fail when commit is exhausted
      // even if physical memory looks free.
      virtualLoadSensor = new Sensor("Virtual Memory", 1, SensorType.Load,
        this, settings);
      ActivateSensor(virtualLoadSensor);

      usedVirtualMemory = new Sensor("Used Virtual Memory", 2, SensorType.Data,
        this, settings);
      ActivateSensor(usedVirtualMemory);

      availableVirtualMemory = new Sensor("Available Virtual Memory", 3,
        SensorType.Data, this, settings);
      ActivateSensor(availableVirtualMemory);
    }

    public override HardwareType HardwareType {
      get {
        return HardwareType.RAM;
      }
    }

    public override void Update() {
      NativeMethods.MemoryStatusEx status = new NativeMethods.MemoryStatusEx();
      status.Length = checked((uint)Marshal.SizeOf(
          typeof(NativeMethods.MemoryStatusEx)));

      if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        return;

      if (status.TotalPhysicalMemory > 0) {
        loadSensor.Value = 100.0f -
          (100.0f * status.AvailablePhysicalMemory) /
          status.TotalPhysicalMemory;
        usedMemory.Value = (status.TotalPhysicalMemory -
          status.AvailablePhysicalMemory) / BytesPerGigabyte;
        availableMemory.Value =
          status.AvailablePhysicalMemory / BytesPerGigabyte;
      }

      if (status.TotalPageFile > 0) {
        virtualLoadSensor.Value = 100.0f -
          (100.0f * status.AvailablePageFile) / status.TotalPageFile;
        usedVirtualMemory.Value = (status.TotalPageFile -
          status.AvailablePageFile) / BytesPerGigabyte;
        availableVirtualMemory.Value =
          status.AvailablePageFile / BytesPerGigabyte;
      }
    }

    private static class NativeMethods {
      [StructLayout(LayoutKind.Sequential)]
      public struct MemoryStatusEx {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
      }

      [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
      [return: MarshalAs(UnmanagedType.Bool)]
      internal static extern bool GlobalMemoryStatusEx(
        ref MemoryStatusEx buffer);
    }
  }
}
