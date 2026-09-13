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
  /// Binding for PawnIOLib, the user-mode client of the PawnIO driver.
  ///
  /// PawnIO is a signed kernel driver that runs small signature-verified
  /// bytecode modules. Unlike WinRing0 each module exposes only whitelisted
  /// operations rather than arbitrary MSR and memory access, which is why it
  /// is not on the vulnerable-driver blocklist.
  ///
  /// Licensing: PawnIO is used only through its library and IOCTL interface
  /// and is deliberately NOT bundled; it is detected if the user installed it.
  ///
  /// Signatures follow PawnIOLib.h as installed by PawnIO 2.2.0. Every export
  /// returns an HRESULT.
  /// </summary>
  internal static class PawnIOLib {

    private const string LibraryFileName = "PawnIOLib.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VersionFunction(out uint version);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenFunction(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadFunction(IntPtr handle, byte[] blob, IntPtr size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int ExecuteFunction(IntPtr handle, string name,
      ulong[] input, IntPtr inputSize, [Out] ulong[] output, IntPtr outputSize,
      out IntPtr returnSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseFunction(IntPtr handle);

    private static readonly object sync = new object();
    private static bool resolveAttempted;
    private static VersionFunction? versionFunction;
    private static OpenFunction? openFunction;
    private static LoadFunction? loadFunction;
    private static ExecuteFunction? executeFunction;
    private static CloseFunction? closeFunction;

    /// <summary>
    /// Full path PawnIOLib was (or would be) loaded from, for diagnostics.
    /// </summary>
    public static string? LibraryPath { get; private set; }

    /// <summary>
    /// PawnIOLib.dll lives in the PawnIO installation directory, not in
    /// System32, so the assembly-wide System32-only DllImport search path
    /// can never find it by name. It is loaded by full path instead, and only
    /// from Program Files: this library runs inside an elevated process, so
    /// it must not be loaded from anywhere a standard user can write.
    /// </summary>
    private static bool Resolve() {
      lock (sync) {
        if (resolveAttempted)
          return closeFunction != null;
        resolveAttempted = true;

        string path = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          "PawnIO", LibraryFileName);
        LibraryPath = path;
        if (!File.Exists(path) ||
          !NativeLibrary.TryLoad(path, out IntPtr library))
          return false;

        VersionFunction? version = Export<VersionFunction>(library, "pawnio_version");
        OpenFunction? open = Export<OpenFunction>(library, "pawnio_open");
        LoadFunction? load = Export<LoadFunction>(library, "pawnio_load");
        ExecuteFunction? execute = Export<ExecuteFunction>(library, "pawnio_execute");
        CloseFunction? close = Export<CloseFunction>(library, "pawnio_close");
        if (version == null || open == null || load == null ||
          execute == null || close == null)
          return false;

        versionFunction = version;
        openFunction = open;
        loadFunction = load;
        executeFunction = execute;
        closeFunction = close;
        return true;
      }
    }

    private static T? Export<T>(IntPtr library, string name) where T : Delegate {
      return NativeLibrary.TryGetExport(library, name, out IntPtr address)
        ? Marshal.GetDelegateForFunctionPointer<T>(address)
        : null;
    }

    /// <summary>True when PawnIOLib can be loaded and reports a version.</summary>
    public static bool IsInstalled {
      get { return Version != 0; }
    }

    /// <summary>
    /// Library version as (major &lt;&lt; 16) | (minor &lt;&lt; 8) | patch, or 0.
    /// </summary>
    public static uint Version {
      get {
        try {
          return Resolve() && versionFunction!(out uint version) >= 0
            ? version : 0;
        } catch (Exception) {
          return 0;
        }
      }
    }

    public static string FormatVersion(uint version) {
      return (version >> 16) + "." + ((version >> 8) & 0xFF) + "." +
        (version & 0xFF);
    }

    /// <summary>
    /// Opens an executor. Fails with an access-denied HRESULT for processes
    /// without administrator rights, which is PawnIO's default device policy.
    /// </summary>
    public static bool TryOpen(out IntPtr handle, out int hresult) {
      handle = IntPtr.Zero;
      hresult = 0;
      try {
        if (!Resolve())
          return false;
        hresult = openFunction!(out handle);
        return hresult >= 0 && handle != IntPtr.Zero;
      } catch (Exception) {
        return false;
      }
    }

    public static bool TryLoad(IntPtr handle, byte[] blob, out int hresult) {
      hresult = 0;
      try {
        if (!Resolve())
          return false;
        hresult = loadFunction!(handle, blob, (IntPtr)blob.Length);
        return hresult >= 0;
      } catch (Exception) {
        return false;
      }
    }

    public static bool TryExecute(IntPtr handle, string function,
      ulong[] input, ulong[] output) {
      try {
        if (!Resolve())
          return false;
        int hr = executeFunction!(handle, function, input,
          (IntPtr)input.Length, output, (IntPtr)output.Length, out _);
        return hr >= 0;
      } catch (Exception) {
        return false;
      }
    }

    public static void Close(IntPtr handle) {
      try {
        if (handle != IntPtr.Zero && Resolve())
          closeFunction!(handle);
      } catch (Exception) {
        // Nothing useful to do while tearing down.
      }
    }

    /// <summary>
    /// Locates a PawnIO module blob. Modules are published separately by the
    /// PawnIO.Modules project (LGPL-2.1, signed), so they are searched for
    /// rather than embedded. A user-writable location is acceptable here
    /// because the driver verifies each module's signature before loading it.
    /// </summary>
    public static byte[]? TryLoadModuleBlob(string moduleName,
      out string? foundPath) {
      foundPath = null;
      foreach (string directory in GetModuleSearchPaths()) {
        try {
          string path = Path.Combine(directory, moduleName + ".bin");
          if (File.Exists(path)) {
            foundPath = path;
            return File.ReadAllBytes(path);
          }
        } catch (Exception) {
          // Unreadable directory; try the next one.
        }
      }
      return null;
    }

    public static string[] GetModuleSearchPaths() {
      return new[] {
        Path.Combine(AppContext.BaseDirectory, "PawnIOModules"),
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "OpenHardwareMonitor", "PawnIOModules"),
        Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          "PawnIO", "modules")
      };
    }
  }
}
