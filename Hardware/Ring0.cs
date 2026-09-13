/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2010-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using OpenHardwareMonitor.Hardware.LowLevel;

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// Facade over whichever <see cref="ILowLevelBackend"/> is active.
  ///
  /// This class used to own WinRing0 outright: it extracted the driver from an
  /// embedded resource, wrote it next to the assembly, registered it as a
  /// kernel service and deleted the file again. All of that is gone. See
  /// <see cref="ILowLevelBackend"/> for why.
  ///
  /// The static shape is kept deliberately. Thirteen files call
  /// <c>Ring0.Rdmsr</c>, <c>Ring0.ReadIoPort</c> and friends; routing them
  /// through a swappable backend here changes one file instead of fourteen.
  /// </summary>
  internal static class Ring0 {

    private static ILowLevelBackend backend = new NullBackend();
    private static Mutex? isaBusMutex;
    private static Mutex? pciBusMutex;
    private static readonly StringBuilder report = new StringBuilder();

    /// <summary>
    /// Resolves a backend and acquires the shared bus mutexes.
    ///
    /// Note that the mutexes are acquired regardless of which backend wins,
    /// including the null one. They coordinate with other monitoring tools
    /// (HWiNFO, AIDA64, CPU-Z) rather than with our own driver, so they are
    /// not the driver's business.
    /// </summary>
    public static void Open() {
      OpenBackend();
      OpenBusMutexes();
    }

    /// <summary>
    /// Why PawnIO could not be used, or null when it is active. Lets the user
    /// interface give the actual reason (module missing, not elevated) rather
    /// than a generic installation hint.
    /// </summary>
    public static string? BackendError { get; private set; }

    /// <summary>
    /// Why model specific registers are unavailable although the backend is
    /// active, or null. PawnIO loads a module per capability, so one can be
    /// missing while another works.
    /// </summary>
    public static string? MsrError {
      get { return (backend as PawnIoBackend)?.MsrError; }
    }

    /// <summary>
    /// Why motherboard Super I/O port access is unavailable although the
    /// backend is active, or null.
    /// </summary>
    public static string? IoPortError {
      get { return (backend as PawnIoBackend)?.LpcIoError; }
    }

    private static void OpenBackend() {
      if (backend.IsOpen && !(backend is NullBackend))
        return;

      PawnIoBackend pawnIo = new PawnIoBackend();
      if (pawnIo.TryOpen(out string? error)) {
        backend = pawnIo;
        BackendError = null;
        report.AppendLine("Backend: " + pawnIo.Name);
        report.Append(pawnIo.GetReport());
        return;
      }

      BackendError = error;
      report.AppendLine("Backend: none");
      report.Append(pawnIo.GetReport());
      pawnIo.Dispose();
      if (!string.IsNullOrEmpty(error))
        report.AppendLine(error);
      backend = new NullBackend();
      report.Append(backend.GetReport());
    }

    private static void OpenBusMutexes() {
      isaBusMutex ??= TryCreateBusMutex("Global\\Access_ISABUS.HTP.Method");
      pciBusMutex ??= TryCreateBusMutex("Global\\Access_PCI");
    }

    private static Mutex? TryCreateBusMutex(string name) {
      try {
        return new Mutex(false, name);
      } catch (UnauthorizedAccessException) {
        // Another tool owns it and we are not allowed to create it; joining
        // the existing one is the whole point of a shared bus lock.
        try {
          return MutexAcl.OpenExisting(name, MutexRights.Synchronize);
        } catch (Exception) {
          return null;
        }
      } catch (Exception) {
        return null;
      }
    }

    public static bool IsOpen {
      get { return backend.IsOpen && HasAnyCapability; }
    }

    private static bool HasAnyCapability {
      get {
        return backend.SupportsMsr || backend.SupportsIoPort ||
          backend.SupportsPciConfig;
      }
    }

    /// <summary>Name of the active backend, for display.</summary>
    public static string BackendName {
      get { return backend.Name; }
    }

    public static bool SupportsMsr {
      get { return backend.SupportsMsr; }
    }

    public static bool SupportsIoPort {
      get { return backend.SupportsIoPort; }
    }

    public static bool SupportsPciConfig {
      get { return backend.SupportsPciConfig; }
    }

    public static void Close() {
      backend.Dispose();
      backend = new NullBackend();

      isaBusMutex?.Close();
      isaBusMutex = null;
      pciBusMutex?.Close();
      pciBusMutex = null;

      report.Length = 0;
    }

    public static string? GetReport() {
      if (report.Length == 0)
        return null;

      StringBuilder r = new StringBuilder();
      r.AppendLine("Ring0");
      r.AppendLine();
      r.Append(report);
      r.AppendLine();
      return r.ToString();
    }

    // ---- shared bus mutexes -------------------------------------------------
    //
    // A null mutex reports success: these are best-effort cooperation with
    // other tools, and failing to obtain one must never disable sensing.

    public static bool WaitIsaBusMutex(int millisecondsTimeout) {
      return Wait(isaBusMutex, millisecondsTimeout);
    }

    public static void ReleaseIsaBusMutex() {
      Release(isaBusMutex);
    }

    public static bool WaitPciBusMutex(int millisecondsTimeout) {
      return Wait(pciBusMutex, millisecondsTimeout);
    }

    public static void ReleasePciBusMutex() {
      Release(pciBusMutex);
    }

    private static bool Wait(Mutex? mutex, int millisecondsTimeout) {
      if (mutex == null)
        return true;
      try {
        return mutex.WaitOne(millisecondsTimeout, false);
      } catch (AbandonedMutexException) {
        // The previous owner died holding it; ownership has passed to us.
        return true;
      } catch (InvalidOperationException) {
        return false;
      }
    }

    private static void Release(Mutex? mutex) {
      if (mutex == null)
        return;
      try {
        mutex.ReleaseMutex();
      } catch (ApplicationException) {
        // Not the owner. Nothing to release, and nothing worth failing over.
      }
    }

    // ---- model specific registers -------------------------------------------

    public static bool Rdmsr(uint index, out uint eax, out uint edx) {
      return backend.ReadMsr(index, out eax, out edx);
    }

    public static bool RdmsrTx(uint index, out uint eax, out uint edx,
      GroupAffinity affinity) {

      GroupAffinity previousAffinity = ThreadAffinity.Set(affinity);
      try {
        return backend.ReadMsr(index, out eax, out edx);
      } finally {
        ThreadAffinity.Set(previousAffinity);
      }
    }

    public static bool Wrmsr(uint index, uint eax, uint edx) {
      return backend.WriteMsr(index, eax, edx);
    }

    // ---- port I/O -----------------------------------------------------------

    /// <summary>
    /// Reads a byte from an I/O port, returning 0xFF when no backend can
    /// service the read.
    ///
    /// 0xFF rather than 0x00 is deliberate: an unclaimed read on a real
    /// ISA/LPC bus floats high, so 0xFF is the value chip-detection code
    /// already treats as "nothing there". Returning 0x00 made an absent
    /// driver indistinguishable from a register that genuinely reads zero.
    /// Prefer <see cref="TryReadIoPort"/> in new code.
    /// </summary>
    public static byte ReadIoPort(uint port) {
      return backend.TryReadIoPort(port, out byte value) ? value : (byte)0xFF;
    }

    public static bool TryReadIoPort(uint port, out byte value) {
      return backend.TryReadIoPort(port, out value);
    }

    public static bool WriteIoPort(uint port, byte value) {
      return backend.WriteIoPort(port, value);
    }

    /// <summary>
    /// Super I/O detection calls this once the chip at
    /// <paramref name="registerPort"/> is in configuration mode and has
    /// returned a valid chip ID, before it leaves configuration mode.
    ///
    /// PawnIO only allows port access to a Super I/O chip's address ranges
    /// after its LpcIO module has discovered them, which in turn needs
    /// configuration mode. Backends without that restriction do nothing.
    /// Returns true when the chip's ranges were made accessible.
    /// </summary>
    public static bool PrepareSuperIoAccess(ushort registerPort) {
      bool prepared = backend.PrepareSuperIoAccess(registerPort,
        out string? detail);
      if (!string.IsNullOrEmpty(detail))
        report.AppendLine(detail);
      return prepared;
    }

    /// <summary>
    /// Replaces the active backend and returns the previous one, without
    /// opening, closing or disposing either. For unit tests only.
    /// </summary>
    internal static ILowLevelBackend SetBackendForTesting(
      ILowLevelBackend replacement) {
      ILowLevelBackend previous = backend;
      backend = replacement ??
        throw new ArgumentNullException(nameof(replacement));
      return previous;
    }

    // ---- PCI configuration space --------------------------------------------

    public const uint InvalidPciAddress = 0xFFFFFFFF;

    public static uint GetPciAddress(byte bus, byte device, byte function) {
      return (uint)(((bus & 0xFF) << 8) | ((device & 0x1F) << 3) |
        (function & 7));
    }

    public static bool ReadPciConfig(uint pciAddress, uint regAddress,
      out uint value) {
      return backend.ReadPciConfig(pciAddress, regAddress, out value);
    }

    public static bool WritePciConfig(uint pciAddress, uint regAddress,
      uint value) {
      return backend.WritePciConfig(pciAddress, regAddress, value);
    }
  }
}
