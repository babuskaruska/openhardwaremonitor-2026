/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Globalization;
using System.Text;

namespace OpenHardwareMonitor.Hardware.CPU {
  internal sealed class IntelCPU : GenericCPU {

    private enum Microarchitecture {
      Unknown,
      NetBurst,
      Core,
      Atom,
      Nehalem,
      SandyBridge,
      IvyBridge,
      Haswell,
      Broadwell,
      Silvermont,
      Skylake,
      Airmont,
      KabyLake,
      Goldmont,
      GoldmontPlus,
      CannonLake,
      IceLake,
      CometLake,
      Tremont,
      TigerLake,
      RocketLake,
      AlderLake,
      RaptorLake,
      MeteorLake,
      ArrowLake,
      LunarLake,
      PantherLake,
      NovaLake,
      SapphireRapids,
      EmeraldRapids,
      GraniteRapids,
      Crestmont,
      Darkmont,
      DiamondRapids
    }

    private readonly Sensor[] coreTemperatures;
    private readonly Sensor packageTemperature;
    private readonly Sensor[] coreClocks;
    private readonly Sensor busClock;
    private readonly Sensor[] powerSensors;

    private readonly Microarchitecture microarchitecture;
    private readonly double timeStampCounterMultiplier;

    private const uint IA32_THERM_STATUS_MSR = 0x019C;
    private const uint IA32_TEMPERATURE_TARGET = 0x01A2;
    private const uint IA32_PERF_STATUS = 0x0198;
    private const uint MSR_PLATFORM_INFO = 0xCE;
    private const uint IA32_PACKAGE_THERM_STATUS = 0x1B1;
    private const uint MSR_RAPL_POWER_UNIT = 0x606;
    private const uint MSR_PKG_ENERY_STATUS = 0x611;
    private const uint MSR_DRAM_ENERGY_STATUS = 0x619;
    private const uint MSR_PP0_ENERY_STATUS = 0x639;
    private const uint MSR_PP1_ENERY_STATUS = 0x641;

    private readonly uint[] energyStatusMSRs = { MSR_PKG_ENERY_STATUS, 
      MSR_PP0_ENERY_STATUS, MSR_PP1_ENERY_STATUS, MSR_DRAM_ENERGY_STATUS };
    private readonly string[] powerSensorLabels = 
      { "CPU Package", "CPU Cores", "CPU Graphics", "CPU DRAM" };
    private float energyUnitMultiplier = 0;
    private DateTime[] lastEnergyTime;
    private uint[] lastEnergyConsumed;


    private float[] Floats(float f) {
      float[] result = new float[coreCount];
      for (int i = 0; i < coreCount; i++)
        result[i] = f;
      return result;
    }

    private float[] GetTjMaxFromMSR() {
      uint eax, edx;
      float[] result = new float[coreCount];
      for (int i = 0; i < coreCount; i++) {
        if (Ring0.RdmsrTx(IA32_TEMPERATURE_TARGET, out eax,
          out edx, cpuid[i][0].Affinity)) {
          result[i] = (eax >> 16) & 0xFF;
        } else {
          result[i] = 100;
        }
      }
      return result;
    }

    /// <summary>
    /// Maps CPUID family/model to a microarchitecture.
    ///
    /// Model numbers follow the canonical list in the Linux kernel's
    /// arch/x86/include/asm/intel-family.h. Note that family 0x06 is no
    /// longer a safe assumption: Nova Lake is family 18 and Diamond Rapids
    /// is family 19, the first Intel parts to leave family 6 since the
    /// Pentium 4.
    /// </summary>
    private static Microarchitecture GetMicroarchitecture(uint family,
      uint model) {

      switch (family) {
        case 0x06:
          switch (model) {
            case 0x0F: case 0x17: return Microarchitecture.Core;
            case 0x1C: case 0x26: case 0x36: return Microarchitecture.Atom;
            case 0x1A: case 0x1E: case 0x1F: case 0x25: case 0x2C:
            case 0x2E: case 0x2F: return Microarchitecture.Nehalem;
            case 0x2A: case 0x2D: return Microarchitecture.SandyBridge;
            case 0x3A: case 0x3E: return Microarchitecture.IvyBridge;
            case 0x3C: case 0x3F: case 0x45: case 0x46:
              return Microarchitecture.Haswell;
            case 0x3D: case 0x47: case 0x4F: case 0x56:
              return Microarchitecture.Broadwell;
            case 0x37: case 0x4A: case 0x4D: case 0x5A: case 0x5D:
              return Microarchitecture.Silvermont;
            case 0x4C: return Microarchitecture.Airmont;
            case 0x4E: case 0x5E: case 0x55:
              return Microarchitecture.Skylake;
            case 0x8E: case 0x9E: return Microarchitecture.KabyLake;
            case 0x5C: case 0x5F: return Microarchitecture.Goldmont;
            case 0x7A: return Microarchitecture.GoldmontPlus;
            case 0x66: return Microarchitecture.CannonLake;
            case 0x6A: case 0x6C: case 0x7D: case 0x7E:
              return Microarchitecture.IceLake;
            case 0xA5: case 0xA6: return Microarchitecture.CometLake;
            case 0x86: case 0x96: case 0x9C:
              return Microarchitecture.Tremont;
            case 0x8C: case 0x8D: return Microarchitecture.TigerLake;
            case 0xA7: return Microarchitecture.RocketLake;

            // --- everything below here was previously unrecognised ---
            case 0x97: case 0x9A: case 0xBE:
              return Microarchitecture.AlderLake;
            case 0xB7: case 0xBA: case 0xBF:
              return Microarchitecture.RaptorLake;
            case 0xAA: case 0xAC: return Microarchitecture.MeteorLake;
            case 0xB5: case 0xC5: case 0xC6:
              return Microarchitecture.ArrowLake;
            case 0xBD: return Microarchitecture.LunarLake;
            case 0xCC: case 0xE5: return Microarchitecture.PantherLake;
            case 0x8F: return Microarchitecture.SapphireRapids;
            case 0xCF: return Microarchitecture.EmeraldRapids;
            case 0xAD: case 0xAE: return Microarchitecture.GraniteRapids;
            case 0xAF: case 0xB6: return Microarchitecture.Crestmont;
            case 0xDD: return Microarchitecture.Darkmont;
            default: return Microarchitecture.Unknown;
          }
        case 0x0F:
          switch (model) {
            case 0x00: case 0x01: case 0x02: case 0x03:
            case 0x04: case 0x06: return Microarchitecture.NetBurst;
            default: return Microarchitecture.Unknown;
          }
        case 0x12:                                  // Nova Lake
          return Microarchitecture.NovaLake;
        case 0x13:                                  // Diamond Rapids
          return Microarchitecture.DiamondRapids;
        default:
          return Microarchitecture.Unknown;
      }
    }

    /// <summary>
    /// Junction temperature, per core.
    ///
    /// Anything Nehalem or newer reports TjMax through
    /// MSR_TEMPERATURE_TARGET, so it is read rather than tabulated. Without a
    /// low-level backend that read fails and 100 °C is assumed, which happens
    /// to be correct for most modern desktop parts. Only the pre-Nehalem
    /// processors, which predate the MSR, need hard-coded values.
    /// </summary>
    private float[] GetTjMax(Microarchitecture microarchitecture, uint family,
      uint model, uint stepping) {

      switch (microarchitecture) {
        case Microarchitecture.Core:
          if (model == 0x0F) {
            switch (stepping) {
              case 0x06: return Floats(85 + 2);
              case 0x0B: return Floats(80 + 9);
              case 0x0D: return Floats(85);
              default: return Floats(85);
            }
          }
          return Floats(100);

        case Microarchitecture.Atom:
          if (model == 0x1C) {
            switch (stepping) {
              case 0x02: return Floats(90);
              case 0x0A: return Floats(100);
              default: return Floats(90);
            }
          }
          return GetTjMaxFromMSR();

        case Microarchitecture.NetBurst:
        case Microarchitecture.Unknown:
          return Floats(100);

        default:
          return GetTjMaxFromMSR();
      }
    }

    /// <summary>
    /// True for microarchitectures that report the maximum non-turbo ratio in
    /// MSR_PLATFORM_INFO, which is everything from Nehalem onward. Expressed
    /// as an exclusion list so that newly added architectures are handled
    /// correctly by default rather than silently losing their clock sensors.
    /// </summary>
    private static bool UsesPlatformInfoMultiplier(
      Microarchitecture microarchitecture) {

      switch (microarchitecture) {
        case Microarchitecture.Unknown:
        case Microarchitecture.NetBurst:
        case Microarchitecture.Atom:
        case Microarchitecture.Core:
          return false;
        default:
          return true;
      }
    }

    /// <summary>
    /// True for microarchitectures with the RAPL energy counters, introduced
    /// with Sandy Bridge. Also an exclusion list, for the same reason.
    /// </summary>
    private static bool SupportsRapl(Microarchitecture microarchitecture) {
      switch (microarchitecture) {
        case Microarchitecture.Unknown:
        case Microarchitecture.NetBurst:
        case Microarchitecture.Core:
        case Microarchitecture.Atom:
        case Microarchitecture.Nehalem:
          return false;
        default:
          return true;
      }
    }

    public IntelCPU(int processorIndex, CPUID[][] cpuid, ISettings settings)
      : base(processorIndex, cpuid, settings) {
      // set tjMax
      //
      // This used to be a three-level switch on family/model/stepping that
      // assigned microarchitecture and TjMax together. It stopped at Tiger
      // Lake, so every processor released after 2020 fell through to
      // Unknown - which disables core temperature, package temperature AND
      // core clocks further down. A Raptor Lake part reported nothing at all.
      microarchitecture = GetMicroarchitecture(family, model);
      float[] tjMax = GetTjMax(microarchitecture, family, model, stepping);


      // set timeStampCounterMultiplier
      if (UsesPlatformInfoMultiplier(microarchitecture)) {
        uint eax, edx;
        if (Ring0.Rdmsr(MSR_PLATFORM_INFO, out eax, out edx))
          timeStampCounterMultiplier = (eax >> 8) & 0xff;
      } else if (microarchitecture != Microarchitecture.Unknown) {
        uint eax, edx;
        if (Ring0.Rdmsr(IA32_PERF_STATUS, out eax, out edx)) {
          timeStampCounterMultiplier =
            ((edx >> 8) & 0x1f) + 0.5 * ((edx >> 14) & 1);
        }
      } else {
        timeStampCounterMultiplier = 0;
      }

      // check if processor supports a digital thermal sensor at core level
      if (Ring0.SupportsMsr &&
        cpuid[0][0].Data.GetLength(0) > 6 &&
        (cpuid[0][0].Data[6, 0] & 1) != 0 &&
        microarchitecture != Microarchitecture.Unknown)
      {
        coreTemperatures = new Sensor[coreCount];
        for (int i = 0; i < coreTemperatures.Length; i++) {
          coreTemperatures[i] = new Sensor(CoreString(i), i,
            SensorType.Temperature, this, new[] { 
              new ParameterDescription(
                "TjMax [°C]", "TjMax temperature of the core sensor.\n" + 
                "Temperature = TjMax - TSlope * Value.", tjMax[i]), 
              new ParameterDescription("TSlope [°C]", 
                "Temperature slope of the digital thermal sensor.\n" + 
                "Temperature = TjMax - TSlope * Value.", 1)}, settings);
          ActivateSensor(coreTemperatures[i]);
        }
      } else {
        coreTemperatures = new Sensor[0];
      }

      // check if processor supports a digital thermal sensor at package level
      if (Ring0.SupportsMsr &&
        cpuid[0][0].Data.GetLength(0) > 6 &&
        (cpuid[0][0].Data[6, 0] & 0x40) != 0 &&
        microarchitecture != Microarchitecture.Unknown)
      {
        packageTemperature = new Sensor("CPU Package",
          coreTemperatures.Length, SensorType.Temperature, this, new[] { 
              new ParameterDescription(
                "TjMax [°C]", "TjMax temperature of the package sensor.\n" + 
                "Temperature = TjMax - TSlope * Value.", tjMax[0]), 
              new ParameterDescription("TSlope [°C]", 
                "Temperature slope of the digital thermal sensor.\n" + 
                "Temperature = TjMax - TSlope * Value.", 1)}, settings);
        ActivateSensor(packageTemperature);
      }

      busClock = new Sensor("Bus Speed", 0, SensorType.Clock, this, settings);
      coreClocks = new Sensor[coreCount];
      for (int i = 0; i < coreClocks.Length; i++) {
        coreClocks[i] =
          new Sensor(CoreString(i), i + 1, SensorType.Clock, this, settings);
        if (HasTimeStampCounter && microarchitecture != Microarchitecture.Unknown)
          ActivateSensor(coreClocks[i]);
      }

      if (Ring0.SupportsMsr && SupportsRapl(microarchitecture)) {
        powerSensors = new Sensor[energyStatusMSRs.Length];
        lastEnergyTime = new DateTime[energyStatusMSRs.Length];
        lastEnergyConsumed = new uint[energyStatusMSRs.Length];

        uint eax, edx;
        if (Ring0.Rdmsr(MSR_RAPL_POWER_UNIT, out eax, out edx))
          switch (microarchitecture) {
            case Microarchitecture.Silvermont:
            case Microarchitecture.Airmont:
              energyUnitMultiplier = 1.0e-6f * (1 << (int)((eax >> 8) & 0x1F));
              break;
            default:
              energyUnitMultiplier = 1.0f / (1 << (int)((eax >> 8) & 0x1F));
              break;
          }
        if (energyUnitMultiplier != 0) {
          for (int i = 0; i < energyStatusMSRs.Length; i++) {
            if (!Ring0.Rdmsr(energyStatusMSRs[i], out eax, out edx))
              continue;

            lastEnergyTime[i] = DateTime.UtcNow;
            lastEnergyConsumed[i] = eax;
            powerSensors[i] = new Sensor(powerSensorLabels[i], i,
              SensorType.Power, this, settings);
            ActivateSensor(powerSensors[i]);
          }
        }
      }

      Update();
    }

    protected override uint[] GetMSRs() {
      return new[] {
        MSR_PLATFORM_INFO,
        IA32_PERF_STATUS ,
        IA32_THERM_STATUS_MSR,
        IA32_TEMPERATURE_TARGET,
        IA32_PACKAGE_THERM_STATUS,
        MSR_RAPL_POWER_UNIT,
        MSR_PKG_ENERY_STATUS,
        MSR_DRAM_ENERGY_STATUS,
        MSR_PP0_ENERY_STATUS,
        MSR_PP1_ENERY_STATUS
      };
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.Append(base.GetReport());

      r.Append("Microarchitecture: ");
      r.AppendLine(microarchitecture.ToString());
      r.Append("Time Stamp Counter Multiplier: ");
      r.AppendLine(timeStampCounterMultiplier.ToString(
        CultureInfo.InvariantCulture));
      r.AppendLine();

      return r.ToString();
    }

    // The three per-core readers below run while the thread is already on the
    // core being read (see Update), so they use plain Rdmsr.

    private void UpdateCoreTemperature(int core) {
      // Valid when bit 31 is set; bits 22:16 hold the distance from TjMax.
      if (Ring0.Rdmsr(IA32_THERM_STATUS_MSR, out uint eax, out _) &&
        (eax & 0x80000000) != 0) {
        float deltaT = (eax & 0x007F0000) >> 16;
        float tjMax = coreTemperatures[core].Parameters[0].Value;
        float tSlope = coreTemperatures[core].Parameters[1].Value;
        coreTemperatures[core].Value = tjMax - tSlope * deltaT;
      } else {
        coreTemperatures[core].Value = null;
      }
    }

    private void UpdatePackageTemperature() {
      if (packageTemperature == null)
        return;
      if (Ring0.Rdmsr(IA32_PACKAGE_THERM_STATUS, out uint eax, out _) &&
        (eax & 0x80000000) != 0) {
        float deltaT = (eax & 0x007F0000) >> 16;
        float tjMax = packageTemperature.Parameters[0].Value;
        float tSlope = packageTemperature.Parameters[1].Value;
        packageTemperature.Value = tjMax - tSlope * deltaT;
      } else {
        packageTemperature.Value = null;
      }
    }

    /// <summary>Reads one core's clock; returns the bus clock it implies.</summary>
    private double UpdateCoreClock(int core, double previousBusClock) {
      if (!Ring0.Rdmsr(IA32_PERF_STATUS, out uint eax, out _)) {
        // if IA32_PERF_STATUS is not available, assume TSC frequency
        coreClocks[core].Value = (float)TimeStampCounterFrequency;
        return previousBusClock;
      }

      double newBusClock = TimeStampCounterFrequency / timeStampCounterMultiplier;
      // Expressed as a chain rather than an explicit architecture list so that
      // a newly recognised architecture decodes with the modern layout by
      // default. The old list-based switch sent anything unlisted to the
      // Core-era decode, which is wrong.
      if (microarchitecture == Microarchitecture.Nehalem) {
        uint multiplier = eax & 0xff;
        coreClocks[core].Value = (float)(multiplier * newBusClock);
      } else if (UsesPlatformInfoMultiplier(microarchitecture)) {
        // Sandy Bridge and everything since.
        uint multiplier = (eax >> 8) & 0xff;
        coreClocks[core].Value = (float)(multiplier * newBusClock);
      } else {
        double multiplier = ((eax >> 8) & 0x1f) + 0.5 * ((eax >> 14) & 1);
        coreClocks[core].Value = (float)(multiplier * newBusClock);
      }
      return newBusClock;
    }

    public override void Update() {
      base.Update();

      // Core temperatures and clocks come from per-core MSRs, so the reading
      // thread has to run on each core in turn. Move to each core once and
      // read everything that core provides, then restore the affinity once.
      // This used to change (and restore) the affinity for every single
      // register and sleep 1 ms per core for the clocks: over 300 ms per poll
      // on a 20-core Raptor Lake.
      //
      // Without a low-level backend there is no time stamp counter multiplier,
      // so clocks fall back to the operating system's per-core accounting
      // rather than reporting nothing.
      bool clocksFromMsr = HasTimeStampCounter && timeStampCounterMultiplier > 0;
      if (!clocksFromMsr)
        TryUpdateClocksFromOperatingSystem(coreClocks);

      int coreCount = Math.Min(cpuid.Length, Math.Max(coreTemperatures.Length,
        clocksFromMsr ? coreClocks.Length : 0));
      double newBusClock = 0;
      bool pinned = false;
      GroupAffinity original = GroupAffinity.Undefined;
      try {
        for (int i = 0; i < coreCount; i++) {
          GroupAffinity previous = ThreadAffinity.Set(cpuid[i][0].Affinity);
          if (!pinned) {
            original = previous;
            pinned = true;
          }

          if (i < coreTemperatures.Length)
            UpdateCoreTemperature(i);
          if (i == 0 && packageTemperature != null)
            UpdatePackageTemperature();
          if (clocksFromMsr && i < coreClocks.Length)
            newBusClock = UpdateCoreClock(i, newBusClock);
        }
        if (coreCount == 0 && packageTemperature != null && cpuid.Length > 0) {
          GroupAffinity previous = ThreadAffinity.Set(cpuid[0][0].Affinity);
          original = previous;
          pinned = true;
          UpdatePackageTemperature();
        }
      } finally {
        if (pinned)
          ThreadAffinity.Set(original);
      }

      if (clocksFromMsr && newBusClock > 0) {
        this.busClock.Value = (float)newBusClock;
        ActivateSensor(this.busClock);
      }

      if (powerSensors != null) {
        foreach (Sensor sensor in powerSensors) {
          if (sensor == null)
            continue;

          uint eax, edx;
          if (!Ring0.Rdmsr(energyStatusMSRs[sensor.Index], out eax, out edx))
            continue;

          DateTime time = DateTime.UtcNow;
          uint energyConsumed = eax;
          float deltaTime =
            (float)(time - lastEnergyTime[sensor.Index]).TotalSeconds;
          if (deltaTime < 0.01)
            continue;

          sensor.Value = energyUnitMultiplier * unchecked(
            energyConsumed - lastEnergyConsumed[sensor.Index]) / deltaTime;
          lastEnergyTime[sensor.Index] = time;
          lastEnergyConsumed[sensor.Index] = energyConsumed;
        }
      }
    }
  }
}
