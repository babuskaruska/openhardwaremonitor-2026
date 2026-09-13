/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>
  /// Privileged access through PawnIO.
  ///
  /// Every capability comes from its own signed module, loaded into its own
  /// handle. A missing or rejected module disables only what it provides:
  /// processor sensors keep working without LpcIO, and motherboard sensors
  /// keep working without the MSR module.
  ///
  /// Intel MSR access follows IntelMSR.p from PawnIO.Modules:
  /// <c>ioctl_read_msr</c> takes [msr] and returns [value], and
  /// <c>ioctl_write_msr</c> takes [msr, value]. The module only permits a
  /// whitelist, which includes every register the processor sensors read
  /// (thermal status, temperature target, perf status, platform info, RAPL).
  ///
  /// The AMD module name and functions are UNVERIFIED.
  ///
  /// Port I/O follows LpcIO.p and reaches only motherboard Super I/O chips:
  /// their configuration ports (0x2E/0x2F and 0x4E/0x4F) and the address
  /// ranges the module discovers for them. <see cref="LpcIoAdapter"/> maps the
  /// raw port operations of the Super I/O code onto that model.
  ///
  /// PCI configuration space is not offered, because no PawnIO module exposes
  /// it generically.
  /// </summary>
  internal sealed class PawnIoBackend : ILowLevelBackend {

    // Module blob names, as published by the PawnIO.Modules project.
    private const string IntelMsrModule = "IntelMSR";
    private const string AmdMsrModule = "AMDFamily17";
    private const string LpcIoModule = "LpcIO";

    private const string ReadMsrFunction = "ioctl_read_msr";
    private const string WriteMsrFunction = "ioctl_write_msr";

    // E_ACCESSDENIED: PawnIO only admits administrators by default.
    private const int AccessDenied = unchecked((int)0x80070005);

    private IPawnIoModule? msrModule;
    private LpcIoAdapter? lpcIo;
    private readonly StringBuilder report = new StringBuilder();

    public PawnIoBackend() {
    }

    /// <summary>
    /// A backend over module instances that are already open, bypassing the
    /// driver. For unit tests. The backend takes ownership of both.
    /// </summary>
    internal PawnIoBackend(IPawnIoModule? msrModule, LpcIoAdapter? lpcIo) {
      this.msrModule = msrModule;
      this.lpcIo = lpcIo;
    }

    public string Name {
      get { return "PawnIO"; }
    }

    public bool IsOpen {
      get { return msrModule != null || lpcIo != null; }
    }

    public bool SupportsMsr {
      get { return msrModule != null; }
    }

    public bool SupportsIoPort {
      get { return lpcIo != null; }
    }

    public bool SupportsPciConfig {
      get { return false; }
    }

    /// <summary>Why the MSR module could not be used, or null.</summary>
    public string? MsrError { get; private set; }

    /// <summary>Why the LpcIO module could not be used, or null.</summary>
    public string? LpcIoError { get; private set; }

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

      msrModule = TryOpenModule(GetMsrModuleForCurrentProcessor(),
        out string? msrError);
      MsrError = msrError;

      lpcIo = TryOpenLpcIo(out string? lpcIoError);
      LpcIoError = lpcIoError;

      if (IsOpen)
        return true;

      // Both failed, usually for the same reason (not elevated), which is
      // then worth saying only once.
      errorMessage = msrError == lpcIoError || lpcIoError == null ? msrError :
        msrError == null ? lpcIoError : msrError + " " + lpcIoError;
      return false;
    }

    private IPawnIoModule? TryOpenModule(string moduleName,
      out string? errorMessage) {
      byte[]? blob = LoadBlob(moduleName, out string? path, out errorMessage);
      return blob == null ? null :
        OpenInstance(moduleName, moduleName, blob, path, out errorMessage);
    }

    /// <summary>
    /// LpcIO is opened once per Super I/O slot. The module keeps its selected
    /// slot and discovered address ranges per handle, so separate instances
    /// let a chip at 0x2E and a second chip at 0x4E be used side by side.
    /// </summary>
    private LpcIoAdapter? TryOpenLpcIo(out string? errorMessage) {
      byte[]? blob = LoadBlob(LpcIoModule, out string? path, out errorMessage);
      if (blob == null)
        return null;

      IPawnIoModule?[] instances =
        new IPawnIoModule?[LpcIoAdapter.SlotRegisterPorts.Count];
      bool any = false;
      for (int i = 0; i < instances.Length; i++) {
        string label = LpcIoModule + " slot " + i + " (0x" +
          LpcIoAdapter.SlotRegisterPorts[i].ToString("X2",
            CultureInfo.InvariantCulture) + ")";
        instances[i] = OpenInstance(LpcIoModule, label, blob, path,
          out string? instanceError);
        if (instances[i] != null)
          any = true;
        else
          errorMessage ??= instanceError;
      }

      if (!any)
        return null;

      // A slot whose instance failed to open stays unreachable; the report
      // says which one.
      errorMessage = null;
      return new LpcIoAdapter(instances);
    }

    private byte[]? LoadBlob(string moduleName, out string? path,
      out string? errorMessage) {
      errorMessage = null;
      byte[]? blob = PawnIOLib.TryLoadModuleBlob(moduleName, out path);
      if (blob == null) {
        errorMessage = "PawnIO is installed, but the " + moduleName +
          " module was not found. Place " + moduleName + ".bin from the " +
          "PawnIO.Modules release in " + PawnIOLib.GetModuleSearchPaths()[1] +
          ".";
        report.AppendLine("Module " + moduleName + ": not found in " +
          string.Join("; ", PawnIOLib.GetModuleSearchPaths()));
      }
      return blob;
    }

    private IPawnIoModule? OpenInstance(string moduleName, string label,
      byte[] blob, string? path, out string? errorMessage) {
      errorMessage = null;

      if (!PawnIOLib.TryOpen(out IntPtr handle, out int openResult)) {
        errorMessage = openResult == AccessDenied
          ? "PawnIO requires Open Hardware Monitor to run as administrator."
          : "The PawnIO driver could not be opened (0x" +
            openResult.ToString("X8") + ").";
        report.AppendLine("Module " + label + ": driver open failed, 0x" +
          openResult.ToString("X8"));
        return null;
      }

      if (!PawnIOLib.TryLoad(handle, blob, out int loadResult)) {
        errorMessage = "PawnIO rejected the " + moduleName + " module (0x" +
          loadResult.ToString("X8") + ").";
        report.AppendLine("Module " + label + " from " + path +
          ": load rejected, 0x" + loadResult.ToString("X8"));
        PawnIOLib.Close(handle);
        return null;
      }

      report.AppendLine("Module " + label + ": loaded from " + path);
      return new PawnIoModule(handle);
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
      if (msrModule == null)
        return false;

      ulong[] input = { index };
      ulong[] output = new ulong[1];
      if (!msrModule.Execute(ReadMsrFunction, input, output, out _))
        return false;

      eax = (uint)(output[0] & 0xFFFFFFFF);
      edx = (uint)(output[0] >> 32);
      return true;
    }

    public bool WriteMsr(uint index, uint eax, uint edx) {
      if (msrModule == null)
        return false;

      ulong[] input = { index, ((ulong)edx << 32) | eax };
      return msrModule.Execute(WriteMsrFunction, input, Array.Empty<ulong>(),
        out _);
    }

    public bool TryReadIoPort(uint port, out byte value) {
      if (lpcIo == null) {
        value = 0xFF;
        return false;
      }
      return lpcIo.TryReadPort(port, out value);
    }

    public bool WriteIoPort(uint port, byte value) {
      return lpcIo != null && lpcIo.WritePort(port, value);
    }

    public bool PrepareSuperIoAccess(ushort registerPort, out string? detail) {
      if (lpcIo == null) {
        detail = null;
        return false;
      }
      bool prepared = lpcIo.PrepareSuperIoAccess(registerPort,
        out string text);
      detail = text;
      return prepared;
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
      msrModule?.Dispose();
      msrModule = null;
      lpcIo?.Dispose();
      lpcIo = null;
    }
  }
}
