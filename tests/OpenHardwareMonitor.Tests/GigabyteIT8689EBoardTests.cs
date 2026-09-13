/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System.Collections.Generic;
using System.Linq;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.LPC;
using Xunit;
using Identification = OpenHardwareMonitor.Hardware.Mainboard.Identification;
using Manufacturer = OpenHardwareMonitor.Hardware.Mainboard.Manufacturer;
using Model = OpenHardwareMonitor.Hardware.Mainboard.Model;
using SuperIOHardware = OpenHardwareMonitor.Hardware.Mainboard.SuperIOHardware;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Board identification and the named sensor layout of the Gigabyte B760M
  /// GAMING PLUS WIFI DDR4, driven end to end through IT87XX with the
  /// register dump from that board.
  /// </summary>
  public class GigabyteIT8689EBoardTests {

    private sealed class MemorySettings : ISettings {
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

    private static SuperIOHardware CreateBoard(FakeIT87Controller controller,
      Model model)
    {
      IT87XX chip = IT8689ETests.Create(controller);
      SuperIOHardware hardware = new SuperIOHardware(null!, chip,
        Manufacturer.Gigabyte, model, new MemorySettings());
      hardware.Update();
      return hardware;
    }

    private static ISensor Sensor(IHardware hardware, SensorType type,
      string name)
    {
      ISensor[] matches = hardware.Sensors
        .Where(s => s.SensorType == type && s.Name == name).ToArray();
      Assert.True(matches.Length == 1,
        "Expected one " + type + " sensor named '" + name + "', found " +
        matches.Length + ".");
      return matches[0];
    }

    private static string[] Names(IHardware hardware, SensorType type) {
      return hardware.Sensors.Where(s => s.SensorType == type)
        .OrderBy(s => s.Index).Select(s => s.Name).ToArray();
    }

    [Theory]
    [InlineData("B760M GAMING PLUS WIFI DDR4")]
    [InlineData("B760M GAMING PLUS WIFI DDR4  ")]
    public void SmbiosBoardNameMapsToTheModel(string name) {
      Assert.Equal(Model.B760M_GAMING_PLUS_WIFI_DDR4,
        Identification.GetModel(name));
    }

    [Fact]
    public void SmbiosManufacturerMapsToGigabyte() {
      Assert.Equal(Manufacturer.Gigabyte,
        Identification.GetManufacturer("Gigabyte Technology Co., Ltd."));
    }

    [Fact]
    public void UnknownOrMissingBoardNamesMapToUnknown() {
      Assert.Equal(Model.Unknown, Identification.GetModel("B760M DS3H DDR4"));
      Assert.Equal(Model.Unknown, Identification.GetModel(null!));
    }

    [Fact]
    public void VoltagesApplyTheBoardDividers() {
      SuperIOHardware board = CreateBoard(FakeIT87Controller.FromB760MDump(),
        Model.B760M_GAMING_PLUS_WIFI_DDR4);

      IT8689ETests.AssertNear(1.320,
        Sensor(board, SensorType.Voltage, "CPU VCore").Value, 0.01);
      IT8689ETests.AssertNear(3.364,
        Sensor(board, SensorType.Voltage, "+3.3V").Value, 0.01);
      IT8689ETests.AssertNear(11.952,
        Sensor(board, SensorType.Voltage, "+12V").Value, 0.01);
      IT8689ETests.AssertNear(5.130,
        Sensor(board, SensorType.Voltage, "+5V").Value, 0.01);
      IT8689ETests.AssertNear(0.036,
        Sensor(board, SensorType.Voltage, "iGPU VAXG").Value, 0.01);
      IT8689ETests.AssertNear(1.836,
        Sensor(board, SensorType.Voltage, "CPU VCCIN_AUX").Value, 0.01);
      IT8689ETests.AssertNear(1.332,
        Sensor(board, SensorType.Voltage, "DRAM").Value, 0.01);
      IT8689ETests.AssertNear(3.336,
        Sensor(board, SensorType.Voltage, "Standby +3.3V").Value, 0.01);
      IT8689ETests.AssertNear(3.048,
        Sensor(board, SensorType.Voltage, "VBat").Value, 0.01);

      ISensor avcc3 = Sensor(board, SensorType.Voltage, "AVCC3");
      Assert.True(avcc3.IsDefaultHidden);
      IT8689ETests.AssertNear(3.072, avcc3.Value, 0.01);
    }

    [Fact]
    public void TemperaturesAreNamed() {
      SuperIOHardware board = CreateBoard(FakeIT87Controller.FromB760MDump(),
        Model.B760M_GAMING_PLUS_WIFI_DDR4);

      Assert.Equal(
        new[] { "System 1", "PCH", "CPU", "PCIEX16", "VRM MOS", "System 2" },
        Names(board, SensorType.Temperature));
      Assert.Equal(43f, Sensor(board, SensorType.Temperature, "CPU").Value);
    }

    [Fact]
    public void OnlySpinningFansAppearAndControlsCoverTheFourHeaders() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      SuperIOHardware board = CreateBoard(controller,
        Model.B760M_GAMING_PLUS_WIFI_DDR4);

      Assert.Equal(new[] { "CPU Fan", "System Fan #1" },
        Names(board, SensorType.Fan));
      IT8689ETests.AssertNear(649.7,
        Sensor(board, SensorType.Fan, "CPU Fan").Value, 0.1);
      IT8689ETests.AssertNear(769.7,
        Sensor(board, SensorType.Fan, "System Fan #1").Value, 0.1);

      Assert.Equal(
        new[] { "CPU Fan", "System Fan #1", "System Fan #2",
          "System Fan #3 / Pump" },
        Names(board, SensorType.Control));
      Assert.All(board.Sensors.Where(s => s.SensorType == SensorType.Control),
        s => Assert.Null(s.Value));
    }

    [Fact]
    public void BuildingUpdatingAndClosingTheBoardWritesNoRegister() {
      FakeIT87Controller controller = FakeIT87Controller.FromB760MDump();
      SuperIOHardware board = CreateBoard(controller,
        Model.B760M_GAMING_PLUS_WIFI_DDR4);
      board.Update();
      board.Close();

      Assert.Empty(controller.DataWrites);
      Assert.Equal(0, controller.MutexDepth);
    }

    [Fact]
    public void UnknownGigabyteIT8689EBoardsGetTheCommonLayout() {
      SuperIOHardware board = CreateBoard(FakeIT87Controller.FromB760MDump(),
        Model.Unknown);

      IT8689ETests.AssertNear(11.952,
        Sensor(board, SensorType.Voltage, "+12V").Value, 0.01);
      IT8689ETests.AssertNear(3.364,
        Sensor(board, SensorType.Voltage, "+3.3V").Value, 0.01);
      IT8689ETests.AssertNear(5.130,
        Sensor(board, SensorType.Voltage, "+5V").Value, 0.01);
      IT8689ETests.AssertNear(3.048,
        Sensor(board, SensorType.Voltage, "VBat").Value, 0.01);
      Assert.True(Sensor(board, SensorType.Voltage, "Voltage #7")
        .IsDefaultHidden);
      Assert.True(Sensor(board, SensorType.Voltage, "AVCC3").IsDefaultHidden);

      Assert.Equal(
        new[] { "System 1", "PCH", "CPU", "PCIEX16", "VRM MOS",
          "Temperature #6" },
        Names(board, SensorType.Temperature));
      Assert.Equal(
        new[] { "CPU Fan", "System Fan #1", "System Fan #2",
          "Fan Control #4", "Fan Control #5", "Fan Control #6" },
        Names(board, SensorType.Control));
    }
  }
}
