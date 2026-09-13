/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using OpenHardwareMonitor.Hardware.LowLevel;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// The LPC bus as the Super I/O code sees it: chips claim ports, and a read
  /// nobody claims floats high.
  /// </summary>
  internal sealed class SimulatedIsaBus {

    public List<FakeSuperIoChip> Chips { get; } = new List<FakeSuperIoChip>();

    /// <summary>Every byte written to any port, in order.</summary>
    public List<(ushort Port, byte Value)> Writes { get; } =
      new List<(ushort Port, byte Value)>();

    public byte In(ushort port) {
      foreach (FakeSuperIoChip chip in Chips) {
        if (chip.Claims(port))
          return chip.Read(port);
      }
      return 0xFF;
    }

    public void Out(ushort port, byte value) {
      Writes.Add((port, value));
      foreach (FakeSuperIoChip chip in Chips) {
        if (chip.Claims(port))
          chip.Write(port, value);
      }
    }
  }

  /// <summary>
  /// A Super I/O chip that answers its configuration ports the way ITE and
  /// Nuvoton parts do, and exposes the index/data pair of its hardware monitor
  /// BAR at base+5 and base+6.
  /// </summary>
  internal sealed class FakeSuperIoChip {

    private readonly byte[] entryKey;
    // Winbond, Nuvoton and Fintek leave configuration mode when 0xAA is
    // written to the index port; ITE when bit 1 of register 0x02 is set.
    private readonly bool exitsOnAA;
    // Nuvoton selects a bank through monitor register 0x4E.
    private readonly bool bankedMonitor;
    private readonly byte[] globalRegisters = new byte[0x30];
    private readonly Dictionary<byte, byte[]> deviceRegisters =
      new Dictionary<byte, byte[]>();
    private readonly Dictionary<int, byte> monitorRegisters =
      new Dictionary<int, byte>();
    private int keyProgress;
    private byte index;
    private byte monitorIndex;
    private byte monitorBank;

    private FakeSuperIoChip(ushort registerPort, byte[] entryKey,
      bool exitsOnAA, bool bankedMonitor, ushort monitorBase) {
      RegisterPort = registerPort;
      MonitorBase = monitorBase;
      this.entryKey = entryKey;
      this.exitsOnAA = exitsOnAA;
      this.bankedMonitor = bankedMonitor;
    }

    /// <summary>
    /// An ITE chip with its environment controller at LDN 0x04 (register
    /// 0x60) and GPIO at LDN 0x07 (register 0x62).
    /// </summary>
    public static FakeSuperIoChip Ite(ushort registerPort, ushort chipId,
      ushort environmentControllerBase, ushort gpioBase) {
      byte[] key = registerPort == 0x4E
        ? new byte[] { 0x87, 0x01, 0x55, 0xAA }
        : new byte[] { 0x87, 0x01, 0x55, 0x55 };
      FakeSuperIoChip chip = new FakeSuperIoChip(registerPort, key, false,
        false, environmentControllerBase);
      chip.globalRegisters[0x20] = (byte)(chipId >> 8);
      chip.globalRegisters[0x21] = (byte)chipId;
      chip.globalRegisters[0x22] = 0x01;
      chip.SetBaseAddress(0x04, 0x60, environmentControllerBase);
      chip.SetBaseAddress(0x07, 0x62, gpioBase);
      chip.GpioBase = gpioBase;
      chip.SetMonitorRegister(0x58, 0x90); // ITE vendor ID
      chip.SetMonitorRegister(0x00, 0x10); // configuration, bit 4 always set
      return chip;
    }

    /// <summary>A Nuvoton chip with its hardware monitor at LDN 0x0B.</summary>
    public static FakeSuperIoChip Nuvoton(ushort registerPort, byte chipId,
      byte revision, ushort hardwareMonitorBase) {
      FakeSuperIoChip chip = new FakeSuperIoChip(registerPort,
        new byte[] { 0x87, 0x87 }, true, true, hardwareMonitorBase);
      chip.globalRegisters[0x20] = chipId;
      chip.globalRegisters[0x21] = revision;
      chip.SetBaseAddress(0x0B, 0x60, hardwareMonitorBase);
      chip.SetMonitorRegister(0x804F, 0x5C); // vendor ID 0x5CA3, high byte
      chip.SetMonitorRegister(0x004F, 0xA3); // and low byte
      return chip;
    }

    public ushort RegisterPort { get; }
    public ushort ValuePort { get { return (ushort)(RegisterPort + 1); } }
    public ushort MonitorBase { get; }
    public ushort GpioBase { get; private set; }
    public bool InConfigMode { get; private set; }
    public byte SelectedDevice { get; private set; }
    public int ConfigEntries { get; private set; }
    public int ConfigExits { get; private set; }

    /// <param name="address">(bank &lt;&lt; 8) | register.</param>
    public void SetMonitorRegister(int address, byte value) {
      monitorRegisters[address] = value;
    }

    public byte GetMonitorRegister(int address) {
      return monitorRegisters.TryGetValue(address, out byte value)
        ? value : (byte)0;
    }

    public bool Claims(ushort port) {
      int window = port & 0xFFF8;
      return port == RegisterPort || port == ValuePort ||
        window == MonitorBase || (GpioBase != 0 && window == GpioBase);
    }

    public byte Read(ushort port) {
      if (port == RegisterPort)
        return InConfigMode ? index : (byte)0xFF;
      if (port == ValuePort)
        return InConfigMode ? ReadConfig(index) : (byte)0xFF;
      if (port == MonitorBase + 5)
        return monitorIndex;
      if (port == MonitorBase + 6) {
        if (bankedMonitor && monitorIndex == 0x4E)
          return monitorBank;
        return GetMonitorRegister((monitorBank << 8) | monitorIndex);
      }
      return 0x00;
    }

    public void Write(ushort port, byte value) {
      if (port == RegisterPort) {
        WriteIndex(value);
      } else if (port == ValuePort) {
        if (InConfigMode)
          WriteConfig(index, value);
      } else if (port == MonitorBase + 5) {
        monitorIndex = value;
      } else if (port == MonitorBase + 6) {
        if (bankedMonitor && monitorIndex == 0x4E)
          monitorBank = value;
        else
          SetMonitorRegister((monitorBank << 8) | monitorIndex, value);
      }
    }

    private void WriteIndex(byte value) {
      if (InConfigMode) {
        if (exitsOnAA && value == 0xAA) {
          InConfigMode = false;
          ConfigExits++;
        } else {
          index = value;
        }
        return;
      }

      if (value == entryKey[keyProgress])
        keyProgress++;
      else
        keyProgress = value == entryKey[0] ? 1 : 0;

      if (keyProgress == entryKey.Length) {
        keyProgress = 0;
        InConfigMode = true;
        ConfigEntries++;
      }
    }

    private byte ReadConfig(byte register) {
      if (register == 0x07)
        return SelectedDevice;
      if (register < 0x30)
        return globalRegisters[register];
      return deviceRegisters.TryGetValue(SelectedDevice, out byte[]? values)
        ? values[register] : (byte)0x00;
    }

    private void WriteConfig(byte register, byte value) {
      if (register == 0x07) {
        SelectedDevice = value;
      } else if (register == 0x02 && !exitsOnAA) {
        if ((value & 0x02) != 0) {
          InConfigMode = false;
          ConfigExits++;
        }
      } else if (register < 0x30) {
        globalRegisters[register] = value;
      } else {
        Device(SelectedDevice)[register] = value;
      }
    }

    private void SetBaseAddress(byte device, byte register, ushort address) {
      byte[] values = Device(device);
      values[register] = (byte)(address >> 8);
      values[register + 1] = (byte)address;
    }

    private byte[] Device(byte device) {
      if (!deviceRegisters.TryGetValue(device, out byte[]? values)) {
        values = new byte[0x100];
        deviceRegisters[device] = values;
      }
      return values;
    }
  }

  /// <summary>
  /// LpcIO.p re-implemented over a <see cref="SimulatedIsaBus"/>, including
  /// the rules that make it awkward to use: exact buffer sizes, slot and BAR
  /// state kept per instance and reset by selecting a slot, find_bars
  /// requiring configuration mode, and the port whitelist.
  /// </summary>
  internal sealed class SimulatedLpcIoModule : IPawnIoModule {

    // HRESULT_FROM_NT of the NTSTATUS values LpcIO.p returns.
    public const int InvalidParameter = unchecked((int)0xD000000D);
    public const int AccessDenied = unchecked((int)0xD0000022);
    public const int DeviceNotReady = unchecked((int)0xD00000A3);
    public const int NotFound = unchecked((int)0xD0000225);

    private readonly SimulatedIsaBus bus;
    private readonly List<ushort> bars = new List<ushort>();
    private ushort registerPort;

    public SimulatedLpcIoModule(SimulatedIsaBus bus) {
      this.bus = bus;
    }

    public IReadOnlyList<ushort> Bars { get { return bars; } }
    public List<string> Calls { get; } = new List<string>();
    public int DeniedCount { get; private set; }
    public int InvalidParameterCount { get; private set; }
    public bool IsDisposed { get; private set; }

    public bool Execute(string function, ulong[] input, ulong[] output,
      out int hresult) {
      if (IsDisposed)
        throw new ObjectDisposedException(nameof(SimulatedLpcIoModule));

      Calls.Add(function);
      hresult = Dispatch(function, input, output);
      if (hresult == AccessDenied)
        DeniedCount++;
      if (hresult == InvalidParameter)
        InvalidParameterCount++;
      return hresult >= 0;
    }

    public void Dispose() {
      IsDisposed = true;
    }

    private int Dispatch(string function, ulong[] input, ulong[] output) {
      switch (function) {
        case "ioctl_select_slot":
          if (input.Length != 1 || output.Length != 0)
            return InvalidParameter;
          registerPort = 0;
          bars.Clear();
          if (input[0] == 0)
            registerPort = 0x2E;
          else if (input[0] == 1)
            registerPort = 0x4E;
          else
            return InvalidParameter;
          return 0;

        case "ioctl_find_bars":
          if (input.Length != 0 || output.Length != 0)
            return InvalidParameter;
          if (registerPort == 0)
            return DeviceNotReady;
          bars.Clear();
          return FindBars();

        case "ioctl_pio_inb": {
          if (input.Length != 1 || output.Length != 1)
            return InvalidParameter;
          ushort port = (ushort)(input[0] & 0xFFFF);
          if (registerPort == 0)
            return DeviceNotReady;
          if (!IsPortAllowed(port))
            return AccessDenied;
          output[0] = bus.In(port);
          return 0;
        }

        case "ioctl_pio_outb": {
          if (input.Length != 2 || output.Length != 0)
            return InvalidParameter;
          ushort port = (ushort)(input[0] & 0xFFFF);
          if (registerPort == 0)
            return DeviceNotReady;
          if (!IsPortAllowed(port))
            return AccessDenied;
          bus.Out(port, (byte)input[1]);
          return 0;
        }

        default:
          return NotFound;
      }
    }

    private int FindBars() {
      byte chipId = SuperIoRead(0x20);
      if (chipId == 0x00 || chipId == 0xFF)
        return NotFound;

      int[,] first = new int[0xFF, 2];
      for (int i = 0; i < 0xFF; i++) {
        if (Select(i)) {
          first[i, 0] = SuperIoReadWord(0x60);
          first[i, 1] = SuperIoReadWord(0x62);
        }
      }
      // The module sleeps for a millisecond here, so unstable values differ.
      for (int i = 0; i < 0xFF; i++) {
        if (Select(i)) {
          AddBar(first[i, 0], SuperIoReadWord(0x60));
          AddBar(first[i, 1], SuperIoReadWord(0x62));
        }
      }
      return 0;
    }

    private bool Select(int device) {
      SuperIoWrite(0x07, (byte)device);
      return SuperIoRead(0x07) == device;
    }

    private byte SuperIoRead(byte register) {
      bus.Out(registerPort, register);
      return bus.In((ushort)(registerPort + 1));
    }

    private void SuperIoWrite(byte register, byte value) {
      bus.Out(registerPort, register);
      bus.Out((ushort)(registerPort + 1), value);
    }

    private int SuperIoReadWord(byte register) {
      return (SuperIoRead(register) << 8) | SuperIoRead((byte)(register + 1));
    }

    private void AddBar(int value, int verify) {
      if (value != verify || value == 0 || value == 0xFFFF)
        return;
      if (value < 0x100)
        return;
      if ((value & 0x07) == 0x05)
        value &= 0xFFF8;
      if (IsBlacklisted(value) || IsBlacklisted(value + 7))
        return;
      if (bars.Count > 0 && bars[^1] == value)
        return;
      if (bars.Count > 1 && bars[^2] == value)
        return;
      if (bars.Count >= 128)
        return;
      bars.Add((ushort)value);
    }

    private static bool IsBlacklisted(int port) {
      return port >= 0x0CF8 && port <= 0x0CFF;
    }

    private bool IsPortAllowed(ushort port) {
      if (IsBlacklisted(port))
        return false;
      if (port == registerPort || port == registerPort + 1)
        return true;
      if (port == 0x25C || port == 0x25D)
        return true;
      int window = port & 0xFFF8;
      foreach (ushort bar in bars) {
        if (bar == window)
          return true;
      }
      return false;
    }
  }
}
