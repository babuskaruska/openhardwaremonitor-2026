/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Text;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>
  /// Privileged access through PawnIO.
  ///
  /// UNVERIFIED. This has been written against PawnIO's published interface
  /// but has not been exercised against a running driver, because installing
  /// one requires administrative elevation. Treat the module names and
  /// exported function names below as the most likely thing to be wrong; they
  /// are isolated here as constants for exactly that reason. Every failure
  /// path degrades to "capability unavailable" rather than throwing, so an
  /// incorrect guess costs the Deep tier, not stability.
  ///
  /// To verify: install PawnIO plus its module package, then confirm that
  /// processor core temperatures appear.
  /// </summary>
  internal sealed class PawnIoBackend : ILowLevelBackend {

    // Module blob names, as published by the PawnIO.Modules project.
    private const string IntelMsrModule = "IntelMSR";
    private const string AmdMsrModule = "AMDFamily17";
    private const string LpcModule = "LpcIO";

    // Exported Pawn function names invoked through pawnio_execute.
    private const string ReadMsrFunction = "ioctl_read_msr";
    private const string WriteMsrFunction = "ioctl_write_msr";
    private const string ReadPortFunction = "ioctl_read_port";
    private const string WritePortFunction = "ioctl_write_port";
    private const string ReadPciFunction = "ioctl_pci_read";
    private const string WritePciFunction = "ioctl_pci_write";

    private IntPtr msrHandle;
    private IntPtr lpcHandle;
    private readonly StringBuilder report = new StringBuilder();
    private bool isOpen;

    public string Name {
      get { return "PawnIO"; }
    }

    public bool IsOpen {
      get { return isOpen; }
    }

    public bool SupportsMsr {
      get { return msrHandle != IntPtr.Zero; }
    }

    // Port I/O and PCI configuration both come from the LPC module.
    public bool SupportsIoPort {
      get { return lpcHandle != IntPtr.Zero; }
    }

    public bool SupportsPciConfig {
      get { return lpcHandle != IntPtr.Zero; }
    }

    public bool TryOpen(out string? errorMessage) {
      errorMessage = null;

      if (!PawnIOLib.IsInstalled) {
        errorMessage = "PawnIO is not installed.";
        report.AppendLine("PawnIOLib could not be loaded.");
        return false;
      }

      report.AppendLine("PawnIO version: 0x" +
        PawnIOLib.Version.ToString("X8"));

      string msrModule = GetMsrModuleForCurrentProcessor();
      msrHandle = TryOpenModule(msrModule);
      lpcHandle = TryOpenModule(LpcModule);

      isOpen = msrHandle != IntPtr.Zero || lpcHandle != IntPtr.Zero;
      if (!isOpen)
        errorMessage = "PawnIO is installed but no usable module was loaded.";
      return isOpen;
    }

    private IntPtr TryOpenModule(string moduleName) {
      byte[]? blob = PawnIOLib.TryLoadModuleBlob(moduleName);
      if (blob == null) {
        report.AppendLine("Module " + moduleName + ": blob not found.");
        return IntPtr.Zero;
      }

      if (!PawnIOLib.TryOpen(out IntPtr handle)) {
        report.AppendLine("Module " + moduleName + ": driver open failed.");
        return IntPtr.Zero;
      }

      if (!PawnIOLib.TryLoad(handle, blob)) {
        report.AppendLine("Module " + moduleName + ": load rejected " +
          "(signature or version mismatch).");
        PawnIOLib.Close(handle);
        return IntPtr.Zero;
      }

      report.AppendLine("Module " + moduleName + ": loaded.");
      return handle;
    }

    private static string GetMsrModuleForCurrentProcessor() {
      CpuInstructions.Cpuid(0, 0, out _, out uint ebx, out uint ecx, out _);
      // "AuthenticAMD" places 'htuA' in EBX and 'DMAc' in ECX.
      bool isAmd = ebx == 0x68747541 && ecx == 0x444D4163;
      return isAmd ? AmdMsrModule : IntelMsrModule;
    }

    public bool ReadMsr(uint index, out uint eax, out uint edx) {
      eax = 0;
      edx = 0;
      if (msrHandle == IntPtr.Zero)
        return false;

      ulong[] input = { index };
      ulong[] output = new ulong[1];
      if (!PawnIOLib.TryExecute(msrHandle, ReadMsrFunction, input, output))
        return false;

      eax = (uint)(output[0] & 0xFFFFFFFF);
      edx = (uint)(output[0] >> 32);
      return true;
    }

    public bool WriteMsr(uint index, uint eax, uint edx) {
      if (msrHandle == IntPtr.Zero)
        return false;

      ulong[] input = { index, ((ulong)edx << 32) | eax };
      return PawnIOLib.TryExecute(msrHandle, WriteMsrFunction, input,
        Array.Empty<ulong>());
    }

    public bool TryReadIoPort(uint port, out byte value) {
      value = 0xFF;
      if (lpcHandle == IntPtr.Zero)
        return false;

      ulong[] input = { port };
      ulong[] output = new ulong[1];
      if (!PawnIOLib.TryExecute(lpcHandle, ReadPortFunction, input, output))
        return false;

      value = (byte)(output[0] & 0xFF);
      return true;
    }

    public bool WriteIoPort(uint port, byte value) {
      if (lpcHandle == IntPtr.Zero)
        return false;

      ulong[] input = { port, value };
      return PawnIOLib.TryExecute(lpcHandle, WritePortFunction, input,
        Array.Empty<ulong>());
    }

    public bool ReadPciConfig(uint pciAddress, uint regAddress,
      out uint value) {
      value = 0;
      if (lpcHandle == IntPtr.Zero || (regAddress & 3) != 0)
        return false;

      ulong[] input = { pciAddress, regAddress };
      ulong[] output = new ulong[1];
      if (!PawnIOLib.TryExecute(lpcHandle, ReadPciFunction, input, output))
        return false;

      value = (uint)(output[0] & 0xFFFFFFFF);
      return true;
    }

    public bool WritePciConfig(uint pciAddress, uint regAddress, uint value) {
      if (lpcHandle == IntPtr.Zero || (regAddress & 3) != 0)
        return false;

      ulong[] input = { pciAddress, regAddress, value };
      return PawnIOLib.TryExecute(lpcHandle, WritePciFunction, input,
        Array.Empty<ulong>());
    }

    public string GetReport() {
      return report.ToString();
    }

    public void Dispose() {
      if (msrHandle != IntPtr.Zero) {
        PawnIOLib.Close(msrHandle);
        msrHandle = IntPtr.Zero;
      }
      if (lpcHandle != IntPtr.Zero) {
        PawnIOLib.Close(lpcHandle);
        lpcHandle = IntPtr.Zero;
      }
      isOpen = false;
    }
  }
}
