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
using System.Globalization;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI {

  /// <summary>Edits the temperature curve that drives one fan control.</summary>
  internal sealed class FanCurveForm : Form {

    private sealed class SensorItem {
      public SensorItem(ISensor sensor) {
        Sensor = sensor;
      }

      public ISensor Sensor { get; }

      public override string ToString() {
        return Sensor.Hardware.Name + " / " + Sensor.Name;
      }
    }

    private readonly CheckBox enabled;
    private readonly ComboBox source;
    private readonly DataGridView grid;
    private readonly float minimum;
    private readonly float maximum;

    public FanCurve? Curve { get; private set; }

    public FanCurveForm(IComputer computer, ISensor controlSensor,
      FanCurve? existing) {

      IControl control = controlSensor.Control ??
        throw new ArgumentException("The sensor has no control.",
          nameof(controlSensor));
      minimum = control.MinSoftwareValue;
      maximum = control.MaxSoftwareValue;

      Text = "Fan Curve - " + controlSensor.Hardware.Name + " / " +
        controlSensor.Name;
      Font = SystemFonts.MessageBoxFont;
      AutoScaleMode = AutoScaleMode.Font;
      FormBorderStyle = FormBorderStyle.FixedDialog;
      MaximizeBox = false;
      MinimizeBox = false;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.CenterParent;
      ClientSize = new Size(460, 430);

      List<ISensor> temperatures = new List<ISensor>();
      computer.Accept(new SensorVisitor(sensor => {
        if (sensor.SensorType == SensorType.Temperature)
          temperatures.Add(sensor);
      }));

      TableLayoutPanel layout = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        Padding = new Padding(12),
        ColumnCount = 1
      };
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

      enabled = new CheckBox {
        Text = "Control this fan with a temperature curve",
        AutoSize = true,
        Checked = true
      };

      Label sourceLabel = new Label {
        Text = "Temperature source:",
        AutoSize = true,
        Margin = new Padding(0, 10, 0, 3)
      };

      source = new ComboBox {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill
      };
      int selected = -1;
      foreach (ISensor sensor in temperatures) {
        int index = source.Items.Add(new SensorItem(sensor));
        if (existing != null &&
          sensor.Identifier.ToString() == existing.SourceSensorIdentifier)
          selected = index;
        else if (selected < 0 && existing == null &&
          sensor.Hardware.HardwareType == controlSensor.Hardware.HardwareType)
          selected = index;
      }
      if (selected < 0 && source.Items.Count > 0)
        selected = 0;
      source.SelectedIndex = selected;

      grid = new DataGridView {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        Margin = new Padding(0, 10, 0, 0)
      };
      grid.Columns.Add("temperature", "Temperature (°C)");
      grid.Columns.Add("duty", "Fan (%)");

      IEnumerable<FanCurvePoint> points = existing != null
        ? (IEnumerable<FanCurvePoint>)existing.Points
        : new[] {
            new FanCurvePoint(30, minimum),
            new FanCurvePoint(50, Math.Max(minimum, 40)),
            new FanCurvePoint(70, Math.Max(minimum, 70)),
            new FanCurvePoint(85, Math.Min(maximum, 100))
          };
      foreach (FanCurvePoint point in points) {
        grid.Rows.Add(
          point.Temperature.ToString("0.#", CultureInfo.CurrentCulture),
          point.Duty.ToString("0.#", CultureInfo.CurrentCulture));
      }

      Label note = new Label {
        AutoSize = true,
        MaximumSize = new Size(ClientSize.Width - 24, 0),
        Margin = new Padding(0, 10, 0, 0),
        Text = string.Format(CultureInfo.CurrentCulture,
          "Speeds between points are interpolated. The fan is always kept " +
          "between {0:0}% and {1:0}%, as the hardware requires. If the " +
          "temperature source stops reporting, control returns to the hardware.",
          minimum, maximum)
      };

      Button ok = new Button { Text = "OK", AutoSize = true };
      Button cancel = new Button {
        Text = "Cancel",
        AutoSize = true,
        DialogResult = DialogResult.Cancel
      };
      FlowLayoutPanel buttons = new FlowLayoutPanel {
        FlowDirection = FlowDirection.RightToLeft,
        Dock = DockStyle.Fill,
        AutoSize = true,
        Margin = new Padding(0, 10, 0, 0)
      };
      buttons.Controls.Add(cancel);
      buttons.Controls.Add(ok);

      layout.Controls.Add(enabled, 0, 0);
      layout.Controls.Add(sourceLabel, 0, 1);
      layout.Controls.Add(source, 0, 2);
      layout.Controls.Add(grid, 0, 3);
      layout.Controls.Add(note, 0, 4);
      layout.Controls.Add(buttons, 0, 5);
      Controls.Add(layout);

      AcceptButton = ok;
      CancelButton = cancel;

      enabled.CheckedChanged += (sender, e) => {
        source.Enabled = enabled.Checked;
        grid.Enabled = enabled.Checked;
      };

      if (temperatures.Count == 0) {
        enabled.Checked = false;
        enabled.Enabled = false;
        note.Text = "No temperature sensors are available to drive a curve. " +
          "Processor temperatures need a low-level driver such as PawnIO.";
      }

      ok.Click += (sender, e) => Accept();
    }

    private void Accept() {
      if (!enabled.Checked) {
        Curve = null;
        DialogResult = DialogResult.OK;
        return;
      }

      if (!(source.SelectedItem is SensorItem item)) {
        Warn("Choose the temperature sensor that should drive this fan.");
        return;
      }

      grid.EndEdit();
      List<FanCurvePoint> points = new List<FanCurvePoint>();
      foreach (DataGridViewRow row in grid.Rows) {
        if (row.IsNewRow)
          continue;
        string temperatureText = Convert.ToString(row.Cells[0].Value,
          CultureInfo.CurrentCulture)?.Trim() ?? "";
        string dutyText = Convert.ToString(row.Cells[1].Value,
          CultureInfo.CurrentCulture)?.Trim() ?? "";
        if (temperatureText.Length == 0 && dutyText.Length == 0)
          continue;
        if (!TryParseNumber(temperatureText, out float temperature) ||
          !TryParseNumber(dutyText, out float duty)) {
          Warn("Every row needs a temperature and a fan speed.");
          return;
        }
        if (temperature < -20 || temperature > 150) {
          Warn("Temperatures must be between -20 and 150 °C.");
          return;
        }
        if (duty < 0 || duty > 100) {
          Warn("Fan speeds must be between 0 and 100%.");
          return;
        }
        points.Add(new FanCurvePoint(temperature, duty));
      }

      try {
        Curve = new FanCurve(item.Sensor.Identifier.ToString(), points);
      } catch (ArgumentException ex) {
        Warn(ex.Message.Split('(')[0].Trim());
        return;
      }
      DialogResult = DialogResult.OK;
    }

    private static bool TryParseNumber(string text, out float value) {
      return float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture,
          out value) ||
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
          out value);
    }

    private void Warn(string message) {
      MessageBox.Show(this, message, "Fan Curve", MessageBoxButtons.OK,
        MessageBoxIcon.Warning);
    }
  }
}
