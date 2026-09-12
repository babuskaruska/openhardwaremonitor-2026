/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>
  /// A source of privileged hardware access (model specific registers, I/O
  /// ports, PCI configuration space).
  ///
  /// This exists because Open Hardware Monitor used to embed WinRing0 1.2.0,
  /// extract it to disk and install it as a kernel service. That driver grants
  /// unrestricted MSR writes, port I/O and physical memory access to any
  /// caller, which is CVE-2020-14979. It now sits on Microsoft's enforced
  /// vulnerable-driver blocklist, so on a current Windows install it is both
  /// flagged by Defender and refused by the loader — the application alarmed
  /// its users *and* silently read nothing.
  ///
  /// Privileged access is therefore no longer assumed. A backend is resolved
  /// at startup, may legitimately provide none of these capabilities, and
  /// callers are expected to degrade rather than fail. Everything that does
  /// not require ring 0 — processor topology, clocks, load, GPU, storage,
  /// memory — must keep working with no backend at all.
  /// </summary>
  internal interface ILowLevelBackend : IDisposable {

    /// <summary>Short name shown in reports and in the user interface.</summary>
    string Name { get; }

    /// <summary>True once the backend has successfully opened.</summary>
    bool IsOpen { get; }

    bool SupportsMsr { get; }
    bool SupportsIoPort { get; }
    bool SupportsPciConfig { get; }

    /// <summary>
    /// Attempts to acquire access. Must not throw: a backend that is simply
    /// not installed is an ordinary, expected outcome, not an error.
    /// </summary>
    bool TryOpen(out string? errorMessage);

    bool ReadMsr(uint index, out uint eax, out uint edx);
    bool WriteMsr(uint index, uint eax, uint edx);

    bool TryReadIoPort(uint port, out byte value);
    bool WriteIoPort(uint port, byte value);

    bool ReadPciConfig(uint pciAddress, uint regAddress, out uint value);
    bool WritePciConfig(uint pciAddress, uint regAddress, uint value);

    /// <summary>Diagnostic detail for the report window.</summary>
    string GetReport();
  }
}
