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
using System.Text;

namespace OpenHardwareMonitor.Hardware.HDD {

  /// <summary>
  /// An NVMe solid state drive, reporting the standard Health Information log.
  ///
  /// Field offsets are from the NVM Express base specification, figure
  /// "SMART / Health Information Log Page". Counters are 128-bit; only the
  /// low 64 bits are read, which is ample for any real drive.
  /// </summary>
  internal sealed class NVMeDrive : Hardware {

    // Offsets within the 512-byte health log page.
    private const int OffsetCriticalWarning = 0;
    private const int OffsetCompositeTemperature = 1;
    private const int OffsetAvailableSpare = 3;
    private const int OffsetAvailableSpareThreshold = 4;
    private const int OffsetPercentageUsed = 5;
    private const int OffsetDataUnitsRead = 32;
    private const int OffsetDataUnitsWritten = 48;
    private const int OffsetPowerCycles = 112;
    private const int OffsetPowerOnHours = 128;
    private const int OffsetUnsafeShutdowns = 144;
    private const int OffsetMediaErrors = 160;
    private const int OffsetTemperatureSensor1 = 200;

    private const int TemperatureSensorCount = 8;

    // A data unit is 1000 * 512 bytes per the specification.
    private const double BytesPerDataUnit = 1000.0 * 512.0;
    private const double BytesPerGigabyte = 1024.0 * 1024.0 * 1024.0;

    private readonly int index;
    private readonly NVMeDevice device;
    private readonly DriveInfo[] driveInfos;

    private readonly Sensor temperature;
    private readonly Sensor[] temperatureSensors;
    private readonly Sensor availableSpare;
    private readonly Sensor remainingLife;
    private readonly Sensor dataRead;
    private readonly Sensor dataWritten;
    private readonly Sensor powerOnHours;
    private readonly Sensor powerCycles;
    private readonly Sensor unsafeShutdowns;
    private readonly Sensor? usedSpace;

    private int updateCounter;

    /// <summary>
    /// Health data changes slowly and each query round-trips to the drive,
    /// so it is polled every N updates rather than every tick.
    /// </summary>
    private const int UpdateDivider = 30;

    private NVMeDrive(NVMeDevice device, string name, string firmwareRevision,
      int index, string[] logicalDrives, ISettings settings)
      : base(name, new Identifier("nvme",
        index.ToString(CultureInfo.InvariantCulture)), settings) {

      this.device = device;
      this.index = index;
      this.FirmwareRevision = firmwareRevision;

      List<DriveInfo> infos = new List<DriveInfo>();
      foreach (string logicalDrive in logicalDrives) {
        try {
          DriveInfo info = new DriveInfo(logicalDrive);
          if (info.TotalSize > 0)
            infos.Add(info);
        } catch (ArgumentException) {
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
      }
      driveInfos = infos.ToArray();

      int i = 0;
      temperature = new Sensor("Temperature", i++, SensorType.Temperature,
        this, settings);

      temperatureSensors = new Sensor[TemperatureSensorCount];
      for (int s = 0; s < TemperatureSensorCount; s++) {
        temperatureSensors[s] = new Sensor("Temperature Sensor " + (s + 1),
          i++, SensorType.Temperature, this, settings);
      }

      availableSpare = new Sensor("Available Spare", 0, SensorType.Level,
        this, settings);
      remainingLife = new Sensor("Remaining Life", 1, SensorType.Level,
        this, settings);

      dataRead = new Sensor("Total Bytes Read", 0, SensorType.Data, this,
        settings);
      dataWritten = new Sensor("Total Bytes Written", 1, SensorType.Data,
        this, settings);

      powerOnHours = new Sensor("Power On Hours", 0, SensorType.Factor, this,
        settings);
      powerCycles = new Sensor("Power Cycles", 1, SensorType.Factor, this,
        settings);
      unsafeShutdowns = new Sensor("Unsafe Shutdowns", 2, SensorType.Factor,
        this, settings);

      if (driveInfos.Length > 0)
        usedSpace = new Sensor("Used Space", 0, SensorType.Load, this,
          settings);

      Update();
    }

    public string FirmwareRevision { get; }

    /// <summary>
    /// Creates a drive for the given physical drive index, or returns null if
    /// it is not an NVMe device.
    /// </summary>
    public static NVMeDrive? CreateInstance(ISmart smart, int index,
      ISettings settings) {

      NVMeDevice? device = NVMeDevice.Open(index);
      if (device == null)
        return null;

      byte[]? identify = device.TryReadIdentifyController();
      if (identify == null) {
        device.Dispose();
        return null;
      }

      // Identify Controller: serial number at 4, model at 24, firmware at 64.
      string model = NVMeDevice.GetAsciiField(identify, 24, 40);
      string firmware = NVMeDevice.GetAsciiField(identify, 64, 8);
      if (string.IsNullOrWhiteSpace(model))
        model = "NVMe Drive " + index;

      string[] logicalDrives;
      try {
        logicalDrives = smart.GetLogicalDrives(index);
      } catch (Exception) {
        logicalDrives = new string[0];
      }

      return new NVMeDrive(device, model.Trim(), firmware.Trim(), index,
        logicalDrives, settings);
    }

    public override HardwareType HardwareType {
      get { return HardwareType.HDD; }
    }

    private static ulong ReadUInt64(byte[] data, int offset) {
      if (offset + 8 > data.Length)
        return 0;
      return BitConverter.ToUInt64(data, offset);
    }

    /// <summary>
    /// Converts an NVMe temperature field (Kelvin) to Celsius, returning null
    /// for the zero value the specification uses to mean "not implemented".
    /// </summary>
    private static float? ToCelsius(ushort kelvin) {
      if (kelvin == 0)
        return null;
      float celsius = kelvin - 273.15f;
      // Guard against obviously broken firmware reporting nonsense.
      if (celsius < -40 || celsius > 200)
        return null;
      return celsius;
    }

    public override void Update() {
      UpdateUsedSpace();

      if (updateCounter > 0) {
        updateCounter--;
        return;
      }
      updateCounter = UpdateDivider;

      byte[]? log = device.TryReadHealthLog();
      if (log == null)
        return;

      float? composite = ToCelsius(BitConverter.ToUInt16(log,
        OffsetCompositeTemperature));
      if (composite.HasValue) {
        temperature.Value = composite;
        ActivateSensor(temperature);
      }

      for (int s = 0; s < TemperatureSensorCount; s++) {
        float? value = ToCelsius(BitConverter.ToUInt16(log,
          OffsetTemperatureSensor1 + s * 2));
        if (value.HasValue) {
          temperatureSensors[s].Value = value;
          ActivateSensor(temperatureSensors[s]);
        }
      }

      availableSpare.Value = log[OffsetAvailableSpare];
      ActivateSensor(availableSpare);

      // "Percentage used" is an endurance estimate that may exceed 100.
      remainingLife.Value = Math.Max(0, 100 - log[OffsetPercentageUsed]);
      ActivateSensor(remainingLife);

      dataRead.Value = (float)(ReadUInt64(log, OffsetDataUnitsRead) *
        BytesPerDataUnit / BytesPerGigabyte);
      ActivateSensor(dataRead);

      dataWritten.Value = (float)(ReadUInt64(log, OffsetDataUnitsWritten) *
        BytesPerDataUnit / BytesPerGigabyte);
      ActivateSensor(dataWritten);

      powerOnHours.Value = ReadUInt64(log, OffsetPowerOnHours);
      ActivateSensor(powerOnHours);

      powerCycles.Value = ReadUInt64(log, OffsetPowerCycles);
      ActivateSensor(powerCycles);

      unsafeShutdowns.Value = ReadUInt64(log, OffsetUnsafeShutdowns);
      ActivateSensor(unsafeShutdowns);
    }

    private void UpdateUsedSpace() {
      if (usedSpace == null)
        return;

      long totalSize = 0;
      long totalFree = 0;
      foreach (DriveInfo info in driveInfos) {
        try {
          if (!info.IsReady)
            continue;
          totalSize += info.TotalSize;
          totalFree += info.TotalFreeSpace;
        } catch (IOException) {
          continue;
        } catch (UnauthorizedAccessException) {
          continue;
        }
      }

      if (totalSize > 0) {
        usedSpace.Value = 100.0f * (totalSize - totalFree) / totalSize;
        ActivateSensor(usedSpace);
      }
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.AppendLine("NVMe Drive");
      r.AppendLine();
      r.AppendLine("Drive Index: " + index);
      r.AppendLine("Name: " + name);
      r.AppendLine("Firmware Revision: " + FirmwareRevision);
      r.AppendLine();

      byte[]? log = device.TryReadHealthLog();
      if (log == null) {
        r.AppendLine("Health log unavailable.");
        return r.ToString();
      }

      r.AppendLine("Critical Warning: 0x" +
        log[OffsetCriticalWarning].ToString("X2"));
      r.AppendLine("Available Spare: " + log[OffsetAvailableSpare] + " %");
      r.AppendLine("Available Spare Threshold: " +
        log[OffsetAvailableSpareThreshold] + " %");
      r.AppendLine("Percentage Used: " + log[OffsetPercentageUsed] + " %");
      r.AppendLine("Media Errors: " + ReadUInt64(log, OffsetMediaErrors));
      r.AppendLine();
      return r.ToString();
    }

    public override void Close() {
      device.Dispose();
      base.Close();
    }

    public void Traverse(IVisitor visitor) {
      foreach (ISensor sensor in Sensors)
        sensor.Accept(visitor);
    }
  }
}
