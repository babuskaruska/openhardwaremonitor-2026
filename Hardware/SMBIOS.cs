/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenHardwareMonitor.Hardware {

  internal class SMBIOS {

    private readonly byte[] raw;
    private readonly Structure[] table;

    private readonly Version version;
    private readonly BIOSInformation biosInformation;
    private readonly SystemInformation systemInformation;
    private readonly BaseBoardInformation baseBoardInformation;
    private readonly ProcessorInformation processorInformation;
    private readonly MemoryDevice[] memoryDevices;

    private static string ReadSysFS(string path) {
      try {
        if (File.Exists(path)) {
          using (StreamReader reader = new StreamReader(path))
            return reader.ReadLine();
        } else {
          return null;
        }
      } catch {
        return null;
      }
    }

    public SMBIOS() {
      if (OperatingSystem.IsUnix) {
        this.raw = null;
        this.table = null;

        string boardVendor = ReadSysFS("/sys/class/dmi/id/board_vendor");
        string boardName = ReadSysFS("/sys/class/dmi/id/board_name");
        string boardVersion = ReadSysFS("/sys/class/dmi/id/board_version");
        this.baseBoardInformation = new BaseBoardInformation(
          boardVendor, boardName, boardVersion, null);

        string systemVendor = ReadSysFS("/sys/class/dmi/id/sys_vendor");
        string productName = ReadSysFS("/sys/class/dmi/id/product_name");
        string productVersion = ReadSysFS("/sys/class/dmi/id/product_version");
        this.systemInformation = new SystemInformation(systemVendor,
          productName, productVersion, null, null);

        string biosVendor = ReadSysFS("/sys/class/dmi/id/bios_vendor");
        string biosVersion = ReadSysFS("/sys/class/dmi/id/bios_version");
        this.biosInformation = new BIOSInformation(biosVendor, biosVersion);

        this.memoryDevices = new MemoryDevice[0];
      } else {
        List<Structure> structureList = new List<Structure>();
        List<MemoryDevice> memoryDeviceList = new List<MemoryDevice>();

        raw = ReadRawTable(out version);

        if (raw != null && raw.Length > 0) {
          int offset = 0;
          byte type = raw[offset];
          while (offset + 4 < raw.Length && type != 127) {

            type = raw[offset];
            int length = raw[offset + 1];
            // Handles are little-endian words; this used to be read big-endian.
            ushort handle = (ushort)(raw[offset + 2] | (raw[offset + 3] << 8));

            // Every structure is at least its 4-byte header. A shorter length
            // means a corrupt table, and walking on would parse garbage.
            if (length < 4 || offset + length > raw.Length)
              break;
            byte[] data = new byte[length];
            Array.Copy(raw, offset, data, 0, length);
            offset += length;

            List<string> stringsList = new List<string>();
            if (offset < raw.Length && raw[offset] == 0)
              offset++;

            while (offset < raw.Length && raw[offset] != 0) {
              StringBuilder sb = new StringBuilder();
              while (offset < raw.Length && raw[offset] != 0) {
                sb.Append((char)raw[offset]); offset++;
              }
              offset++;
              stringsList.Add(sb.ToString());
            }
            offset++;
            switch (type) {
              case 0x00:
                this.biosInformation = new BIOSInformation(
                  type, handle, data, stringsList.ToArray());
                structureList.Add(this.biosInformation); break;
              case 0x01:
                this.systemInformation = new SystemInformation(
                  type, handle, data, stringsList.ToArray());
                structureList.Add(this.systemInformation); break;
              case 0x02: this.baseBoardInformation = new BaseBoardInformation(
                  type, handle, data, stringsList.ToArray());
                structureList.Add(this.baseBoardInformation); break;
              case 0x04: this.processorInformation = new ProcessorInformation(
                  type, handle, data, stringsList.ToArray());
                structureList.Add(this.processorInformation); break;
              case 0x11: MemoryDevice m = new MemoryDevice(
                  type, handle, data, stringsList.ToArray());
                memoryDeviceList.Add(m);
                structureList.Add(m); break;
              default: structureList.Add(new Structure(
                type, handle, data, stringsList.ToArray())); break;
            }
          }
        }

        memoryDevices = memoryDeviceList.ToArray();
        table = structureList.ToArray();
      }
    }

    /// <summary>
    /// Reads the raw SMBIOS table through GetSystemFirmwareTable('RSMB').
    ///
    /// This used to query WMI (MSSMBios_RawSMBiosTables), which returns the
    /// same bytes but needs the WMI service running and costs roughly 100 ms at
    /// startup. The firmware table call has neither problem. Its buffer starts
    /// with an 8-byte RawSMBIOSData header: calling method, major version,
    /// minor version, DMI revision, then a 32-bit length of the table proper.
    /// </summary>
    private static byte[] ReadRawTable(out Version version) {
      const int HeaderSize = 8;
      version = null;

      byte[] firmware = FirmwareTable.GetTable(FirmwareTable.Provider.RSMB, 0);
      if (firmware == null || firmware.Length < HeaderSize)
        return null;

      byte majorVersion = firmware[1];
      byte minorVersion = firmware[2];
      if (majorVersion > 0 || minorVersion > 0)
        version = new Version(majorVersion, minorVersion);

      uint declaredLength = BitConverter.ToUInt32(firmware, 4);
      int length = (int)Math.Min(declaredLength, (uint)(firmware.Length - HeaderSize));
      byte[] data = new byte[length];
      Array.Copy(firmware, HeaderSize, data, 0, length);
      return data;
    }

    public string GetReport() {
      StringBuilder r = new StringBuilder();

      if (version != null) {
        r.Append("SMBIOS Version: "); r.AppendLine(version.ToString(2));
        r.AppendLine();
      }

      if (BIOS != null) {
        r.Append("BIOS Vendor: "); r.AppendLine(BIOS.Vendor);
        r.Append("BIOS Version: "); r.AppendLine(BIOS.Version);
        r.AppendLine();
      }

      if (System != null) {
        r.Append("System Manufacturer: ");
        r.AppendLine(System.ManufacturerName);
        r.Append("System Name: ");
        r.AppendLine(System.ProductName);
        r.Append("System Version: ");
        r.AppendLine(System.Version);
        r.AppendLine();
      }

      if (Board != null) {
        r.Append("Mainboard Manufacturer: ");
        r.AppendLine(Board.ManufacturerName);
        r.Append("Mainboard Name: ");
        r.AppendLine(Board.ProductName);
        r.Append("Mainboard Version: ");
        r.AppendLine(Board.Version);
        r.AppendLine();
      }

      if (Processor != null) {
        r.Append("Processor Manufacturer: ");
        r.AppendLine(Processor.ManufacturerName);
        r.Append("Processor Version: ");
        r.AppendLine(Processor.Version);
        r.Append("Processor Core Count: ");
        r.AppendLine(Processor.CoreCount.ToString());
        r.Append("Processor Core Enabled: ");
        r.AppendLine(Processor.CoreEnabled.ToString());
        r.Append("Processor Thread Count: ");
        r.AppendLine(Processor.ThreadCount.ToString());
        r.Append("Processor External Clock: ");
        r.Append(Processor.ExternalClock);
        r.AppendLine(" Mhz");
        r.AppendLine();
      }

      for (int i = 0; i < MemoryDevices.Length; i++) {
        MemoryDevice device = MemoryDevices[i];
        string prefix = "Memory Device [" + i + "] ";
        r.Append(prefix + "Device Locator: ");
        r.AppendLine(device.DeviceLocator);
        r.Append(prefix + "Bank Locator: ");
        r.AppendLine(device.BankLocator);
        if (!device.IsInstalled) {
          r.AppendLine(prefix + "Size: (empty slot)");
          r.AppendLine();
          continue;
        }
        r.Append(prefix + "Manufacturer: ");
        r.AppendLine(device.ManufacturerName);
        r.Append(prefix + "Part Number: ");
        r.AppendLine(device.PartNumber);
        r.Append(prefix + "Type: ");
        r.AppendLine(device.MemoryType);
        r.Append(prefix + "Size: ");
        r.Append(device.SizeMegabytes);
        r.AppendLine(" MB");
        r.Append(prefix + "Rank: ");
        r.AppendLine(device.Rank.ToString(CultureInfo.InvariantCulture));
        r.Append(prefix + "Speed: ");
        r.Append(device.Speed);
        r.AppendLine(" MT/s");
        r.Append(prefix + "Configured Speed: ");
        r.Append(device.ConfiguredSpeed);
        r.AppendLine(" MT/s");
        if (device.ConfiguredVoltage > 0) {
          r.Append(prefix + "Configured Voltage: ");
          r.Append(device.ConfiguredVoltage);
          r.AppendLine(" mV");
        }
        r.AppendLine();
      }

      if (raw != null) {
        string base64 = Convert.ToBase64String(raw);
        r.AppendLine("SMBIOS Table");
        r.AppendLine();

        for (int i = 0; i < Math.Ceiling(base64.Length / 64.0); i++) {
          r.Append(" ");
          for (int j = 0; j < 0x40; j++) {
            int index = (i << 6) | j;
            if (index < base64.Length) {
              r.Append(base64[index]);
            }
          }
          r.AppendLine();
        }
        r.AppendLine();
      }

      return r.ToString();
    }

    public BIOSInformation BIOS {
      get { return biosInformation; }
    }

    public SystemInformation System {
      get { return systemInformation; }
    }

    public BaseBoardInformation Board {
      get { return baseBoardInformation; }
    }


    public ProcessorInformation Processor {
      get { return processorInformation; }
    }

    public MemoryDevice[] MemoryDevices {
      get { return memoryDevices; }
    }

    public class Structure {
      private readonly byte type;
      private readonly ushort handle;

      private readonly byte[] data;
      private readonly string[] strings;

      protected int GetByte(int offset) {
        if (offset < data.Length && offset >= 0)
          return data[offset];
        else
          return 0;
      }

      protected int GetWord(int offset) {
        if (offset + 1 < data.Length && offset >= 0)
          return (data[offset + 1] << 8) | data[offset];
        else
          return 0;
      }

      protected long GetDWord(int offset) {
        if (offset + 3 < data.Length && offset >= 0)
          return BitConverter.ToUInt32(data, offset);
        else
          return 0;
      }

      protected string GetString(int offset) {
        if (offset < data.Length && data[offset] > 0 &&
         data[offset] <= strings.Length)
          return strings[data[offset] - 1];
        else
          return "";
      }

      public Structure(byte type, ushort handle, byte[] data, string[] strings)
      {
        this.type = type;
        this.handle = handle;
        this.data = data;
        this.strings = strings;
      }

      public byte Type { get { return type; } }

      public ushort Handle { get { return handle; } }
    }

    public class BIOSInformation : Structure {

      private readonly string vendor;
      private readonly string version;

      public BIOSInformation(string vendor, string version)
        : base (0x00, 0, null, null)
      {
        this.vendor = vendor;
        this.version = version;
      }

      public BIOSInformation(byte type, ushort handle, byte[] data,
        string[] strings)
        : base(type, handle, data, strings)
      {
        this.vendor = GetString(0x04);
        this.version = GetString(0x05);
      }

      public string Vendor { get { return vendor; } }

      public string Version { get { return version; } }
    }

    public class SystemInformation : Structure {

      private readonly string manufacturerName;
      private readonly string productName;
      private readonly string version;
      private readonly string serialNumber;
      private readonly string family;

      public SystemInformation(string manufacturerName, string productName,
        string version, string serialNumber, string family)
        : base (0x01, 0, null, null)
      {
        this.manufacturerName = manufacturerName;
        this.productName = productName;
        this.version = version;
        this.serialNumber = serialNumber;
        this.family = family;
      }

      public SystemInformation(byte type, ushort handle, byte[] data,
        string[] strings)
        : base(type, handle, data, strings)
      {
        this.manufacturerName = GetString(0x04);
        this.productName = GetString(0x05);
        this.version = GetString(0x06);
        this.serialNumber = GetString(0x07);
        this.family = GetString(0x1A);
      }

      public string ManufacturerName { get { return manufacturerName; } }

      public string ProductName { get { return productName; } }

      public string Version { get { return version; } }

      public string SerialNumber { get { return serialNumber; } }

      public string Family { get { return family; } }

    }

    public class BaseBoardInformation : Structure {

      private readonly string manufacturerName;
      private readonly string productName;
      private readonly string version;
      private readonly string serialNumber;

      public BaseBoardInformation(string manufacturerName, string productName,
        string version, string serialNumber)
        : base(0x02, 0, null, null)
      {
        this.manufacturerName = manufacturerName;
        this.productName = productName;
        this.version = version;
        this.serialNumber = serialNumber;
      }

      public BaseBoardInformation(byte type, ushort handle, byte[] data,
        string[] strings)
        : base(type, handle, data, strings) {

        this.manufacturerName = GetString(0x04).Trim();
        this.productName = GetString(0x05).Trim();
        this.version = GetString(0x06).Trim();
        this.serialNumber = GetString(0x07).Trim();
      }

      public string ManufacturerName { get { return manufacturerName; } }

      public string ProductName { get { return productName; } }

      public string Version { get { return version; } }

      public string SerialNumber { get { return serialNumber; } }

    }

    public class ProcessorInformation : Structure {

      public ProcessorInformation(byte type, ushort handle, byte[] data,
        string[] strings)
        : base(type, handle, data, strings)
      {
        this.ManufacturerName = GetString(0x07).Trim();
        this.Version = GetString(0x10).Trim();
        this.CoreCount = GetByte(0x23);
        this.CoreEnabled = GetByte(0x24);
        this.ThreadCount = GetByte(0x25);
        this.ExternalClock = GetWord(0x12);
      }

      public string ManufacturerName { get; private set; }

      public string Version { get; private set; }

      public int CoreCount { get; private set; }

      public int CoreEnabled { get; private set; }

      public int ThreadCount { get; private set; }

      public int ExternalClock { get; private set; }
    }

    /// <summary>
    /// SMBIOS type 17, Memory Device.
    ///
    /// Previously only the strings and the 16-bit rated speed were read, so
    /// the application could not tell DDR4 from DDR5, did not know how large a
    /// module was, reported the JEDEC rated speed rather than the XMP/EXPO speed
    /// the memory actually runs at, and listed empty slots as if populated.
    /// Offsets are from the DMTF SMBIOS 3.x specification.
    /// </summary>
    public class MemoryDevice : Structure {

      public MemoryDevice(byte type, ushort handle, byte[] data,
        string[] strings)
        : base(type, handle, data, strings)
      {
        DeviceLocator = GetString(0x10).Trim();
        BankLocator = GetString(0x11).Trim();
        ManufacturerName = GetString(0x17).Trim();
        SerialNumber = GetString(0x18).Trim();
        PartNumber = GetString(0x1A).Trim();
        MemoryTypeCode = GetByte(0x12);
        FormFactorCode = GetByte(0x0E);
        Rank = GetByte(0x1B) & 0x0F;
        Speed = ReadSpeed(0x15, 0x54);
        ConfiguredSpeed = ReadSpeed(0x20, 0x58);
        ConfiguredVoltage = GetWord(0x26);
        SizeMegabytes = ReadSizeMegabytes();
      }

      /// <summary>
      /// The 16-bit speed fields saturate; SMBIOS 3.3 sets them to 0xFFFF and
      /// moves the real value into a 32-bit extended field.
      /// </summary>
      private int ReadSpeed(int wordOffset, int extendedOffset) {
        int speed = GetWord(wordOffset);
        if (speed == 0xFFFF)
          speed = (int)(GetDWord(extendedOffset) & 0x7FFFFFFF);
        return speed;
      }

      private long ReadSizeMegabytes() {
        int size = GetWord(0x0C);
        if (size == 0 || size == 0xFFFF)
          return 0;                                  // empty slot, or unknown
        if (size == 0x7FFF)
          return GetDWord(0x1C) & 0x7FFFFFFF;        // extended size, in MB
        if ((size & 0x8000) != 0)
          return (size & 0x7FFF) / 1024;             // granularity is KB
        return size;                                 // granularity is MB
      }

      public string DeviceLocator { get; private set; }

      public string BankLocator { get; private set; }

      public string ManufacturerName { get; private set; }

      public string SerialNumber { get; private set; }

      public string PartNumber { get; private set; }

      /// <summary>Rated (JEDEC) speed in MT/s.</summary>
      public int Speed { get; private set; }

      /// <summary>Speed the module is configured to run at, in MT/s.</summary>
      public int ConfiguredSpeed { get; private set; }

      /// <summary>Configured voltage in millivolts, or 0 if not reported.</summary>
      public int ConfiguredVoltage { get; private set; }

      public int Rank { get; private set; }

      public int MemoryTypeCode { get; private set; }

      public int FormFactorCode { get; private set; }

      public long SizeMegabytes { get; private set; }

      /// <summary>False for an empty slot.</summary>
      public bool IsInstalled {
        get { return SizeMegabytes > 0; }
      }

      /// <summary>Memory technology name, e.g. "DDR5", or "" if unknown.</summary>
      public string MemoryType {
        get { return GetMemoryTypeName(MemoryTypeCode); }
      }

      private static string GetMemoryTypeName(int code) {
        switch (code) {
          case 0x12: return "DDR";
          case 0x13: return "DDR2";
          case 0x14: return "DDR2 FB-DIMM";
          case 0x18: return "DDR3";
          case 0x1A: return "DDR4";
          case 0x1B: return "LPDDR";
          case 0x1C: return "LPDDR2";
          case 0x1D: return "LPDDR3";
          case 0x1E: return "LPDDR4";
          case 0x20: return "HBM";
          case 0x21: return "HBM2";
          case 0x22: return "DDR5";
          case 0x23: return "LPDDR5";
          case 0x24: return "HBM3";
          default: return "";
        }
      }
    }
  }
}
