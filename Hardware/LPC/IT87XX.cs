/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
	
  IT8689E support: the channel counts, the VIN9 (0x2F), fan 6 (0x4C/0x4D)
  and PWM 6 (0xAF/0xAB) register tables and the fan tachometer enable
  detection are adapted from LibreHardwareMonitor (MPL-2.0),
  LibreHardwareMonitorLib/Hardware/Motherboard/Lpc/IT87XX.cs,
  Copyright (C) LibreHardwareMonitor and Contributors. The PWM 6 enable bit
  and the bank select field were cross-checked against the register
  documentation in the Linux it87 driver (github.com/frankcrawford/it87);
  no code was taken from it.

*/

using System.Globalization;
using System.Text;
using System;

namespace OpenHardwareMonitor.Hardware.LPC {
  internal class IT87XX : ISuperIO {
       
    /// <summary>
    /// Port access used by this class. Production code goes through Ring0;
    /// unit tests substitute an emulated environment controller.
    /// </summary>
    internal interface IPortAccess {
      byte ReadIoPort(ushort port);
      void WriteIoPort(ushort port, byte value);
      bool WaitIsaBusMutex(int millisecondsTimeout);
      void ReleaseIsaBusMutex();
    }

    private sealed class Ring0PortAccess : IPortAccess {
      public static readonly Ring0PortAccess Instance = new Ring0PortAccess();

      public byte ReadIoPort(ushort port) {
        return Ring0.ReadIoPort(port);
      }

      public void WriteIoPort(ushort port, byte value) {
        Ring0.WriteIoPort(port, value);
      }

      public bool WaitIsaBusMutex(int millisecondsTimeout) {
        return Ring0.WaitIsaBusMutex(millisecondsTimeout);
      }

      public void ReleaseIsaBusMutex() {
        Ring0.ReleaseIsaBusMutex();
      }
    }

    private const int MAX_FAN_HEADERS = 6;

    private readonly IPortAccess port;

    private readonly ushort address;
    private readonly Chip chip;
    private readonly byte version;

    private readonly ushort gpioAddress;
    private readonly int gpioCount;

    private readonly ushort addressReg;
    private readonly ushort dataReg;

    private readonly float?[] voltages = new float?[0];
    private readonly float?[] temperatures = new float?[0];
    private readonly float?[] fans = new float?[0];
    private readonly float?[] controls = new float?[0];

    // Channels the firmware left disabled. They are never read (fans) or
    // written (controls).
    private readonly bool[] fansDisabled = new bool[0];
    private readonly bool[] controlsDisabled = new bool[0];

    private readonly float voltageGain;
    private readonly bool has16bitFanCounter;
   
    // 8-bit PWM duty lives in FAN_PWM_CTRL_EXT_REG instead of the 7-bit
    // field of FAN_PWM_CTRL_REG.
    private readonly bool hasExtReg;

    // Registers 0x10-0xAF are banked; bits 5-6 of BANK_REGISTER select the
    // bank. Only bank 0 holds the monitoring and fan control registers.
    private readonly bool hasBankSelect;

    // Consts
    private const byte ITE_VENDOR_ID = 0x90;
       
    // Environment Controller
    private const byte ADDRESS_REGISTER_OFFSET = 0x05;
    private const byte DATA_REGISTER_OFFSET = 0x06;

    // Environment Controller Registers    
    private const byte CONFIGURATION_REGISTER = 0x00;
    private const byte BANK_REGISTER = 0x06;
    private const byte BANK_SELECT_MASK = 0x60;
    private const byte TEMPERATURE_BASE_REG = 0x29;
    private const byte VENDOR_ID_REGISTER = 0x58;
    private const byte FAN_TACHOMETER_DIVISOR_REGISTER = 0x0B;
    private const byte FAN_TACHOMETER_16BIT_REGISTER = 0x0C;
    // VIN0-VIN8 are contiguous; VIN9 (AVCC3) sits after the temperatures.
    private readonly byte[] VOLTAGE_REG =
      { 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2f };
    private readonly byte[] FAN_TACHOMETER_REG = 
      { 0x0d, 0x0e, 0x0f, 0x80, 0x82, 0x4c };
    private readonly byte[] FAN_TACHOMETER_EXT_REG =
      { 0x18, 0x19, 0x1a, 0x81, 0x83, 0x4d };
    private const byte FAN_MAIN_CTRL_REG = 0x13;
    private readonly byte[] FAN_PWM_CTRL_REG;
    private readonly byte[] FAN_PWM_CTRL_EXT_REG = 
      { 0x63, 0x6b, 0x73, 0x7b, 0xa3, 0xab };

    private bool[] restoreDefaultFanPwmControlRequired =
      new bool[MAX_FAN_HEADERS];
    private bool[] initialFanOutputModeEnabled = new bool[3];
    private byte[] initialFanPwmControl = new byte[MAX_FAN_HEADERS];
    private byte[] initialFanPwmControlExt = new byte[MAX_FAN_HEADERS];

    private byte ReadByte(byte register, out bool valid) {
      port.WriteIoPort(addressReg, register);
      byte value = port.ReadIoPort(dataReg);
      // The IT8688E does not return the written index when the address
      // register is read back, so the check is skipped for it. The IT8689E
      // does echo the index (verified on a Gigabyte B760M GAMING PLUS WIFI
      // DDR4: 0x20, 0x29 and 0x58 read back unchanged) and keeps the check.
      if (this.chip == Chip.IT8688E)
        valid = true;
      else
        valid = register == port.ReadIoPort(addressReg);
      return value;
    }

    private bool WriteByte(byte register, byte value) {
      port.WriteIoPort(addressReg, register);
      port.WriteIoPort(dataReg, value);
      return register == port.ReadIoPort(addressReg);
    }

    // Checks (without changing it) that bank 0 is mapped. Another program
    // may have left a different bank selected; reads would then return
    // unrelated registers and writes would corrupt them.
    private bool IsDefaultBankSelected() {
      if (!hasBankSelect)
        return true;

      byte value = ReadByte(BANK_REGISTER, out bool valid);
      return valid && (value & BANK_SELECT_MASK) == 0;
    }

    public byte? ReadGPIO(int index) {
      if (index >= gpioCount)
        return null;

      return port.ReadIoPort((ushort)(gpioAddress + index));
    }

    public void WriteGPIO(int index, byte value) {
      if (index >= gpioCount)
        return;

      port.WriteIoPort((ushort)(gpioAddress + index), value);
    } 

    private void SaveDefaultFanPwmControl(int index) {
      if (!restoreDefaultFanPwmControlRequired[index]) {
        initialFanPwmControl[index] = ReadByte(FAN_PWM_CTRL_REG[index], out _);

        if (index < 3) {
          initialFanOutputModeEnabled[index] = 
            (ReadByte(FAN_MAIN_CTRL_REG, out _) & (1 << index)) > 0;
        }

        if (hasExtReg) {
          initialFanPwmControlExt[index] =
            ReadByte(FAN_PWM_CTRL_EXT_REG[index], out _);
        }
        restoreDefaultFanPwmControlRequired[index] = true;
      }
    }

    private void RestoreDefaultFanPwmControl(int index) {
      if (restoreDefaultFanPwmControlRequired[index]) {
        // IT8689E: put the saved duty (the automatic mode start value) back
        // before handing the channel back to the firmware.
        if (chip == Chip.IT8689E)
          WriteByte(FAN_PWM_CTRL_EXT_REG[index], initialFanPwmControlExt[index]);

        WriteByte(FAN_PWM_CTRL_REG[index], initialFanPwmControl[index]);

        if (index < 3) {
          var value = ReadByte(FAN_MAIN_CTRL_REG, out _);

          if ((value & (1 << index)) > 0 != initialFanOutputModeEnabled[index]) {
            WriteByte(FAN_MAIN_CTRL_REG, (byte)(value ^ (1 << index)));
          }
        }

        if (hasExtReg && chip != Chip.IT8689E) {
          WriteByte(FAN_PWM_CTRL_EXT_REG[index], initialFanPwmControlExt[index]);
        }
        restoreDefaultFanPwmControlRequired[index] = false;
      }
    }

    public void SetControl(int index, byte? value) {
      if (index < 0 || index >= controls.Length)
        throw new ArgumentOutOfRangeException("index");

      // Never drive a PWM output the firmware has not enabled.
      if (controlsDisabled[index])
        return;

      if (!port.WaitIsaBusMutex(10))
        return;

      try {
        // Fan control registers are banked; do not write into another bank.
        if (!IsDefaultBankSelected())
          return;

        if (value.HasValue) {
          SaveDefaultFanPwmControl(index);

          if (index < 3) {
            if (!initialFanOutputModeEnabled[index]) {
              WriteByte(FAN_MAIN_CTRL_REG,
                (byte)(ReadByte(FAN_MAIN_CTRL_REG, out _) | (1 << index)));
            }
          }

          if (chip == Chip.IT8689E) {
            // Load the duty first and only then clear the automatic mode
            // bit (bit 7), keeping the other bits as the firmware set them,
            // so the fan never runs at the automatic start value in between.
            // Gigabyte firmware can additionally enable extra SmartFan
            // vectors in banks 2 and 3 that may override a manual duty;
            // those are not touched here.
            WriteByte(FAN_PWM_CTRL_EXT_REG[index], value.Value);
            WriteByte(FAN_PWM_CTRL_REG[index],
              (byte)(initialFanPwmControl[index] & 0x7F));
          } else if (hasExtReg) {
            WriteByte(FAN_PWM_CTRL_REG[index],
              (byte)(initialFanPwmControl[index] & 0x7F));
            WriteByte(FAN_PWM_CTRL_EXT_REG[index], value.Value);
          } else {
            WriteByte(FAN_PWM_CTRL_REG[index], (byte)(value.Value >> 1));
          }
        } else {
          RestoreDefaultFanPwmControl(index);
        }
      } finally {
        port.ReleaseIsaBusMutex();
      }
    }

    public IT87XX(Chip chip, ushort address, ushort gpioAddress, byte version)
      : this(chip, address, gpioAddress, version, Ring0PortAccess.Instance) { }

    internal IT87XX(Chip chip, ushort address, ushort gpioAddress, byte version,
      IPortAccess port)
    {
      if (port == null)
        throw new ArgumentNullException(nameof(port));

      this.port = port;
      this.address = address;
      this.chip = chip;
      this.version = version;
      this.addressReg = (ushort)(address + ADDRESS_REGISTER_OFFSET);
      this.dataReg = (ushort)(address + DATA_REGISTER_OFFSET);
      this.gpioAddress = gpioAddress;

      hasExtReg =
        chip == Chip.IT8721F ||
        chip == Chip.IT8665E ||
        chip == Chip.IT8686E ||
        chip == Chip.IT8688E ||
        chip == Chip.IT8689E ||
        chip == Chip.IT879XE;

      hasBankSelect = chip == Chip.IT8689E;

      // Check vendor id
      bool valid;
      byte vendorId = ReadByte(VENDOR_ID_REGISTER, out valid);
      if (!valid || vendorId != ITE_VENDOR_ID)
        return;

      // Bit 0x10 of the configuration register should always be 1
      byte configuration = ReadByte(CONFIGURATION_REGISTER, out valid);
      if ((configuration & 0x10) == 0 && 
        chip != Chip.IT8655E && chip != Chip.IT8665E)
        return;
      if (!valid)
        return;

      if (chip == Chip.IT8665E) {
        FAN_PWM_CTRL_REG = new byte[] { 0x15, 0x16, 0x17, 0x1e, 0x1f };
      } else {
        FAN_PWM_CTRL_REG = new byte[] { 0x15, 0x16, 0x17, 0x7f, 0xa7, 0xaf };
      }

      switch (chip) {        
        case Chip.IT8665E:
        case Chip.IT8686E:
        case Chip.IT8688E:
          voltages = new float?[9];
          temperatures = new float?[6];
          fans = new float?[5];
          controls = new float?[5];
          break;
        case Chip.IT8689E:
          // VIN0-VIN8 plus AVCC3 at 0x2F, six temperatures at 0x29-0x2E,
          // six 16-bit tachometers and six PWM outputs.
          voltages = new float?[10];
          temperatures = new float?[6];
          fans = new float?[6];
          controls = new float?[6];
          break;
        case Chip.IT8655E:
          voltages = new float?[9];
          temperatures = new float?[6];
          fans = new float?[3];
          break;
        case Chip.IT879XE:
          voltages = new float?[9];
          temperatures = new float?[3];
          fans = new float?[3];
          controls = new float?[3];
          break;
        case Chip.IT8705F:
          voltages = new float?[9];
          temperatures = new float?[3];
          fans = new float?[3];
          controls = new float?[3];
          break;
        default:
          voltages = new float?[9];
          temperatures = new float?[3];
          fans = new float?[5];
          controls = new float?[3];
          break;
      }

      fansDisabled = new bool[fans.Length];
      controlsDisabled = new bool[controls.Length];

      if (chip == Chip.IT8689E) {
        // Register 0x0C enables the optional tachometer inputs: bit 4 fan 4,
        // bit 5 fan 5, bit 2 fan 6. Register 0x0B bit 3 enables PWM 6.
        // Both are below 0x10 and therefore not banked. If either cannot be
        // read, the optional channels are treated as disabled.
        byte fanModes = ReadByte(FAN_TACHOMETER_16BIT_REGISTER,
          out bool fanModesValid);
        byte fanDivisor = ReadByte(FAN_TACHOMETER_DIVISOR_REGISTER,
          out bool fanDivisorValid);

        fansDisabled[3] = !fanModesValid || (fanModes & (1 << 4)) == 0;
        fansDisabled[4] = !fanModesValid || (fanModes & (1 << 5)) == 0;
        fansDisabled[5] = !fanModesValid || (fanModes & (1 << 2)) == 0;
        controlsDisabled[5] = !fanDivisorValid || (fanDivisor & (1 << 3)) == 0;
      }

      // set the voltage for the ADC LSB 
      switch (chip) {
        case Chip.IT8620E:
        case Chip.IT8628E:
        case Chip.IT8686E:
        case Chip.IT8688E:
        case Chip.IT8689E:
        case Chip.IT8721F:
        case Chip.IT8728F:
        case Chip.IT8771E:
        case Chip.IT8772E:
          voltageGain = 0.012f;
          break;
        case Chip.IT8655E:
        case Chip.IT8665E:
        case Chip.IT879XE:
          voltageGain = 0.011f;
          break;
        default:
          voltageGain = 0.016f;
          break;
      }

      // older IT8705F and IT8721F revisions do not have 16-bit fan counters
      if ((chip == Chip.IT8705F && version < 3) || 
          (chip == Chip.IT8712F && version < 8)) 
      {
        has16bitFanCounter = false;
      } else {
        has16bitFanCounter = true;
      }

      // Set the number of GPIO sets
      switch (chip) {
        case Chip.IT8712F:
        case Chip.IT8716F:
        case Chip.IT8718F:
        case Chip.IT8726F:
          gpioCount = 5;
          break;
        case Chip.IT8720F:
        case Chip.IT8721F:
          gpioCount = 8;
          break;
        default:
          gpioCount = 0;
          break;
      }
    }

    public Chip Chip { get { return chip; } }
    public float?[] Voltages { get { return voltages; } }
    public float?[] Temperatures { get { return temperatures; } }
    public float?[] Fans { get { return fans; } }
    public float?[] Controls { get { return controls; } }

    private static string EnabledChannels(bool[] disabled) {
      StringBuilder s = new StringBuilder();
      for (int i = 0; i < disabled.Length; i++) {
        if (disabled[i])
          continue;
        if (s.Length > 0)
          s.Append(", ");
        s.Append((i + 1).ToString(CultureInfo.InvariantCulture));
      }
      return s.Length > 0 ? s.ToString() : "none";
    }

    public string GetReport() {
      StringBuilder r = new StringBuilder();

      r.AppendLine("LPC " + this.GetType().Name);
      r.AppendLine();
      r.Append("Chip ID: 0x"); r.AppendLine(chip.ToString("X"));
      r.Append("Chip Version: 0x"); r.AppendLine(
        version.ToString("X", CultureInfo.InvariantCulture));
      r.Append("Base Address: 0x"); r.AppendLine(
        address.ToString("X4", CultureInfo.InvariantCulture));
      r.Append("GPIO Address: 0x"); r.AppendLine(
        gpioAddress.ToString("X4", CultureInfo.InvariantCulture));
      if (chip == Chip.IT8689E) {
        r.Append("Fan Tachometers Enabled: ");
        r.AppendLine(EnabledChannels(fansDisabled));
        r.Append("PWM Outputs Enabled: ");
        r.AppendLine(EnabledChannels(controlsDisabled));
      }
      r.AppendLine();

      if (!port.WaitIsaBusMutex(100))
        return r.ToString();

      r.AppendLine("Environment Controller Registers");
      r.AppendLine();
      r.AppendLine("      00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F");
      r.AppendLine();
      for (int i = 0; i <= 0xA; i++) {
        r.Append(" "); 
        r.Append((i << 4).ToString("X2", CultureInfo.InvariantCulture)); 
        r.Append("  ");
        for (int j = 0; j <= 0xF; j++) {
          r.Append(" ");
          bool valid;
          byte value = ReadByte((byte)((i << 4) | j), out valid);
          r.Append(
            valid ? value.ToString("X2", CultureInfo.InvariantCulture) : "??");
        }
        r.AppendLine();
      }
      r.AppendLine();

      r.AppendLine("GPIO Registers");
      r.AppendLine();
      for (int i = 0; i < gpioCount; i++) {
        r.Append(" ");
        r.Append(ReadGPIO(i).Value.ToString("X2",
          CultureInfo.InvariantCulture));
      }
      r.AppendLine();
      r.AppendLine();

      port.ReleaseIsaBusMutex();

      return r.ToString();
    }

    public void Update() {
      if (!port.WaitIsaBusMutex(10))
        return;

      try {
        if (!IsDefaultBankSelected())
          return;

        UpdateValues();
      } finally {
        port.ReleaseIsaBusMutex();
      }
    }

    private void UpdateValues() {
      for (int i = 0; i < voltages.Length; i++) {
        bool valid;
        
        float value = voltageGain * ReadByte(VOLTAGE_REG[i], out valid);

        if (!valid)
          continue;
        if (value > 0)
          voltages[i] = value;  
        else
          voltages[i] = null;
      }

      for (int i = 0; i < temperatures.Length; i++) {
        bool valid;
        sbyte value = (sbyte)ReadByte(
          (byte)(TEMPERATURE_BASE_REG + i), out valid);
        if (!valid)
          continue;

        if (value < sbyte.MaxValue && value > 0)
          temperatures[i] = value;
        else
          temperatures[i] = null;       
      }

      if (has16bitFanCounter) {
        for (int i = 0; i < fans.Length; i++) {
          if (fansDisabled[i])
            continue;

          bool valid;
          int value = ReadByte(FAN_TACHOMETER_REG[i], out valid);
          if (!valid)
            continue;
          value |= ReadByte(FAN_TACHOMETER_EXT_REG[i], out valid) << 8;
          if (!valid)
            continue;

          if (value > 0x3f) {
            fans[i] = (value < 0xffff) ? 1.35e6f / (value * 2) : 0;
          } else {
            fans[i] = null;
          }
        }
      } else {
        for (int i = 0; i < fans.Length; i++) {
          bool valid;
          int value = ReadByte(FAN_TACHOMETER_REG[i], out valid);
          if (!valid)
            continue;

          int divisor = 2;
          if (i < 2) {
            int divisors = ReadByte(FAN_TACHOMETER_DIVISOR_REGISTER, out valid);
            if (!valid)
              continue;
            divisor = 1 << ((divisors >> (3 * i)) & 0x7);
          }

          if (value > 0) {
            fans[i] = (value < 0xff) ? 1.35e6f / (value * divisor) : 0;
          } else {
            fans[i] = null;
          }
        }
      }

      for (int i = 0; i < controls.Length; i++) {
        if (controlsDisabled[i])
          continue;

        bool valid;
        byte value = ReadByte(FAN_PWM_CTRL_REG[i], out valid);
        if (!valid)
          continue;

        if ((value & 0x80) > 0) {
           // automatic operation (value can't be read)
           controls[i] = null;
        } else {
          // software operation
          if (hasExtReg) {
            value = ReadByte(FAN_PWM_CTRL_EXT_REG[i], out valid);
            if (valid)
              controls[i] = (float)Math.Round(value * 100.0f / 0xFF);
          } else {
            controls[i] = (float)Math.Round((value & 0x7F) * 100.0f / 0x7F);
          }
        }
      }
    }
  } 
}
