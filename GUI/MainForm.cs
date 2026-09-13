/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
	Copyright (C) 2010 Paul Werelds <paul@werelds.net>
	Copyright (C) 2012 Prince Samuel <prince.samuel@gmail.com>

*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor.GUI {
  public partial class MainForm : Form {

    private PersistentSettings settings;
    private UnitManager unitManager;
    private Computer computer;
    private Node root;
    private TreeModel treeModel;
    private IDictionary<ISensor, Color> sensorPlotColors = 
      new Dictionary<ISensor, Color>();
    private Color[] plotColorPalette;
    private SystemTray systemTray;    
    private StartupManager startupManager = new StartupManager();
    private UpdateVisitor updateVisitor = new UpdateVisitor();
    private SensorGadget gadget;
    private Form plotForm;
    private PlotPanel plotPanel;

    private UserOption showHiddenSensors;
    private UserOption showPlot;
    private UserOption showValue;
    private UserOption showMin;
    private UserOption showMax;
    private UserOption startMinimized;
    private UserOption minimizeToTray;
    private UserOption minimizeOnClose;
    private UserOption autoStart;

    private UserOption readMainboardSensors;
    private UserOption readCpuSensors;
    private UserOption readRamSensors;
    private UserOption readGpuSensors;
    private UserOption readFanControllersSensors;
    private UserOption readHddSensors;

    private UserOption showGadget;
    private UserRadioGroup plotLocation;
    private UserRadioGroup theme;

    private UserOption runWebServer;
    private HttpServer server;

    private UserOption logSensors;
    private UserRadioGroup loggingInterval;
    private Logger logger;
    private FanCurveController fanCurves;

    private bool selectionDragging = false;

    public MainForm() {      
      InitializeComponent();

      // check if the OpenHardwareMonitorLib assembly has the correct version
      if (Assembly.GetAssembly(typeof(Computer)).GetName().Version !=
        Assembly.GetExecutingAssembly().GetName().Version) {
        MessageBox.Show(
          "The version of the file OpenHardwareMonitorLib.dll is incompatible.",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(0);
      }

      this.settings = new PersistentSettings();      
      this.settings.Load(GetConfigurationPath());

      this.unitManager = new UnitManager(settings);

      // make sure the buffers used for double buffering are not disposed 
      // after each draw call
      BufferedGraphicsManager.Current.MaximumBuffer =
        Screen.PrimaryScreen.Bounds.Size;  

      // set the DockStyle here, to avoid conflicts with the MainMenu
      this.splitContainer.Dock = DockStyle.Fill;
            
      this.Font = SystemFonts.MessageBoxFont;
      treeView.Font = SystemFonts.MessageBoxFont;

      plotPanel = new PlotPanel(settings, unitManager);
      plotPanel.Font = SystemFonts.MessageBoxFont;
      plotPanel.Dock = DockStyle.Fill;
      
      nodeCheckBox.IsVisibleValueNeeded += nodeCheckBox_IsVisibleValueNeeded;
      nodeTextBoxText.DrawText += nodeTextBoxText_DrawText;
      nodeTextBoxValue.DrawText += nodeTextBoxText_DrawText;
      nodeTextBoxMin.DrawText += nodeTextBoxText_DrawText;
      nodeTextBoxMax.DrawText += nodeTextBoxText_DrawText;
      nodeTextBoxText.EditorShowing += nodeTextBoxText_EditorShowing;

      this.sensor.Width = DpiHelper.LogicalToDeviceUnits(250);
      this.value.Width = DpiHelper.LogicalToDeviceUnits(100);
      this.min.Width = DpiHelper.LogicalToDeviceUnits(100);
      this.max.Width = DpiHelper.LogicalToDeviceUnits(100);

      foreach (TreeColumn column in treeView.Columns) 
        column.Width = Math.Max(DpiHelper.LogicalToDeviceUnits(20), Math.Min(
          DpiHelper.LogicalToDeviceUnits(400),
          settings.GetValue("treeView.Columns." + column.Header + ".Width",
          column.Width)));

      treeModel = new TreeModel();
      root = new Node(System.Environment.MachineName);
      root.Image = Utilities.EmbeddedResources.GetImage("computer.png");
      
      treeModel.Nodes.Add(root);
      treeView.Model = treeModel;

      this.computer = new Computer(settings);

      systemTray = new SystemTray(computer, settings, unitManager);
      systemTray.HideShowCommand += hideShowClick;
      systemTray.ExitCommand += exitClick;
      systemTray.ExportDiagnosticsCommand += exportDiagnosticsMenuItem_Click;

      if (Hardware.OperatingSystem.IsUnix) { // Unix
        treeView.RowHeight = Math.Max(treeView.RowHeight,
          DpiHelper.LogicalToDeviceUnits(18)); 
        splitContainer.BorderStyle = BorderStyle.None;
        splitContainer.Border3DStyle = Border3DStyle.Adjust;
        splitContainer.SplitterWidth = 4;
        treeView.BorderStyle = BorderStyle.Fixed3D;
        plotPanel.BorderStyle = BorderStyle.Fixed3D;
        gadgetMenuItem.Visible = false;
        minCloseMenuItem.Visible = false;
        minTrayMenuItem.Visible = false;
        startMinMenuItem.Visible = false;
      } else { // Windows
        treeView.RowHeight = Math.Max(treeView.Font.Height +
          DpiHelper.LogicalToDeviceUnits(1),
          DpiHelper.LogicalToDeviceUnits(18)); 

        gadget = new SensorGadget(computer, settings, unitManager);
        gadget.HideShowCommand += hideShowClick;

      }

      logger = new Logger(computer);

      // Temperature-driven fan curves, set from a fan control context menu.
      // Every fan this drove is handed back to the hardware however the process
      // ends, including Environment.Exit from the crash handler, which never
      // reaches FormClosed.
      fanCurves = new FanCurveController(computer, settings);
      AppDomain.CurrentDomain.ProcessExit += delegate { fanCurves.Release(); };

      plotColorPalette = new Color[13];
      plotColorPalette[0] = Color.Blue;
      plotColorPalette[1] = Color.OrangeRed;
      plotColorPalette[2] = Color.Green;
      plotColorPalette[3] = Color.LightSeaGreen;
      plotColorPalette[4] = Color.Goldenrod;
      plotColorPalette[5] = Color.DarkViolet;
      plotColorPalette[6] = Color.YellowGreen;
      plotColorPalette[7] = Color.SaddleBrown;
      plotColorPalette[8] = Color.RoyalBlue;
      plotColorPalette[9] = Color.DeepPink;
      plotColorPalette[10] = Color.MediumSeaGreen;
      plotColorPalette[11] = Color.Olive;
      plotColorPalette[12] = Color.Firebrick;
      
      InitializeModernInterface();

      computer.HardwareAdded += new HardwareEventHandler(HardwareAdded);
      computer.HardwareRemoved += new HardwareEventHandler(HardwareRemoved);        

      // Detecting hardware takes from half a second to several seconds, so the
      // computer is opened on the sensor thread once the window exists.
      CreateSensorPoller();

      Microsoft.Win32.SystemEvents.PowerModeChanged += PowerModeChanged;

      showHiddenSensors = new UserOption("hiddenMenuItem", false,
        hiddenMenuItem, settings);
      showHiddenSensors.Changed += delegate(object sender, EventArgs e) {
        treeModel.ForceVisible = showHiddenSensors.Value;
      };

      showValue = new UserOption("valueMenuItem", true, valueMenuItem,
        settings);
      showValue.Changed += delegate(object sender, EventArgs e) {
        treeView.Columns[1].IsVisible = showValue.Value;
      };

      showMin = new UserOption("minMenuItem", false, minMenuItem, settings);
      showMin.Changed += delegate(object sender, EventArgs e) {
        treeView.Columns[2].IsVisible = showMin.Value;
      };

      showMax = new UserOption("maxMenuItem", true, maxMenuItem, settings);
      showMax.Changed += delegate(object sender, EventArgs e) {
        treeView.Columns[3].IsVisible = showMax.Value;
      };

      startMinimized = new UserOption("startMinMenuItem", false,
        startMinMenuItem, settings);

      minimizeToTray = new UserOption("minTrayMenuItem", true,
        minTrayMenuItem, settings);
      minimizeToTray.Changed += delegate(object sender, EventArgs e) {
        systemTray.IsMainIconEnabled = minimizeToTray.Value;
      };

      minimizeOnClose = new UserOption("minCloseMenuItem", false,
        minCloseMenuItem, settings);

      autoStart = new UserOption(null, startupManager.Startup,
        startupMenuItem, settings);
      autoStart.Changed += delegate(object sender, EventArgs e) {
        try {
          startupManager.Startup = autoStart.Value;
        } catch (InvalidOperationException) {
          MessageBox.Show("Updating the auto-startup option failed.", "Error", 
            MessageBoxButtons.OK, MessageBoxIcon.Error);
          autoStart.Value = startupManager.Startup;
        }
      };

      readMainboardSensors = new UserOption("mainboardMenuItem", true, 
        mainboardMenuItem, settings);
      readMainboardSensors.Changed += delegate(object sender, EventArgs e) {
        poller.RunLocked(() => computer.MainboardEnabled = readMainboardSensors.Value);
      };

      readCpuSensors = new UserOption("cpuMenuItem", true,
        cpuMenuItem, settings);
      readCpuSensors.Changed += delegate(object sender, EventArgs e) {
        poller.RunLocked(() => computer.CPUEnabled = readCpuSensors.Value);
      };

      readRamSensors = new UserOption("ramMenuItem", true,
        ramMenuItem, settings);
      readRamSensors.Changed += delegate(object sender, EventArgs e) {
        poller.RunLocked(() => computer.RAMEnabled = readRamSensors.Value);
      };

      readGpuSensors = new UserOption("gpuMenuItem", true,
        gpuMenuItem, settings);
      readGpuSensors.Changed += delegate(object sender, EventArgs e) {
        poller.RunLocked(() => computer.GPUEnabled = readGpuSensors.Value);
      };

      readFanControllersSensors = new UserOption("fanControllerMenuItem", true,
        fanControllerMenuItem, settings);
      readFanControllersSensors.Changed += delegate(object sender, EventArgs e) {
        poller.RunLocked(() => computer.FanControllerEnabled = readFanControllersSensors.Value);
      };

      readHddSensors = new UserOption("hddMenuItem", true, hddMenuItem,
        settings);
      readHddSensors.Changed += delegate(object sender, EventArgs e) {
        poller.RunLocked(() => computer.HDDEnabled = readHddSensors.Value);
      };

      showGadget = new UserOption("gadgetMenuItem", false, gadgetMenuItem,
        settings);
      showGadget.Changed += delegate(object sender, EventArgs e) {
        if (gadget != null) 
          gadget.Visible = showGadget.Value;
      };

      celsiusMenuItem.Checked = 
        unitManager.TemperatureUnit == TemperatureUnit.Celsius;
      fahrenheitMenuItem.Checked = !celsiusMenuItem.Checked;

      // Theme. WinForms applies the colour mode only to windows created
      // after it is set (see Program.ReadColorMode), so a change applies on
      // the next start rather than immediately.
      ToolStripMenuItem themeMenuItem = new ToolStripMenuItem("Theme");
      ToolStripMenuItem[] themeItems = {
        new ToolStripMenuItem("Follow Windows"),
        new ToolStripMenuItem("Light"),
        new ToolStripMenuItem("Dark") };
      themeMenuItem.DropDownItems.AddRange(themeItems);
      optionsMenuItem.DropDownItems.Insert(
        optionsMenuItem.DropDownItems.IndexOf(temperatureUnitsMenuItem),
        themeMenuItem);
      theme = new UserRadioGroup("theme", 0, themeItems, settings);
      bool themeInitialized = false;
      theme.Changed += delegate(object sender, EventArgs e) {
        // Changed fires once on subscription; only react to real changes.
        if (!themeInitialized)
          return;
        MessageBox.Show(this, "The new theme will be applied the next time " +
          "Open Hardware Monitor starts.", "Theme", MessageBoxButtons.OK,
          MessageBoxIcon.Information);
      };
      themeInitialized = true;

      server = new HttpServer(root, computer, this, settings);
      server.HardwareLock = poller.Sync;
      if (server.PlatformNotSupported) {
        webMenuItemSeparator.Visible = false;
        webMenuItem.Visible = false;
      }

      runWebServer = new UserOption("runWebServerMenuItem", false,
        runWebServerMenuItem, settings);
      runWebServer.Changed += delegate(object sender, EventArgs e) {
        if (runWebServer.Value) {
          // Starting used to fail silently (port in use, no rights to bind),
          // leaving Run checked with nothing listening.
          if (!server.StartHTTPListener()) {
            string error = server.LastError;
            runWebServer.Value = false;
            if (Visible && error != null)
              MessageBox.Show(this, "The web server could not be started:\n\n" +
                error, "Web Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
          }
        } else {
          server.StopHTTPListener();
        }
      };

      // Remote access is opt-in and token-protected; see HttpServer.
      ToolStripMenuItem allowRemoteMenuItem =
        new ToolStripMenuItem("Allow Remote Connections");
      allowRemoteMenuItem.Checked = server.AllowRemoteConnections;
      allowRemoteMenuItem.Click += delegate(object sender, EventArgs e) {
        bool allow = !server.AllowRemoteConnections;
        if (allow && MessageBox.Show(this,
          "Other devices on your network will be able to read your sensors " +
          "using a link that contains an access token (see Web Server > Port).\n\n" +
          "Listening on the network requires running as administrator.",
          "Allow Remote Connections", MessageBoxButtons.OKCancel,
          MessageBoxIcon.Information) != DialogResult.OK)
          return;
        server.AllowRemoteConnections = allow;
        allowRemoteMenuItem.Checked = allow;
        if (runWebServer.Value && !server.IsListening) {
          string error = server.LastError;
          runWebServer.Value = false;
          if (error != null)
            MessageBox.Show(this, "The web server could not be restarted:\n\n" +
              error, "Web Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      };
      webMenuItem.DropDownItems.Insert(1, allowRemoteMenuItem);

      logSensors = new UserOption("logSensorsMenuItem", false, logSensorsMenuItem,
        settings);

      // Logs now live under %LOCALAPPDATA% (see Logger), not beside the
      // executable, so give people a way to find them.
      ToolStripMenuItem openLogFolderMenuItem =
        new ToolStripMenuItem("Open Log Folder");
      openLogFolderMenuItem.Click += delegate(object sender, EventArgs e) {
        try {
          Directory.CreateDirectory(Logger.LogDirectory);
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Logger.LogDirectory) { UseShellExecute = true });
        } catch (Exception ex) {
          MessageBox.Show(this, "The log folder could not be opened:\n\n" +
            ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
      };
      optionsMenuItem.DropDownItems.Insert(
        optionsMenuItem.DropDownItems.IndexOf(loggingIntervalMenuItem) + 1,
        openLogFolderMenuItem);

      loggingInterval = new UserRadioGroup("loggingInterval", 0,
        new[] { log1sMenuItem, log2sMenuItem, log5sMenuItem, log10sMenuItem,
        log30sMenuItem, log1minMenuItem, log2minMenuItem, log5minMenuItem, 
        log10minMenuItem, log30minMenuItem, log1hMenuItem, log2hMenuItem, 
        log6hMenuItem},
        settings);
      loggingInterval.Changed += (sender, e) => {
        switch (loggingInterval.Value) {
          case 0: logger.LoggingInterval = new TimeSpan(0, 0, 1); break;
          case 1: logger.LoggingInterval = new TimeSpan(0, 0, 2); break;
          case 2: logger.LoggingInterval = new TimeSpan(0, 0, 5); break;
          case 3: logger.LoggingInterval = new TimeSpan(0, 0, 10); break;
          case 4: logger.LoggingInterval = new TimeSpan(0, 0, 30); break;
          case 5: logger.LoggingInterval = new TimeSpan(0, 1, 0); break;
          case 6: logger.LoggingInterval = new TimeSpan(0, 2, 0); break;
          case 7: logger.LoggingInterval = new TimeSpan(0, 5, 0); break;
          case 8: logger.LoggingInterval = new TimeSpan(0, 10, 0); break;
          case 9: logger.LoggingInterval = new TimeSpan(0, 30, 0); break;
          case 10: logger.LoggingInterval = new TimeSpan(1, 0, 0); break;
          case 11: logger.LoggingInterval = new TimeSpan(2, 0, 0); break;
          case 12: logger.LoggingInterval = new TimeSpan(6, 0, 0); break;
        }
      };

      InitializePlotForm();

      startupMenuItem.Visible = startupManager.IsAvailable;
      
      if (startMinMenuItem.Checked) {
        if (!minTrayMenuItem.Checked) {
          WindowState = FormWindowState.Minimized;
          Show();
        }
      } else {
        Show();
      }

      InitializePages();

      // Create a handle, otherwise calling Close() does not fire FormClosed     
      IntPtr handle = Handle;

      // Sensors are polled only once the window is fully built. Starting earlier
      // made every sensor option above wait for the first update to release
      // the hardware lock, which delayed the window by several seconds.
      StartSensorPolling();

      // Make sure the settings are saved when the user logs off
      Microsoft.Win32.SystemEvents.SessionEnded += delegate {
        StopSensorPolling();
        // Waits for the sensor thread if it is still opening the hardware.
        poller.RunLocked(() => {
          fanCurves.Release();
          computer.Close();
        });
        SaveConfiguration();
        if (runWebServer.Value) 
          server.Quit();
      };
    }

    private void PowerModeChanged(object sender,
      Microsoft.Win32.PowerModeChangedEventArgs e) {

      if (e.Mode == Microsoft.Win32.PowerModes.Resume) {
        poller.RunLocked(() => computer.Reset());
      }
    }

    private void InitializePlotForm() {
      plotForm = new Form();
      plotForm.FormBorderStyle = FormBorderStyle.SizableToolWindow;
      plotForm.ShowInTaskbar = false;
      plotForm.StartPosition = FormStartPosition.Manual;
      this.AddOwnedForm(plotForm);
      plotForm.Bounds = new Rectangle {
        X = settings.GetValue("plotForm.Location.X", -100000),
        Y = settings.GetValue("plotForm.Location.Y", 100),
        Width = settings.GetValue("plotForm.Width", 600),
        Height = settings.GetValue("plotForm.Height", 400)
      };

      showPlot = new UserOption("plotMenuItem", false, plotMenuItem, settings);
      plotLocation = new UserRadioGroup("plotLocation", 0,
        new[] { plotWindowMenuItem, plotBottomMenuItem, plotRightMenuItem },
        settings);

      showPlot.Changed += delegate(object sender, EventArgs e) {
        if (plotLocation.Value == 0) {
          if (showPlot.Value && this.Visible)
            plotForm.Show();
          else
            plotForm.Hide();
        } else {
          splitContainer.Panel2Collapsed = !showPlot.Value;
        }
        treeView.Invalidate();
      };
      plotLocation.Changed += delegate(object sender, EventArgs e) {
        switch (plotLocation.Value) {
          case 0:
            splitContainer.Panel2.Controls.Clear();
            splitContainer.Panel2Collapsed = true;
            plotForm.Controls.Add(plotPanel);
            if (showPlot.Value && this.Visible)
              plotForm.Show();
            break;
          case 1:
            plotForm.Controls.Clear();
            plotForm.Hide();
            splitContainer.Orientation = Orientation.Horizontal;
            splitContainer.Panel2.Controls.Add(plotPanel);
            splitContainer.Panel2Collapsed = !showPlot.Value;
            break;
          case 2:
            plotForm.Controls.Clear();
            plotForm.Hide();
            splitContainer.Orientation = Orientation.Vertical;
            splitContainer.Panel2.Controls.Add(plotPanel);
            splitContainer.Panel2Collapsed = !showPlot.Value;
            break;
        }
      };

      plotForm.FormClosing += delegate(object sender, FormClosingEventArgs e) {
        if (e.CloseReason == CloseReason.UserClosing) {
          // just switch off the plotting when the user closes the form
          if (plotLocation.Value == 0) {
            showPlot.Value = false;
          }
          e.Cancel = true;
        }
      };

      EventHandler moveOrResizePlotForm = delegate(object sender, EventArgs e) {
        if (plotForm.WindowState != FormWindowState.Minimized) {
          settings.SetValue("plotForm.Location.X", plotForm.Bounds.X);
          settings.SetValue("plotForm.Location.Y", plotForm.Bounds.Y);
          settings.SetValue("plotForm.Width", plotForm.Bounds.Width);
          settings.SetValue("plotForm.Height", plotForm.Bounds.Height);
        }
      };
      plotForm.Move += moveOrResizePlotForm;
      plotForm.Resize += moveOrResizePlotForm;

      plotForm.VisibleChanged += delegate(object sender, EventArgs e) {
        Rectangle bounds = new Rectangle(plotForm.Location, plotForm.Size);
        Screen screen = Screen.FromRectangle(bounds);
        Rectangle intersection =
          Rectangle.Intersect(screen.WorkingArea, bounds);
        if (intersection.Width < Math.Min(16, bounds.Width) ||
            intersection.Height < Math.Min(16, bounds.Height)) {
          plotForm.Location = new Point(
            screen.WorkingArea.Width / 2 - bounds.Width / 2,
            screen.WorkingArea.Height / 2 - bounds.Height / 2);
        }
      };

      this.VisibleChanged += delegate(object sender, EventArgs e) {
        if (this.Visible && showPlot.Value && plotLocation.Value == 0)
          plotForm.Show();
        else
          plotForm.Hide();
      };
    }

    private void InsertSorted(Collection<Node> nodes, HardwareNode node) {
      int i = 0;
      while (i < nodes.Count && nodes[i] is HardwareNode &&
        ((HardwareNode)nodes[i]).Hardware.HardwareType <= 
          node.Hardware.HardwareType)
        i++;
      nodes.Insert(i, node);
    }
    
    private void SubHardwareAdded(IHardware hardware, Node node) {
      HardwareNode hardwareNode = 
        new HardwareNode(hardware, settings, unitManager);
      hardwareNode.PlotSelectionChanged += PlotSelectionChanged;

      InsertSorted(node.Nodes, hardwareNode);

      foreach (IHardware subHardware in hardware.SubHardware)
        SubHardwareAdded(subHardware, hardwareNode);  
    }

    private void HardwareAdded(IHardware hardware) {
      // Raised on the sensor thread while it opens the hardware.
      if (UiThread.Redirect(() => HardwareAdded(hardware)))
        return;
      SubHardwareAdded(hardware, root);
      PlotSelectionChanged(this, null);
    }

    private void HardwareRemoved(IHardware hardware) {
      if (UiThread.Redirect(() => HardwareRemoved(hardware)))
        return;
      List<HardwareNode> nodesToRemove = new List<HardwareNode>();
      foreach (Node node in root.Nodes) {
        HardwareNode hardwareNode = node as HardwareNode;
        if (hardwareNode != null && hardwareNode.Hardware == hardware)
          nodesToRemove.Add(hardwareNode);
      }
      foreach (HardwareNode hardwareNode in nodesToRemove) {
        root.Nodes.Remove(hardwareNode);
        hardwareNode.PlotSelectionChanged -= PlotSelectionChanged;
      }
      PlotSelectionChanged(this, null);
    }

    private void nodeTextBoxText_DrawText(object sender, DrawEventArgs e) {       
      Node node = e.Node.Tag as Node;
      if (node != null) {
        Color color;
        if (node.IsVisible) {
          SensorNode sensorNode = node as SensorNode;
          if (plotMenuItem.Checked && sensorNode != null &&
            sensorPlotColors.TryGetValue(sensorNode.Sensor, out color))
            e.TextColor = color;
        } else {
          e.TextColor = uiTheme != null ? uiTheme.TextTertiary : Color.DarkGray;
        }
      }
    }

    private void PlotSelectionChanged(object sender, EventArgs e) {
      List<ISensor> selected = new List<ISensor>();
      IDictionary<ISensor, Color> colors = new Dictionary<ISensor, Color>();
      int colorIndex = 0;
      foreach (TreeNodeAdv node in treeView.AllNodes) {
        SensorNode sensorNode = node.Tag as SensorNode;
        if (sensorNode != null) {
          if (sensorNode.Plot) {
            if (!sensorNode.PenColor.HasValue) {
              colors.Add(sensorNode.Sensor,
                plotColorPalette[colorIndex % plotColorPalette.Length]);
            }
            selected.Add(sensorNode.Sensor);
          }
          colorIndex++;
        }
      }

      // if a sensor is assigned a color that's already being used by another 
      // sensor, try to assign it a new color. This is done only after the 
      // previous loop sets an unchanging default color for all sensors, so that 
      // colors jump around as little as possible as sensors get added/removed 
      // from the plot
      var usedColors = new List<Color>();
      foreach (var curSelectedSensor in selected) {
        if (!colors.ContainsKey(curSelectedSensor)) continue;
        var curColor = colors[curSelectedSensor];
        if (usedColors.Contains(curColor)) {
          foreach (var potentialNewColor in plotColorPalette) {
            if (!colors.Values.Contains(potentialNewColor)) {
              colors[curSelectedSensor] = potentialNewColor;
              usedColors.Add(potentialNewColor);
              break;
            }
          }
        } else {
          usedColors.Add(curColor);
        }
      }

      foreach (TreeNodeAdv node in treeView.AllNodes) {
        SensorNode sensorNode = node.Tag as SensorNode;
        if (sensorNode != null && sensorNode.Plot && sensorNode.PenColor.HasValue)
          colors.Add(sensorNode.Sensor, sensorNode.PenColor.Value);
      }

      sensorPlotColors = colors;
      plotPanel.SetSensors(selected, colors);
    }

    private void nodeTextBoxText_EditorShowing(object sender,
      CancelEventArgs e) 
    {
      e.Cancel = !(treeView.CurrentNode != null &&
        (treeView.CurrentNode.Tag is SensorNode || 
         treeView.CurrentNode.Tag is HardwareNode));
    }

    private void nodeCheckBox_IsVisibleValueNeeded(object sender, 
      NodeControlValueEventArgs e) {
      SensorNode node = e.Node.Tag as SensorNode;
      e.Value = (node != null) && plotMenuItem.Checked;
    }

    private void exitClick(object sender, EventArgs e) {
      Close();
    }

    private int delayCount = 0;
    private void timer_Tick(object sender, EventArgs e) {
      // Sensors are polled on a background thread; see StartSensorPolling.
      // This timer is no longer started.
    }

    /// <summary>
    /// Settings live under %LOCALAPPDATA%. They used to be written next to
    /// the executable, which inside Program Files only worked because the
    /// process ran elevated, and left the file owned by the elevated token.
    /// An existing file beside the executable is migrated once, so upgrading
    /// users keep their configuration.
    /// </summary>
    internal static string GetConfigurationPath() {
      string directory = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OpenHardwareMonitor");
      string path = Path.Combine(directory, "OpenHardwareMonitor.config");
      try {
        Directory.CreateDirectory(directory);
        if (!File.Exists(path)) {
          string legacy = Path.ChangeExtension(Application.ExecutablePath,
            ".config");
          if (File.Exists(legacy))
            File.Copy(legacy, path);
        }
      } catch (IOException) {
      } catch (UnauthorizedAccessException) {
      }
      return path;
    }

    private void SaveConfiguration() {
      if (settings == null)
        return;

      if (plotPanel != null) {
        plotPanel.SetCurrentSettings();
        foreach (TreeColumn column in treeView.Columns)
          settings.SetValue("treeView.Columns." + column.Header + ".Width",
            column.Width);
      }

      if (server != null) {
        this.settings.SetValue("listenerPort", server.ListenerPort);
      }

      string fileName = GetConfigurationPath();
      try {
        settings.Save(fileName);
      } catch (UnauthorizedAccessException) {
        MessageBox.Show("Access to the path '" + fileName + "' is denied. " +
          "The current settings could not be saved.",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      } catch (IOException) {
        MessageBox.Show("The path '" + fileName + "' is not writeable. " +
          "The current settings could not be saved.",
          "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void MainForm_Load(object sender, EventArgs e) {
      Rectangle newBounds = new Rectangle {
        X = settings.GetValue("mainForm.Location.X", Location.X),
        Y = settings.GetValue("mainForm.Location.Y", Location.Y),
        Width = settings.GetValue("mainForm.Width",
          DpiHelper.LogicalToDeviceUnits(1120)),
        Height = settings.GetValue("mainForm.Height",
          DpiHelper.LogicalToDeviceUnits(780))
      };

      Rectangle fullWorkingArea = new Rectangle(int.MaxValue, int.MaxValue,
        int.MinValue, int.MinValue);

      foreach (Screen screen in Screen.AllScreens)
        fullWorkingArea = Rectangle.Union(fullWorkingArea, screen.Bounds);

      Rectangle intersection = Rectangle.Intersect(fullWorkingArea, newBounds);
      if (intersection.Width < 20 || intersection.Height < 20 ||
        !settings.Contains("mainForm.Location.X")
      ) {
        newBounds.X = (Screen.PrimaryScreen.WorkingArea.Width / 2) -
                      (newBounds.Width/2);

        newBounds.Y = (Screen.PrimaryScreen.WorkingArea.Height / 2) -
                      (newBounds.Height / 2);
      }

      this.Bounds = newBounds;
    }
    
    private void MainForm_FormClosed(object sender, FormClosedEventArgs e) {
      Visible = false;      
      systemTray.IsMainIconEnabled = false;
      timer.Enabled = false;            
      StopSensorPolling();
      // Waits for the sensor thread if it is still opening the hardware.
      poller.RunLocked(() => {
        fanCurves.Release();
        computer.Close();
      });
      SaveConfiguration();
      if (runWebServer.Value)
          server.Quit();
      systemTray.Dispose();
    }

    private void aboutMenuItem_Click(object sender, EventArgs e) {
      new AboutBox().ShowDialog();
    }

    private void treeView_Click(object sender, EventArgs e) {

      MouseEventArgs m = e as MouseEventArgs;
      if (m == null || m.Button != MouseButtons.Right)
        return;

      NodeControlInfo info = treeView.GetNodeControlInfoAt(
        new Point(m.X, m.Y)
      );
      treeView.SelectedNode = info.Node;
      if (info.Node != null) {
        SensorNode node = info.Node.Tag as SensorNode;
        if (node != null && node.Sensor != null) {
          treeContextMenu.Items.Clear();
          if (node.Sensor.Parameters.Length > 0) {
            ToolStripMenuItem item = new ToolStripMenuItem("Parameters...");
            item.Click += delegate(object obj, EventArgs args) {
              ShowParameterForm(node.Sensor);
            };
            treeContextMenu.Items.Add(item);
          }
          if (nodeTextBoxText.EditEnabled) {
            ToolStripMenuItem item = new ToolStripMenuItem("Rename");
            item.Click += delegate(object obj, EventArgs args) {
              nodeTextBoxText.BeginEdit();
            };
            treeContextMenu.Items.Add(item);
          }
          if (node.IsVisible) {
            ToolStripMenuItem item = new ToolStripMenuItem("Hide");
            item.Click += delegate(object obj, EventArgs args) {
              node.IsVisible = false;
            };
            treeContextMenu.Items.Add(item);
          } else {
            ToolStripMenuItem item = new ToolStripMenuItem("Unhide");
            item.Click += delegate(object obj, EventArgs args) {
              node.IsVisible = true;
            };
            treeContextMenu.Items.Add(item);
          }
          treeContextMenu.Items.Add(new ToolStripSeparator());
          {
            ToolStripMenuItem item = new ToolStripMenuItem("Pen Color...");
            item.Click += delegate(object obj, EventArgs args) {
              ColorDialog dialog = new ColorDialog();
              dialog.Color = node.PenColor.GetValueOrDefault();
              if (dialog.ShowDialog() == DialogResult.OK)
                node.PenColor = dialog.Color;
            };
            treeContextMenu.Items.Add(item);
          }
          {
            ToolStripMenuItem item = new ToolStripMenuItem("Reset Pen Color");
            item.Click += delegate(object obj, EventArgs args) {
              node.PenColor = null;
            };
            treeContextMenu.Items.Add(item);
          }
          treeContextMenu.Items.Add(new ToolStripSeparator());
          {
            ToolStripMenuItem item = new ToolStripMenuItem("Show in Tray");
            item.Checked = systemTray.Contains(node.Sensor);
            item.Click += delegate(object obj, EventArgs args) {
              if (item.Checked)
                systemTray.Remove(node.Sensor);
              else
                systemTray.Add(node.Sensor, true);
            };
            treeContextMenu.Items.Add(item);
          }
          if (gadget != null) {
            ToolStripMenuItem item = new ToolStripMenuItem("Show in Gadget");
            item.Checked = gadget.Contains(node.Sensor);
            item.Click += delegate(object obj, EventArgs args) {
              if (item.Checked) {
                gadget.Remove(node.Sensor);
              } else {
                gadget.Add(node.Sensor);
              }
            };
            treeContextMenu.Items.Add(item);
          }
          if (node.Sensor.Control != null) {
            treeContextMenu.Items.Add(new ToolStripSeparator());
            IControl control = node.Sensor.Control;
            ToolStripMenuItem controlItem = new ToolStripMenuItem("Control");
            ToolStripMenuItem defaultItem = new ToolStripMenuItem("Default");
            defaultItem.Checked = control.ControlMode == ControlMode.Default;
            controlItem.DropDownItems.Add(defaultItem);
            defaultItem.Click += delegate(object obj, EventArgs args) {
              poller.RunLocked(() => {
                fanCurves.RemoveCurve(node.Sensor);
                control.SetDefault();
              });
            };
            ToolStripMenuItem manualItem = new ToolStripMenuItem("Manual");
            controlItem.DropDownItems.Add(manualItem);
            manualItem.Checked = control.ControlMode == ControlMode.Software;
            for (int i = 0; i <= 100; i += 5) {
              if (i <= control.MaxSoftwareValue &&
                  i >= control.MinSoftwareValue) {
                ToolStripMenuItem item = new ToolStripMenuItem(i + " %");
                manualItem.DropDownItems.Add(item);
                item.Checked = control.ControlMode == ControlMode.Software &&
                  Math.Round(control.SoftwareValue) == i;
                int softwareValue = i;
                item.Click += delegate(object obj, EventArgs args) {
                  poller.RunLocked(() => {
                    fanCurves.RemoveCurve(node.Sensor);
                    control.SetSoftware(fanCurves.LimitDuty(node.Sensor, softwareValue, false));
                  });
                };
              }
            }
            ToolStripMenuItem curveItem = new ToolStripMenuItem("Curve...");
            curveItem.Checked = fanCurves.HasCurve(node.Sensor);
            curveItem.Click += delegate(object obj, EventArgs args) {
              ShowFanCurveForm(node.Sensor);
            };
            controlItem.DropDownItems.Add(new ToolStripSeparator());
            controlItem.DropDownItems.Add(curveItem);
            treeContextMenu.Items.Add(controlItem);
          }

          treeContextMenu.Show(treeView, new Point(m.X, m.Y));
        }

        HardwareNode hardwareNode = info.Node.Tag as HardwareNode;
        if (hardwareNode != null && hardwareNode.Hardware != null) {
          treeContextMenu.Items.Clear();

          if (nodeTextBoxText.EditEnabled) {
            ToolStripMenuItem item = new ToolStripMenuItem("Rename");
            item.Click += delegate(object obj, EventArgs args) {
              nodeTextBoxText.BeginEdit();
            };
            treeContextMenu.Items.Add(item);
          }

          treeContextMenu.Show(treeView, new Point(m.X, m.Y));
        }
      }
    }

    private void saveReportMenuItem_Click(object sender, EventArgs e) {
      string report = poller.RunLocked(() => computer.GetReport());
      if (saveFileDialog.ShowDialog() == DialogResult.OK) {
        using (TextWriter w = new StreamWriter(saveFileDialog.FileName)) {
          w.Write(report);
        }
      }
    }

    private void SysTrayHideShow() {
      Visible = !Visible;
      if (Visible)
        Activate();    
    }

    protected override void WndProc(ref Message m) {
      // While the window is dragged or resized, hold animations so nothing
      // competes with the move; they land where they belong afterwards.
      const int WM_ENTERSIZEMOVE = 0x0231;
      const int WM_EXITSIZEMOVE = 0x0232;
      if (m.Msg == WM_ENTERSIZEMOVE)
        Modern.Animator.Paused = true;
      else if (m.Msg == WM_EXITSIZEMOVE)
        Modern.Animator.Paused = false;

      const int WM_SYSCOMMAND = 0x112;
      const int SC_MINIMIZE = 0xF020;
      const int SC_CLOSE = 0xF060;

      if (minimizeToTray.Value && 
        m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == SC_MINIMIZE) {
        SysTrayHideShow();
      } else if (minimizeOnClose.Value &&
        m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == SC_CLOSE) {
        /*
         * Apparently the user wants to minimize rather than close
         * Now we still need to check if we're going to the tray or not
         * 
         * Note: the correct way to do this would be to send out SC_MINIMIZE,
         * but since the code here is so simple,
         * that would just be a waste of time.
         */
        if (minimizeToTray.Value)
          SysTrayHideShow();
        else
          WindowState = FormWindowState.Minimized;
      } else {      
        base.WndProc(ref m);
      }
    }

    private void hideShowClick(object sender, EventArgs e) {
      SysTrayHideShow();
    }

    private void ShowFanCurveForm(ISensor controlSensor) {
      using (FanCurveForm form = new FanCurveForm(computer, controlSensor,
        fanCurves.GetCurve(controlSensor))) {
        if (form.ShowDialog(this) != DialogResult.OK)
          return;
        if (form.Curve != null) {
          fanCurves.SetCurve(controlSensor, form.Curve);
        } else if (fanCurves.HasCurve(controlSensor)) {
          fanCurves.RemoveCurve(controlSensor);
          poller.RunLocked(() => controlSensor.Control?.SetDefault());
        }
      }
    }

    private void ShowParameterForm(ISensor sensor) {
      ParameterForm form = new ParameterForm();
      form.Parameters = sensor.Parameters;
      form.captionLabel.Text = sensor.Name;
      form.ShowDialog();
    }

    private void treeView_NodeMouseDoubleClick(object sender, 
      TreeNodeAdvMouseEventArgs e) {
      SensorNode node = e.Node.Tag as SensorNode;
      if (node != null && node.Sensor != null && 
        node.Sensor.Parameters.Length > 0) {
        ShowParameterForm(node.Sensor);
      }
    }

    private void celsiusMenuItem_Click(object sender, EventArgs e) {
      celsiusMenuItem.Checked = true;
      fahrenheitMenuItem.Checked = false;
      unitManager.TemperatureUnit = TemperatureUnit.Celsius;
    }

    private void fahrenheitMenuItem_Click(object sender, EventArgs e) {
      celsiusMenuItem.Checked = false;
      fahrenheitMenuItem.Checked = true;
      unitManager.TemperatureUnit = TemperatureUnit.Fahrenheit;
    }

    private void sumbitReportMenuItem_Click(object sender, EventArgs e) 
    {
      ReportForm form = new ReportForm();
      form.Report = poller.RunLocked(() => computer.GetReport());
      form.ShowDialog();      
    }

    /// <summary>
    /// Menu bar "Export for AI", File > Export Diagnostics and the tray item.
    /// Runs on the UI thread, which owns the sensor tree.
    /// </summary>
    private void exportDiagnosticsMenuItem_Click(object sender, EventArgs e) {
      DiagnosticsExport.Run(Visible ? this : null, computer, poller.Sync,
        alerts.GetRecentAlerts());
    }

    private void resetMinMaxMenuItem_Click(object sender, EventArgs e) {
      poller.RunLocked(() => computer.Accept(new SensorVisitor(
        delegate(ISensor sensor) {
          sensor.ResetMin();
          sensor.ResetMax();
        })));
    }

    private void MainForm_MoveOrResize(object sender, EventArgs e) {
      if (WindowState != FormWindowState.Minimized) {
        settings.SetValue("mainForm.Location.X", Bounds.X);
        settings.SetValue("mainForm.Location.Y", Bounds.Y);
        settings.SetValue("mainForm.Width", Bounds.Width);
        settings.SetValue("mainForm.Height", Bounds.Height);
      }
    }

    private void resetClick(object sender, EventArgs e) {
      // disable the fallback MainIcon during reset, otherwise icon visibility
      // might be lost 
      systemTray.IsMainIconEnabled = false;
      poller.RunLocked(() => computer.Reset());
      // restore the MainIcon setting
      systemTray.IsMainIconEnabled = minimizeToTray.Value;
    }

    private void treeView_MouseMove(object sender, MouseEventArgs e) {
      selectionDragging = selectionDragging &
        (e.Button & (MouseButtons.Left | MouseButtons.Right)) > 0; 

      if (selectionDragging)
        treeView.SelectedNode = treeView.GetNodeAt(e.Location);     
    }

    private void treeView_MouseDown(object sender, MouseEventArgs e) {
      selectionDragging = true;
    }

    private void treeView_MouseUp(object sender, MouseEventArgs e) {
      selectionDragging = false;
    }

    private void serverPortMenuItem_Click(object sender, EventArgs e) {
      new PortForm(this).ShowDialog();
    }

    public HttpServer Server {
      get { return server; }
    }

  }
}
