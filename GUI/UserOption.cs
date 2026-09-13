/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2010 Michael Möller <mmoeller@openhardwaremonitor.org>

*/

using System;
using System.Windows.Forms;
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// A persisted boolean option bound to a checkable menu item.
  /// </summary>
  public class UserOption {
    private readonly string name;
    private bool value;
    private readonly ToolStripMenuItem menuItem;
    private event EventHandler changed;
    private readonly PersistentSettings settings;

    public UserOption(string name, bool value,
      ToolStripMenuItem menuItem, PersistentSettings settings) {

      this.settings = settings;
      this.name = name;
      if (name != null)
        this.value = settings.GetValue(name, value);
      else
        this.value = value;
      this.menuItem = menuItem;
      this.menuItem.Checked = this.value;
      // CheckOnClick stays off: the check mark follows Value, so that a
      // Changed handler which rejects the new value (see autoStart in
      // MainForm) can put the check back without fighting the menu item.
      this.menuItem.Click += new EventHandler(menuItem_Click);
    }

    private void menuItem_Click(object sender, EventArgs e) {
      this.Value = !this.Value;
    }

    public bool Value {
      get { return value; }
      set {
        if (this.value != value) {
          this.value = value;
          if (this.name != null)
            settings.SetValue(name, value);
          this.menuItem.Checked = value;
          if (changed != null)
            changed(this, null);
        }
      }
    }

    public event EventHandler Changed {
      add {
        changed += value;
        if (changed != null)
          changed(this, null);
      }
      remove {
        changed -= value;
      }
    }
  }
}
