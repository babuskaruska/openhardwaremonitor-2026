/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>The steps that lead to full sensor access, in order.</summary>
  public enum PawnIoSetupStep {
    InstallDriver,
    InstallModules,
    RestartAsAdministrator
  }

  /// <summary>
  /// Where the user stands on the way to the Deep access tier: PawnIO
  /// installed, the modules this processor needs present, and this process
  /// elevated with the modules actually loaded.
  ///
  /// A snapshot, cheap enough to capture on every settings refresh. Modules
  /// only load when the application starts, so installing everything during a
  /// session still leaves the restart step to do.
  /// </summary>
  public sealed class PawnIoSetupStatus {

    private static readonly Lazy<bool> isElevated = new Lazy<bool>(DetectElevation);
    private static readonly Lazy<bool> isAmd = new Lazy<bool>(PawnIoBackend.IsAmdProcessor);

    private PawnIoSetupStatus(bool driverInstalled, string? driverVersion,
      IReadOnlyList<string> requiredModules, IReadOnlyList<string> missingModules,
      string modulesDirectory, bool elevated, AccessTier tier,
      bool fullAccessActive) {
      DriverInstalled = driverInstalled;
      DriverVersion = driverVersion;
      RequiredModules = requiredModules;
      MissingModules = missingModules;
      ModulesDirectory = modulesDirectory;
      IsElevated = elevated;
      Tier = tier;
      FullAccessActive = fullAccessActive;

      List<PawnIoSetupStep> remaining = new List<PawnIoSetupStep>();
      if (!driverInstalled)
        remaining.Add(PawnIoSetupStep.InstallDriver);
      if (missingModules.Count > 0)
        remaining.Add(PawnIoSetupStep.InstallModules);
      if (!fullAccessActive)
        remaining.Add(PawnIoSetupStep.RestartAsAdministrator);
      RemainingSteps = remaining;
    }

    public bool DriverInstalled { get; }

    /// <summary>PawnIO's version as "major.minor.patch", or null.</summary>
    public string? DriverVersion { get; }

    /// <summary>Module names (without ".bin") this processor needs.</summary>
    public IReadOnlyList<string> RequiredModules { get; }

    public IReadOnlyList<string> MissingModules { get; }

    public bool ModulesInstalled {
      get { return MissingModules.Count == 0; }
    }

    /// <summary>Where the setup installs modules.</summary>
    public string ModulesDirectory { get; }

    public bool IsElevated { get; }

    public AccessTier Tier { get; }

    /// <summary>
    /// True when this process runs as administrator and already uses both the
    /// processor and the motherboard module.
    /// </summary>
    public bool FullAccessActive { get; }

    /// <summary>Steps still to do, in the order they should be done.</summary>
    public IReadOnlyList<PawnIoSetupStep> RemainingSteps { get; }

    public bool IsComplete {
      get { return RemainingSteps.Count == 0; }
    }

    public bool IsDone(PawnIoSetupStep step) {
      foreach (PawnIoSetupStep remaining in RemainingSteps)
        if (remaining == step)
          return false;
      return true;
    }

    /// <summary>The current state of this process and computer.</summary>
    public static PawnIoSetupStatus Capture() {
      uint version = PawnIOLib.Version;
      return Create(version, isAmd.Value, PawnIOLib.GetModuleSearchPaths(),
        File.Exists, DefaultModulesDirectory, isElevated.Value,
        HardwareAccess.Tier,
        HardwareAccess.SupportsModelSpecificRegisters && HardwareAccess.SupportsIoPort);
    }

    /// <summary>The per-user folder the module installer writes to.</summary>
    public static string DefaultModulesDirectory {
      get { return PawnIOLib.GetModuleSearchPaths()[1]; }
    }

    /// <param name="driverVersion">PawnIOLib's packed version, 0 when it is
    /// not installed.</param>
    /// <param name="modulesActive">Whether both the MSR and the LpcIO module
    /// are loaded in this process.</param>
    internal static PawnIoSetupStatus Create(uint driverVersion, bool amdProcessor,
      IEnumerable<string> searchDirectories, Func<string, bool> fileExists,
      string modulesDirectory, bool elevated, AccessTier tier, bool modulesActive) {

      IReadOnlyList<string> required = GetRequiredModules(amdProcessor);
      List<string> missing = new List<string>();
      foreach (string module in required)
        if (!IsModulePresent(module, searchDirectories, fileExists))
          missing.Add(module);

      bool driverInstalled = driverVersion != 0;
      return new PawnIoSetupStatus(driverInstalled,
        driverInstalled ? PawnIOLib.FormatVersion(driverVersion) : null,
        required, missing, modulesDirectory, elevated, tier,
        elevated && tier == AccessTier.Deep && modulesActive);
    }

    /// <summary>
    /// The modules <see cref="PawnIoBackend"/> loads on this kind of
    /// processor: the vendor's MSR module and LpcIO for motherboard chips.
    /// </summary>
    internal static IReadOnlyList<string> GetRequiredModules(bool amdProcessor) {
      return new[] {
        PawnIoBackend.GetMsrModule(amdProcessor),
        PawnIoBackend.LpcIoModule
      };
    }

    private static bool IsModulePresent(string module,
      IEnumerable<string> searchDirectories, Func<string, bool> fileExists) {
      foreach (string directory in searchDirectories) {
        try {
          if (fileExists(Path.Combine(directory, module + ".bin")))
            return true;
        } catch (Exception) {
          // An unusable search path counts as not containing the module.
        }
      }
      return false;
    }

    private static bool DetectElevation() {
      try {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
          return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
      } catch (Exception) {
        return false;
      }
    }
  }
}
