/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenHardwareMonitor.Hardware.LPC;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Emulates the index/data port pair of an ITE environment controller.
  /// Every data port write is recorded so tests can prove that reading
  /// sensors never writes a register.
  /// </summary>
  internal sealed class FakeIT87Controller : IT87XX.IPortAccess {

    public const ushort BaseAddress = 0x0A40;
    private const ushort AddressPort = BaseAddress + 5;
    private const ushort DataPort = BaseAddress + 6;

    public readonly byte[] Registers = new byte[256];
    public readonly List<byte> IndexWrites = new List<byte>();
    public readonly List<(byte Register, byte Value)> DataWrites =
      new List<(byte Register, byte Value)>();

    /// <summary>False emulates the IT8688E, whose index does not read back.</summary>
    public bool EchoIndex = true;
    public int MutexDepth;

    private byte index;

    public byte ReadIoPort(ushort port) {
      if (port == AddressPort)
        return EchoIndex ? index : (byte)0x00;
      if (port == DataPort)
        return Registers[index];
      throw new InvalidOperationException(
        "Unexpected read of port 0x" + port.ToString("X4"));
    }

    public void WriteIoPort(ushort port, byte value) {
      if (port == AddressPort) {
        index = value;
        IndexWrites.Add(value);
        return;
      }
      if (port == DataPort) {
        DataWrites.Add((index, value));
        Registers[index] = value;
        return;
      }
      throw new InvalidOperationException(
        "Unexpected write of port 0x" + port.ToString("X4"));
    }

    public bool WaitIsaBusMutex(int millisecondsTimeout) {
      MutexDepth++;
      return true;
    }

    public void ReleaseIsaBusMutex() {
      MutexDepth--;
    }

    /// <summary>
    /// Pass 1 of the read-only register dump taken on a Gigabyte B760M
    /// GAMING PLUS WIFI DDR4 (IT8689E version 2, EC base 0x0A40) at idle.
    /// Registers 0x01-0x03 clear on read and were not captured.
    /// </summary>
    public static FakeIT87Controller FromB760MDump() {
      const string dump =
        "00: 13 -- -- -- FF FF 18 20 FF 98 20 40 10 0F 6D FF\n" +
        "10: FF FF FF 77 C4 D0 D0 C0 04 03 FF FF FF FF FF FF\n" +
        "20: 6E AA A6 AB 03 99 6F 8B 7F 24 2B 2B 26 2A 1F 80\n" +
        "30: FF 00 FF 00 FF 00 FF 00 FF 00 FF 00 FF 00 FF 00\n" +
        "40: 7F 7F 7F 7F 7F 7F 5F 40 AD 6A D4 00 00 00 00 00\n" +
        "50: FF 18 7F 7F 7F 40 00 00 90 64 15 12 61 00 00 00\n" +
        "60: 00 28 55 4E 01 03 00 00 00 28 5A 41 01 03 00 FF\n" +
        "70: 00 14 3C 3F 05 01 00 2E 00 14 3C 3F 05 01 00 C0\n" +
        "80: FF FF 00 00 FF FF FF FF 02 30 01 02 01 FE E0 EF\n" +
        "90: 00 00 00 00 FF 10 10 10 40 97 00 00 40 90 C1 00\n" +
        "A0: 00 00 96 00 00 0A 0F 98 7F 7F 96 80 00 00 0F 98\n" +
        "B0: C7 19 75 70 7F 00 64 00 7F 00 80 24 80 80 80 00\n" +
        "C0: 00 C0 00 09 02 01 10 10 10 10 10 10 10 10 10 10\n" +
        "D0: FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF 01\n" +
        "E0: FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF\n" +
        "F0: FF FF FF FF FF FF FF FF FF FF FF FF FF FF 00 00\n";

      FakeIT87Controller controller = new FakeIT87Controller();
      foreach (string line in dump.Split('\n',
        StringSplitOptions.RemoveEmptyEntries))
      {
        string[] parts = line.Split(':');
        int row = int.Parse(parts[0], NumberStyles.HexNumber,
          CultureInfo.InvariantCulture);
        string[] cells = parts[1].Split(' ',
          StringSplitOptions.RemoveEmptyEntries);
        for (int column = 0; column < cells.Length; column++) {
          if (cells[column] != "--") {
            controller.Registers[row + column] = byte.Parse(cells[column],
              NumberStyles.HexNumber, CultureInfo.InvariantCulture);
          }
        }
      }
      return controller;
    }
  }

  public class IT8689ETests {

    internal static void AssertNear(double expected, float? actual,
      double tolerance = 0.005)
    {
      Assert.True(actual.HasValue, "Expected " +
        expected.ToString(CultureInfo.InvariantCulture) + " but got null.");
      Assert.InRange((double)actual!.Value, expected - tolerance,
        expected + tolerance);
    }

    internal static IT87XX Create(FakeIT87Controller controller,
      Chip chip = Chip.IT8689E)
    {
      return new IT87XX(chip, FakeIT87Controller.BaseAddress, 0, 2,
        controller);
    }

    [Fact]
    public void ChannelLayoutMatchesTheChip() {
      IT87XX chip = Create(FakeIT87Controller.FromB760MDump());

      Assert.Equal(Chip.IT8689E, chip.Chip);
      Assert.Equal(10, chip.Voltages.Length);
      Assert.Equal(6, chip.Temperatures.Length);
      Assert.Equal(6, chip.Fans.Length);
      Assert.Equal(6, chip.Controls.Length);
    }

    [Fact]
    public void ConstructionUpdateAndReportNeverWriteARegister() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      byte[] before = (byte[])controller.Registers.Clone();

      IT87XX chip = Create(controller);
      chip.Update();
      chip.Update();
      chip.GetReport();

      Assert.Empty(controller.DataWrites);
      Assert.Equal(before, controller.Registers);
      Assert.Equal(0, controller.MutexDepth);
    }

    [Fact]
    public void VoltagesUseTheTwelveMillivoltLsbAndVin9At0x2F() {
      IT87XX chip = Create(FakeIT87Controller.FromB760MDump());
      chip.Update();

      AssertNear(0x6E * 0.012, chip.Voltages[0]); // 1.320 V
      AssertNear(0xAA * 0.012, chip.Voltages[1]); // 2.040 V
      AssertNear(0xA6 * 0.012, chip.Voltages[2]); // 1.992 V
      AssertNear(0xAB * 0.012, chip.Voltages[3]); // 2.052 V
      AssertNear(0x03 * 0.012, chip.Voltages[4]); // 0.036 V
      AssertNear(0x99 * 0.012, chip.Voltages[5]); // 1.836 V
      AssertNear(0x6F * 0.012, chip.Voltages[6]); // 1.332 V
      AssertNear(0x8B * 0.012, chip.Voltages[7]); // 1.668 V
      AssertNear(0x7F * 0.012, chip.Voltages[8]); // 1.524 V
      // VIN9 must come from 0x2F (0x80), not 0x29 (a temperature, 0x24).
      AssertNear(0x80 * 0.012, chip.Voltages[9]); // 1.536 V
    }

    [Fact]
    public void ZeroVoltageReadsAsNoValue() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      controller.Registers[0x24] = 0;
      IT87XX chip = Create(controller);
      chip.Update();

      Assert.Null(chip.Voltages[4]);
    }

    [Fact]
    public void TemperaturesAreSignedBytesAt0x29To0x2E() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      IT87XX chip = Create(controller);
      chip.Update();

      Assert.Equal(new float?[] { 36, 43, 43, 38, 42, 31 }, chip.Temperatures);

      // 0x80 (-128) and 0x7F are "no sensor".
      controller.Registers[0x2E] = 0x80;
      controller.Registers[0x2D] = 0x7F;
      chip.Update();
      Assert.Null(chip.Temperatures[5]);
      Assert.Null(chip.Temperatures[4]);
    }

    [Fact]
    public void FanCountersAreSixteenBitWithExtendedRegisters() {
      IT87XX chip = Create(FakeIT87Controller.FromB760MDump());
      chip.Update();

      // Fan 1: 0x18:0x0D = 0x040F, fan 2: 0x19:0x0E = 0x036D.
      AssertNear(1.35e6 / (0x040F * 2), chip.Fans[0], 0.05); // ~650 RPM
      AssertNear(1.35e6 / (0x036D * 2), chip.Fans[1], 0.05); // ~770 RPM
      // 0xFFFF means enabled but stopped or unplugged.
      Assert.Equal(0f, chip.Fans[2]);
      Assert.Equal(0f, chip.Fans[3]);
    }

    [Fact]
    public void FanCountersBelowTheThresholdReadAsNoValue() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      IT87XX chip = Create(controller);

      controller.Registers[0x0D] = 0x3F;
      controller.Registers[0x18] = 0x00;
      chip.Update();
      Assert.Null(chip.Fans[0]);

      controller.Registers[0x0D] = 0x40;
      chip.Update();
      AssertNear(1.35e6 / (0x40 * 2), chip.Fans[0], 0.05);
    }

    [Fact]
    public void OptionalTachometersFollowTheEnableRegister() {
      // The dump has 0x0C = 0x10: fan 4 enabled, fans 5 and 6 disabled.
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      controller.Registers[0x82] = 0x00; // fan 5 LSB
      controller.Registers[0x83] = 0x04; // fan 5 MSB
      controller.Registers[0x4C] = 0x00; // fan 6 LSB
      controller.Registers[0x4D] = 0x04; // fan 6 MSB
      IT87XX chip = Create(controller);
      chip.Update();

      Assert.Null(chip.Fans[4]);
      Assert.Null(chip.Fans[5]);
      Assert.DoesNotContain((byte)0x82, controller.IndexWrites);
      Assert.DoesNotContain((byte)0x4C, controller.IndexWrites);

      // Enable fan 5 (bit 5) and fan 6 (bit 2) and construct again.
      controller.Registers[0x0C] = 0x10 | 0x20 | 0x04;
      chip = Create(controller);
      chip.Update();

      AssertNear(1.35e6 / (0x0400 * 2), chip.Fans[4], 0.05);
      AssertNear(1.35e6 / (0x0400 * 2), chip.Fans[5], 0.05);
    }

    [Fact]
    public void AutomaticPwmChannelsHaveNoControlValue() {
      IT87XX chip = Create(FakeIT87Controller.FromB760MDump());
      chip.Update();

      // Bit 7 is set in 0x15, 0x16, 0x17, 0x7F, 0xA7 and 0xAF.
      Assert.All(chip.Controls, value => Assert.Null(value));
    }

    [Fact]
    public void SoftwarePwmChannelsReportTheEightBitDuty() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      controller.Registers[0x16] = 0x50; // PWM 2 manual
      controller.Registers[0x6B] = 0x80; // duty 128/255
      controller.Registers[0xA7] = 0x18; // PWM 5 manual
      controller.Registers[0xA3] = 0xFF; // duty 255/255
      IT87XX chip = Create(controller);
      chip.Update();

      Assert.Equal(50f, chip.Controls[1]);
      Assert.Equal(100f, chip.Controls[4]);
    }

    [Fact]
    public void Pwm6IsIgnoredWhenTheFirmwareHasNotEnabledIt() {
      // 0x0B = 0x40: bit 3 clear, PWM 6 disabled.
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      controller.Registers[0xAF] = 0x18; // would read as manual
      controller.Registers[0xAB] = 0x80;
      IT87XX chip = Create(controller);
      chip.Update();
      Assert.Null(chip.Controls[5]);

      chip.SetControl(5, 0xFF);
      Assert.Empty(controller.DataWrites);

      controller.Registers[0x0B] = 0x48; // bit 3 set, PWM 6 enabled
      chip = Create(controller);
      chip.Update();
      Assert.Equal(50f, chip.Controls[5]);
    }

    [Fact]
    public void TheIndexReadbackCheckStillAppliesToTheIT8689E() {
      // Without readback, the IT8689E fails the vendor check.
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      controller.EchoIndex = false;
      Assert.Empty(Create(controller).Voltages);

      // The IT8688E workaround is unchanged.
      Assert.Equal(9, Create(controller, Chip.IT8688E).Voltages.Length);
    }

    [Fact]
    public void ReadsWhoseIndexDoesNotReadBackAreDiscarded() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      IT87XX chip = Create(controller);
      chip.Update();
      AssertNear(1.32, chip.Voltages[0]);

      controller.Registers[0x20] = 0x10;
      controller.EchoIndex = false;
      chip.Update();
      AssertNear(1.32, chip.Voltages[0]);
    }

    [Fact]
    public void UpdateReadsNothingWhileAnotherBankIsSelected() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      IT87XX chip = Create(controller);
      controller.Registers[0x06] = 0x18 | 0x20; // bank 1
      controller.IndexWrites.Clear();

      chip.Update();

      Assert.Equal(new byte[] { 0x06 }, controller.IndexWrites);
      Assert.All(chip.Voltages, value => Assert.Null(value));
      Assert.Empty(controller.DataWrites);
      Assert.Equal(0, controller.MutexDepth);

      controller.IndexWrites.Clear();
      chip.SetControl(0, 0x80);
      Assert.Equal(new byte[] { 0x06 }, controller.IndexWrites);
      Assert.Empty(controller.DataWrites);
    }

    // Exercises the control write path against the emulated controller only.
    [Fact]
    public void SoftwareControlLoadsTheDutyBeforeLeavingAutomaticMode() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      byte[] before = (byte[])controller.Registers.Clone();
      IT87XX chip = Create(controller);

      chip.SetControl(1, 0x80);

      Assert.Equal(new[] { ((byte)0x6B, (byte)0x80), ((byte)0x16, (byte)0x50) },
        controller.DataWrites);
      chip.Update();
      Assert.Equal(50f, chip.Controls[1]);

      controller.DataWrites.Clear();
      chip.SetControl(1, null);

      Assert.Equal(new[] { ((byte)0x6B, (byte)0x41), ((byte)0x16, (byte)0xD0) },
        controller.DataWrites);
      Assert.Equal(before, controller.Registers);
      Assert.Equal(0, controller.MutexDepth);

      // Nothing left to restore.
      controller.DataWrites.Clear();
      chip.SetControl(1, null);
      Assert.Empty(controller.DataWrites);
    }

    [Fact]
    public void ReportListsTheEnabledOptionalChannels() {
      string report = Create(FakeIT87Controller.FromB760MDump()).GetReport();

      Assert.Contains("Fan Tachometers Enabled: 1, 2, 3, 4", report);
      Assert.Contains("PWM Outputs Enabled: 1, 2, 3, 4, 5", report);
    }
  }
}
