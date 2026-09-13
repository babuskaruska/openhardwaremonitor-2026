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
  /// One PawnIO module instance: a driver handle with a single module blob
  /// loaded into it.
  ///
  /// The driver keeps a module's global variables per handle, so two handles
  /// with the same module loaded do not share state. <see cref="LpcIoAdapter"/>
  /// relies on that to keep one LpcIO instance per Super I/O slot.
  ///
  /// The interface exists so the adapters built on it can be tested against a
  /// simulated module: PawnIO itself refuses non-administrator processes.
  /// </summary>
  internal interface IPawnIoModule : IDisposable {

    /// <summary>
    /// Runs a module function. Input and output lengths must equal the sizes
    /// the function declares, otherwise the driver fails the call with
    /// STATUS_INVALID_PARAMETER.
    /// </summary>
    /// <param name="hresult">The HRESULT returned by pawnio_execute. It is
    /// negative on failure, which includes the module refusing the
    /// request.</param>
    bool Execute(string function, ulong[] input, ulong[] output,
      out int hresult);
  }

  /// <summary>The real module instance, owning a PawnIOLib handle.</summary>
  internal sealed class PawnIoModule : IPawnIoModule {

    // E_HANDLE: the instance has been disposed.
    private const int InvalidHandle = unchecked((int)0x80070006);

    private IntPtr handle;

    /// <param name="handle">An open PawnIOLib handle with the module already
    /// loaded. Ownership passes to this instance.</param>
    public PawnIoModule(IntPtr handle) {
      this.handle = handle;
    }

    public bool Execute(string function, ulong[] input, ulong[] output,
      out int hresult) {
      IntPtr current = handle;
      if (current == IntPtr.Zero) {
        hresult = InvalidHandle;
        return false;
      }
      return PawnIOLib.TryExecute(current, function, input, output,
        out hresult);
    }

    public void Dispose() {
      IntPtr current = handle;
      handle = IntPtr.Zero;
      PawnIOLib.Close(current);
    }
  }
}
