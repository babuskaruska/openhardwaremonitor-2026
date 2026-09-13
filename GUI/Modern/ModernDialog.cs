/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// A small themed window: a page header, content and buttons along the
  /// bottom, so dialogs match the rest of the interface rather than stock
  /// WinForms. Escape closes it.
  /// </summary>
  internal class ModernDialog : Form {

    private readonly Panel footer;
    private readonly List<ModernButton> buttons = new List<ModernButton>();
    private readonly Font dialogFont;

    public ModernDialog(Theme theme, string title, string subtitle) {
      UiTheme = theme;
      Text = title;
      AutoScaleMode = AutoScaleMode.None;
      dialogFont = new Font(Theme.TextFamily, 9.5f);
      Font = dialogFont;
      FormBorderStyle = FormBorderStyle.Sizable;
      MinimizeBox = false;
      MaximizeBox = false;
      ShowIcon = false;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.CenterParent;
      BackColor = theme.Background;
      ForeColor = theme.Text;

      Header = new PageHeader {
        Dock = DockStyle.Top,
        Theme = theme,
        Title = title,
        Subtitle = subtitle
      };
      footer = new Panel {
        Dock = DockStyle.Bottom,
        BackColor = theme.Background,
        Height = Px(68)
      };
      footer.Resize += delegate { LayoutFooter(); };

      // Docking resolves from the last-added control, so the header claims
      // the top first; content set later docks last into what remains.
      Controls.Add(footer);
      Controls.Add(Header);
    }

    protected Theme UiTheme { get; }

    protected PageHeader Header { get; }

    protected int Px(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    protected void SetContent(Control content) {
      content.Dock = DockStyle.Fill;
      Controls.Add(content);
      Controls.SetChildIndex(content, 0);
    }

    /// <summary>Adds a footer button; the first one added sits rightmost.</summary>
    protected ModernButton AddButton(string text, ButtonKind kind, Action click) {
      ModernButton button = new ModernButton {
        Text = text,
        Kind = kind,
        Theme = UiTheme,
        SurfaceColor = UiTheme.Background
      };
      button.Click += delegate { click(); };
      buttons.Add(button);
      footer.Controls.Add(button);
      LayoutFooter();
      return button;
    }

    private void LayoutFooter() {
      int x = footer.ClientSize.Width - Px(24);
      foreach (ModernButton button in buttons) {
        Size size = button.GetPreferredSize(Size.Empty);
        size.Width = Math.Max(size.Width, Px(96));
        x -= size.Width;
        button.Bounds = new Rectangle(x, (footer.ClientSize.Height - size.Height) / 2,
          size.Width, size.Height);
        x -= Px(10);
      }
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      UiTheme.ApplyWindowChrome(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
      if (keyData == Keys.Escape) {
        Close();
        return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        dialogFont.Dispose();
      base.Dispose(disposing);
    }
  }
}
