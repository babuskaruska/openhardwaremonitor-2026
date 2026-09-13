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
  /// weight and a soft shadow rather than lines.
  ///
  /// Motion is small and cheap. Cards ease in, lift under the pointer and
  /// press when clicked; readings glide to new values. Readings change every
  /// second, so the steady state is engineered to cost almost nothing: the
  /// shadow and surface are rendered once into a cached bitmap, a changing
  /// value repaints only its own region, and everything outside the region
  /// is skipped while drawing.
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

    // Regions in control coordinates, recorded by the last full render, so an
    // animated value can repaint just the pixels it affects.
    private RectangleF heroRegion, trendRegion;
    private readonly List<RectangleF> metricBarRegions = new List<RectangleF>();
    private readonly List<RectangleF> rowBarRegions = new List<RectangleF>();

    // Per paint: the dirty rectangle (empty means everything) and where the
    // content origin sits in control coordinates.
    private Rectangle paintClip;
    private PointF contentOrigin;

    private Bitmap? chrome;
    private double entranceStart = double.NaN;
    private float entranceDelay;
    private bool pointerDown;

    private int fontDpi;
    private Font? badgeFont, titleFont, subtitleFont, linkFont, heroFont,
      unitFont, labelFont, valueFont, rowFont, rowValueFont;
    private Size linkSize, arrowSize;

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
      heroNumber = new AnimatedValue(this, 0.35f) {
        InvalidateRegion = () => RegionFor(heroRegion)
      };
      severityMix = new AnimatedValue(this, 0.4f) {
        InvalidateRegion = () => RegionFor(RectangleF.Union(heroRegion, trendRegion))
      };
      severityMix.Set(1);
      trendReveal = new AnimatedValue(this, 0.9f) {
        InvalidateRegion = () => RegionFor(trendRegion)
      };
      trendReveal.Set(0);
      ping = new AnimatedValue(this, 0.9f) {
        InvalidateRegion = () => RegionFor(trendRegion)
      };
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
        DiscardChrome();
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
      if (!Animator.Enabled || !Animator.IsOnScreen(this))
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

    private Rectangle RegionFor(RectangleF region) {
      if (region.IsEmpty)
        return ClientRectangle;
      return Rectangle.Ceiling(RectangleF.Inflate(region, S(3), S(3)));
    }

    // ---- model changes ------------------------------------------------------

    private void ApplyModel(CardModel next) {
      if (next.HeroNumber.HasValue)
        heroNumber.Set(next.HeroNumber.Value, hasModel && model.HeroNumber.HasValue);

      if (!hasModel) {
        previousSeverity = next.HeroSeverity;
      } else if (next.HeroSeverity != model.HeroSeverity) {
        // Colour cross-fade and a single soft ping when a reading moves into
        // (or out of) a warm or hot range - not on every change, which with
        // readings updating each second would mean constant motion.
        previousSeverity = model.HeroSeverity;
        severityMix.Set(0, false);
        severityMix.Set(1);
        ping.Set(0, false);
        ping.Set(1);
      }

      SyncBars(metricBars, next.Metrics, metricBarRegions);
      SyncBars(rowBars, next.Rows, rowBarRegions);

      if (next.Trend != null && next.Trend.Length > 1 && trendReveal.Target < 1)
        trendReveal.Set(1);

      model = next;
      hasModel = true;
      AccessibleName = next.Title;
      AccessibleDescription = (HeroText() + " " + next.HeroUnit + " " +
        next.HeroLabel).Trim();
      Invalidate();
    }

    private void SyncBars(List<AnimatedValue> bars, List<CardMetric> items,
      List<RectangleF> regions) {
      while (bars.Count < items.Count) {
        int index = bars.Count;
        AnimatedValue bar = new AnimatedValue(this, 0.45f) {
          InvalidateRegion = () => RegionFor(index < regions.Count ? regions[index] : RectangleF.Empty)
        };
        bar.Set(0);
        bars.Add(bar);
      }
      for (int i = 0; i < items.Count; i++) {
        if (!items[i].Fraction.HasValue)
          continue;
        float target = items[i].Fraction!.Value;
        // Glide on a visible change; tiny second-to-second jitter just snaps.
        bars[i].Set(target, Math.Abs(bars[i].Target - target) >= 0.02f);
      }
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
      linkSize = TextRenderer.MeasureText(MoreInfoText, linkFont, Size.Empty, Flags);
      arrowSize = TextRenderer.MeasureText(MoreInfoArrow, linkFont, Size.Empty, Flags);
    }

    private void DisposeFonts() {
      foreach (Font? font in new[] { badgeFont, titleFont, subtitleFont, linkFont,
        heroFont, unitFont, labelFont, valueFont, rowFont, rowValueFont })
        font?.Dispose();
      badgeFont = null;
    }

    private void DiscardChrome() {
      chrome?.Dispose();
      chrome = null;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      DiscardChrome();
      Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e) {
      base.OnSizeChanged(e);
      DiscardChrome();
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        DisposeFonts();
        DiscardChrome();
      }
      base.Dispose(disposing);
    }

    // ---- painting -------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      Padding margin = ShadowPadding;
      float hoverAmount = hover.Value;
      float pressAmount = press.Value;
      float enter = EntranceProgress;
      float radius = S(14);

      bool steady = hoverAmount < 0.001f && pressAmount < 0.001f && enter > 0.999f;
      float offsetY = (1 - enter) * S(18) - S(2) * hoverAmount + S(1) * pressAmount;
      RectangleF surface = new RectangleF(margin.Left + 0.5f, margin.Top + 0.5f,
        Width - margin.Horizontal - 1f, Height - margin.Vertical - 1f);
      if (pressAmount > 0)
        surface.Inflate(-S(2) * pressAmount, -S(2) * pressAmount);
      surface.Offset(0, offsetY);

      if (steady) {
        // The expensive part - shadow layers, rounded paths, gradients - is
        // the same every frame; copy it from the cache.
        Bitmap background = GetChrome(surface, radius);
        g.DrawImage(background, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
      } else {
        DrawChrome(g, surface, radius, hoverAmount, pressAmount, enter);
      }

      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;

      if (Focused && ShowFocusCues) {
        RectangleF ring = RectangleF.Inflate(surface, S(2.5f), S(2.5f));
        using (GraphicsPath path = Theme.RoundedRect(ring, radius + S(2.5f)))
        using (Pen pen = new Pen(theme.Accent, S(2)))
          g.DrawPath(pen, path);
      }

      float originX = (float)Math.Round(surface.X - 0.5f);
      float originY = (float)Math.Round(surface.Y - 0.5f);
      contentOrigin = new PointF(originX, originY);
      paintClip = e.ClipRectangle == ClientRectangle ? Rectangle.Empty : e.ClipRectangle;

      GraphicsState state = g.Save();
      g.TranslateTransform(originX, originY);
      Render(g, (int)Math.Round(surface.Width + 1), hoverAmount);
      g.Restore(state);
      paintClip = Rectangle.Empty;

      if (enter < 1) {
        using (SolidBrush veil = new SolidBrush(
          Color.FromArgb((int)Math.Round((1 - enter) * 255), theme.Background)))
          g.FillRectangle(veil, ClientRectangle);
      }
    }

    private Bitmap GetChrome(RectangleF surface, float radius) {
      if (chrome != null && chrome.Width == Width && chrome.Height == Height)
        return chrome;
      DiscardChrome();
      Bitmap bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height),
        PixelFormat.Format32bppPArgb);
      using (Graphics g = Graphics.FromImage(bitmap))
        DrawChrome(g, surface, radius, 0, 0, 1);
      chrome = bitmap;
      return bitmap;
    }

    private void DrawChrome(Graphics g, RectangleF surface, float radius,
      float hoverAmount, float pressAmount, float enter) {
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;

      DrawShadow(g, surface, radius, hoverAmount * (1 - pressAmount * 0.6f), enter);

      using (GraphicsPath path = Theme.RoundedRect(surface, radius)) {
        Color fill = ColorMath.Lerp(theme.Surface, theme.SurfaceHover, hoverAmount);
        using (SolidBrush brush = new SolidBrush(fill))
          g.FillPath(brush, path);

        if (theme.IsDark) {
          // A faint top highlight gives the dark surface a hint of depth.
          RectangleF sheen = new RectangleF(surface.X, surface.Y, surface.Width, S(28));
          using (LinearGradientBrush highlight = new LinearGradientBrush(
            RectangleF.Inflate(sheen, 0, 1), Color.FromArgb(14, Color.White),
            Color.FromArgb(0, Color.White), LinearGradientMode.Vertical)) {
            GraphicsState clip = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            g.FillRectangle(highlight, sheen);
            g.Restore(clip);
          }
        }

        Color borderHover = Theme.Blend(theme.Border, theme.Accent,
          theme.IsDark ? 0.45f : 0.30f);
        using (Pen border = new Pen(ColorMath.Lerp(theme.Border, borderHover, hoverAmount),
          Math.Max(1f, S(1))))
          g.DrawPath(border, path);
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

    /// <summary>True when a content-space rectangle needs drawing this paint.</summary>
    private bool Dirty(RectangleF content) {
      if (paintClip.IsEmpty)
        return true;
      content.Offset(contentOrigin);
      content.Inflate(S(2), S(2));
      return content.IntersectsWith(paintClip);
    }

    private RectangleF ToControl(RectangleF content) {
      content.Offset(contentOrigin);
      return content;
    }

    /// <summary>
    /// Lays out and, when a Graphics is supplied, draws the card contents at
    /// the origin. Measuring and drawing share one pass so the preferred
    /// height always matches what is drawn. Sections outside the dirty
    /// rectangle are skipped.
    /// </summary>
    private float Render(Graphics? g, int width, float hoverAmount = 0) {
      EnsureFonts();
      bool drawing = g != null;
      float pad = S(18);
      float x = pad;
      float y = pad;
      float inner = Math.Max(S(120), width - 2 * pad);

      // Header: badge, title and subtitle, "More info" link.
      float badgeSize = S(34);
      float linkWidth = linkSize.Width + S(6) + arrowSize.Width + S(3);
      float titleX = x + badgeSize + S(12);
      float titleWidth = Math.Max(S(40), width - pad - titleX - linkWidth - S(12));
      if (drawing && Dirty(new RectangleF(0, 0, width, pad + badgeSize))) {
        RectangleF badge = new RectangleF(x, y, badgeSize, badgeSize);
        Color badgeFill = Theme.Blend(theme.Surface, theme.Accent,
          (theme.IsDark ? 0.20f : 0.11f) + 0.06f * hoverAmount);
        using (GraphicsPath path = Theme.RoundedRect(badge, S(9)))
        using (SolidBrush fill = new SolidBrush(badgeFill))
          g!.FillPath(fill, path);
        TextRenderer.DrawText(g!, model.Badge, badgeFont!, Rectangle.Round(badge),
          theme.Accent, Flags | TextFormatFlags.HorizontalCenter |
          TextFormatFlags.VerticalCenter);

        float titleTop = y + (badgeSize - titleFont!.Height - subtitleFont!.Height) / 2;
        TextRenderer.DrawText(g!, model.Title, titleFont,
          new Rectangle((int)titleX, (int)titleTop, (int)titleWidth, titleFont.Height),
          theme.Text, Flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g!, model.Subtitle, subtitleFont,
          new Rectangle((int)titleX, (int)titleTop + titleFont.Height,
            (int)titleWidth, subtitleFont.Height),
          theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);

        Color linkColor = ColorMath.Lerp(theme.Accent,
          Theme.Blend(theme.Accent, theme.Text, theme.IsDark ? 0.30f : 0.20f), hoverAmount);
        float linkX = width - pad - linkWidth;
        float linkY = y + (badgeSize - linkSize.Height) / 2;
        TextRenderer.DrawText(g!, MoreInfoText, linkFont!,
          new Point((int)linkX, (int)linkY), linkColor, Flags);
        TextRenderer.DrawText(g!, MoreInfoArrow, linkFont!,
          new Point((int)(linkX + linkSize.Width + S(6) + S(3) * hoverAmount), (int)linkY),
          linkColor, Flags);
      }
      y += badgeSize;
      bool anything = false;

      // Hero reading with its trend.
      if (model.HasHero) {
        y += S(18);
        anything = true;
        float trendWidth = model.Trend != null && model.Trend.Length > 1
          ? Math.Min(S(180), inner * 0.46f) : 0;
        float heroHeight = heroFont!.Height + S(2) + labelFont!.Height;
        RectangleF heroBlock = new RectangleF(x, y,
          inner - (trendWidth > 0 ? trendWidth + S(8) : 0), heroHeight);
        RectangleF area = trendWidth > 0
          ? new RectangleF(width - pad - trendWidth, y + S(6), trendWidth,
            heroHeight - S(6) - labelFont.Height - S(4))
          : RectangleF.Empty;

        if (drawing) {
          heroRegion = ToControl(new RectangleF(x, y, heroBlock.Width, heroFont.Height));
          trendRegion = trendWidth > 0
            ? ToControl(RectangleF.Inflate(area, S(14), S(14)))
            : RectangleF.Empty;
        }

        float mix = severityMix.Value;
        if (drawing && Dirty(heroBlock)) {
          string heroText = HeroText();
          Size heroSize = TextRenderer.MeasureText(heroText, heroFont, Size.Empty, Flags);
          Color heroColor = ColorMath.Lerp(HeroColor(previousSeverity),
            HeroColor(model.HeroSeverity), mix);
          TextRenderer.DrawText(g!, heroText, heroFont, new Point((int)x, (int)y), heroColor, Flags);
          float baseline = y + Theme.Ascent(heroFont);
          TextRenderer.DrawText(g!, model.HeroUnit, unitFont!,
            new Point((int)(x + heroSize.Width + S(3)),
              (int)(baseline - Theme.Ascent(unitFont!))),
            theme.TextSecondary, Flags);
          TextRenderer.DrawText(g!, model.HeroLabel, labelFont,
            new Rectangle((int)x, (int)(y + heroFont.Height + S(2)),
              (int)Math.Max(S(40), heroBlock.Width), labelFont.Height),
            theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
        }

        if (drawing && trendWidth > 0 && Dirty(RectangleF.Inflate(area, S(14), S(14)))) {
          Color trendColor = ColorMath.Lerp(TrendColor(previousSeverity),
            TrendColor(model.HeroSeverity), mix);
          DrawTrend(g!, area, model.Trend!, trendColor, MinimumTrendRange(),
            trendReveal.Value);
          TextRenderer.DrawText(g!, model.TrendLabel, labelFont,
            new Rectangle((int)area.Left, (int)(y + heroHeight - labelFont.Height),
              (int)area.Width, labelFont.Height),
            theme.TextTertiary, Flags | TextFormatFlags.Right);
        }
        y += heroHeight;
      } else if (drawing) {
        heroRegion = RectangleF.Empty;
        trendRegion = RectangleF.Empty;
      }

      // Secondary readings in a grid.
      if (drawing)
        metricBarRegions.Clear();
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
          RectangleF barRect = new RectangleF(cx,
            cy + labelFont.Height + S(3) + valueFont.Height + S(6), cellWidth, S(4));
          if (!drawing)
            continue;
          metricBarRegions.Add(metric.Fraction.HasValue ? ToControl(barRect) : RectangleF.Empty);
          if (!Dirty(new RectangleF(cx, cy, cellWidth, cellHeight)))
            continue;
          TextRenderer.DrawText(g!, metric.Label, labelFont,
            new Rectangle((int)cx, (int)cy, (int)cellWidth, labelFont.Height),
            theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
          Color valueColor = metric.Severity == Severity.Normal
            ? theme.Text : theme.ForSeverity(metric.Severity);
          TextRenderer.DrawText(g!, metric.Value, valueFont,
            new Rectangle((int)cx, (int)(cy + labelFont.Height + S(3)),
              (int)cellWidth, valueFont.Height),
            valueColor, Flags | TextFormatFlags.EndEllipsis);
          if (metric.Fraction.HasValue && i < metricBars.Count)
            DrawBar(g!, barRect, metricBars[i].Value, metric.Severity);
        }
        int rows = (count + columns - 1) / columns;
        y += rows * cellHeight + (rows - 1) * S(14);
      }

      // List rows (fans, drives, rails).
      if (drawing)
        rowBarRegions.Clear();
      if (model.Rows.Count > 0) {
        y += S(18);
        anything = true;
        for (int i = 0; i < model.Rows.Count; i++) {
          CardMetric row = model.Rows[i];
          float rowHeight = rowFont!.Height + (row.Fraction.HasValue ? S(8) : 0);
          RectangleF barRect = new RectangleF(x, y + rowFont.Height + S(5), inner, S(3));
          if (drawing) {
            rowBarRegions.Add(row.Fraction.HasValue ? ToControl(barRect) : RectangleF.Empty);
            if (Dirty(new RectangleF(x, y, inner, rowHeight))) {
              Size valueSize = TextRenderer.MeasureText(row.Value, rowValueFont!,
                Size.Empty, Flags);
              TextRenderer.DrawText(g!, row.Label, rowFont,
                new Rectangle((int)x, (int)y,
                  (int)(inner - valueSize.Width - S(12)), rowFont.Height),
                theme.Text, Flags | TextFormatFlags.EndEllipsis);
              Color valueColor = row.Severity == Severity.Normal
                ? theme.TextSecondary : theme.ForSeverity(row.Severity);
              TextRenderer.DrawText(g!, row.Value, rowValueFont!,
                new Point((int)(x + inner - valueSize.Width), (int)y),
                valueColor, Flags);
              if (row.Fraction.HasValue && i < rowBars.Count)
                DrawBar(g!, barRect, rowBars[i].Value, row.Severity);
            }
          }
          y += rowHeight + S(10);
        }
        y -= S(10);
      }

      // Footnote.
      if (!string.IsNullOrEmpty(model.Note)) {
        y += anything ? S(16) : S(14);
        Size noteSize = TextRenderer.MeasureText(model.Note, labelFont!,
          new Size((int)inner, int.MaxValue),
          TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        if (drawing && Dirty(new RectangleF(x, y, inner, noteSize.Height)))
          TextRenderer.DrawText(g!, model.Note, labelFont!,
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

      PointF[] line = points.ToArray();
      using (GraphicsPath fillPath = new GraphicsPath()) {
        fillPath.AddLines(line);
        PointF last = line[line.Length - 1];
        fillPath.AddLine(last, new PointF(last.X, area.Bottom));
        fillPath.AddLine(new PointF(last.X, area.Bottom), new PointF(line[0].X, area.Bottom));
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
        g.DrawLines(pen, line);
      }

      PointF end = line[line.Length - 1];
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
