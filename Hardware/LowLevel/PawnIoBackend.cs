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
  /// Intel MSR access follows IntelMSR.p from PawnIO.Modules:
  /// <c>ioctl_read_msr</c> takes [msr] and returns [value], and
  /// <c>ioctl_write_msr</c> takes [msr, value]. The module only permits a
  /// whitelist, which includes every register the processor sensors read
  /// (thermal status, temperature target, perf status, platform info, RAPL).
  ///
  /// The AMD module name and functions are UNVERIFIED.
  ///
  /// Port I/O and PCI configuration are deliberately not offered. PawnIO has
  /// no arbitrary port access: LpcIO.p exposes a Super I/O slot model
  /// (ioctl_select_slot, ioctl_find_bars, ioctl_pio_inb/outb restricted to
  /// the chip's register ports and discovered BARs), and no module exposes
  /// generic PCI configuration space. The Super I/O and AMD code issues raw
  /// port and PCI operations, so it needs an adapter to that model before
  /// it can run on PawnIO.
  /// </summary>
  internal sealed class PawnIoBackend : ILowLevelBackend {

    // Module blob names, as published by the PawnIO.Modules project.
    private const string IntelMsrModule = "IntelMSR";
    private const string AmdMsrModule = "AMDFamily17";

    private const string ReadMsrFunction = "ioctl_read_msr";
    private const string WriteMsrFunction = "ioctl_write_msr";

    // E_ACCESSDENIED: PawnIO only admits administrators by default.
    private const int AccessDenied = unchecked((int)0x80070005);

    private IntPtr msrHandle;
    private readonly StringBuilder report = new StringBuilder();

    public string Name {
      get { return "PawnIO"; }
    }

    public bool IsOpen {
      get { return msrHandle != IntPtr.Zero; }
    }

    public bool SupportsMsr {
      get { return msrHandle != IntPtr.Zero; }
    }

    public bool SupportsIoPort {
      get { return false; }
    }

    public bool SupportsPciConfig {
      get { return false; }
    }

    public bool TryOpen(out string? errorMessage) {
      errorMessage = null;

      if (!PawnIOLib.IsInstalled) {
        errorMessage = "PawnIO is not installed.";
        report.AppendLine("PawnIOLib not found or not loadable at " +
          PawnIOLib.LibraryPath + ".");
        return false;
      }

      report.AppendLine("PawnIOLib " +
        PawnIOLib.FormatVersion(PawnIOLib.Version) + " at " +
        PawnIOLib.LibraryPath);

      string module = GetMsrModuleForCurrentProcessor();
      msrHandle = TryOpenModule(module, out errorMessage);
      return msrHandle != IntPtr.Zero;
    }

    private IntPtr TryOpenModule(string moduleName, out string? errorMessage) {
      errorMessage = null;

      byte[]? blob = PawnIOLib.TryLoadModuleBlob(moduleName, out string? path);
      if (blob == null) {
        errorMessage = "PawnIO is installed, but the " + moduleName +
          " module was not found. Place " + moduleName + ".bin from the " +
          "PawnIO.Modules release in " + PawnIOLib.GetModuleSearchPaths()[1] +
          ".";
        report.AppendLine("Module " + moduleName + ": not found in " +
          string.Join("; ", PawnIOLib.GetModuleSearchPaths()));
        return IntPtr.Zero;
      }

      if (!PawnIOLib.TryOpen(out IntPtr handle, out int openResult)) {
        errorMessage = openResult == AccessDenied
          ? "PawnIO requires Open Hardware Monitor to run as administrator."
          : "The PawnIO driver could not be opened (0x" +
            openResult.ToString("X8") + ").";
        report.AppendLine("Module " + moduleName + ": driver open failed, 0x" +
          openResult.ToString("X8"));
        return IntPtr.Zero;
      }

      if (!PawnIOLib.TryLoad(handle, blob, out int loadResult)) {
        errorMessage = "PawnIO rejected the " + moduleName + " module (0x" +
          loadResult.ToString("X8") + ").";
        report.AppendLine("Module " + moduleName + " from " + path +
          ": load rejected, 0x" + loadResult.ToString("X8"));
        PawnIOLib.Close(handle);
        return IntPtr.Zero;
      }

      report.AppendLine("Module " + moduleName + ": loaded from " + path);
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
      return false;
    }

    public bool WriteIoPort(uint port, byte value) {
      return false;
    }

    public bool ReadPciConfig(uint pciAddress, uint regAddress,
      out uint value) {
      value = 0;
      return false;
    }

    public bool WritePciConfig(uint pciAddress, uint regAddress, uint value) {
      return false;
    }

    public string GetReport() {
      return report.ToString();
    }

    public void Dispose() {
      if (msrHandle != IntPtr.Zero) {
        PawnIOLib.Close(msrHandle);
        msrHandle = IntPtr.Zero;
      }
    }
  }
}
