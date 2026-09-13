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

namespace OpenHardwareMonitor.GUI.Modern {

  public sealed class CardMetric {

    public CardMetric(string label, string value,
      Severity severity = Severity.Normal, float? fraction = null) {
      Label = label;
      Value = value;
      Severity = severity;
      Fraction = fraction.HasValue
        ? Math.Max(0, Math.Min(1, fraction.Value)) : (float?)null;
    }

    public string Label { get; }
    public string Value { get; }
    public Severity Severity { get; }

    /// <summary>Optional 0..1 fill drawn as a slim bar.</summary>
    public float? Fraction { get; }
  }

  /// <summary>What one overview card shows. Built fresh on every update.</summary>
  public sealed class CardModel {
    public string Badge { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>
    /// Numeric hero reading; when set it glides between updates and
    /// <see cref="HeroValue"/> is ignored.
    /// </summary>
    public float? HeroNumber { get; set; }
    public int HeroDecimals { get; set; }

    /// <summary>Text hero reading. Empty, with no number, hides the hero row.</summary>
    public string HeroValue { get; set; } = "";
    public string HeroUnit { get; set; } = "";
    public string HeroLabel { get; set; } = "";
    public Severity HeroSeverity { get; set; }
    public List<CardMetric> Metrics { get; } = new List<CardMetric>();
    public List<CardMetric> Rows { get; } = new List<CardMetric>();

    /// <summary>Oldest to newest; null hides the trend.</summary>
    public float[]? Trend { get; set; }
    public string TrendLabel { get; set; } = "";
    public string? Note { get; set; }

    internal bool HasHero {
      get { return HeroNumber.HasValue || !string.IsNullOrEmpty(HeroValue); }
    }
  }

  /// <summary>
  /// A rounded, owner-drawn summary card. Structure comes from spacing, type
  /// weight and a soft shadow rather than lines. Motion is deliberately
  /// small: cards ease in, lift under the pointer, press when clicked, and
  /// readings glide to new values. All of it respects the Windows animation
  /// setting and only runs while something actually changes.
  /// </summary>
  public sealed class SensorCard : Control {

    private const string MoreInfoText = "More info";
    private const string MoreInfoArrow = "›";
    private const float EntranceSeconds = 0.65f;

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
      TextFormatFlags.PreserveGraphicsTranslateTransform |
      TextFormatFlags.PreserveGraphicsClipping;

    private const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
      TextFormatFlags.PreserveGraphicsTranslateTransform |
      TextFormatFlags.PreserveGraphicsClipping;

    private CardModel model = new CardModel();
    private bool hasModel;
    private Theme theme = Theme.Current;

    private readonly AnimatedValue hover;
    private readonly AnimatedValue press;
    private readonly AnimatedValue heroNumber;
    private readonly AnimatedValue severityMix;
    private readonly AnimatedValue trendReveal;
    private readonly AnimatedValue ping;
    private readonly List<AnimatedValue> metricBars = new List<AnimatedValue>();
    private readonly List<AnimatedValue> rowBars = new List<AnimatedValue>();
    private Severity previousSeverity;

    private double entranceStart = double.NaN;
    private float entranceDelay;
    private bool pointerDown;

    private int fontDpi;
    private Font? badgeFont, titleFont, subtitleFont, linkFont, heroFont,
      unitFont, labelFont, valueFont, rowFont, rowValueFont;

    public SensorCard() {
      SetStyle(ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
        ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
      TabStop = true;
      Cursor = Cursors.Hand;
      AccessibleRole = AccessibleRole.PushButton;

      hover = new AnimatedValue(this, 0.22f);
      hover.Set(0);
      press = new AnimatedValue(this, 0.12f);
      press.Set(0);
      heroNumber = new AnimatedValue(this, 0.7f);
      severityMix = new AnimatedValue(this, 0.5f);
      severityMix.Set(1);
      trendReveal = new AnimatedValue(this, 1.1f);
      trendReveal.Set(0);
      ping = new AnimatedValue(this, 1.0f);
      ping.Set(1);
    }

    public event EventHandler? MoreInfoClicked;

    public CardModel Model {
      get { return model; }
      set { ApplyModel(value ?? new CardModel()); }
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        Invalidate();
      }
    }

    /// <summary>
    /// Room around the visible card for its shadow. The layout places cards
    /// so that neighbouring shadow margins meet exactly and never overlap.
    /// </summary>
    public Padding ShadowPadding {
      get {
        int side = (int)Math.Round(S(8));
        return new Padding(side, (int)Math.Round(S(6)), side, (int)Math.Round(S(10)));
      }
    }

    /// <summary>Height of the visible card (without shadow) at a given width.</summary>
    public int GetPreferredHeight(int width) {
      return (int)Math.Ceiling(Render(null, width));
    }

    /// <summary>Fades and slides the card in after <paramref name="delaySeconds"/>.</summary>
    public void PlayEntrance(float delaySeconds) {
      if (!Animator.Enabled)
        return;
      entranceStart = Animator.Now;
      entranceDelay = delaySeconds;
      Animator.Run(() => {
        if (IsDisposed)
          return false;
        Invalidate();
        return EntranceProgress < 1;
      });
    }

    private float EntranceProgress {
      get {
        if (double.IsNaN(entranceStart))
          return 1;
        return Easing.OutQuint(
          (float)((Animator.Now - entranceStart - entranceDelay) / EntranceSeconds));
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    // ---- model changes ------------------------------------------------------

    private void ApplyModel(CardModel next) {
      if (next.HeroNumber.HasValue) {
        bool visibleChange = hasModel && model.HeroNumber.HasValue &&
          Math.Round(next.HeroNumber.Value, next.HeroDecimals) !=
          Math.Round(model.HeroNumber.Value, model.HeroDecimals);
        heroNumber.Set(next.HeroNumber.Value, hasModel && model.HeroNumber.HasValue);
        if (visibleChange) {
          ping.Set(0, false);
          ping.Set(1);
        }
      }

      if (!hasModel) {
        previousSeverity = next.HeroSeverity;
      } else if (next.HeroSeverity != model.HeroSeverity) {
        previousSeverity = model.HeroSeverity;
        severityMix.Set(0, false);
        severityMix.Set(1);
      }

      SyncBars(metricBars, next.Metrics);
      SyncBars(rowBars, next.Rows);

      if (next.Trend != null && next.Trend.Length > 1 && trendReveal.Target < 1)
        trendReveal.Set(1);

      model = next;
      hasModel = true;
      AccessibleName = next.Title;
      AccessibleDescription = (HeroText() + " " + next.HeroUnit + " " +
        next.HeroLabel).Trim();
      Invalidate();
    }

    private void SyncBars(List<AnimatedValue> bars, List<CardMetric> items) {
      while (bars.Count < items.Count) {
        AnimatedValue bar = new AnimatedValue(this, 0.8f);
        bar.Set(0);
        bars.Add(bar);
      }
      for (int i = 0; i < items.Count; i++)
        if (items[i].Fraction.HasValue)
          bars[i].Set(items[i].Fraction!.Value);
    }

    private string HeroText() {
      return model.HeroNumber.HasValue
        ? heroNumber.Value.ToString("F" + model.HeroDecimals, CultureInfo.CurrentCulture)
        : model.HeroValue;
    }

    private Color HeroColor(Severity severity) {
      return severity == Severity.Normal ? theme.Text : theme.ForSeverity(severity);
    }

    private Color TrendColor(Severity severity) {
      return theme.ForSeverity(severity);
    }

    // ---- fonts ----------------------------------------------------------------

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && badgeFont != null)
        return;
      DisposeFonts();
      int dpi = DeviceDpi;
      fontDpi = dpi;
      badgeFont = Theme.CreateFont(Theme.SemiboldFamily, 8.5f, FontStyle.Regular, dpi);
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 10.5f, FontStyle.Regular, dpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, dpi);
      linkFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, dpi);
      heroFont = Theme.CreateFont(Theme.DisplayFamily, 28f, FontStyle.Regular, dpi);
      unitFont = Theme.CreateFont(Theme.TextFamily, 13f, FontStyle.Regular, dpi);
      labelFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, dpi);
      valueFont = Theme.CreateFont(Theme.SemiboldFamily, 11f, FontStyle.Regular, dpi);
      rowFont = Theme.CreateFont(Theme.TextFamily, 9.5f, FontStyle.Regular, dpi);
      rowValueFont = Theme.CreateFont(Theme.SemiboldFamily, 9.5f, FontStyle.Regular, dpi);
    }

    private void DisposeFonts() {
      foreach (Font? font in new[] { badgeFont, titleFont, subtitleFont, linkFont,
        heroFont, unitFont, labelFont, valueFont, rowFont, rowValueFont })
        font?.Dispose();
      badgeFont = null;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      Invalidate();
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        DisposeFonts();
      base.Dispose(disposing);
    }

    // ---- painting -------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;

      Padding margin = ShadowPadding;
      float hoverAmount = hover.Value;
      float pressAmount = press.Value;
      float enter = EntranceProgress;

      float offsetY = (1 - enter) * S(18) - S(2) * hoverAmount + S(1) * pressAmount;
      RectangleF surface = new RectangleF(margin.Left + 0.5f, margin.Top + 0.5f,
        Width - margin.Horizontal - 1f, Height - margin.Vertical - 1f);
      if (pressAmount > 0)
        surface.Inflate(-S(2) * pressAmount, -S(2) * pressAmount);
      surface.Offset(0, offsetY);
      float radius = S(14);

      DrawShadow(g, surface, radius, hoverAmount * (1 - pressAmount * 0.6f), enter);

      using (GraphicsPath path = Theme.RoundedRect(surface, radius)) {
        Color fill = ColorMath.Lerp(theme.Surface, theme.SurfaceHover, hoverAmount);
        using (SolidBrush brush = new SolidBrush(fill))
          g.FillPath(brush, path);

        // A faint top highlight gives the surface a hint of depth.
        RectangleF sheen = new RectangleF(surface.X, surface.Y, surface.Width, S(28));
        using (LinearGradientBrush highlight = new LinearGradientBrush(
          RectangleF.Inflate(sheen, 0, 1),
          Color.FromArgb(theme.IsDark ? 14 : 0, Color.White),
          Color.FromArgb(0, Color.White), LinearGradientMode.Vertical)) {
          GraphicsState clip = g.Save();
          g.SetClip(path, CombineMode.Intersect);
          g.FillRectangle(highlight, sheen);
          g.Restore(clip);
        }

        Color borderHover = Theme.Blend(theme.Border, theme.Accent,
          theme.IsDark ? 0.45f : 0.30f);
        using (Pen border = new Pen(ColorMath.Lerp(theme.Border, borderHover, hoverAmount),
          Math.Max(1f, S(1))))
          g.DrawPath(border, path);
      }

      if (Focused && ShowFocusCues) {
        RectangleF ring = RectangleF.Inflate(surface, S(2.5f), S(2.5f));
        using (GraphicsPath path = Theme.RoundedRect(ring, radius + S(2.5f)))
        using (Pen pen = new Pen(theme.Accent, S(2)))
          g.DrawPath(pen, path);
      }

      GraphicsState state = g.Save();
      g.TranslateTransform((float)Math.Round(surface.X - 0.5f),
        (float)Math.Round(surface.Y - 0.5f));
      Render(g, (int)Math.Round(surface.Width + 1), hoverAmount);
      g.Restore(state);

      if (enter < 1) {
        using (SolidBrush veil = new SolidBrush(
          Color.FromArgb((int)Math.Round((1 - enter) * 255), theme.Background)))
          g.FillRectangle(veil, ClientRectangle);
      }
    }

    private void DrawShadow(Graphics g, RectangleF surface, float radius,
      float hoverAmount, float enter) {
      const int layers = 6;
      float strength = (theme.IsDark
        ? 0.55f + 0.25f * hoverAmount
        : 0.10f + 0.10f * hoverAmount) * enter;
      float spread = S(2.5f) + S(4) * hoverAmount;
      for (int i = layers; i >= 1; i--) {
        float f = (float)i / layers;
        float grow = spread * f;
        RectangleF layer = RectangleF.Inflate(surface, grow * 0.6f, grow * 0.6f);
        layer.Offset(0, S(1) + grow * 0.75f);
        int alpha = (int)Math.Round(255 * strength / layers * (1.25f - f));
        if (alpha <= 0)
          continue;
        using (GraphicsPath path = Theme.RoundedRect(layer, radius + grow * 0.6f))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(Math.Min(255, alpha), 0, 0, 0)))
          g.FillPath(brush, path);
      }
    }

    /// <summary>
    /// Lays out and, when a Graphics is supplied, draws the card contents at
    /// the origin. Measuring and drawing share one pass so the preferred
    /// height always matches what is drawn.
    /// </summary>
    private float Render(Graphics? g, int width, float hoverAmount = 0) {
      EnsureFonts();
      float pad = S(18);
      float x = pad;
      float y = pad;
      float inner = Math.Max(S(120), width - 2 * pad);

      // Header: badge, title and subtitle, "More info" link.
      float badgeSize = S(34);
      Size linkSize = TextRenderer.MeasureText(MoreInfoText, linkFont!, Size.Empty, Flags);
      Size arrowSize = TextRenderer.MeasureText(MoreInfoArrow, linkFont!, Size.Empty, Flags);
      float linkWidth = linkSize.Width + S(6) + arrowSize.Width + S(3);
      float titleX = x + badgeSize + S(12);
      float titleWidth = Math.Max(S(40), width - pad - titleX - linkWidth - S(12));
      if (g != null) {
        RectangleF badge = new RectangleF(x, y, badgeSize, badgeSize);
        Color badgeFill = Theme.Blend(theme.Surface, theme.Accent,
          (theme.IsDark ? 0.20f : 0.11f) + 0.06f * hoverAmount);
        using (GraphicsPath path = Theme.RoundedRect(badge, S(9)))
        using (SolidBrush fill = new SolidBrush(badgeFill))
          g.FillPath(fill, path);
        TextRenderer.DrawText(g, model.Badge, badgeFont!, Rectangle.Round(badge),
          theme.Accent, Flags | TextFormatFlags.HorizontalCenter |
          TextFormatFlags.VerticalCenter);

        float titleTop = y + (badgeSize - titleFont!.Height - subtitleFont!.Height) / 2;
        TextRenderer.DrawText(g, model.Title, titleFont,
          new Rectangle((int)titleX, (int)titleTop, (int)titleWidth, titleFont.Height),
          theme.Text, Flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, model.Subtitle, subtitleFont,
          new Rectangle((int)titleX, (int)titleTop + titleFont.Height,
            (int)titleWidth, subtitleFont.Height),
          theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);

        Color linkColor = ColorMath.Lerp(theme.Accent,
          Theme.Blend(theme.Accent, theme.Text, theme.IsDark ? 0.30f : 0.20f), hoverAmount);
        float linkX = width - pad - linkWidth;
        float linkY = y + (badgeSize - linkSize.Height) / 2;
        TextRenderer.DrawText(g, MoreInfoText, linkFont!,
          new Point((int)linkX, (int)linkY), linkColor, Flags);
        TextRenderer.DrawText(g, MoreInfoArrow, linkFont!,
          new Point((int)(linkX + linkSize.Width + S(6) + S(3) * hoverAmount), (int)linkY),
          linkColor, Flags);
      }
      y += badgeSize;
      bool anything = false;

      // Hero reading with its trend.
      if (model.HasHero) {
        y += S(18);
        anything = true;
        string heroText = HeroText();
        Size heroSize = TextRenderer.MeasureText(heroText, heroFont!, Size.Empty, Flags);
        float trendWidth = model.Trend != null && model.Trend.Length > 1
          ? Math.Min(S(180), inner * 0.46f) : 0;
        float heroHeight = heroSize.Height + S(2) + labelFont!.Height;
        if (g != null) {
          float mix = severityMix.Value;
          Color heroColor = ColorMath.Lerp(HeroColor(previousSeverity),
            HeroColor(model.HeroSeverity), mix);
          TextRenderer.DrawText(g, heroText, heroFont!,
            new Point((int)x, (int)y), heroColor, Flags);
          float baseline = y + Theme.Ascent(heroFont!);
          TextRenderer.DrawText(g, model.HeroUnit, unitFont!,
            new Point((int)(x + heroSize.Width + S(3)),
              (int)(baseline - Theme.Ascent(unitFont!))),
            theme.TextSecondary, Flags);

          // The label stops short of the trend's caption.
          float labelWidth = inner - (trendWidth > 0 ? trendWidth + S(16) : 0);
          TextRenderer.DrawText(g, model.HeroLabel, labelFont,
            new Rectangle((int)x, (int)(y + heroSize.Height + S(2)),
              (int)Math.Max(S(40), labelWidth), labelFont.Height),
            theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);

          if (trendWidth > 0) {
            RectangleF area = new RectangleF(width - pad - trendWidth, y + S(6),
              trendWidth, heroHeight - S(6) - labelFont.Height - S(4));
            Color trendColor = ColorMath.Lerp(TrendColor(previousSeverity),
              TrendColor(model.HeroSeverity), mix);
            DrawTrend(g, area, model.Trend!, trendColor, MinimumTrendRange(),
              trendReveal.Value);
            TextRenderer.DrawText(g, model.TrendLabel, labelFont,
              new Rectangle((int)area.Left, (int)(y + heroHeight - labelFont.Height),
                (int)area.Width, labelFont.Height),
              theme.TextTertiary, Flags | TextFormatFlags.Right);
          }
        }
        y += heroHeight;
      }

      // Secondary readings in a grid.
      if (model.Metrics.Count > 0) {
        y += S(18);
        anything = true;
        int count = model.Metrics.Count;
        int columns = count % 3 == 0 && inner >= S(330) ? 3 : Math.Min(2, count);
        float cellWidth = (inner - (columns - 1) * S(16)) / columns;
        bool bars = model.Metrics.Exists(m => m.Fraction.HasValue);
        float cellHeight = labelFont!.Height + S(3) + valueFont!.Height +
          (bars ? S(10) : 0);
        for (int i = 0; i < count; i++) {
          CardMetric metric = model.Metrics[i];
          float cx = x + (i % columns) * (cellWidth + S(16));
          float cy = y + (i / columns) * (cellHeight + S(14));
          if (g != null) {
            TextRenderer.DrawText(g, metric.Label, labelFont,
              new Rectangle((int)cx, (int)cy, (int)cellWidth, labelFont.Height),
              theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
            Color valueColor = metric.Severity == Severity.Normal
              ? theme.Text : theme.ForSeverity(metric.Severity);
            TextRenderer.DrawText(g, metric.Value, valueFont,
              new Rectangle((int)cx, (int)(cy + labelFont.Height + S(3)),
                (int)cellWidth, valueFont.Height),
              valueColor, Flags | TextFormatFlags.EndEllipsis);
            if (metric.Fraction.HasValue && i < metricBars.Count)
              DrawBar(g, new RectangleF(cx,
                cy + labelFont.Height + S(3) + valueFont.Height + S(6),
                cellWidth, S(4)), metricBars[i].Value, metric.Severity);
          }
        }
        int rows = (count + columns - 1) / columns;
        y += rows * cellHeight + (rows - 1) * S(14);
      }

      // List rows (fans, drives, rails).
      if (model.Rows.Count > 0) {
        y += S(18);
        anything = true;
        for (int i = 0; i < model.Rows.Count; i++) {
          CardMetric row = model.Rows[i];
          if (g != null) {
            Size valueSize = TextRenderer.MeasureText(row.Value, rowValueFont!,
              Size.Empty, Flags);
            TextRenderer.DrawText(g, row.Label, rowFont!,
              new Rectangle((int)x, (int)y,
                (int)(inner - valueSize.Width - S(12)), rowFont!.Height),
              theme.Text, Flags | TextFormatFlags.EndEllipsis);
            Color valueColor = row.Severity == Severity.Normal
              ? theme.TextSecondary : theme.ForSeverity(row.Severity);
            TextRenderer.DrawText(g, row.Value, rowValueFont!,
              new Point((int)(x + inner - valueSize.Width), (int)y),
              valueColor, Flags);
          }
          y += rowFont!.Height;
          if (row.Fraction.HasValue) {
            if (g != null && i < rowBars.Count)
              DrawBar(g, new RectangleF(x, y + S(5), inner, S(3)),
                rowBars[i].Value, row.Severity);
            y += S(8);
          }
          y += S(10);
        }
        y -= S(10);
      }

      // Footnote.
      if (!string.IsNullOrEmpty(model.Note)) {
        y += anything ? S(16) : S(14);
        Size noteSize = TextRenderer.MeasureText(model.Note, labelFont!,
          new Size((int)inner, int.MaxValue),
          TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        if (g != null)
          TextRenderer.DrawText(g, model.Note, labelFont!,
            new Rectangle((int)x, (int)y, (int)inner, noteSize.Height),
            theme.TextSecondary, WrapFlags);
        y += noteSize.Height;
      }

      return y + pad;
    }

    /// <summary>
    /// Smallest vertical span a trend covers, so a one-degree wobble does not
    /// fill the whole graph and look like a spike.
    /// </summary>
    private float MinimumTrendRange() {
      if (model.HeroUnit.IndexOf('°') >= 0)
        return 8f;
      if (model.HeroUnit == "%")
        return 20f;
      return 1f;
    }

    private void DrawBar(Graphics g, RectangleF track, float fraction,
      Severity severity) {
      float radius = track.Height / 2;
      using (GraphicsPath path = Theme.RoundedRect(track, radius))
      using (SolidBrush brush = new SolidBrush(theme.SurfaceSunken))
        g.FillPath(brush, path);
      if (fraction <= 0.001f)
        return;
      RectangleF fill = new RectangleF(track.X, track.Y,
        Math.Max(track.Height, track.Width * Math.Min(1, fraction)), track.Height);
      Color color = theme.ForSeverity(severity);
      using (GraphicsPath path = Theme.RoundedRect(fill, radius))
      using (LinearGradientBrush brush = new LinearGradientBrush(
        RectangleF.Inflate(fill, 1, 0),
        Theme.Blend(color, theme.Surface, 0.25f), color, LinearGradientMode.Horizontal))
        g.FillPath(brush, path);
    }

    private void DrawTrend(Graphics g, RectangleF area, float[] values,
      Color color, float minimumRange, float reveal) {
      float min = float.MaxValue, max = float.MinValue;
      int finite = 0;
      foreach (float value in values) {
        if (float.IsNaN(value) || float.IsInfinity(value))
          continue;
        min = Math.Min(min, value);
        max = Math.Max(max, value);
        finite++;
      }
      if (finite < 2 || area.Width < 4 || area.Height < 4 || reveal <= 0)
        return;
      if (max - min < minimumRange) {
        float middle = (max + min) / 2;
        min = middle - minimumRange / 2;
        max = middle + minimumRange / 2;
      } else {
        float margin = (max - min) * 0.12f;
        min -= margin;
        max += margin;
      }

      List<PointF> points = new List<PointF>(values.Length);
      float step = area.Width / (values.Length - 1);
      for (int i = 0; i < values.Length; i++) {
        float value = values[i];
        if (float.IsNaN(value) || float.IsInfinity(value))
          continue;
        points.Add(new PointF(area.Left + i * step,
          area.Bottom - (value - min) / (max - min) * area.Height));
      }
      if (points.Count < 2)
        return;

      GraphicsState state = g.Save();
      g.SetClip(new RectangleF(area.Left - S(8), area.Top - S(12),
        area.Width * reveal + S(16), area.Height + S(24)), CombineMode.Intersect);

      using (GraphicsPath fillPath = new GraphicsPath()) {
        fillPath.AddLines(points.ToArray());
        PointF last = points[points.Count - 1];
        fillPath.AddLine(last, new PointF(last.X, area.Bottom));
        fillPath.AddLine(new PointF(last.X, area.Bottom),
          new PointF(points[0].X, area.Bottom));
        fillPath.CloseFigure();
        using (LinearGradientBrush brush = new LinearGradientBrush(
          RectangleF.Inflate(area, 0, 1),
          Color.FromArgb(theme.IsDark ? 90 : 64, color),
          Color.FromArgb(0, color), LinearGradientMode.Vertical))
          g.FillPath(brush, fillPath);
      }

      using (Pen pen = new Pen(color, S(2))) {
        pen.LineJoin = LineJoin.Round;
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        g.DrawLines(pen, points.ToArray());
      }

      PointF end = points[points.Count - 1];
      float dot = S(3.5f);
      if (ping.IsAnimating) {
        float p = ping.Value;
        float ring = dot + S(9) * p;
        using (SolidBrush halo = new SolidBrush(
          Color.FromArgb((int)Math.Round(90 * (1 - p)), color)))
          g.FillEllipse(halo, end.X - ring, end.Y - ring, ring * 2, ring * 2);
      }
      using (SolidBrush brush = new SolidBrush(theme.Surface))
        g.FillEllipse(brush, end.X - dot - S(1.5f), end.Y - dot - S(1.5f),
          (dot + S(1.5f)) * 2, (dot + S(1.5f)) * 2);
      using (SolidBrush brush = new SolidBrush(color))
        g.FillEllipse(brush, end.X - dot, end.Y - dot, dot * 2, dot * 2);

      g.Restore(state);
    }

    // ---- interaction ------------------------------------------------------------

    protected override void OnMouseEnter(EventArgs e) {
      base.OnMouseEnter(e);
      hover.Set(1);
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      hover.Set(0);
      press.Set(0);
      pointerDown = false;
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button != MouseButtons.Left)
        return;
      pointerDown = true;
      press.Set(1);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left)
        return;
      press.Set(0);
      bool click = pointerDown && ClientRectangle.Contains(e.Location);
      pointerDown = false;
      if (click)
        MoreInfoClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) {
        e.Handled = true;
        MoreInfoClicked?.Invoke(this, EventArgs.Empty);
      }
    }

    protected override void OnGotFocus(EventArgs e) {
      base.OnGotFocus(e);
      Invalidate();
    }

    protected override void OnLostFocus(EventArgs e) {
      base.OnLostFocus(e);
      Invalidate();
    }
  }
}
