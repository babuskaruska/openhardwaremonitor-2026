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
    /// A low-level backend is active. Depending on which of its modules
    /// loaded, this additionally allows processor core temperatures and
    /// package power, and motherboard fan, voltage and temperature sensors.
    /// <see cref="HardwareAccess.UnavailableReason"/> names anything that is
    /// still missing.
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

    /// <summary>
    /// True when motherboard Super I/O chips can be reached, which their fan,
    /// voltage and temperature sensors need. Through PawnIO this is not
    /// general port access: only the chips' configuration ports and the
    /// address ranges its LpcIO module discovered for them are allowed.
    /// </summary>
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
        if (Tier == AccessTier.Deep) {
          string? processor = Ring0.SupportsMsr ? null : Explain(
            "Processor core temperatures and package power are unavailable.",
            Ring0.MsrError);
          string? motherboard = Ring0.SupportsIoPort ? null : Explain(
            "Motherboard fan, voltage and temperature sensors are " +
            "unavailable.", Ring0.IoPortError);
          return Join(processor, motherboard);
        }

        string? error = Ring0.BackendError;
        if (LowLevel.PawnIOLib.IsInstalled && !string.IsNullOrEmpty(error))
          return "Processor core temperatures, package power and motherboard " +
            "sensors are unavailable. " + error;

        return "Processor core temperatures, package power and motherboard " +
          "fan and voltage sensors require a low-level driver. Install " +
          "PawnIO to enable them (" + InstallCommand + "). Everything else " +
          "is available without it.";
      }
    }

    private static string Explain(string what, string? why) {
      return string.IsNullOrEmpty(why) ? what : what + " " + why;
    }

    private static string? Join(string? first, string? second) {
      if (first == null)
        return second;
      if (second == null)
        return first;
      return first + " " + second;
    }
  }
}
