/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Threading;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Hands work to the UI thread. Sensors update on a background thread, and
  /// hardware can add or remove sensors during an update; every handler that
  /// reacts by touching controls redirects itself here first.
  /// </summary>
  internal static class UiThread {

    private static SynchronizationContext? context;
    private static int threadId;

    /// <summary>Call once on the UI thread, after the first control exists.</summary>
    public static void Initialize() {
      context = SynchronizationContext.Current;
      threadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>True on the UI thread, and before initialisation.</summary>
    public static bool IsCurrent {
      get {
        return context == null || Environment.CurrentManagedThreadId == threadId;
      }
    }

    /// <summary>
    /// Posts <paramref name="action"/> to the UI thread when called from any
    /// other thread and returns true; returns false on the UI thread, so a
    /// handler can write <c>if (UiThread.Redirect(() => Handler(x))) return;</c>.
    /// </summary>
    public static bool Redirect(Action action) {
      if (IsCurrent)
        return false;
      context!.Post(_ => action(), null);
      return true;
    }
  }
}
