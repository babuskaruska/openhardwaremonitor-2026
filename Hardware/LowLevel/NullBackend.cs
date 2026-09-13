/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>
  /// The default backend: no kernel driver, no privileged access, nothing
  /// installed and nothing written to disk.
  ///
  /// This is what makes the application antivirus-clean out of the box. Every
  /// operation reports failure so that callers skip registering the sensors
  /// they cannot actually read, rather than registering them and publishing
  /// null forever.
  /// </summary>
  internal sealed class NullBackend : ILowLevelBackend {

    public string Name {
      get { return "None (driver-free)"; }
    }

    public bool IsOpen {
      get { return true; }
    }

    public bool SupportsMsr {
      get { return false; }
    }

    public bool SupportsIoPort {
      get { return false; }
    }

    public bool SupportsPciConfig {
      get { return false; }
    }

    public bool TryOpen(out string? errorMessage) {
      errorMessage = null;
      return true;
    }

    public bool ReadMsr(uint index, out uint eax, out uint edx) {
      eax = 0;
      edx = 0;
      return false;
    }

    public bool WriteMsr(uint index, uint eax, uint edx) {
      return false;
    }

    public bool TryReadIoPort(uint port, out byte value) {
      value = 0xFF;
      return false;
    }

    public bool WriteIoPort(uint port, byte value) {
      return false;
    }

    public bool PrepareSuperIoAccess(ushort registerPort, out string? detail) {
      detail = null;
      return false;
    }

    public bool ReadPciConfig(uint pciAddress, uint regAddress, out uint value) {
      value = 0;
      return false;
    }

    public bool WritePciConfig(uint pciAddress, uint regAddress, uint value) {
      return false;
    }

    public string GetReport() {
      return
        "No low-level backend is active." + System.Environment.NewLine +
        "Processor temperatures, package power and motherboard sensors are " +
        "unavailable." + System.Environment.NewLine +
        "Install PawnIO to enable them: winget install -e --id namazso.PawnIO" +
        System.Environment.NewLine;
    }

    public void Dispose() {
    }
  }
}
