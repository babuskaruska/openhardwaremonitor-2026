/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using OpenHardwareMonitor.Hardware.Maintenance;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Opens web pages, files and Explorer windows for the user.
  ///
  /// The application usually runs as administrator, and anything it starts
  /// directly inherits that: a browser would run elevated. When elevated,
  /// pages and files are therefore handed to explorer.exe, which passes them
  /// to the desktop shell running with the user's normal rights.
  /// </summary>
  internal static class ShellLauncher {

    private static string ExplorerPath {
      get {
        return Path.Combine(Environment.GetFolderPath(
          Environment.SpecialFolder.Windows), "explorer.exe");
      }
    }

    /// <summary>Opens an https address in the default browser.</summary>
    public static bool OpenUrl(string url) {
      if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
        uri.Scheme != Uri.UriSchemeHttps)
        return false;
      return Open(uri.AbsoluteUri, "the browser");
    }

    /// <summary>Opens a file with its default application.</summary>
    public static bool OpenFile(string path) {
      if (!File.Exists(path))
        return false;
      return Open(path, Path.GetFileName(path));
    }

    /// <summary>Opens an Explorer window with the file selected.</summary>
    public static bool ShowInExplorer(string path) {
      return Start(new ProcessStartInfo(ExplorerPath, "/select,\"" + path + "\"") {
        UseShellExecute = false
      }, "File Explorer");
    }

    private static bool Open(string target, string description) {
      ProcessStartInfo info = Environment.IsPrivilegedProcess
        ? new ProcessStartInfo(ExplorerPath, "\"" + target + "\"") { UseShellExecute = false }
        : new ProcessStartInfo(target) { UseShellExecute = true };
      return Start(info, description);
    }

    private static bool Start(ProcessStartInfo info, string description) {
      try {
        using (Process.Start(info)) { }
        return true;
      } catch (Exception ex) when (ex is Win32Exception ||
        ex is InvalidOperationException || ex is IOException ||
        ex is PlatformNotSupportedException) {
        ApplicationLog.Warning("Could not open " + description + ": " + ex.Message);
        return false;
      }
    }
  }
}
