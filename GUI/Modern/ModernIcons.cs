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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// Small vector icons for the sensor list, drawn at the display's DPI in
  /// the current theme: a softly tinted rounded badge with a simple line
  /// glyph. Sensor kinds get their own hue so a long list scans at a glance.
  /// </summary>
  public static class ModernIcons {

    private delegate void Glyph(Graphics g, float s, Pen pen, Brush brush);

    private static readonly Dictionary<string, Image> cache =
      new Dictionary<string, Image>();

    public static Image Computer() {
      return Get("computer", Theme.Current.Accent, DrawComputer);
    }

    public static Image ForHardware(HardwareType type) {
      Color accent = Theme.Current.Accent;
      switch (type) {
        case HardwareType.CPU: return Get("cpu", accent, DrawChip);
        case HardwareType.GpuNvidia:
        case HardwareType.GpuAti: return Get("gpu", accent, DrawGraphicsCard);
        case HardwareType.RAM: return Get("ram", accent, DrawMemory);
        case HardwareType.Mainboard: return Get("board", accent, DrawBoard);
        case HardwareType.SuperIO: return Get("superio", accent, DrawSmallChip);
        case HardwareType.HDD: return Get("drive", accent, DrawDrive);
        default: return Get("device", accent, DrawDevice);
      }
    }

    public static Image ForSensorType(SensorType type) {
      Theme theme = Theme.Current;
      bool dark = theme.IsDark;
      Color violet = dark ? Color.FromArgb(0xA7, 0x8B, 0xFA) : Color.FromArgb(0x7C, 0x3A, 0xED);
      Color indigo = dark ? Color.FromArgb(0x81, 0x8C, 0xF8) : Color.FromArgb(0x4F, 0x46, 0xE5);
      Color yellow = dark ? Color.FromArgb(0xFA, 0xCC, 0x15) : Color.FromArgb(0xCA, 0x8A, 0x04);
      Color slate = dark ? Color.FromArgb(0x94, 0xA3, 0xB8) : Color.FromArgb(0x47, 0x55, 0x69);
      Color cyan = dark ? Color.FromArgb(0x22, 0xD3, 0xEE) : Color.FromArgb(0x08, 0x91, 0xB2);
      switch (type) {
        case SensorType.Voltage: return Get("voltage", yellow, DrawBolt);
        case SensorType.Clock: return Get("clock", indigo, DrawClock);
        case SensorType.Temperature: return Get("temperature", theme.Warm, DrawThermometer);
        case SensorType.Load: return Get("load", theme.Accent, DrawBars);
        case SensorType.Fan: return Get("fan", theme.Good, DrawFan);
        case SensorType.Flow: return Get("flow", cyan, DrawDrop);
        case SensorType.Control: return Get("control", theme.Accent, DrawSliders);
        case SensorType.Level: return Get("level", theme.Accent, DrawGauge);
        case SensorType.Factor: return Get("factor", slate, DrawHash);
        case SensorType.Power: return Get("power", violet, DrawPlug);
        case SensorType.Data:
        case SensorType.SmallData: return Get("data", slate, DrawCylinder);
        case SensorType.Throughput: return Get("throughput", theme.Good, DrawArrows);
        default: return Get("generic", slate, DrawDevice);
      }
    }

    private static int SystemDpi {
      get {
        try {
          using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            return (int)Math.Round(g.DpiX);
        } catch (Exception) {
          return 96;
        }
      }
    }

    private static Image Get(string name, Color color, Glyph glyph) {
      Theme theme = Theme.Current;
      int dpi = SystemDpi;
      string key = name + (theme.IsDark ? "-dark-" : "-light-") + dpi;
      lock (cache) {
        if (cache.TryGetValue(key, out Image? cached))
          return cached;

        int size = Math.Max(16, (int)Math.Round(16 * dpi / 96f));
        float s = size / 16f;
        Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap)) {
          g.SmoothingMode = SmoothingMode.AntiAlias;
          g.PixelOffsetMode = PixelOffsetMode.HighQuality;
          using (GraphicsPath badge = Theme.RoundedRect(
            new RectangleF(0.5f * s, 0.5f * s, 15 * s, 15 * s), 4 * s))
          using (SolidBrush tint = new SolidBrush(
            Color.FromArgb(theme.IsDark ? 56 : 36, color)))
            g.FillPath(tint, badge);
          using (Pen pen = new Pen(color, 1.35f * s))
          using (SolidBrush brush = new SolidBrush(color)) {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            glyph(g, s, pen, brush);
          }
        }
        cache[key] = bitmap;
        return bitmap;
      }
    }

    private static RectangleF R(float x, float y, float w, float h, float s) {
      return new RectangleF(x * s, y * s, w * s, h * s);
    }

    private static PointF P(float x, float y, float s) {
      return new PointF(x * s, y * s);
    }

    private static void Rounded(Graphics g, Pen pen, float x, float y, float w,
      float h, float r, float s) {
      using (GraphicsPath path = Theme.RoundedRect(R(x, y, w, h, s), r * s))
        g.DrawPath(pen, path);
    }

    // ---- hardware -------------------------------------------------------------

    private static void DrawComputer(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 3.5f, 4, 9, 6, 1.2f, s);
      g.DrawLine(pen, P(8, 10, s), P(8, 12, s));
      g.DrawLine(pen, P(6, 12.2f, s), P(10, 12.2f, s));
    }

    private static void DrawChip(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 5, 5, 6, 6, 1.2f, s);
      g.FillRectangle(brush, R(7, 7, 2, 2, s));
      foreach (float t in new[] { 6.7f, 9.3f }) {
        g.DrawLine(pen, P(3.2f, t, s), P(4.4f, t, s));
        g.DrawLine(pen, P(11.6f, t, s), P(12.8f, t, s));
        g.DrawLine(pen, P(t, 3.2f, s), P(t, 4.4f, s));
        g.DrawLine(pen, P(t, 11.6f, s), P(t, 12.8f, s));
      }
    }

    private static void DrawSmallChip(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 5, 5.5f, 6, 5, 1, s);
      foreach (float x in new[] { 6.5f, 9.5f }) {
        g.DrawLine(pen, P(x, 3.6f, s), P(x, 5, s));
        g.DrawLine(pen, P(x, 11, s), P(x, 12.4f, s));
      }
    }

    private static void DrawGraphicsCard(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 3, 5, 9.5f, 6, 1.2f, s);
      g.DrawEllipse(pen, R(5.6f, 6.6f, 2.8f, 2.8f, s));
      g.DrawLine(pen, P(10, 7, s), P(10, 9, s));
      g.DrawLine(pen, P(13.2f, 4.5f, s), P(13.2f, 11.5f, s));
    }

    private static void DrawMemory(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 3, 5.5f, 10, 4.5f, 1, s);
      foreach (float x in new[] { 5.5f, 8, 10.5f })
        g.DrawLine(pen, P(x, 10, s), P(x, 11.8f, s));
    }

    private static void DrawBoard(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 3.5f, 3.5f, 9, 9, 1.4f, s);
      g.FillRectangle(brush, R(5.4f, 5.4f, 2.8f, 2.8f, s));
      g.DrawLine(pen, P(10, 5.6f, s), P(10.8f, 5.6f, s));
      g.DrawLine(pen, P(10, 8, s), P(10.8f, 8, s));
      g.DrawLine(pen, P(5.4f, 10.6f, s), P(10.8f, 10.6f, s));
    }

    private static void DrawDrive(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 3, 4.5f, 10, 7, 1.5f, s);
      g.DrawLine(pen, P(5.2f, 9.2f, s), P(8, 9.2f, s));
      g.FillEllipse(brush, R(10, 8.4f, 1.6f, 1.6f, s));
    }

    private static void DrawDevice(Graphics g, float s, Pen pen, Brush brush) {
      Rounded(g, pen, 4, 4, 8, 8, 2, s);
      g.FillEllipse(brush, R(7, 7, 2, 2, s));
    }

    // ---- sensor kinds -----------------------------------------------------------

    private static void DrawBolt(Graphics g, float s, Pen pen, Brush brush) {
      g.FillPolygon(brush, new[] {
        P(9.2f, 2.8f, s), P(4.8f, 9, s), P(7.6f, 9, s),
        P(6.8f, 13.2f, s), P(11.2f, 7, s), P(8.4f, 7, s)
      });
    }

    private static void DrawClock(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawEllipse(pen, R(3.5f, 3.5f, 9, 9, s));
      g.DrawLine(pen, P(8, 8, s), P(8, 5.4f, s));
      g.DrawLine(pen, P(8, 8, s), P(10, 9.2f, s));
    }

    private static void DrawThermometer(Graphics g, float s, Pen pen, Brush brush) {
      using (Pen stem = (Pen)pen.Clone()) {
        stem.Width = 2.2f * s;
        g.DrawLine(stem, P(8, 3.8f, s), P(8, 9.2f, s));
      }
      g.FillEllipse(brush, R(5.8f, 8.8f, 4.4f, 4.4f, s));
    }

    private static void DrawBars(Graphics g, float s, Pen pen, Brush brush) {
      float[] heights = { 3.5f, 6, 8.5f };
      for (int i = 0; i < heights.Length; i++) {
        using (GraphicsPath bar = Theme.RoundedRect(
          R(3.8f + i * 3.1f, 12.2f - heights[i], 2.2f, heights[i], s), 0.8f * s))
          g.FillPath(brush, bar);
      }
    }

    private static void DrawFan(Graphics g, float s, Pen pen, Brush brush) {
      GraphicsState state = g.Save();
      g.TranslateTransform(8 * s, 8 * s);
      for (int i = 0; i < 3; i++) {
        g.RotateTransform(120);
        g.FillEllipse(brush, -1.4f * s, -5.2f * s, 2.8f * s, 4.2f * s);
      }
      g.Restore(state);
      using (SolidBrush hub = new SolidBrush(Color.FromArgb(255, ((SolidBrush)brush).Color)))
        g.FillEllipse(hub, R(6.9f, 6.9f, 2.2f, 2.2f, s));
    }

    private static void DrawDrop(Graphics g, float s, Pen pen, Brush brush) {
      using (GraphicsPath drop = new GraphicsPath()) {
        drop.AddBezier(P(8, 3, s), P(8, 3, s), P(4.2f, 7.8f, s), P(4.2f, 9.6f, s));
        drop.AddBezier(P(4.2f, 9.6f, s), P(4.2f, 14, s), P(11.8f, 14, s), P(11.8f, 9.6f, s));
        drop.AddBezier(P(11.8f, 9.6f, s), P(11.8f, 7.8f, s), P(8, 3, s), P(8, 3, s));
        g.FillPath(brush, drop);
      }
    }

    private static void DrawSliders(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawLine(pen, P(3.8f, 6, s), P(12.2f, 6, s));
      g.DrawLine(pen, P(3.8f, 10.5f, s), P(12.2f, 10.5f, s));
      g.FillEllipse(brush, R(4.6f, 4.4f, 3.2f, 3.2f, s));
      g.FillEllipse(brush, R(8.4f, 8.9f, 3.2f, 3.2f, s));
    }

    private static void DrawGauge(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawArc(pen, R(3.5f, 5, 9, 9, s), 180, 180);
      g.DrawLine(pen, P(8, 9.5f, s), P(10.4f, 6.8f, s));
      g.FillEllipse(brush, R(7, 8.5f, 2, 2, s));
    }

    private static void DrawHash(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawLine(pen, P(6.4f, 3.8f, s), P(5.6f, 12.2f, s));
      g.DrawLine(pen, P(10.4f, 3.8f, s), P(9.6f, 12.2f, s));
      g.DrawLine(pen, P(3.8f, 6.6f, s), P(12.4f, 6.6f, s));
      g.DrawLine(pen, P(3.6f, 9.6f, s), P(12.2f, 9.6f, s));
    }

    private static void DrawPlug(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawLine(pen, P(6.6f, 3.4f, s), P(6.6f, 5.6f, s));
      g.DrawLine(pen, P(9.4f, 3.4f, s), P(9.4f, 5.6f, s));
      Rounded(g, pen, 4.8f, 5.6f, 6.4f, 4, 1.4f, s);
      g.DrawLine(pen, P(8, 9.6f, s), P(8, 12.6f, s));
    }

    private static void DrawCylinder(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawEllipse(pen, R(4, 3.6f, 8, 3, s));
      g.DrawLine(pen, P(4, 5.1f, s), P(4, 10.9f, s));
      g.DrawLine(pen, P(12, 5.1f, s), P(12, 10.9f, s));
      g.DrawArc(pen, R(4, 9.4f, 8, 3, s), 0, 180);
    }

    private static void DrawArrows(Graphics g, float s, Pen pen, Brush brush) {
      g.DrawLine(pen, P(5.8f, 12, s), P(5.8f, 4.2f, s));
      g.DrawLines(pen, new[] { P(3.8f, 6.2f, s), P(5.8f, 4.2f, s), P(7.8f, 6.2f, s) });
      g.DrawLine(pen, P(10.2f, 4, s), P(10.2f, 11.8f, s));
      g.DrawLines(pen, new[] { P(8.2f, 9.8f, s), P(10.2f, 11.8f, s), P(12.2f, 9.8f, s) });
    }
  }
}
