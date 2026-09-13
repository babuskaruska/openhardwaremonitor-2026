/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware.Maintenance;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Shown after an unhandled exception, once <see cref="CrashReporter"/> has
  /// saved the report. The 2009 dialog asked for an email address and a
  /// comment to upload to a server that no longer exists; this one points to
  /// the saved file and to GitHub instead.
  /// </summary>
  internal sealed class CrashForm : Form {

    private readonly string? reportPath;
    private readonly string issueUrl;
    private readonly Theme theme = Theme.Current;
    private readonly Font titleFont;
    private readonly Font bodyFont;
    private readonly Font reportFont;
    private readonly ModernButton closeButton;

    public CrashForm(string report, string? reportPath, string issueUrl) {
      this.reportPath = reportPath;
      this.issueUrl = issueUrl;
      int dpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 14f, FontStyle.Regular, dpi);
      bodyFont = Theme.CreateFont(Theme.TextFamily, 9.5f, FontStyle.Regular, dpi);
      reportFont = Theme.CreateFont("Consolas", 9f, FontStyle.Regular, dpi);

      SuspendLayout();
      Text = "Open Hardware Monitor";
      // Sizes below are already in device pixels.
      AutoScaleMode = AutoScaleMode.None;
      Font = bodyFont;
      BackColor = theme.Background;
      ForeColor = theme.Text;
      MinimizeBox = false;
      MaximizeBox = false;
      ShowIcon = false;
      // The main window may be hidden in the notification area.
      ShowInTaskbar = true;
      StartPosition = FormStartPosition.CenterScreen;
      KeyPreview = true;
      ClientSize = new Size(S(680), S(540));
      MinimumSize = new Size(S(520), S(420));

      TableLayoutPanel layout = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 5,
        BackColor = theme.Background,
        Padding = new Padding(S(24), S(20), S(24), S(20))
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

      Label title = new Label {
        AutoSize = true,
        Font = titleFont,
        ForeColor = theme.Text,
        Text = "Open Hardware Monitor stopped working",
        Margin = new Padding(0, 0, 0, S(8))
      };

      Label message = new Label {
        AutoSize = true,
        Font = bodyFont,
        ForeColor = theme.TextSecondary,
        Margin = new Padding(0, 0, 0, S(14)),
        Text = reportPath != null
          ? "A crash report was saved. It lists the error and facts about the app " +
            "and Windows, and nothing personal. To help fix the problem, report it " +
            "on GitHub and attach the report."
          : "The crash report could not be saved. To help fix the problem, report " +
            "it on GitHub and paste the text below."
      };

      Panel border = new Panel {
        Dock = DockStyle.Fill,
        BackColor = theme.Border,
        Padding = new Padding(Math.Max(1, S(1))),
        Margin = new Padding(0)
      };
      Panel surface = new Panel {
        Dock = DockStyle.Fill,
        BackColor = theme.Surface,
        Padding = new Padding(S(10), S(8), S(2), S(2))
      };
      TextBox reportBox = new TextBox {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        BorderStyle = BorderStyle.None,
        BackColor = theme.Surface,
        ForeColor = theme.Text,
        Font = reportFont,
        TabStop = false,
        Text = (report ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")
      };
      surface.Controls.Add(reportBox);
      border.Controls.Add(surface);

      Label location = new Label {
        AutoSize = true,
        Font = bodyFont,
        ForeColor = theme.TextTertiary,
        Margin = new Padding(0, S(10), 0, 0),
        Text = reportPath != null ? "Saved as " + PrivacyFilter.Current.Scrub(reportPath) : ""
      };

      FlowLayoutPanel buttons = new FlowLayoutPanel {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.RightToLeft,
        WrapContents = false,
        BackColor = theme.Background,
        Margin = new Padding(0, S(16), 0, 0)
      };
      closeButton = CreateButton("Close", ButtonKind.Primary, Close);
      ModernButton gitHubButton = CreateButton("Report on GitHub", ButtonKind.Secondary,
        ReportOnGitHub);
      ModernButton openButton = CreateButton("Open report", ButtonKind.Secondary, OpenReport);
      openButton.Enabled = reportPath != null;
      // Right to left: Close ends up on the right.
      buttons.Controls.Add(closeButton);
      buttons.Controls.Add(gitHubButton);
      buttons.Controls.Add(openButton);

      layout.Controls.Add(title, 0, 0);
      layout.Controls.Add(message, 0, 1);
      layout.Controls.Add(border, 0, 2);
      layout.Controls.Add(location, 0, 3);
      layout.Controls.Add(buttons, 0, 4);
      // Wrap long lines to the dialog's width as it is resized.
      layout.Resize += delegate {
        int width = Math.Max(S(200), layout.ClientSize.Width - layout.Padding.Horizontal);
        message.MaximumSize = new Size(width, 0);
        location.MaximumSize = new Size(width, 0);
      };
      Controls.Add(layout);
      ResumeLayout(true);

      Shown += delegate { closeButton.Focus(); };
    }

    private int S(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    private ModernButton CreateButton(string text, ButtonKind kind, Action action) {
      ModernButton button = new ModernButton {
        Text = text,
        Kind = kind,
        Theme = theme,
        SurfaceColor = theme.Background,
        AutoSize = true,
        Margin = new Padding(S(8), 0, 0, 0)
      };
      button.Click += delegate { action(); };
      return button;
    }

    private void OpenReport() {
      if (reportPath != null && !ShellLauncher.OpenFile(reportPath))
        ShellLauncher.ShowInExplorer(reportPath);
    }

    private void ReportOnGitHub() {
      // Explorer shows the file, ready to drag into the issue.
      if (reportPath != null)
        ShellLauncher.ShowInExplorer(reportPath);
      if (!ShellLauncher.OpenUrl(issueUrl))
        MessageBox.Show(this, "The browser could not be opened. Report the problem at:\n\n" +
          IssueLink.NewIssueUrl, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      theme.ApplyWindowChrome(this);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (e.KeyCode == Keys.Escape) {
        e.Handled = true;
        Close();
      }
    }

    protected override void Dispose(bool disposing) {
      base.Dispose(disposing);
      if (disposing) {
        titleFont.Dispose();
        bodyFont.Dispose();
        reportFont.Dispose();
      }
    }
  }
}
