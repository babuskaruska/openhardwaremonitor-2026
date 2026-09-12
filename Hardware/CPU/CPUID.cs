/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Text;

namespace OpenHardwareMonitor.Hardware.CPU {

  internal enum Vendor {
    Unknown,
    Intel,
    AMD,
  }

  /// <summary>
  /// Physical class of a core on a heterogeneous ("hybrid") processor.
  /// Reported by CPUID leaf 0x1A, present since Alder Lake.
  /// </summary>
  internal enum CoreType {

    /// <summary>Homogeneous processor, or the class could not be read.</summary>
    Unknown,

    /// <summary>P-core. Intel calls this the Core / "big" type (0x40).</summary>
    Performance,

    /// <summary>E-core. Intel calls this the Atom / "small" type (0x20).</summary>
    Efficiency
  }

  internal class CPUID {

    private readonly int group;
    private readonly int thread;
    private readonly GroupAffinity affinity;

    private readonly Vendor vendor = Vendor.Unknown;

    private readonly string cpuBrandString = "";
    private readonly string name = "";

    private readonly uint[,] cpuidData = new uint[0, 0];
    private readonly uint[,] cpuidExtData = new uint[0, 0];

    private readonly uint family;
    private readonly uint model;
    private readonly uint stepping;

    private readonly uint apicId;

    private readonly uint threadMaskWith;
    private readonly uint coreMaskWith;

    private readonly uint processorId;
    private readonly uint coreId;
    private readonly uint threadId;

    private readonly CoreType coreType = CoreType.Unknown;

    public const uint CPUID_0 = 0;
    public const uint CPUID_EXT = 0x80000000;

    private static void AppendRegister(StringBuilder b, uint value) {
      b.Append((char)((value) & 0xff));
      b.Append((char)((value >> 8) & 0xff));
      b.Append((char)((value >> 16) & 0xff));
      b.Append((char)((value >> 24) & 0xff));
    }

    private static uint NextLog2(long x) {
      if (x <= 0)
        return 0;

      x--;
      uint count = 0;
      while (x > 0) {
        x >>= 1;
        count++;
      }

      return count;
    }

    public static CPUID Get(int group, int thread) {
      if (thread >= 64)
        return null;

      var affinity = GroupAffinity.Single((ushort)group, thread);

      var previousAffinity = ThreadAffinity.Set(affinity);
      if (previousAffinity == GroupAffinity.Undefined)
        return null;

      try {
        return new CPUID(group, thread, affinity);
      } finally {
        ThreadAffinity.Set(previousAffinity);
      }
    }

    private CPUID(int group, int thread, GroupAffinity affinity) {
      this.group = group;
      this.thread = thread;
      this.affinity = affinity;

      uint maxCpuid = 0;
      uint maxCpuidExt = 0;

      uint eax, ebx, ecx, edx;

      CpuInstructions.Cpuid(CPUID_0, 0, out eax, out ebx, out ecx, out edx);
      if (eax > 0)
        maxCpuid = eax;
      else
        return;

      StringBuilder vendorBuilder = new StringBuilder();
      AppendRegister(vendorBuilder, ebx);
      AppendRegister(vendorBuilder, edx);
      AppendRegister(vendorBuilder, ecx);
      string cpuVendor = vendorBuilder.ToString();
      switch (cpuVendor) {
        case "GenuineIntel":
          vendor = Vendor.Intel;
          break;
        case "AuthenticAMD":
          vendor = Vendor.AMD;
          break;
        default:
          vendor = Vendor.Unknown;
          break;
      }
      eax = ebx = ecx = edx = 0;
      CpuInstructions.Cpuid(CPUID_EXT, 0, out eax, out ebx, out ecx, out edx);
      if (eax > CPUID_EXT)
        maxCpuidExt = eax - CPUID_EXT;
      else
        return;

      maxCpuid = Math.Min(maxCpuid, 1024);
      maxCpuidExt = Math.Min(maxCpuidExt, 1024);

      cpuidData = new uint[maxCpuid + 1, 4];
      for (uint i = 0; i < (maxCpuid + 1); i++)
        CpuInstructions.Cpuid(CPUID_0 + i, 0,
          out cpuidData[i, 0], out cpuidData[i, 1],
          out cpuidData[i, 2], out cpuidData[i, 3]);

      cpuidExtData = new uint[maxCpuidExt + 1, 4];
      for (uint i = 0; i < (maxCpuidExt + 1); i++)
        CpuInstructions.Cpuid(CPUID_EXT + i, 0,
          out cpuidExtData[i, 0], out cpuidExtData[i, 1],
          out cpuidExtData[i, 2], out cpuidExtData[i, 3]);

      StringBuilder nameBuilder = new StringBuilder();
      for (uint i = 2; i <= 4; i++) {
        CpuInstructions.Cpuid(CPUID_EXT + i, 0, out eax, out ebx, out ecx, out edx);
        AppendRegister(nameBuilder, eax);
        AppendRegister(nameBuilder, ebx);
        AppendRegister(nameBuilder, ecx);
        AppendRegister(nameBuilder, edx);        
      }
      nameBuilder.Replace('\0', ' ');
      cpuBrandString = nameBuilder.ToString().Trim();
      nameBuilder.Replace("Dual-Core Processor", "");
      nameBuilder.Replace("Triple-Core Processor", "");
      nameBuilder.Replace("Quad-Core Processor", "");
      nameBuilder.Replace("Six-Core Processor", "");
      nameBuilder.Replace("Eight-Core Processor", "");
      nameBuilder.Replace("Dual Core Processor", "");
      nameBuilder.Replace("Quad Core Processor", "");
      nameBuilder.Replace("12-Core Processor", "");
      nameBuilder.Replace("16-Core Processor", "");
      nameBuilder.Replace("24-Core Processor", "");
      nameBuilder.Replace("32-Core Processor", "");
      nameBuilder.Replace("64-Core Processor", "");
      nameBuilder.Replace("6-Core Processor", "");
      nameBuilder.Replace("8-Core Processor", "");
      nameBuilder.Replace("with Radeon Vega Mobile Gfx", "");
      nameBuilder.Replace("w/ Radeon Vega Mobile Gfx", "");
      nameBuilder.Replace("with Radeon Vega Graphics", "");
      nameBuilder.Replace("with Radeon Graphics", "");
      nameBuilder.Replace("APU with Radeon(tm) HD Graphics", "");
      nameBuilder.Replace("APU with Radeon(TM) HD Graphics", "");
      nameBuilder.Replace("APU with AMD Radeon R2 Graphics", "");
      nameBuilder.Replace("APU with AMD Radeon R3 Graphics", "");
      nameBuilder.Replace("APU with AMD Radeon R4 Graphics", "");
      nameBuilder.Replace("APU with AMD Radeon R5 Graphics", "");
      nameBuilder.Replace("APU with Radeon(tm) R3", "");
      nameBuilder.Replace("RADEON R2, 4 COMPUTE CORES 2C+2G", "");
      nameBuilder.Replace("RADEON R4, 5 COMPUTE CORES 2C+3G", "");
      nameBuilder.Replace("RADEON R5, 5 COMPUTE CORES 2C+3G", "");
      nameBuilder.Replace("RADEON R5, 10 COMPUTE CORES 4C+6G", "");
      nameBuilder.Replace("RADEON R7, 10 COMPUTE CORES 4C+6G", "");
      nameBuilder.Replace("RADEON R7, 12 COMPUTE CORES 4C+8G", "");
      nameBuilder.Replace("Radeon R5, 6 Compute Cores 2C+4G", "");
      nameBuilder.Replace("Radeon R5, 8 Compute Cores 4C+4G", "");
      nameBuilder.Replace("Radeon R6, 10 Compute Cores 4C+6G", "");
      nameBuilder.Replace("Radeon R7, 10 Compute Cores 4C+6G", "");
      nameBuilder.Replace("Radeon R7, 12 Compute Cores 4C+8G", "");
      nameBuilder.Replace("R5, 10 Compute Cores 4C+6G", "");
      nameBuilder.Replace("R7, 12 COMPUTE CORES 4C+8G", "");
      nameBuilder.Replace("(R)", " ");
      nameBuilder.Replace("(TM)", " ");
      nameBuilder.Replace("(tm)", " ");
      nameBuilder.Replace("CPU", " ");

      for (int i = 0; i < 10; i++) nameBuilder.Replace("  ", " ");
      name = nameBuilder.ToString();
      if (name.Contains("@"))
        name = name.Remove(name.LastIndexOf('@'));
      name = name.Trim();

      this.family = ((cpuidData[1, 0] & 0x0FF00000) >> 20) +
        ((cpuidData[1, 0] & 0x0F00) >> 8);
      this.model = ((cpuidData[1, 0] & 0x0F0000) >> 12) +
        ((cpuidData[1, 0] & 0xF0) >> 4);
      this.stepping = (cpuidData[1, 0] & 0x0F);

      this.apicId = (cpuidData[1, 1] >> 24) & 0xFF;

      switch (vendor) {
        case Vendor.Intel:
          uint maxCoreAndThreadIdPerPackage = (cpuidData[1, 1] >> 16) & 0xFF;
          uint maxCoreIdPerPackage;
          if (maxCpuid >= 4)
            maxCoreIdPerPackage = ((cpuidData[4, 0] >> 26) & 0x3F) + 1;
          else
            maxCoreIdPerPackage = 1;
          threadMaskWith =
            NextLog2(maxCoreAndThreadIdPerPackage / maxCoreIdPerPackage);
          coreMaskWith = NextLog2(maxCoreIdPerPackage);
          break;
        case Vendor.AMD:
          if (this.family == 0x17 || this.family == 0x19) {
            coreMaskWith = (cpuidExtData[8, 2] >> 12) & 0xF;
            threadMaskWith =
              NextLog2(((cpuidExtData[0x1E, 1] >> 8) & 0xFF) + 1);
          } else {
            uint corePerPackage;
            if (maxCpuidExt >= 8)
              corePerPackage = (cpuidExtData[8, 2] & 0xFF) + 1;
            else
              corePerPackage = 1;
            coreMaskWith = NextLog2(corePerPackage);
            threadMaskWith = 0;
          }
          break;
        default:
          threadMaskWith = 0;
          coreMaskWith = 0;
          break;
      }

      // Heterogeneous processors report the class of the core this thread is
      // running on through leaf 0x1A. Read it before topology so that it is
      // available even when the extended topology leaves are absent.
      if (maxCpuid >= 0x1A) {
        CpuInstructions.Cpuid(0x1A, 0, out uint coreTypeEax, out _, out _,
          out _);
        switch ((coreTypeEax >> 24) & 0xFF) {
          case 0x40: coreType = CoreType.Performance; break;
          case 0x20: coreType = CoreType.Efficiency; break;
        }
      }

      // Prefer the extended topology leaves. They give the shift widths
      // directly from the hardware, which is the only way to get this right
      // on a hybrid processor.
      //
      // The legacy path below derives the SMT width by dividing logical
      // processors per package by cores per package and rounding up to a
      // power of two. That assumes every core has the same number of threads.
      // Since Alder Lake that is false - P-cores are SMT-2 while E-cores are
      // SMT-1 - so the computed width is wrong and distinct cores collapse
      // onto the same coreId.
      if (TryGetExtendedTopology(maxCpuid, out uint x2ApicId,
        out uint smtShift, out uint coreShift)) {

        apicId = x2ApicId;
        threadMaskWith = smtShift;
        coreMaskWith = coreShift - smtShift;

        threadId = x2ApicId & ((1u << (int)smtShift) - 1);
        coreId = (x2ApicId >> (int)smtShift) &
          ((1u << (int)(coreShift - smtShift)) - 1);
        processorId = x2ApicId >> (int)coreShift;
      } else {
        processorId = (apicId >> (int)(coreMaskWith + threadMaskWith));
        coreId = ((apicId >> (int)(threadMaskWith))
          - (processorId << (int)(coreMaskWith)));
        threadId = apicId
          - (processorId << (int)(coreMaskWith + threadMaskWith))
          - (coreId << (int)(threadMaskWith));
      }
    }

    /// <summary>
    /// Walks CPUID leaf 0x1F (V2 extended topology), falling back to leaf 0x0B,
    /// to obtain this thread's 32-bit x2APIC identifier and the shift widths
    /// that separate SMT threads, cores and packages within it.
    /// </summary>
    private static bool TryGetExtendedTopology(uint maxCpuid,
      out uint x2ApicId, out uint smtShift, out uint coreShift) {

      x2ApicId = 0;
      smtShift = 0;
      coreShift = 0;

      uint topologyLeaf;
      if (maxCpuid >= 0x1F && HasTopologyLevels(0x1F))
        topologyLeaf = 0x1F;
      else if (maxCpuid >= 0x0B && HasTopologyLevels(0x0B))
        topologyLeaf = 0x0B;
      else
        return false;

      bool foundCoreLevel = false;
      for (uint subLeaf = 0; subLeaf < 16; subLeaf++) {
        CpuInstructions.Cpuid(topologyLeaf, subLeaf, out uint eax, out _,
          out uint ecx, out uint edx);

        uint levelType = (ecx >> 8) & 0xFF;
        if (levelType == 0)
          break;

        uint shift = eax & 0x1F;
        x2ApicId = edx;

        switch (levelType) {
          case 1:                       // SMT
            smtShift = shift;
            break;
          case 2:                       // Core
            coreShift = shift;
            foundCoreLevel = true;
            break;
          default:
            // Module, Tile and Die levels sit above Core; anything at or
            // above the core boundary still separates packages correctly.
            break;
        }
      }

      // A processor with no SMT reports no SMT level at all, which is fine:
      // a zero shift simply means every logical processor is its own core.
      return foundCoreLevel && coreShift >= smtShift;
    }

    private static bool HasTopologyLevels(uint leaf) {
      CpuInstructions.Cpuid(leaf, 0, out _, out uint ebx, out _, out _);
      return (ebx & 0xFFFF) != 0;
    }

    public string Name {
      get { return name; }
    }

    public string BrandString {
      get { return cpuBrandString; }
    }

    public int Group {
      get {
        return group;
      }
    }

    public int Thread {
      get { return thread; }
    }

    public GroupAffinity Affinity {
      get {
        return affinity;
      }
    }

    public Vendor Vendor {
      get { return vendor; }
    }

    /// <summary>
    /// Physical class of the core this logical processor belongs to.
    /// <see cref="CoreType.Unknown"/> on homogeneous processors.
    /// </summary>
    public CoreType CoreType {
      get { return coreType; }
    }

    public uint Family {
      get { return family; }
    }

    public uint Model {
      get { return model; }
    }

    public uint Stepping {
      get { return stepping; }
    }

    public uint ApicId {
      get { return apicId; }
    }

    public uint ProcessorId {
      get { return processorId; }
    }

    public uint CoreId {
      get { return coreId; }
    }

    public uint ThreadId {
      get { return threadId; }
    }

    public uint[,] Data {
      get { return cpuidData; }
    }

    public uint[,] ExtData {
      get { return cpuidExtData; }
    }
  }
}
