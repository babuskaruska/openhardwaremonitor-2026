/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenHardwareMonitor.Hardware.HDD {

  /// <summary>
  /// Health and identity data for an NVMe drive, read through the Windows
  /// storage stack.
  ///
  /// The pre-existing SMART code speaks ATA: it issues SMART_RCV_DRIVE_DATA
  /// with the 0xB0 command and parses 12-byte ATA attribute records. NVMe
  /// devices cannot answer any of that, so every NVMe drive previously fell
  /// through to a "Generic Hard Disk" with nothing but a used-space bar, and
  /// drives without a mounted volume disappeared entirely.
  ///
  /// This uses IOCTL_STORAGE_QUERY_PROPERTY, which the storage driver
  /// services on the caller's behalf. Two consequences worth noting:
  ///  - The handle is opened with no access rights at all, which is
  ///    sufficient for a property query and, unlike the ATA path's
  ///    GENERIC_READ | GENERIC_WRITE, does not require elevation.
  ///  - No pass-through command is issued, so this cannot be used to send
  ///    arbitrary commands to the device.
  /// </summary>
  internal sealed class NVMeDevice : IDisposable {

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint PropertyStandardQuery = 0;

    private const uint ProtocolTypeNvme = 3;
    private const uint NVMeDataTypeIdentify = 1;
    private const uint NVMeDataTypeLogPage = 2;

    private const uint NVMeIdentifyCnsController = 1;
    private const uint NVMeLogPageHealthInformation = 2;

    private const int ProtocolSpecificDataSize = 40;
    private const int DataDescriptorHeaderSize = 8;
    private const int PayloadOffset =
      DataDescriptorHeaderSize + ProtocolSpecificDataSize;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery {
      public uint PropertyId;
      public uint QueryType;
      public uint ProtocolType;
      public uint DataType;
      public uint ProtocolDataRequestValue;
      public uint ProtocolDataRequestSubValue;
      public uint ProtocolDataOffset;
      public uint ProtocolDataLength;
      public uint FixedProtocolReturnData;
      public uint ProtocolDataRequestSubValue2;
      public uint ProtocolDataRequestSubValue3;
      public uint ProtocolDataRequestSubValue4;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName,
      uint desiredAccess, uint shareMode, IntPtr securityAttributes,
      uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device,
      uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
      IntPtr outBuffer, uint outBufferSize, out uint bytesReturned,
      IntPtr overlapped);

    private readonly SafeFileHandle handle;

    private NVMeDevice(SafeFileHandle handle) {
      this.handle = handle;
    }

    /// <summary>
    /// Opens physical drive <paramref name="index"/>, or returns null if it
    /// does not exist or does not answer NVMe property queries.
    /// </summary>
    public static NVMeDevice? Open(int index) {
      const uint OPEN_EXISTING = 3;
      const uint FILE_SHARE_READ_WRITE = 3;

      SafeFileHandle handle = CreateFile(@"\\.\PhysicalDrive" + index,
        0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

      if (handle.IsInvalid) {
        handle.Dispose();
        return null;
      }

      NVMeDevice device = new NVMeDevice(handle);
      if (device.TryReadIdentifyController() == null) {
        device.Dispose();
        return null;
      }
      return device;
    }

    /// <summary>Identify Controller data (4096 bytes), or null.</summary>
    public byte[]? TryReadIdentifyController() {
      return Query(NVMeDataTypeIdentify, NVMeIdentifyCnsController, 4096);
    }

    /// <summary>SMART / Health Information log page (512 bytes), or null.</summary>
    public byte[]? TryReadHealthLog() {
      return Query(NVMeDataTypeLogPage, NVMeLogPageHealthInformation, 512);
    }

    private byte[]? Query(uint dataType, uint requestValue, int payloadSize) {
      int bufferSize = PayloadOffset + payloadSize;
      IntPtr buffer = IntPtr.Zero;

      try {
        buffer = Marshal.AllocHGlobal(bufferSize);

        // Zero the whole buffer: the storage driver reads reserved fields.
        for (int i = 0; i < bufferSize; i++)
          Marshal.WriteByte(buffer, i, 0);

        StoragePropertyQuery query = new StoragePropertyQuery {
          PropertyId = StorageDeviceProtocolSpecificProperty,
          QueryType = PropertyStandardQuery,
          ProtocolType = ProtocolTypeNvme,
          DataType = dataType,
          ProtocolDataRequestValue = requestValue,
          ProtocolDataRequestSubValue = 0,
          ProtocolDataOffset = ProtocolSpecificDataSize,
          ProtocolDataLength = (uint)payloadSize
        };
        Marshal.StructureToPtr(query, buffer, false);

        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
          buffer, (uint)bufferSize, buffer, (uint)bufferSize,
          out uint returned, IntPtr.Zero))
          return null;

        if (returned < PayloadOffset + payloadSize)
          return null;

        byte[] payload = new byte[payloadSize];
        Marshal.Copy(buffer + PayloadOffset, payload, 0, payloadSize);
        return payload;
      } catch (Exception) {
        return null;
      } finally {
        if (buffer != IntPtr.Zero)
          Marshal.FreeHGlobal(buffer);
      }
    }

    /// <summary>
    /// Reads a fixed-length ASCII field from Identify Controller data. These
    /// are space-padded rather than null-terminated.
    /// </summary>
    public static string GetAsciiField(byte[] data, int offset, int length) {
      if (data == null || offset + length > data.Length)
        return string.Empty;
      return Encoding.ASCII.GetString(data, offset, length)
        .Trim('\0', ' ');
    }

    public void Dispose() {
      handle.Dispose();
    }
  }
}
