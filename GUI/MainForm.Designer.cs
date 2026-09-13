/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2013 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI {
  partial class MainForm {
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing) {
      if (disposing && (components != null)) {
        components.Dispose();
      }
      base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    // Ported from MainMenu/MenuItem/ContextMenu, all of which were removed
    // from WinForms in .NET Core 3.1. Field names are unchanged so MainForm.cs
    // keeps compiling. MenuItem.Index has no equivalent (order is the order
    // items are added) and MenuItem.RadioCheck has none either (UserRadioGroup
    // maintains the exclusive check state itself).

    private void InitializeComponent() {
      this.components = new System.ComponentModel.Container();
      System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
      this.sensor = new Aga.Controls.Tree.TreeColumn();
      this.value = new Aga.Controls.Tree.TreeColumn();
      this.min = new Aga.Controls.Tree.TreeColumn();
      this.max = new Aga.Controls.Tree.TreeColumn();
      this.nodeImage = new Aga.Controls.Tree.NodeControls.NodeIcon();
      this.nodeCheckBox = new Aga.Controls.Tree.NodeControls.NodeCheckBox();
      this.nodeTextBoxText = new Aga.Controls.Tree.NodeControls.NodeTextBox();
      this.nodeTextBoxValue = new Aga.Controls.Tree.NodeControls.NodeTextBox();
      this.nodeTextBoxMin = new Aga.Controls.Tree.NodeControls.NodeTextBox();
      this.nodeTextBoxMax = new Aga.Controls.Tree.NodeControls.NodeTextBox();
      this.mainMenu = new MenuStrip();
      this.fileMenuItem = new ToolStripMenuItem();
      this.saveReportMenuItem = new ToolStripMenuItem();
      this.sumbitReportMenuItem = new ToolStripMenuItem();
      this.MenuItem2 = new ToolStripSeparator();
      this.resetMenuItem = new ToolStripMenuItem();
      this.menuItem5 = new ToolStripMenuItem();
      this.mainboardMenuItem = new ToolStripMenuItem();
      this.cpuMenuItem = new ToolStripMenuItem();
      this.ramMenuItem = new ToolStripMenuItem();
      this.gpuMenuItem = new ToolStripMenuItem();
      this.fanControllerMenuItem = new ToolStripMenuItem();
      this.hddMenuItem = new ToolStripMenuItem();
      this.menuItem6 = new ToolStripSeparator();
      this.exitMenuItem = new ToolStripMenuItem();
      this.viewMenuItem = new ToolStripMenuItem();
      this.resetMinMaxMenuItem = new ToolStripMenuItem();
      this.MenuItem3 = new ToolStripSeparator();
      this.hiddenMenuItem = new ToolStripMenuItem();
      this.plotMenuItem = new ToolStripMenuItem();
      this.gadgetMenuItem = new ToolStripMenuItem();
      this.MenuItem1 = new ToolStripSeparator();
      this.columnsMenuItem = new ToolStripMenuItem();
      this.valueMenuItem = new ToolStripMenuItem();
      this.minMenuItem = new ToolStripMenuItem();
      this.maxMenuItem = new ToolStripMenuItem();
      this.optionsMenuItem = new ToolStripMenuItem();
      this.startMinMenuItem = new ToolStripMenuItem();
      this.minTrayMenuItem = new ToolStripMenuItem();
      this.minCloseMenuItem = new ToolStripMenuItem();
      this.startupMenuItem = new ToolStripMenuItem();
      this.separatorMenuItem = new ToolStripSeparator();
      this.temperatureUnitsMenuItem = new ToolStripMenuItem();
      this.celsiusMenuItem = new ToolStripMenuItem();
      this.fahrenheitMenuItem = new ToolStripMenuItem();
      this.plotLocationMenuItem = new ToolStripMenuItem();
      this.plotWindowMenuItem = new ToolStripMenuItem();
      this.plotBottomMenuItem = new ToolStripMenuItem();
      this.plotRightMenuItem = new ToolStripMenuItem();
      this.logSeparatorMenuItem = new ToolStripSeparator();
      this.logSensorsMenuItem = new ToolStripMenuItem();
      this.loggingIntervalMenuItem = new ToolStripMenuItem();
      this.log1sMenuItem = new ToolStripMenuItem();
      this.log2sMenuItem = new ToolStripMenuItem();
      this.log5sMenuItem = new ToolStripMenuItem();
      this.log10sMenuItem = new ToolStripMenuItem();
      this.log30sMenuItem = new ToolStripMenuItem();
      this.log1minMenuItem = new ToolStripMenuItem();
      this.log2minMenuItem = new ToolStripMenuItem();
      this.log5minMenuItem = new ToolStripMenuItem();
      this.log10minMenuItem = new ToolStripMenuItem();
      this.log30minMenuItem = new ToolStripMenuItem();
      this.log1hMenuItem = new ToolStripMenuItem();
      this.log2hMenuItem = new ToolStripMenuItem();
      this.log6hMenuItem = new ToolStripMenuItem();
      this.webMenuItemSeparator = new ToolStripSeparator();
      this.webMenuItem = new ToolStripMenuItem();
      this.runWebServerMenuItem = new ToolStripMenuItem();
      this.serverPortMenuItem = new ToolStripMenuItem();
      this.helpMenuItem = new ToolStripMenuItem();
      this.aboutMenuItem = new ToolStripMenuItem();
      this.treeContextMenu = new ContextMenuStrip(this.components);
      this.saveFileDialog = new SaveFileDialog();
      this.timer = new Timer(this.components);
      this.splitContainer = new OpenHardwareMonitor.GUI.SplitContainerAdv();
      this.treeView = new Aga.Controls.Tree.TreeViewAdv();
      this.mainMenu.SuspendLayout();
      this.splitContainer.Panel1.SuspendLayout();
      this.splitContainer.SuspendLayout();
      this.SuspendLayout();
      //
      // columns
      //
      this.sensor.Header = "Sensor";
      this.sensor.SortOrder = SortOrder.None;
      this.sensor.TooltipText = null;
      this.value.Header = "Value";
      this.value.SortOrder = SortOrder.None;
      this.value.TooltipText = null;
      this.min.Header = "Min";
      this.min.SortOrder = SortOrder.None;
      this.min.TooltipText = null;
      this.max.Header = "Max";
      this.max.SortOrder = SortOrder.None;
      this.max.TooltipText = null;
      //
      // node controls
      //
      this.nodeImage.DataPropertyName = "Image";
      this.nodeImage.LeftMargin = 1;
      this.nodeImage.ParentColumn = this.sensor;
      this.nodeImage.ScaleMode = Aga.Controls.Tree.ImageScaleMode.Fit;
      this.nodeCheckBox.DataPropertyName = "Plot";
      this.nodeCheckBox.EditEnabled = true;
      this.nodeCheckBox.LeftMargin = 3;
      this.nodeCheckBox.ParentColumn = this.sensor;
      this.nodeTextBoxText.DataPropertyName = "Text";
      this.nodeTextBoxText.EditEnabled = true;
      this.nodeTextBoxText.IncrementalSearchEnabled = true;
      this.nodeTextBoxText.LeftMargin = 3;
      this.nodeTextBoxText.ParentColumn = this.sensor;
      this.nodeTextBoxText.Trimming = System.Drawing.StringTrimming.EllipsisCharacter;
      this.nodeTextBoxValue.DataPropertyName = "Value";
      this.nodeTextBoxValue.IncrementalSearchEnabled = true;
      this.nodeTextBoxValue.LeftMargin = 3;
      this.nodeTextBoxValue.ParentColumn = this.value;
      this.nodeTextBoxValue.Trimming = System.Drawing.StringTrimming.EllipsisCharacter;
      this.nodeTextBoxMin.DataPropertyName = "Min";
      this.nodeTextBoxMin.IncrementalSearchEnabled = true;
      this.nodeTextBoxMin.LeftMargin = 3;
      this.nodeTextBoxMin.ParentColumn = this.min;
      this.nodeTextBoxMin.Trimming = System.Drawing.StringTrimming.EllipsisCharacter;
      this.nodeTextBoxMax.DataPropertyName = "Max";
      this.nodeTextBoxMax.IncrementalSearchEnabled = true;
      this.nodeTextBoxMax.LeftMargin = 3;
      this.nodeTextBoxMax.ParentColumn = this.max;
      this.nodeTextBoxMax.Trimming = System.Drawing.StringTrimming.EllipsisCharacter;
      //
      // mainMenu
      //
      this.mainMenu.Items.AddRange(new ToolStripItem[] {
        this.fileMenuItem,
        this.viewMenuItem,
        this.optionsMenuItem,
        this.helpMenuItem});
      this.mainMenu.Location = new System.Drawing.Point(0, 0);
      this.mainMenu.Name = "mainMenu";
      this.mainMenu.TabIndex = 0;
      //
      // File
      //
      this.fileMenuItem.Text = "&File";
      this.fileMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.saveReportMenuItem,
        this.sumbitReportMenuItem,
        this.MenuItem2,
        this.resetMenuItem,
        this.menuItem5,
        this.menuItem6,
        this.exitMenuItem});
      this.saveReportMenuItem.Text = "Save Report...";
      this.saveReportMenuItem.Click += new System.EventHandler(this.saveReportMenuItem_Click);
      this.sumbitReportMenuItem.Text = "Submit Report...";
      this.sumbitReportMenuItem.Click += new System.EventHandler(this.sumbitReportMenuItem_Click);
      this.resetMenuItem.Text = "Reset";
      this.resetMenuItem.Click += new System.EventHandler(this.resetClick);
      this.menuItem5.Text = "Hardware";
      this.menuItem5.DropDownItems.AddRange(new ToolStripItem[] {
        this.mainboardMenuItem,
        this.cpuMenuItem,
        this.ramMenuItem,
        this.gpuMenuItem,
        this.fanControllerMenuItem,
        this.hddMenuItem});
      this.mainboardMenuItem.Text = "Mainboard";
      this.cpuMenuItem.Text = "CPU";
      this.ramMenuItem.Text = "RAM";
      this.gpuMenuItem.Text = "GPU";
      this.fanControllerMenuItem.Text = "Fan Controllers";
      this.hddMenuItem.Text = "Storage Drives";
      this.exitMenuItem.Text = "Exit";
      this.exitMenuItem.Click += new System.EventHandler(this.exitClick);
      //
      // View
      //
      this.viewMenuItem.Text = "&View";
      this.viewMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.resetMinMaxMenuItem,
        this.MenuItem3,
        this.hiddenMenuItem,
        this.plotMenuItem,
        this.gadgetMenuItem,
        this.MenuItem1,
        this.columnsMenuItem});
      this.resetMinMaxMenuItem.Text = "Reset Min/Max";
      this.resetMinMaxMenuItem.Click += new System.EventHandler(this.resetMinMaxMenuItem_Click);
      this.hiddenMenuItem.Text = "Show Hidden Sensors";
      this.plotMenuItem.Text = "Show Plot";
      this.gadgetMenuItem.Text = "Show Gadget";
      this.columnsMenuItem.Text = "Columns";
      this.columnsMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.valueMenuItem,
        this.minMenuItem,
        this.maxMenuItem});
      this.valueMenuItem.Text = "Value";
      this.minMenuItem.Text = "Min";
      this.maxMenuItem.Text = "Max";
      //
      // Options
      //
      this.optionsMenuItem.Text = "&Options";
      this.optionsMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.startMinMenuItem,
        this.minTrayMenuItem,
        this.minCloseMenuItem,
        this.startupMenuItem,
        this.separatorMenuItem,
        this.temperatureUnitsMenuItem,
        this.plotLocationMenuItem,
        this.logSeparatorMenuItem,
        this.logSensorsMenuItem,
        this.loggingIntervalMenuItem,
        this.webMenuItemSeparator,
        this.webMenuItem});
      this.startMinMenuItem.Text = "Start Minimized";
      this.minTrayMenuItem.Text = "Minimize To Tray";
      this.minCloseMenuItem.Text = "Minimize On Close";
      this.startupMenuItem.Text = "Run On Windows Startup";
      this.temperatureUnitsMenuItem.Text = "Temperature Unit";
      this.temperatureUnitsMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.celsiusMenuItem,
        this.fahrenheitMenuItem});
      this.celsiusMenuItem.Text = "Celsius";
      this.celsiusMenuItem.Click += new System.EventHandler(this.celsiusMenuItem_Click);
      this.fahrenheitMenuItem.Text = "Fahrenheit";
      this.fahrenheitMenuItem.Click += new System.EventHandler(this.fahrenheitMenuItem_Click);
      this.plotLocationMenuItem.Text = "Plot Location";
      this.plotLocationMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.plotWindowMenuItem,
        this.plotBottomMenuItem,
        this.plotRightMenuItem});
      this.plotWindowMenuItem.Text = "Window";
      this.plotBottomMenuItem.Text = "Bottom";
      this.plotRightMenuItem.Text = "Right";
      this.logSensorsMenuItem.Text = "Log Sensors";
      this.loggingIntervalMenuItem.Text = "Logging Interval";
      this.loggingIntervalMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.log1sMenuItem,
        this.log2sMenuItem,
        this.log5sMenuItem,
        this.log10sMenuItem,
        this.log30sMenuItem,
        this.log1minMenuItem,
        this.log2minMenuItem,
        this.log5minMenuItem,
        this.log10minMenuItem,
        this.log30minMenuItem,
        this.log1hMenuItem,
        this.log2hMenuItem,
        this.log6hMenuItem});
      this.log1sMenuItem.Text = "1s";
      this.log2sMenuItem.Text = "2s";
      this.log5sMenuItem.Text = "5s";
      this.log10sMenuItem.Text = "10s";
      this.log30sMenuItem.Text = "30s";
      this.log1minMenuItem.Text = "1min";
      this.log2minMenuItem.Text = "2min";
      this.log5minMenuItem.Text = "5min";
      this.log10minMenuItem.Text = "10min";
      this.log30minMenuItem.Text = "30min";
      this.log1hMenuItem.Text = "1h";
      this.log2hMenuItem.Text = "2h";
      this.log6hMenuItem.Text = "6h";
      this.webMenuItem.Text = "Web Server";
      this.webMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.runWebServerMenuItem,
        this.serverPortMenuItem});
      this.runWebServerMenuItem.Text = "Run";
      this.serverPortMenuItem.Text = "Port";
      this.serverPortMenuItem.Click += new System.EventHandler(this.serverPortMenuItem_Click);
      //
      // Help
      //
      this.helpMenuItem.Text = "&Help";
      this.helpMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
        this.aboutMenuItem});
      this.aboutMenuItem.Text = "About";
      this.aboutMenuItem.Click += new System.EventHandler(this.aboutMenuItem_Click);
      //
      // saveFileDialog
      //
      this.saveFileDialog.DefaultExt = "txt";
      this.saveFileDialog.FileName = "OpenHardwareMonitor.Report.txt";
      this.saveFileDialog.Filter = "Text Documents|*.txt|All Files|*.*";
      this.saveFileDialog.RestoreDirectory = true;
      this.saveFileDialog.Title = "Save Report As";
      //
      // timer
      //
      this.timer.Interval = 1000;
      this.timer.Tick += new System.EventHandler(this.timer_Tick);
      //
      // splitContainer
      //
      this.splitContainer.Border3DStyle = Border3DStyle.Raised;
      this.splitContainer.Color = System.Drawing.SystemColors.Control;
      this.splitContainer.Cursor = Cursors.Default;
      this.splitContainer.Location = new System.Drawing.Point(12, 12);
      this.splitContainer.Name = "splitContainer";
      this.splitContainer.Orientation = Orientation.Horizontal;
      this.splitContainer.Panel1.Controls.Add(this.treeView);
      this.splitContainer.Panel2.Cursor = Cursors.Default;
      this.splitContainer.Size = new System.Drawing.Size(386, 483);
      this.splitContainer.SplitterDistance = 354;
      this.splitContainer.SplitterWidth = 5;
      this.splitContainer.TabIndex = 3;
      //
      // treeView
      //
      this.treeView.BackColor = System.Drawing.SystemColors.Window;
      this.treeView.BorderStyle = BorderStyle.None;
      this.treeView.Columns.Add(this.sensor);
      this.treeView.Columns.Add(this.value);
      this.treeView.Columns.Add(this.min);
      this.treeView.Columns.Add(this.max);
      this.treeView.DefaultToolTipProvider = null;
      this.treeView.Dock = DockStyle.Fill;
      this.treeView.DragDropMarkColor = System.Drawing.Color.Black;
      this.treeView.FullRowSelect = true;
      this.treeView.GridLineStyle = Aga.Controls.Tree.GridLineStyle.Horizontal;
      this.treeView.LineColor = System.Drawing.SystemColors.ControlDark;
      this.treeView.Location = new System.Drawing.Point(0, 0);
      this.treeView.Model = null;
      this.treeView.Name = "treeView";
      this.treeView.NodeControls.Add(this.nodeImage);
      this.treeView.NodeControls.Add(this.nodeCheckBox);
      this.treeView.NodeControls.Add(this.nodeTextBoxText);
      this.treeView.NodeControls.Add(this.nodeTextBoxValue);
      this.treeView.NodeControls.Add(this.nodeTextBoxMin);
      this.treeView.NodeControls.Add(this.nodeTextBoxMax);
      this.treeView.SelectedNode = null;
      this.treeView.Size = new System.Drawing.Size(386, 354);
      this.treeView.TabIndex = 0;
      this.treeView.Text = "treeView";
      this.treeView.UseColumns = true;
      this.treeView.NodeMouseDoubleClick += new System.EventHandler<Aga.Controls.Tree.TreeNodeAdvMouseEventArgs>(this.treeView_NodeMouseDoubleClick);
      this.treeView.Click += new System.EventHandler(this.treeView_Click);
      this.treeView.MouseDown += new MouseEventHandler(this.treeView_MouseDown);
      this.treeView.MouseMove += new MouseEventHandler(this.treeView_MouseMove);
      this.treeView.MouseUp += new MouseEventHandler(this.treeView_MouseUp);
      //
      // MainForm
      //
      this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
      this.AutoScaleMode = AutoScaleMode.Font;
      this.ClientSize = new System.Drawing.Size(418, 554);
      // Docking is resolved from the last-added control backwards, so the
      // menu strip must be added after the fill-docked split container.
      this.Controls.Add(this.splitContainer);
      this.Controls.Add(this.mainMenu);
      this.MainMenuStrip = this.mainMenu;
      this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
      this.Name = "MainForm";
      this.StartPosition = FormStartPosition.Manual;
      this.Text = "Open Hardware Monitor";
      this.FormClosed += new FormClosedEventHandler(this.MainForm_FormClosed);
      this.Load += new System.EventHandler(this.MainForm_Load);
      this.ResizeEnd += new System.EventHandler(this.MainForm_MoveOrResize);
      this.Move += new System.EventHandler(this.MainForm_MoveOrResize);
      this.mainMenu.ResumeLayout(false);
      this.mainMenu.PerformLayout();
      this.splitContainer.Panel1.ResumeLayout(false);
      this.splitContainer.ResumeLayout(false);
      this.ResumeLayout(false);
      this.PerformLayout();
    }

    #endregion

    private Aga.Controls.Tree.TreeViewAdv treeView;
    private MenuStrip mainMenu;
    private ToolStripMenuItem fileMenuItem;
    private ToolStripMenuItem exitMenuItem;
    private Aga.Controls.Tree.TreeColumn sensor;
    private Aga.Controls.Tree.TreeColumn value;
    private Aga.Controls.Tree.TreeColumn min;
    private Aga.Controls.Tree.TreeColumn max;
    private Aga.Controls.Tree.NodeControls.NodeIcon nodeImage;
    private Aga.Controls.Tree.NodeControls.NodeTextBox nodeTextBoxText;
    private Aga.Controls.Tree.NodeControls.NodeTextBox nodeTextBoxValue;
    private Aga.Controls.Tree.NodeControls.NodeTextBox nodeTextBoxMin;
    private Aga.Controls.Tree.NodeControls.NodeTextBox nodeTextBoxMax;
    private SplitContainerAdv splitContainer;
    private ToolStripMenuItem viewMenuItem;
    private ToolStripMenuItem plotMenuItem;
    private Aga.Controls.Tree.NodeControls.NodeCheckBox nodeCheckBox;
    private ToolStripMenuItem helpMenuItem;
    private ToolStripMenuItem aboutMenuItem;
    private ToolStripMenuItem saveReportMenuItem;
    private ToolStripMenuItem optionsMenuItem;
    private ToolStripMenuItem hddMenuItem;
    private ToolStripMenuItem minTrayMenuItem;
    private ToolStripSeparator separatorMenuItem;
    private ContextMenuStrip treeContextMenu;
    private ToolStripMenuItem startMinMenuItem;
    private ToolStripMenuItem startupMenuItem;
    private SaveFileDialog saveFileDialog;
    private Timer timer;
    private ToolStripMenuItem hiddenMenuItem;
    private ToolStripSeparator MenuItem1;
    private ToolStripMenuItem columnsMenuItem;
    private ToolStripMenuItem valueMenuItem;
    private ToolStripMenuItem minMenuItem;
    private ToolStripMenuItem maxMenuItem;
    private ToolStripMenuItem temperatureUnitsMenuItem;
    private ToolStripSeparator webMenuItemSeparator;
    private ToolStripMenuItem celsiusMenuItem;
    private ToolStripMenuItem fahrenheitMenuItem;
    private ToolStripMenuItem sumbitReportMenuItem;
    private ToolStripSeparator MenuItem2;
    private ToolStripMenuItem resetMinMaxMenuItem;
    private ToolStripSeparator MenuItem3;
    private ToolStripMenuItem gadgetMenuItem;
    private ToolStripMenuItem minCloseMenuItem;
    private ToolStripMenuItem resetMenuItem;
    private ToolStripSeparator menuItem6;
    private ToolStripMenuItem plotLocationMenuItem;
    private ToolStripMenuItem plotWindowMenuItem;
    private ToolStripMenuItem plotBottomMenuItem;
    private ToolStripMenuItem plotRightMenuItem;
    private ToolStripMenuItem webMenuItem;
    private ToolStripMenuItem runWebServerMenuItem;
    private ToolStripMenuItem serverPortMenuItem;
    private ToolStripMenuItem menuItem5;
    private ToolStripMenuItem mainboardMenuItem;
    private ToolStripMenuItem cpuMenuItem;
    private ToolStripMenuItem gpuMenuItem;
    private ToolStripMenuItem fanControllerMenuItem;
    private ToolStripMenuItem ramMenuItem;
    private ToolStripMenuItem logSensorsMenuItem;
    private ToolStripSeparator logSeparatorMenuItem;
    private ToolStripMenuItem loggingIntervalMenuItem;
    private ToolStripMenuItem log1sMenuItem;
    private ToolStripMenuItem log2sMenuItem;
    private ToolStripMenuItem log5sMenuItem;
    private ToolStripMenuItem log10sMenuItem;
    private ToolStripMenuItem log30sMenuItem;
    private ToolStripMenuItem log1minMenuItem;
    private ToolStripMenuItem log2minMenuItem;
    private ToolStripMenuItem log5minMenuItem;
    private ToolStripMenuItem log10minMenuItem;
    private ToolStripMenuItem log30minMenuItem;
    private ToolStripMenuItem log1hMenuItem;
    private ToolStripMenuItem log2hMenuItem;
    private ToolStripMenuItem log6hMenuItem;
  }
}
