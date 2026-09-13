/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>
  /// Presents PawnIO's LpcIO module as the raw port I/O that the Super I/O
  /// code (LPCIO, LPCPort and the chip classes) already issues.
  ///
  /// LpcIO.p offers no arbitrary port access:
  ///  * An instance is bound to one slot by ioctl_select_slot. Slot 0 uses
  ///    ports 0x2E/0x2F and slot 1 uses 0x4E/0x4F. Selecting a slot discards
  ///    everything the instance had discovered.
  ///  * It allows only that slot's two configuration ports, 0x25C/0x25D, and
  ///    the chip's base address ranges (BARs). Each BAR is treated as an
  ///    8-byte window matched as (port &amp; 0xFFF8).
  ///  * BARs are discovered by ioctl_find_bars, which only works while the
  ///    chip is in configuration mode.
  ///
  /// The mapping onto that model:
  ///  * There is one module instance per slot, each selected exactly once.
  ///    The driver keeps module state per handle, so a chip at 0x2E and a
  ///    second chip at 0x4E both keep their BARs and no slot is ever
  ///    re-selected. The alternative, a single instance, would have to
  ///    re-enter configuration mode behind the chip drivers' backs whenever
  ///    access alternated between chips.
  ///  * Configuration ports go to their slot's instance.
  ///  * Any other port goes to the instance that last served its 8-byte
  ///    window, or else to each instance in turn. The module checks a port
  ///    before touching hardware, so a refused request has no side effects.
  ///    The first instance that accepts becomes the window's owner.
  ///  * <see cref="PrepareSuperIoAccess"/> runs find_bars. Detection calls it
  ///    while the chip is in configuration mode with a valid chip ID.
  ///
  /// A refused or failed request is always reported as a failure, never as a
  /// value. Callers hold the ISA bus mutex, as LpcIO.p asks. The internal lock
  /// only protects this object's own state.
  /// </summary>
  internal sealed class LpcIoAdapter : IDisposable {

    public const string SelectSlotFunction = "ioctl_select_slot";
    public const string FindBarsFunction = "ioctl_find_bars";
    public const string ReadPortFunction = "ioctl_pio_inb";
    public const string WritePortFunction = "ioctl_pio_outb";

    /// <summary>
    /// Configuration (index) port of each LpcIO slot, by slot number. The data
    /// port is the next port up.
    /// </summary>
    public static readonly IReadOnlyList<ushort> SlotRegisterPorts =
      new ushort[] { 0x2E, 0x4E };

    private const byte DeviceSelectRegister = 0x07;

    // LpcIO.p matches (port & 0xFFF8) against each discovered BAR.
    private const int WindowMask = 0xFFF8;

    private static readonly ulong[] NoData = Array.Empty<ulong>();

    private sealed class Slot {
      public Slot(IPawnIoModule module) {
        Module = module;
      }

      public IPawnIoModule Module { get; }

      public bool Selected { get; set; }
    }

    private readonly Slot?[] slots;
    private readonly Dictionary<int, int> windowOwners =
      new Dictionary<int, int>();
    private readonly object sync = new object();

    /// <param name="modules">One module instance per slot, indexed like
    /// <see cref="SlotRegisterPorts"/>. A null entry leaves that slot
    /// unreachable. The adapter takes ownership of the instances.</param>
    public LpcIoAdapter(IReadOnlyList<IPawnIoModule?> modules) {
      if (modules == null)
        throw new ArgumentNullException(nameof(modules));
      if (modules.Count != SlotRegisterPorts.Count)
        throw new ArgumentException(
          "Expected one module instance per LpcIO slot.", nameof(modules));

      slots = new Slot?[modules.Count];
      for (int i = 0; i < slots.Length; i++) {
        IPawnIoModule? module = modules[i];
        slots[i] = module == null ? null : new Slot(module);
      }
    }

    public bool TryReadPort(uint port, out byte value) {
      value = 0xFF;
      if (port > 0xFFFF)
        return false;

      ulong[] output = new ulong[1];
      lock (sync) {
        if (!Route((int)port, ReadPortFunction, new ulong[] { port }, output))
          return false;
      }
      value = (byte)output[0];
      return true;
    }

    public bool WritePort(uint port, byte value) {
      if (port > 0xFFFF)
        return false;

      lock (sync) {
        return Route((int)port, WritePortFunction,
          new ulong[] { port, value }, NoData);
      }
    }

    /// <summary>
    /// Makes the BARs of the chip at <paramref name="registerPort"/>
    /// accessible. The chip must be in configuration mode with a valid chip
    /// ID. It stays in configuration mode, and its logical device selection
    /// is put back afterwards.
    /// </summary>
    /// <param name="detail">A line for the report.</param>
    public bool PrepareSuperIoAccess(ushort registerPort, out string detail) {
      string chip = "Super I/O at 0x" +
        registerPort.ToString("X2", CultureInfo.InvariantCulture);

      int index = GetConfigSlot(registerPort);
      if (index < 0) {
        detail = chip + ": not an LpcIO slot, its I/O ranges stay " +
          "inaccessible.";
        return false;
      }

      lock (sync) {
        Slot? slot = slots[index];
        if (slot == null || !EnsureSelected(index, slot)) {
          detail = chip + ": the LpcIO module instance for this slot is " +
            "unavailable.";
          return false;
        }

        ulong indexPort = SlotRegisterPorts[index];
        ulong dataPort = indexPort + 1;

        // find_bars selects every logical device in turn. Remember the
        // current selection so the chip is left as detection had it: NCT677X,
        // for one, later rewrites its I/O space lock without reselecting.
        ulong[] selection = new ulong[1];
        bool haveSelection =
          Execute(slot, WritePortFunction,
            new ulong[] { indexPort, DeviceSelectRegister }, NoData) &&
          Execute(slot, ReadPortFunction, new ulong[] { dataPort }, selection);

        bool found = slot.Module.Execute(FindBarsFunction, NoData, NoData,
          out int hresult);
        ForgetWindows(index);

        if (!found) {
          detail = chip + ": LpcIO could not discover the chip's I/O ranges " +
            "(find_bars 0x" +
            hresult.ToString("X8", CultureInfo.InvariantCulture) + ").";
          return false;
        }

        if (haveSelection) {
          Execute(slot, WritePortFunction,
            new ulong[] { indexPort, DeviceSelectRegister }, NoData);
          Execute(slot, WritePortFunction,
            new ulong[] { dataPort, selection[0] & 0xFF }, NoData);
        }

        detail = chip + ": LpcIO discovered the chip's I/O ranges.";
        return true;
      }
    }

    private bool Route(int port, string function, ulong[] input,
      ulong[] output) {

      int configSlot = GetConfigSlot(port);
      if (configSlot >= 0)
        return ExecuteOnSlot(configSlot, function, input, output);

      int window = port & WindowMask;
      int tried = -1;
      if (windowOwners.TryGetValue(window, out int owner)) {
        if (ExecuteOnSlot(owner, function, input, output))
          return true;
        windowOwners.Remove(window);
        tried = owner;
      }

      for (int i = 0; i < slots.Length; i++) {
        if (i != tried && ExecuteOnSlot(i, function, input, output)) {
          windowOwners[window] = i;
          return true;
        }
      }
      return false;
    }

    private bool ExecuteOnSlot(int index, string function, ulong[] input,
      ulong[] output) {
      Slot? slot = slots[index];
      return slot != null && EnsureSelected(index, slot) &&
        Execute(slot, function, input, output);
    }

    private static bool Execute(Slot slot, string function, ulong[] input,
      ulong[] output) {
      return slot.Module.Execute(function, input, output, out _);
    }

    private bool EnsureSelected(int index, Slot slot) {
      if (slot.Selected)
        return true;
      if (!Execute(slot, SelectSlotFunction, new ulong[] { (ulong)index },
        NoData))
        return false;
      slot.Selected = true;
      ForgetWindows(index);
      return true;
    }

    private void ForgetWindows(int index) {
      List<int>? stale = null;
      foreach (KeyValuePair<int, int> entry in windowOwners) {
        if (entry.Value == index)
          (stale ??= new List<int>()).Add(entry.Key);
      }
      if (stale == null)
        return;
      foreach (int window in stale)
        windowOwners.Remove(window);
    }

    private static int GetConfigSlot(int port) {
      for (int i = 0; i < SlotRegisterPorts.Count; i++) {
        if (port == SlotRegisterPorts[i] || port == SlotRegisterPorts[i] + 1)
          return i;
      }
      return -1;
    }

    public void Dispose() {
      lock (sync) {
        for (int i = 0; i < slots.Length; i++) {
          slots[i]?.Module.Dispose();
          slots[i] = null;
        }
        windowOwners.Clear();
      }
    }
  }
}
