/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.RAM;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// SMBIOS type 17 (Memory Device) parsing and the memory node's name.
  /// Offsets follow the DMTF SMBIOS 3.x specification.
  /// </summary>
  public class MemoryTests {

    private const int MemoryTypeDdr4 = 0x1A;
    private const int MemoryTypeDdr5 = 0x22;

    private static void Word(byte[] data, int offset, int value) {
      data[offset] = (byte)value;
      data[offset + 1] = (byte)(value >> 8);
    }

    private static void DWord(byte[] data, int offset, long value) {
      BitConverter.GetBytes((uint)value).CopyTo(data, offset);
    }

    private static SMBIOS.MemoryDevice Device(int size, int type, int speed,
      int configuredSpeed, long extendedSize = 0, long extendedSpeed = 0,
      long extendedConfiguredSpeed = 0, string manufacturer = "Corsair",
      int length = 0x5C) {

      byte[] data = new byte[length];
      data[0] = 17;
      data[1] = (byte)length;
      Word(data, 0x0C, size);
      data[0x10] = 1;                         // device locator string
      data[0x11] = 2;                         // bank locator string
      data[0x12] = (byte)type;
      Word(data, 0x15, speed);
      data[0x17] = 3;                         // manufacturer string
      data[0x1A] = 4;                         // part number string
      if (length > 0x1B)
        data[0x1B] = 2;                       // rank 2
      if (length > 0x1F)
        DWord(data, 0x1C, extendedSize);
      if (length > 0x21)
        Word(data, 0x20, configuredSpeed);
      if (length > 0x27)
        Word(data, 0x26, 1350);
      if (length > 0x5B) {
        DWord(data, 0x54, extendedSpeed);
        DWord(data, 0x58, extendedConfiguredSpeed);
      }

      return new SMBIOS.MemoryDevice(17, 0x1100, data,
        new[] { "DIMM-A2", "BANK 0", manufacturer, "CMK32GX4M2E3200C16" });
    }

    [Fact]
    public void ReadsAConventionalDdr4Module() {
      SMBIOS.MemoryDevice device = Device(0x4000, MemoryTypeDdr4, 2133, 3200);
      Assert.True(device.IsInstalled);
      Assert.Equal(16384, device.SizeMegabytes);
      Assert.Equal("DDR4", device.MemoryType);
      Assert.Equal(2133, device.Speed);
      Assert.Equal(3200, device.ConfiguredSpeed);
      Assert.Equal(2, device.Rank);
      Assert.Equal(1350, device.ConfiguredVoltage);
      Assert.Equal("DIMM-A2", device.DeviceLocator);
      Assert.Equal("Corsair", device.ManufacturerName);
    }

    [Fact]
    public void UsesExtendedFieldsWhenTheWordFieldsSaturate() {
      // SMBIOS 3.3: 0x7FFF size and 0xFFFF speeds redirect to 32-bit fields.
      SMBIOS.MemoryDevice device = Device(0x7FFF, MemoryTypeDdr5, 0xFFFF,
        0xFFFF, extendedSize: 65536, extendedSpeed: 5600,
        extendedConfiguredSpeed: 8000);
      Assert.Equal(65536, device.SizeMegabytes);
      Assert.Equal("DDR5", device.MemoryType);
      Assert.Equal(5600, device.Speed);
      Assert.Equal(8000, device.ConfiguredSpeed);
    }

    [Fact]
    public void HonoursKilobyteGranularity() {
      SMBIOS.MemoryDevice device = Device(0x8000 | 4096, MemoryTypeDdr4, 0, 0);
      Assert.Equal(4, device.SizeMegabytes);
    }

    [Fact]
    public void RecognisesAnEmptySlot() {
      SMBIOS.MemoryDevice device = Device(0, 0x02, 0, 0);
      Assert.False(device.IsInstalled);
    }

    [Fact]
    public void ToleratesAShortSmbios2Structure() {
      // A 2.3-era structure ends before the configured speed and voltage.
      SMBIOS.MemoryDevice device = Device(0x2000, MemoryTypeDdr4, 1600, 0,
        length: 0x1B);
      Assert.Equal(8192, device.SizeMegabytes);
      Assert.Equal(1600, device.Speed);
      Assert.Equal(0, device.ConfiguredSpeed);
      Assert.Equal(0, device.ConfiguredVoltage);
    }

    [Fact]
    public void NamesAMatchedKit() {
      Assert.Equal("Corsair 32 GB DDR4-3200", RAMGroup.BuildName(new[] {
        Device(0x4000, MemoryTypeDdr4, 2133, 3200),
        Device(0x4000, MemoryTypeDdr4, 2133, 3200)
      }));
    }

    [Fact]
    public void OmitsWhateverAMixedKitDisagreesOn() {
      Assert.Equal("24 GB DDR4", RAMGroup.BuildName(new[] {
        Device(0x4000, MemoryTypeDdr4, 2133, 3200, manufacturer: "Corsair"),
        Device(0x2000, MemoryTypeDdr4, 2133, 3600, manufacturer: "Kingston")
      }));
    }

    [Fact]
    public void FallsBackWithoutModules() {
      Assert.Equal("Generic Memory", RAMGroup.BuildName(
        Array.Empty<SMBIOS.MemoryDevice>()));
    }
  }
}
