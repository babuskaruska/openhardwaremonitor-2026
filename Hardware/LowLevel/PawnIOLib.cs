/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>
  /// P/Invoke surface for PawnIOLib, the user-mode client of the PawnIO
  /// driver.
  ///
  /// PawnIO is a signed, Microsoft-attested kernel driver that runs small
  /// signature-verified bytecode modules in a sandboxed VM. Unlike WinRing0 it
  /// exposes only whitelisted operations rather than arbitrary MSR and memory
  /// access, which is why it is not on the vulnerable-driver blocklist.
  ///
  /// Licensing: PawnIO is GPLv2 with an explicit exception for independent
  /// modules that communicate with the driver solely through its IOCTL
  /// interface. Talking to it through this library therefore does not impose
  /// GPL terms on this MPL-2.0 codebase. PawnIO is deliberately NOT bundled —
  /// it is detected if the user installed it.
  /// </summary>
  internal static class PawnIOLib {

    private const string DllName = "PawnIOLib";

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern uint pawnio_version();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_open(out IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_load(IntPtr handle, byte[] blob,
      IntPtr size);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall,
      CharSet = CharSet.Ansi)]
    private static extern int pawnio_execute(IntPtr handle, string name,
      ulong[] input, IntPtr inputSize, ulong[] output, IntPtr outputSize,
      out IntPtr returnSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    private static extern int pawnio_close(IntPtr handle);

    /// <summary>
    /// True when PawnIOLib.dll can be resolved and responds. Any failure to
    /// bind is an ordinary "not installed" outcome, so every exception the
    /// loader can raise is treated as a negative answer rather than an error.
    /// </summary>
    public static bool IsInstalled {
      get {
        try {
          return pawnio_version() != 0;
        } catch (DllNotFoundException) {
          return false;
        } catch (EntryPointNotFoundException) {
          return false;
        } catch (BadImageFormatException) {
          return false;
        }
      }
    }

    public static uint Version {
      get {
        try {
          return pawnio_version();
        } catch (Exception) {
          return 0;
        }
      }
    }

    public static bool TryOpen(out IntPtr handle) {
      handle = IntPtr.Zero;
      try {
        return pawnio_open(out handle) >= 0 && handle != IntPtr.Zero;
      } catch (Exception) {
        return false;
      }
    }

    public static bool TryLoad(IntPtr handle, byte[] blob) {
      try {
        return pawnio_load(handle, blob, (IntPtr)blob.Length) >= 0;
      } catch (Exception) {
        return false;
      }
    }

    public static bool TryExecute(IntPtr handle, string function,
      ulong[] input, ulong[] output) {
      try {
        int hr = pawnio_execute(handle, function, input,
          (IntPtr)input.Length, output, (IntPtr)output.Length, out _);
        return hr >= 0;
      } catch (Exception) {
        return false;
      }
    }

    public static void Close(IntPtr handle) {
      try {
        if (handle != IntPtr.Zero)
          pawnio_close(handle);
      } catch (Exception) {
        // Nothing useful to do while tearing down.
      }
    }

    /// <summary>
    /// Locates a signed PawnIO module blob. Modules are published separately
    /// from this application (they are LGPL and signed by the PawnIO project),
    /// so they are searched for rather than embedded.
    /// </summary>
    public static byte[]? TryLoadModuleBlob(string moduleName) {
      foreach (string directory in GetModuleSearchPaths()) {
        try {
          string path = Path.Combine(directory, moduleName + ".bin");
          if (File.Exists(path))
            return File.ReadAllBytes(path);
        } catch (Exception) {
          // Unreadable directory; try the next one.
        }
      }
      return null;
    }

    private static string[] GetModuleSearchPaths() {
      string appDirectory = AppContext.BaseDirectory;
      return new[] {
        Path.Combine(appDirectory, "PawnIOModules"),
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          "PawnIO", "modules"),
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          "PawnIO")
      };
    }
  }
}
