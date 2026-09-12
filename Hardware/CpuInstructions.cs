/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2010-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// Replaces the former <c>Opcode</c> class, which allocated a
  /// PAGE_EXECUTE_READWRITE page, copied hand-assembled CPUID/RDTSC machine
  /// code into it, and invoked it through a function pointer.
  ///
  /// That approach is no longer viable and was never necessary here:
  ///  - RWX-allocate-then-execute is one of the most reliable malware
  ///    heuristics there is, and was an independent reason this application
  ///    was flagged by antivirus, quite apart from the WinRing0 driver.
  ///  - The page was never re-protected to PAGE_EXECUTE_READ, and the
  ///    indirect call was not a registered Control Flow Guard target.
  ///  - It fails outright under Arbitrary Code Guard.
  ///
  /// CPUID is an unprivileged instruction, so the JIT can emit it directly.
  /// No kernel driver and no dynamic code are involved in anything here.
  /// </summary>
  internal static class CpuInstructions {

    /// <summary>
    /// True when CPUID can be issued. False on ARM64, where the caller is
    /// expected to fall back to operating-system topology information.
    /// </summary>
    public static bool IsAvailable {
      get { return X86Base.IsSupported; }
    }

    /// <summary>
    /// Executes CPUID for the given leaf and sub-leaf. Unlike the code this
    /// replaces, <paramref name="ecxValue"/> is honoured, which is required
    /// to read the topology (0x0B/0x1F), hybrid (0x1A) and cache (0x04)
    /// leaves that modern processors expose only through sub-leaves.
    /// </summary>
    public static bool Cpuid(uint index, uint ecxValue,
      out uint eax, out uint ebx, out uint ecx, out uint edx) {

      if (!X86Base.IsSupported) {
        eax = ebx = ecx = edx = 0;
        return false;
      }

      (int a, int b, int c, int d) = X86Base.CpuId((int)index, (int)ecxValue);
      eax = (uint)a;
      ebx = (uint)b;
      ecx = (uint)c;
      edx = (uint)d;
      return true;
    }

    /// <summary>
    /// Nominal time stamp counter frequency in MHz.
    ///
    /// The previous implementation busy-waited on Stopwatch for up to five
    /// 25 ms windows at startup, sampling RDTSC at each end, and kept
    /// re-estimating on every update. On any processor with an invariant TSC
    /// that is a noisy measurement of a constant the processor will simply
    /// report, so we ask instead. This removes ~125 ms of spinning from
    /// startup and yields an exact figure rather than one with an error bar.
    /// </summary>
    public static bool TryGetTimeStampCounterFrequency(out double megahertz) {
      megahertz = 0;

      if (X86Base.IsSupported) {
        if (TryGetFrequencyFromCpuidLeaf15(out megahertz))
          return true;
        if (TryGetFrequencyFromCpuidLeaf16(out megahertz))
          return true;
      }

      return TryGetNominalFrequencyFromRegistry(out megahertz);
    }

    /// <summary>
    /// CPUID leaf 0x15 reports the TSC as an exact rational multiple of the
    /// core crystal clock: TSC = crystal * numerator / denominator.
    /// </summary>
    private static bool TryGetFrequencyFromCpuidLeaf15(out double megahertz) {
      megahertz = 0;

      Cpuid(0, 0, out uint maxLeaf, out _, out _, out _);
      if (maxLeaf < 0x15)
        return false;

      Cpuid(0x15, 0, out uint denominator, out uint numerator,
        out uint crystalHertz, out _);
      if (denominator == 0 || numerator == 0)
        return false;

      // Some processors implement the ratio but report a crystal frequency of
      // zero. The Intel SDM documents the nominal value for those families.
      if (crystalHertz == 0)
        crystalHertz = GetKnownCrystalClockHertz();
      if (crystalHertz == 0)
        return false;

      megahertz = 1e-6 * crystalHertz * numerator / denominator;
      return megahertz > 0;
    }

    /// <summary>
    /// CPUID leaf 0x16 reports the base frequency directly, in MHz. Present
    /// from Skylake onward, but several parts (Raptor Lake among them) leave
    /// it zero, so it is only a fallback.
    /// </summary>
    private static bool TryGetFrequencyFromCpuidLeaf16(out double megahertz) {
      megahertz = 0;

      Cpuid(0, 0, out uint maxLeaf, out _, out _, out _);
      if (maxLeaf < 0x16)
        return false;

      Cpuid(0x16, 0, out uint baseMhz, out _, out _, out _);
      baseMhz &= 0xFFFF;
      if (baseMhz == 0)
        return false;

      megahertz = baseMhz;
      return true;
    }

    private static uint GetKnownCrystalClockHertz() {
      Cpuid(1, 0, out uint versionInformation, out _, out _, out _);
      uint family = ((versionInformation & 0x0FF00000) >> 20) +
        ((versionInformation & 0x0F00) >> 8);
      uint model = ((versionInformation & 0x0F0000) >> 12) +
        ((versionInformation & 0xF0) >> 4);

      if (family != 0x06)
        return 0;

      switch (model) {
        case 0x55:                                    // Skylake-X / Cascade Lake-X
          return 25000000;
        case 0x5C: case 0x5F: case 0x7A:              // Goldmont / Denverton
          return 19200000;
        case 0x4E: case 0x5E: case 0x8E: case 0x9E:   // Skylake / Kaby Lake
          return 24000000;
        default:
          return 0;
      }
    }

    /// <summary>
    /// Last resort, and the only source available on AMD, which does not
    /// implement CPUID leaf 0x15. This is the nominal rated frequency the
    /// firmware advertised at boot, not a live measurement.
    /// </summary>
    private static bool TryGetNominalFrequencyFromRegistry(out double megahertz) {
      megahertz = 0;
      try {
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(
          @"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) {
          if (key?.GetValue("~MHz") is int value && value > 0) {
            megahertz = value;
            return true;
          }
        }
      } catch (Exception) {
        // The key is absent on some virtualised and ARM64 systems.
      }
      return false;
    }
  }
}
