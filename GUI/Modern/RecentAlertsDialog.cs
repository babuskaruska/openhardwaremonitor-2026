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
using System.Globalization;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware.Alerts;
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>The recent-alert log, newest first.</summary>
  internal sealed class RecentAlertsDialog : ModernDialog {

    public RecentAlertsDialog(Theme theme, IReadOnlyList<AlertRecord> alerts)
      : base(theme, "Recent alerts",
        "The last " + AlertEngine.MaxRecentAlerts.ToString(CultureInfo.CurrentCulture) +
        " alerts since Open Hardware Monitor started, newest first.") {
      if (alerts.Count == 0)
        Header.EmptyMessage = "Nothing yet. Alerts appear here when a reading needs attention.";
      else
        SetContent(new AlertListBox(theme, alerts));
      AddButton("Close", ButtonKind.Primary, Close);
      ClientSize = new Size(Px(640), Px(620));
      MinimumSize = new Size(Px(420), Px(360));
    }
  }

  /// <summary>One card per alert: severity dot, title, time and message.</summary>
  internal sealed class AlertListBox : ListBox {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private readonly Theme theme;
    private int fontDpi;
    private Font? titleFont, bodyFont;
    private int measuredWidth;

    public AlertListBox(Theme theme, IReadOnlyList<AlertRecord> alerts) {
      this.theme = theme;
      DrawMode = DrawMode.OwnerDrawVariable;
      BorderStyle = BorderStyle.None;
      IntegralHeight = false;
      SelectionMode = SelectionMode.None;
      BackColor = theme.Background;
      ForeColor = theme.Text;
      foreach (AlertRecord alert in alerts)
        Items.Add(alert);
    }

    private int Px(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      titleFont?.Dispose();
      bodyFont?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 10f, FontStyle.Regular, DeviceDpi);
      bodyFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
    }

    // Measured as if a scroll bar were showing, so a scroll bar appearing
    // never leaves a message taller than its row.
    private int TextWidth {
      get {
        return Math.Max(Px(120), ClientSize.Width - SystemInformation.VerticalScrollBarWidth -
          2 * Px(24) - 2 * Px(14) - Px(18));
      }
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e) {
      base.OnMeasureItem(e);
      if (e.Index < 0 || e.Index >= Items.Count || !(Items[e.Index] is AlertRecord alert))
        return;
      EnsureFonts();
      int message = TextRenderer.MeasureText(alert.Message, bodyFont!,
        new Size(TextWidth, int.MaxValue), Flags | TextFormatFlags.WordBreak).Height;
      // Variable list box rows cannot be taller than 255 pixels.
      e.ItemHeight = Math.Min(255, Px(35) + titleFont!.Height + message);
    }

    protected override void OnResize(EventArgs e) {
      base.OnResize(e);
      if (!IsHandleCreated || Items.Count == 0 ||
        Math.Abs(ClientSize.Width - measuredWidth) <= SystemInformation.VerticalScrollBarWidth)
        return;
      measuredWidth = ClientSize.Width;
      // Variable row heights are measured when items are added, so re-add them.
      object[] items = new object[Items.Count];
      Items.CopyTo(items, 0);
      BeginUpdate();
      Items.Clear();
      Items.AddRange(items);
      EndUpdate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e) {
      if (e.Index < 0 || e.Index >= Items.Count || !(Items[e.Index] is AlertRecord alert))
        return;
      EnsureFonts();
      Graphics g = e.Graphics;
      using (SolidBrush ground = new SolidBrush(theme.Background))
        g.FillRectangle(ground, e.Bounds);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      int margin = Px(24), pad = Px(14), dot = Px(8);
      Rectangle card = new Rectangle(e.Bounds.X + margin, e.Bounds.Y + Px(4),
        e.Bounds.Width - 2 * margin, e.Bounds.Height - Px(8));
      using (GraphicsPath path = Theme.RoundedRect(card, Px(10)))
      using (SolidBrush surface = new SolidBrush(theme.Surface))
      using (Pen border = new Pen(theme.Border, Math.Max(1f, DeviceDpi / 96f))) {
        g.FillPath(surface, path);
        g.DrawPath(border, path);
      }

      Color tint = alert.Severity == DiagnosticSeverity.Critical ? theme.Hot
        : alert.Severity == DiagnosticSeverity.Warning ? theme.Warm : theme.Good;
      int y = card.Y + Px(12);
      int textX = card.X + pad + Px(18);
      int right = card.Right - pad;
      using (SolidBrush brush = new SolidBrush(tint))
        g.FillEllipse(brush, card.X + pad, y + (titleFont!.Height - dot) / 2, dot, dot);

      string time = AlertValueFormat.Time(alert.Time);
      Size timeSize = TextRenderer.MeasureText(time, bodyFont!, Size.Empty,
        Flags | TextFormatFlags.SingleLine);
      TextRenderer.DrawText(g, time, bodyFont!,
        new Point(right - timeSize.Width, y + (titleFont.Height - timeSize.Height) / 2),
        theme.TextTertiary, Flags | TextFormatFlags.SingleLine);
      TextRenderer.DrawText(g, alert.Title, titleFont,
        new Rectangle(textX, y, Math.Max(0, right - timeSize.Width - Px(12) - textX), titleFont.Height),
        theme.Text, Flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
      y += titleFont.Height + Px(3);
      TextRenderer.DrawText(g, alert.Message, bodyFont!,
        new Rectangle(textX, y, TextWidth, Math.Max(0, card.Bottom - y)),
        theme.TextSecondary, Flags | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        titleFont?.Dispose();
        bodyFont?.Dispose();
      }
      base.Dispose(disposing);
    }
  }
}
