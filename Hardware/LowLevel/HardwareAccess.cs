/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// How much of the hardware this process can currently reach.
  /// </summary>
  public enum AccessTier {

    /// <summary>
    /// No privileged access, and nothing installed. Processor topology,
    /// clocks and load, GPUs, storage (including NVMe health), memory and
    /// network are all still available.
    /// </summary>
    Base,

    /// <summary>
    /// A low-level backend is active, additionally allowing processor core
    /// temperatures, package power, and motherboard fan and voltage sensors.
    /// </summary>
    Deep
  }

  /// <summary>
  /// Public, read-only view of the low-level access situation, so the user
  /// interface can explain which sensors are available and why.
  ///
  /// This is deliberately honest rather than reassuring: a monitoring tool
  /// that silently shows nothing where a temperature should be is worse than
  /// one that says it cannot read temperatures and what to do about it.
  /// </summary>
  public static class HardwareAccess {

    /// <summary>Command that enables the Deep tier.</summary>
    public const string InstallCommand =
      "winget install -e --id namazso.PawnIO";

    public static AccessTier Tier {
      get { return Ring0.IsOpen ? AccessTier.Deep : AccessTier.Base; }
    }

    /// <summary>Name of the active backend, for display.</summary>
    public static string BackendName {
      get { return Ring0.BackendName; }
    }

    public static bool SupportsModelSpecificRegisters {
      get { return Ring0.SupportsMsr; }
    }

    public static bool SupportsIoPort {
      get { return Ring0.SupportsIoPort; }
    }

    public static bool SupportsPciConfig {
      get { return Ring0.SupportsPciConfig; }
    }

    /// <summary>
    /// A sentence suitable for showing to the user, explaining what is
    /// missing and how to get it. Null when everything is available.
    /// </summary>
    public static string? UnavailableReason {
      get {
        if (Tier == AccessTier.Deep)
          return null;
        return "Processor core temperatures, package power and motherboard " +
          "fan and voltage sensors require a low-level driver. Install " +
          "PawnIO to enable them (" + InstallCommand + "). Everything else " +
          "is available without it.";
      }
    }
  }
}
