/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.LowLevel;
using OpenHardwareMonitor.Hardware.LPC;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Super I/O access through PawnIO's LpcIO module. PawnIO refuses
  /// non-administrator processes, so these run against
  /// <see cref="SimulatedLpcIoModule"/>, which enforces the module's rules,
  /// and fake chips.
  ///
  /// Ring0 is a static facade. Every test that swaps its backend lives in this
  /// class, and xUnit runs the tests of one class one at a time.
  /// </summary>
  public class SuperIoPawnIoTests {

    private const ushort IteEnvironmentController = 0x0A40;
    private const ushort IteGpio = 0x0A00;
    private const ushort NuvotonHardwareMonitor = 0x0A20;

    private sealed class Rig {

      public Rig(params FakeSuperIoChip[] chips) {
        Bus.Chips.AddRange(chips);
        Modules = new[] {
          new SimulatedLpcIoModule(Bus), new SimulatedLpcIoModule(Bus)
        };
        Adapter = new LpcIoAdapter(Modules);
      }

      public SimulatedIsaBus Bus { get; } = new SimulatedIsaBus();
      public SimulatedLpcIoModule[] Modules { get; }
      public LpcIoAdapter Adapter { get; }

      public int DeniedCount {
        get { return Modules[0].DeniedCount + Modules[1].DeniedCount; }
      }

      public void Out(ushort port, byte value) {
        Assert.True(Adapter.WritePort(port, value));
      }

      public byte In(ushort port) {
        Assert.True(Adapter.TryReadPort(port, out byte value));
        return value;
      }

      public void EnterIte(ushort registerPort) {
        Out(registerPort, 0x87);
        Out(registerPort, 0x01);
        Out(registerPort, 0x55);
        Out(registerPort, registerPort == 0x4E ? (byte)0xAA : (byte)0x55);
      }

      public void ExitIte(ushort registerPort) {
        Out(registerPort, 0x02);
        Out((ushort)(registerPort + 1), 0x02);
      }

      public void EnterNuvoton(ushort registerPort) {
        Out(registerPort, 0x87);
        Out(registerPort, 0x87);
      }

      public void ExitNuvoton(ushort registerPort) {
        Out(registerPort, 0xAA);
      }

      public byte ReadConfig(ushort registerPort, byte register) {
        Out(registerPort, register);
        return In((ushort)(registerPort + 1));
      }

      public void SelectDevice(ushort registerPort, byte device) {
        Out(registerPort, 0x07);
        Out((ushort)(registerPort + 1), device);
      }

      public bool Prepare(ushort registerPort) {
        return Adapter.PrepareSuperIoAccess(registerPort, out _);
      }

      public byte ReadMonitor(ushort monitorBase, byte register) {
        Out((ushort)(monitorBase + 5), register);
        return In((ushort)(monitorBase + 6));
      }

      public byte ReadBankedMonitor(ushort monitorBase, ushort address) {
        Out((ushort)(monitorBase + 5), 0x4E);
        Out((ushort)(monitorBase + 6), (byte)(address >> 8));
        Out((ushort)(monitorBase + 5), (byte)address);
        return In((ushort)(monitorBase + 6));
      }

      public void AssertBufferSizesWereExact() {
        foreach (SimulatedLpcIoModule module in Modules)
          Assert.Equal(0, module.InvalidParameterCount);
      }
    }

    private sealed class StubMsrModule : IPawnIoModule {
      public bool Execute(string function, ulong[] input, ulong[] output,
        out int hresult) {
        hresult = 0;
        return true;
      }

      public void Dispose() {
      }
    }

    private static FakeSuperIoChip Ite() {
      return FakeSuperIoChip.Ite(0x2E, 0x8686, IteEnvironmentController,
        IteGpio);
    }

    private static FakeSuperIoChip Nuvoton(ushort registerPort) {
      return FakeSuperIoChip.Nuvoton(registerPort, 0xD4, 0x2B,
        NuvotonHardwareMonitor);
    }

    [Fact]
    public void DetectionSequenceReachesTheChipThroughItsConfigurationPorts() {
      FakeSuperIoChip ite = Ite();
      Rig rig = new Rig(ite);

      // LPCIO probes for Winbond, Nuvoton and Fintek first; ITE ignores that.
      rig.EnterNuvoton(0x2E);
      Assert.Equal((byte)0xFF, rig.ReadConfig(0x2E, 0x20));
      Assert.False(ite.InConfigMode);

      rig.EnterIte(0x2E);
      Assert.True(ite.InConfigMode);
      Assert.Equal((byte)0x86, rig.ReadConfig(0x2E, 0x20));
      Assert.Equal((byte)0x86, rig.ReadConfig(0x2E, 0x21));
      rig.SelectDevice(0x2E, 0x04);
      Assert.Equal((byte)0x0A, rig.ReadConfig(0x2E, 0x60));
      Assert.Equal((byte)0x40, rig.ReadConfig(0x2E, 0x61));
      rig.ExitIte(0x2E);
      Assert.False(ite.InConfigMode);

      // The second slot is probed as well; with nothing there it floats high.
      Assert.Equal((byte)0xFF, rig.ReadConfig(0x4E, 0x20));

      Assert.Single(rig.Modules[0].Calls,
        c => c == LpcIoAdapter.SelectSlotFunction);
      Assert.Single(rig.Modules[1].Calls,
        c => c == LpcIoAdapter.SelectSlotFunction);
      Assert.Equal(0, rig.DeniedCount);
      rig.AssertBufferSizesWereExact();
    }

    [Fact]
    public void BarAccessIsRefusedBeforePrepare() {
      FakeSuperIoChip ite = Ite();
      Rig rig = new Rig(ite);
      rig.EnterIte(0x2E);
      Assert.Equal((byte)0x86, rig.ReadConfig(0x2E, 0x20));

      Assert.False(rig.Adapter.WritePort(IteEnvironmentController + 5, 0x58));
      Assert.False(rig.Adapter.TryReadPort(IteEnvironmentController + 6,
        out byte value));
      Assert.Equal((byte)0xFF, value);
      Assert.NotEqual(0, rig.Modules[0].DeniedCount);
      Assert.NotEqual(0, rig.Modules[1].DeniedCount);
      Assert.DoesNotContain(rig.Bus.Writes,
        w => (w.Port & 0xFFF8) == IteEnvironmentController);

      rig.ExitIte(0x2E);
      Assert.False(ite.InConfigMode);
      rig.AssertBufferSizesWereExact();
    }

    [Fact]
    public void BarReadAndWriteWorkAfterPrepare() {
      FakeSuperIoChip ite = Ite();
      Rig rig = new Rig(ite);
      rig.EnterIte(0x2E);
      Assert.Equal((byte)0x86, rig.ReadConfig(0x2E, 0x20));
      Assert.True(rig.Prepare(0x2E));
      rig.ExitIte(0x2E);

      Assert.Contains(IteEnvironmentController, rig.Modules[0].Bars);
      Assert.Contains(IteGpio, rig.Modules[0].Bars);

      Assert.Equal((byte)0x90,
        rig.ReadMonitor(IteEnvironmentController, 0x58));
      rig.Out(IteEnvironmentController + 5, 0x29);
      rig.Out(IteEnvironmentController + 6, 0x2D);
      Assert.Equal((byte)0x2D, ite.GetMonitorRegister(0x29));
      Assert.Equal((byte)0x2D,
        rig.ReadMonitor(IteEnvironmentController, 0x29));
      rig.In(IteGpio + 3);

      // Ports outside the discovered windows stay refused, PCI configuration
      // ports always.
      Assert.False(rig.Adapter.TryReadPort(IteEnvironmentController + 8,
        out _));
      Assert.False(rig.Adapter.WritePort(0xCF8, 0x80));
      Assert.False(rig.Adapter.TryReadPort(0xCFC, out _));
      rig.AssertBufferSizesWereExact();
    }

    [Fact]
    public void PrepareKeepsConfigurationModeAndTheSelectedDevice() {
      FakeSuperIoChip nuvoton = Nuvoton(0x2E);
      Rig rig = new Rig(nuvoton);
      rig.EnterNuvoton(0x2E);
      Assert.Equal((byte)0xD4, rig.ReadConfig(0x2E, 0x20));
      rig.SelectDevice(0x2E, 0x0B);

      Assert.True(rig.Prepare(0x2E));

      Assert.True(nuvoton.InConfigMode);
      Assert.Equal((byte)0x0B, nuvoton.SelectedDevice);
      Assert.Equal(NuvotonHardwareMonitor, Assert.Single(rig.Modules[0].Bars));

      rig.ExitNuvoton(0x2E);
      Assert.False(nuvoton.InConfigMode);
      Assert.Equal(1, nuvoton.ConfigEntries);
      Assert.Equal(1, nuvoton.ConfigExits);
      Assert.Equal((byte)0x5C,
        rig.ReadBankedMonitor(NuvotonHardwareMonitor, 0x804F));
      rig.AssertBufferSizesWereExact();
    }

    [Fact]
    public void PrepareFailsOutsideConfigurationMode() {
      FakeSuperIoChip ite = Ite();
      Rig rig = new Rig(ite);

      Assert.False(rig.Adapter.PrepareSuperIoAccess(0x2E, out string detail));
      Assert.Contains("find_bars", detail);
      Assert.Empty(rig.Modules[0].Bars);
      // find_bars gave up at the chip ID and selected no logical device.
      Assert.DoesNotContain(rig.Bus.Writes, w => w.Port == 0x2F);
      Assert.False(ite.InConfigMode);
      Assert.False(rig.Adapter.TryReadPort(IteEnvironmentController + 6,
        out _));

      Assert.False(rig.Adapter.PrepareSuperIoAccess(0x290, out _));
      rig.AssertBufferSizesWereExact();
    }

    [Fact]
    public void ChipsOnBothSlotsStayReachableWhenAccessAlternates() {
      FakeSuperIoChip ite = Ite();
      FakeSuperIoChip nuvoton = Nuvoton(0x4E);
      Rig rig = new Rig(ite, nuvoton);

      rig.EnterIte(0x2E);
      Assert.True(rig.Prepare(0x2E));
      rig.ExitIte(0x2E);
      rig.EnterNuvoton(0x4E);
      Assert.True(rig.Prepare(0x4E));
      rig.ExitNuvoton(0x4E);

      Assert.Equal((byte)0x90,
        rig.ReadMonitor(IteEnvironmentController, 0x58));
      Assert.Equal((byte)0x5C,
        rig.ReadBankedMonitor(NuvotonHardwareMonitor, 0x804F));
      int deniedWhileLearning = rig.DeniedCount;

      for (int i = 0; i < 3; i++) {
        Assert.Equal((byte)0x90,
          rig.ReadMonitor(IteEnvironmentController, 0x58));
        Assert.Equal((byte)0xA3,
          rig.ReadBankedMonitor(NuvotonHardwareMonitor, 0x004F));
      }
      // Once learned, each window goes straight to the instance owning it.
      Assert.Equal(deniedWhileLearning, rig.DeniedCount);

      // Runtime configuration access to one chip, as NCT677X does to disable
      // its I/O space lock again, costs neither chip its windows.
      rig.EnterNuvoton(0x4E);
      rig.ReadConfig(0x4E, 0x28);
      rig.ExitNuvoton(0x4E);
      Assert.Equal((byte)0x90,
        rig.ReadMonitor(IteEnvironmentController, 0x58));
      Assert.Equal((byte)0x5C,
        rig.ReadBankedMonitor(NuvotonHardwareMonitor, 0x804F));

      // Each instance was bound to its slot once and never reset.
      Assert.Single(rig.Modules[0].Calls,
        c => c == LpcIoAdapter.SelectSlotFunction);
      Assert.Single(rig.Modules[1].Calls,
        c => c == LpcIoAdapter.SelectSlotFunction);
      Assert.DoesNotContain(NuvotonHardwareMonitor, rig.Modules[0].Bars);
      Assert.DoesNotContain(IteEnvironmentController, rig.Modules[1].Bars);
      Assert.False(ite.InConfigMode);
      Assert.False(nuvoton.InConfigMode);
      rig.AssertBufferSizesWereExact();
    }

    [Fact]
    public void LpcioDetectsAndReadsBothChipsThroughPawnIo() {
      FakeSuperIoChip ite = Ite();
      FakeSuperIoChip nuvoton = Nuvoton(0x4E);
      ite.SetMonitorRegister(0x29, 45);       // temperature 1: 45 degrees
      ite.SetMonitorRegister(0x20, 100);      // voltage 1: 100 x 12 mV
      nuvoton.SetMonitorRegister(0x480, 125); // voltage 1: 125 x 8 mV
      Rig rig = new Rig(ite, nuvoton);

      using PawnIoBackend backend = new PawnIoBackend(null, rig.Adapter);
      ILowLevelBackend previous = Ring0.SetBackendForTesting(backend);
      try {
        LPCIO lpcio = new LPCIO();

        ISuperIO[] chips = lpcio.SuperIO;
        Assert.Collection(chips,
          chip => Assert.Equal(Chip.IT8686E, chip.Chip),
          chip => Assert.Equal(Chip.NCT6798D, chip.Chip));
        Assert.Null(lpcio.GetReport());

        // Detection left both chips out of configuration mode, and the
        // Nuvoton chip with its hardware monitor device still selected.
        Assert.False(ite.InConfigMode);
        Assert.False(nuvoton.InConfigMode);
        Assert.Equal(ite.ConfigEntries, ite.ConfigExits);
        Assert.Equal(nuvoton.ConfigEntries, nuvoton.ConfigExits);
        Assert.Equal((byte)0x0B, nuvoton.SelectedDevice);

        foreach (ISuperIO chip in chips)
          chip.Update();

        Assert.Equal(45f, chips[0].Temperatures[0]);
        Assert.InRange(chips[0].Voltages[0].GetValueOrDefault(), 1.19f, 1.21f);
        Assert.InRange(chips[1].Voltages[0].GetValueOrDefault(), 0.99f, 1.01f);

        // A refused port is a failure, not a reading.
        Assert.False(Ring0.TryReadIoPort(0x0A48, out _));
        Assert.Equal((byte)0xFF, Ring0.ReadIoPort(0x0A48));
        rig.AssertBufferSizesWereExact();
      } finally {
        Ring0.SetBackendForTesting(previous);
      }
    }

    [Fact]
    public void WithoutABackendNothingIsProbed() {
      ILowLevelBackend previous = Ring0.SetBackendForTesting(new NullBackend());
      try {
        Assert.False(Ring0.SupportsIoPort);
        Assert.False(Ring0.PrepareSuperIoAccess(0x2E));
        Assert.False(Ring0.TryReadIoPort(0x2F, out byte value));
        Assert.Equal((byte)0xFF, value);
        Assert.Empty(new LPCIO().SuperIO);
      } finally {
        Ring0.SetBackendForTesting(previous);
      }
    }

    [Fact]
    public void EachModuleProvidesOnlyItsOwnCapability() {
      using (PawnIoBackend msrOnly =
        new PawnIoBackend(new StubMsrModule(), null)) {
        Assert.True(msrOnly.IsOpen);
        Assert.True(msrOnly.SupportsMsr);
        Assert.False(msrOnly.SupportsIoPort);
        Assert.False(msrOnly.SupportsPciConfig);
        Assert.True(msrOnly.ReadMsr(0x19C, out _, out _));
        Assert.False(msrOnly.TryReadIoPort(0x2E, out byte value));
        Assert.Equal((byte)0xFF, value);
        Assert.False(msrOnly.WriteIoPort(0x2E, 0x87));
        Assert.False(msrOnly.PrepareSuperIoAccess(0x2E, out _));

        ILowLevelBackend previous = Ring0.SetBackendForTesting(msrOnly);
        try {
          Assert.Equal(AccessTier.Deep, HardwareAccess.Tier);
          Assert.False(HardwareAccess.SupportsIoPort);
          Assert.StartsWith("Motherboard fan, voltage and temperature sensors",
            HardwareAccess.UnavailableReason);
        } finally {
          Ring0.SetBackendForTesting(previous);
        }
      }

      using (PawnIoBackend lpcIoOnly =
        new PawnIoBackend(null, new Rig(Ite()).Adapter)) {
        Assert.True(lpcIoOnly.IsOpen);
        Assert.False(lpcIoOnly.SupportsMsr);
        Assert.True(lpcIoOnly.SupportsIoPort);
        Assert.False(lpcIoOnly.ReadMsr(0x19C, out _, out _));

        ILowLevelBackend previous = Ring0.SetBackendForTesting(lpcIoOnly);
        try {
          Assert.Equal(AccessTier.Deep, HardwareAccess.Tier);
          Assert.StartsWith("Processor core temperatures and package power",
            HardwareAccess.UnavailableReason);
        } finally {
          Ring0.SetBackendForTesting(previous);
        }
      }

      using (PawnIoBackend both =
        new PawnIoBackend(new StubMsrModule(), new Rig(Ite()).Adapter)) {
        ILowLevelBackend previous = Ring0.SetBackendForTesting(both);
        try {
          Assert.True(HardwareAccess.SupportsModelSpecificRegisters);
          Assert.True(HardwareAccess.SupportsIoPort);
          Assert.False(HardwareAccess.SupportsPciConfig);
          Assert.Null(HardwareAccess.UnavailableReason);
        } finally {
          Ring0.SetBackendForTesting(previous);
        }
      }
    }
  }
}
