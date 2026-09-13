/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using Aga.Controls.Tree;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// The 2026 interface: an overview of cards as the default screen, the full
  /// sensor list one click away, one theme across menus, lists and window
  /// chrome, and short transitions between the two views.
  /// </summary>
  partial class MainForm {

    private Theme uiTheme = null!;
    private OverviewPanel overview = null!;
    private DetailsHeader detailsHeader = null!;
    private ToolStripMenuItem overviewMenuItem = null!;
    private ToolStripMenuItem allSensorsMenuItem = null!;

    private int ScaleLogical(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    /// <summary>
    /// Called from the constructor after the computer exists and before it is
    /// opened, so the overview receives every HardwareAdded event.
    /// </summary>
    private void InitializeModernInterface() {
      uiTheme = Theme.Current;
      BackColor = uiTheme.Background;
      ForeColor = uiTheme.Text;

      // Menu bar, drop-downs, the sensor context menu and the tray menus all
      // render through the manager renderer.
      ToolStripManager.Renderer = new ModernMenuRenderer(uiTheme);
      ModernMenuRenderer.Apply(mainMenu, uiTheme);

      overviewMenuItem = new ToolStripMenuItem("Overview", null,
        delegate { ShowOverview(); }) {
        ShortcutKeys = Keys.Control | Keys.D1,
        Checked = true
      };
      allSensorsMenuItem = new ToolStripMenuItem("All sensors", null,
        delegate { ShowDetails(null); }) {
        ShortcutKeys = Keys.Control | Keys.D2
      };
      viewMenuItem.DropDownItems.Insert(0, overviewMenuItem);
      viewMenuItem.DropDownItems.Insert(1, allSensorsMenuItem);
      viewMenuItem.DropDownItems.Insert(2, new ToolStripSeparator());

      // Overview, the default screen. Index 0 docks last, below the menu.
      overview = new OverviewPanel(computer, unitManager) {
        Dock = DockStyle.Fill,
        Theme = uiTheme,
        ShowExportButton = true
      };
      overview.DetailsRequested += delegate(object? sender, IHardware? hardware) {
        ShowDetails(hardware);
      };
      overview.ExportRequested += delegate {
        exportDiagnosticsMenuItem_Click(this, EventArgs.Empty);
      };
      Controls.Add(overview);
      Controls.SetChildIndex(overview, 0);

      // The full list: a header with the way back, and the tree with room
      // to breathe instead of grid lines.
      detailsHeader = new DetailsHeader { Dock = DockStyle.Top, Theme = uiTheme };
      detailsHeader.BackClicked += delegate { ShowOverview(); };

      Panel treeHost = new Panel {
        Dock = DockStyle.Fill,
        BackColor = uiTheme.Background,
        Padding = new Padding(ScaleLogical(18), ScaleLogical(2), ScaleLogical(18), ScaleLogical(10))
      };
      splitContainer.Panel1.Controls.Remove(treeView);
      treeHost.Controls.Add(treeView);
      splitContainer.Panel1.Controls.Add(treeHost);
      splitContainer.Panel1.Controls.Add(detailsHeader);

      splitContainer.BackColor = uiTheme.Background;
      splitContainer.Panel1.BackColor = uiTheme.Background;
      splitContainer.Panel2.BackColor = uiTheme.Background;
      splitContainer.Color = uiTheme.Background;
      splitContainer.BorderStyle = BorderStyle.None;
      splitContainer.Border3DStyle = Border3DStyle.Adjust;

      TreeColumn.FlatHeaders = true;
      TreeColumn.HeaderBackground = uiTheme.Background;
      TreeColumn.HeaderForeground = uiTheme.Text;
      treeView.Font = new Font(Theme.TextFamily, 9.5f);
      treeView.BackColor = uiTheme.Background;
      treeView.ForeColor = uiTheme.Text;
      treeView.LineColor = uiTheme.Border;
      treeView.GridLineStyle = GridLineStyle.None;
      treeView.ShowLines = false;
      root.Image = ModernIcons.Computer();
      treeView.BorderStyle = BorderStyle.None;
      treeView.RowHeight = treeView.Font.Height + ScaleLogical(12);

      splitContainer.Visible = false;
      overview.Visible = true;
    }

    private SensorPoller poller = null!;

    /// <summary>
    /// Sensors are read on a background thread so the window never waits for
    /// hardware, and that thread also opens the computer. Created early in the
    /// constructor, because the sensor options use its lock, and started at
    /// the end.
    /// </summary>
    private void CreateSensorPoller() {
      UiThread.Initialize();
      poller = new SensorPoller(computer, this, PollOnSensorThread, RefreshAfterPoll,
        () => computer.Open());
    }

    private void StartSensorPolling() {
      poller.Start();
    }

    private void StopSensorPolling() {
      poller?.Stop();
    }

    /// <summary>Runs on the sensor thread after each update, under the hardware lock.</summary>
    private void PollOnSensorThread() {
      fanCurves.Update();
      if (logSensors != null && logSensors.Value && delayCount >= 4)
        logger.Log();
      if (delayCount < 4)
        delayCount++;
    }

    /// <summary>
    /// Runs on the UI thread after each update while the sensor thread waits,
    /// so nothing reads sensor values while they change. Work for views that
    /// are not visible is skipped.
    /// </summary>
    private void RefreshAfterPoll() {
      overview.UpdateValues();
      if (splitContainer.Visible)
        treeView.Invalidate();
      if (showPlot != null && showPlot.Value)
        plotPanel.InvalidatePlot();
      systemTray.Redraw();
      gadget?.Redraw();
      if (fansPage != null && fansPage.Visible)
        fansPage.UpdateValues();
      if (settingsPage != null && settingsPage.Visible)
        settingsPage.RefreshValues();
      UpdateHistoryPage();
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      uiTheme?.ApplyWindowChrome(this);
    }

    private void ShowDetails(IHardware? hardware) {
      if (hardware != null)
        RevealHardware(hardware);

      ShowPage(1);

      if (hardware != null && treeView.SelectedNode != null)
        treeView.EnsureVisible(treeView.SelectedNode);
      treeView.Focus();
    }

    private void ShowOverview() {
      ShowPage(0);
    }

    /// <summary>Expands and selects the list entry of one piece of hardware.</summary>
    private void RevealHardware(IHardware hardware) {
      foreach (TreeNodeAdv node in treeView.AllNodes) {
        if (node.Tag is HardwareNode hardwareNode && hardwareNode.Hardware == hardware) {
          node.IsExpanded = true;
          treeView.SelectedNode = node;
          treeView.EnsureVisible(node);
          return;
        }
      }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
      if (ProcessHistoryKey(keyData))
        return true;
      // Leave Escape to an in-place sensor rename, which hosts an editor
      // inside the tree.
      bool editing = treeView.ContainsFocus && !treeView.Focused;
      if (currentPage != 0 && !editing &&
        (keyData == Keys.Escape || keyData == (Keys.Alt | Keys.Left))) {
        ShowOverview();
        return true;
      }
      switch (keyData) {
        case Keys.Control | Keys.D1: ShowPage(0); return true;
        case Keys.Control | Keys.D2: ShowPage(1); return true;
        case Keys.Control | Keys.D3: ShowPage(2); return true;
        case Keys.Control | Keys.D4: ShowPage(3); return true;
        case Keys.Control | Keys.E:
          exportDiagnosticsMenuItem_Click(this, EventArgs.Empty);
          return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }
  }
}
