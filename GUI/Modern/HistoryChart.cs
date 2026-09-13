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
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>How the readings of one sensor type are shown in the history view.</summary>
  public sealed class HistoryUnit {

    private static readonly Dictionary<int, HistoryUnit> cache = new Dictionary<int, HistoryUnit>();

    private readonly float scale;
    private readonly float offset;

    private HistoryUnit(string suffix, int decimals, float minimumSpan,
      bool percent = false, float scale = 1, float offset = 0) {
      Suffix = suffix;
      Decimals = decimals;
      MinimumSpan = minimumSpan;
      IsPercent = percent;
      this.scale = scale;
      this.offset = offset;
    }

    public string Suffix { get; }
    public int Decimals { get; }

    /// <summary>
    /// Smallest span of the value axis, so a wobble of a degree does not fill
    /// the chart and look like a spike.
    /// </summary>
    public float MinimumSpan { get; }

    public bool IsPercent { get; }

    /// <summary>One instance per type and temperature unit, so units compare by reference.</summary>
    public static HistoryUnit For(SensorType sensorType, bool fahrenheit) {
      int key = (int)sensorType * 2 + (fahrenheit ? 1 : 0);
      if (!cache.TryGetValue(key, out HistoryUnit? unit)) {
        unit = Create(sensorType, fahrenheit);
        cache[key] = unit;
      }
      return unit;
    }

    private static HistoryUnit Create(SensorType sensorType, bool fahrenheit) {
      switch (sensorType) {
        case SensorType.Temperature:
          return fahrenheit
            ? new HistoryUnit("°F", 1, 9, scale: 1.8f, offset: 32)
            : new HistoryUnit("°C", 1, 5);
        case SensorType.Voltage: return new HistoryUnit("V", 3, 0.05f);
        case SensorType.Clock: return new HistoryUnit("MHz", 0, 100);
        case SensorType.Load: return new HistoryUnit("%", 1, 10, percent: true);
        case SensorType.Control: return new HistoryUnit("%", 1, 10, percent: true);
        case SensorType.Level: return new HistoryUnit("%", 1, 10, percent: true);
        case SensorType.Fan: return new HistoryUnit("RPM", 0, 100);
        case SensorType.Flow: return new HistoryUnit("L/h", 0, 10);
        case SensorType.Factor: return new HistoryUnit("", 3, 0.1f);
        case SensorType.Power: return new HistoryUnit("W", 1, 5);
        case SensorType.Data: return new HistoryUnit("GB", 1, 1);
        case SensorType.SmallData: return new HistoryUnit("MB", 0, 100);
        case SensorType.Throughput: return new HistoryUnit("MB/s", 2, 1);
        default: return new HistoryUnit("", 2, 1);
      }
    }

    public float ToDisplay(float value) {
      return value * scale + offset;
    }

    /// <summary>Formats a reading already converted with <see cref="ToDisplay"/>.</summary>
    public string Format(float display) {
      if (float.IsNaN(display))
        return "–";
      string text = display.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture),
        CultureInfo.CurrentCulture);
      return Suffix.Length > 0 ? text + " " + Suffix : text;
    }
  }

  /// <summary>
  /// A line chart of up to two sensor histories over a chosen time range: a
  /// band from the lowest to the highest reading of each pixel column, the
  /// average as a line on top, gaps where nothing was recorded, statistics for
  /// the visible range, and a crosshair that reads out time and values.
  ///
  /// Sensor values change on the sensor thread, so the chart copies what it
  /// needs in <see cref="CopySamples"/> (called where values cannot change) and
  /// paints only from its own buffers. Shapes are rendered into cached bitmaps
  /// and text laid out once, rebuilt only when the samples, size, unit or
  /// theme change, or when the time window moves on by a pixel column; hovering
  /// copies the bitmaps, draws the text in the invalid area and the crosshair.
  /// </summary>
  public sealed class HistoryChart : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static readonly string[] ColumnNames = { "Lowest", "Average", "Highest", "Now" };

    private static readonly int[] TimeSteps = {
      60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200
    };

    private sealed class Series {
      public bool Active;
      public string Name = "";
      public SensorValue[] Samples = Array.Empty<SensorValue>();
      public int Count;
      public float Current = float.NaN;
      public HistoryBucket[] Buckets = Array.Empty<HistoryBucket>();
      public HistoryStatistics Statistics;

      // What the last copy looked like, to skip a rebuild when nothing changed.
      public DateTime FirstTime, LastTime;
      public float LastValue;
    }

    private readonly Series[] series = { new Series(), new Series() };
    private readonly List<PointF> upper = new List<PointF>();
    private readonly List<PointF> lower = new List<PointF>();
    private readonly List<PointF> middle = new List<PointF>();

    private Theme theme = Theme.Current;
    private HistoryUnit unit = HistoryUnit.For(SensorType.Temperature, false);
    private TimeSpan range = TimeSpan.FromHours(1);
    private DateTime dataTime = DateTime.UtcNow;

    // Geometry of the last rebuild.
    private bool cacheValid;
    private Bitmap? background;
    private Bitmap? plotLayer;
    private Rectangle plot;
    private int plotMargin;
    private float labelWidth;
    private long bucketTicks;
    private int bucketCount;
    private DateTime windowStart;
    private bool hasData;
    private float axisMinimum, axisMaximum;
    private double axisStep = 1;
    private int axisDecimals;

    // Crosshair read-out, prepared when the pointer moves to another column.
    private int hoverColumn = -1;
    private int readoutColumn = -1;
    private RectangleF readoutBubble;
    private string readoutTime = "";
    private readonly string[] readoutText = { "", "" };
    private readonly float[] readoutY = { float.NaN, float.NaN };
    private Rectangle overlayBounds;

    private int fontDpi;
    private Font? labelFont, valueFont, nameFont, axisFont, tipFont, tipBoldFont;

    public HistoryChart() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      AccessibleRole = AccessibleRole.Chart;
      AccessibleName = "History chart";
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        InvalidateCache();
      }
    }

    /// <summary>Readings are converted and labelled with this unit.</summary>
    public HistoryUnit Unit {
      get { return unit; }
      set {
        if (value == null || value == unit)
          return;
        unit = value;
        InvalidateCache();
      }
    }

    /// <summary>
    /// The visible duration, ending now. Copy the samples again after changing
    /// it: a longer range needs older samples than were copied.
    /// </summary>
    public TimeSpan Range {
      get { return range; }
      set {
        if (value <= TimeSpan.Zero || value == range)
          return;
        range = value;
        InvalidateCache();
      }
    }

    /// <summary>The accent for the first series, violet for the second.</summary>
    public Color SeriesColor(int index) {
      if (index == 0)
        return theme.Accent;
      return theme.IsDark ? Color.FromArgb(0xC0, 0x8C, 0xFF) : Color.FromArgb(0x7C, 0x3A, 0xED);
    }

    /// <summary>Statistics of a series over the visible range, as of the last paint.</summary>
    public HistoryStatistics GetStatistics(int index) {
      return series[index].Statistics;
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    // ---- data ---------------------------------------------------------------------

    /// <summary>
    /// Copies the samples the visible range needs from <paramref name="sensor"/>
    /// into series <paramref name="index"/> (0 or 1), or clears the series when
    /// the sensor is null. Call only where sensor values cannot change: in the
    /// refresh after a sensor update, or under the hardware lock.
    /// </summary>
    public void CopySamples(int index, ISensor? sensor, string name) {
      Series target = series[index];
      if (sensor == null) {
        if (target.Active) {
          target.Active = false;
          target.Count = 0;
          InvalidateCache();
        }
        return;
      }

      DateTime now = DateTime.UtcNow;
      // A little before the range too, for the reading that runs into its left edge.
      DateTime from = now - range - TimeSpan.FromTicks(range.Ticks / 50);
      int count = 0;
      if (sensor.Values is IReadOnlyList<SensorValue> list) {
        int first = Math.Max(0, SensorHistory.FindFirst(list, from) - 1);
        count = list.Count - first;
        EnsureCapacity(ref target.Samples, count);
        for (int i = 0; i < count; i++)
          target.Samples[i] = list[first + i];
      } else {
        bool hasPrevious = false;
        SensorValue previous = default;
        foreach (SensorValue value in sensor.Values) {
          if (value.Time < from) {
            previous = value;
            hasPrevious = true;
            continue;
          }
          EnsureCapacity(ref target.Samples, count + 2);
          if (count == 0 && hasPrevious)
            target.Samples[count++] = previous;
          target.Samples[count++] = value;
        }
      }

      float current = sensor.Value ?? float.NaN;
      DateTime firstTime = count > 0 ? target.Samples[0].Time : default;
      DateTime lastTime = count > 0 ? target.Samples[count - 1].Time : default;
      float lastValue = count > 0 ? target.Samples[count - 1].Value : float.NaN;
      bool changed = !target.Active || target.Name != name || target.Count != count ||
        target.FirstTime != firstTime || target.LastTime != lastTime ||
        !Same(target.LastValue, lastValue) || !Same(target.Current, current);

      target.Active = true;
      target.Name = name;
      target.Count = count;
      target.Current = current;
      target.FirstTime = firstTime;
      target.LastTime = lastTime;
      target.LastValue = lastValue;
      dataTime = now;

      // Without new samples the window still moves on once "now" reaches the
      // next column.
      if (changed || !cacheValid ||
        AlignedEnd(now) != windowStart.Ticks + bucketTicks * bucketCount)
        InvalidateCache();
    }

    private static bool Same(float a, float b) {
      return a == b || float.IsNaN(a) && float.IsNaN(b);
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int count) {
      if (buffer.Length < count)
        Array.Resize(ref buffer, Math.Max(count, buffer.Length * 2));
    }

    private long AlignedEnd(DateTime now) {
      if (bucketTicks <= 0)
        return 0;
      return (now.Ticks / bucketTicks + 1) * bucketTicks;
    }

    private void InvalidateCache() {
      cacheValid = false;
      Invalidate();
    }

    // ---- fonts and resources ------------------------------------------------------------

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && labelFont != null)
        return;
      DisposeFonts();
      fontDpi = DeviceDpi;
      labelFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      valueFont = Theme.CreateFont(Theme.SemiboldFamily, 11f, FontStyle.Regular, DeviceDpi);
      nameFont = Theme.CreateFont(Theme.SemiboldFamily, 9.5f, FontStyle.Regular, DeviceDpi);
      axisFont = Theme.CreateFont(Theme.TextFamily, 8f, FontStyle.Regular, DeviceDpi);
      tipFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      tipBoldFont = Theme.CreateFont(Theme.SemiboldFamily, 9f, FontStyle.Regular, DeviceDpi);
      cacheValid = false;
    }

    private void DisposeFonts() {
      foreach (Font? font in new[] { labelFont, valueFont, nameFont, axisFont, tipFont, tipBoldFont })
        font?.Dispose();
      labelFont = null;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      InvalidateCache();
    }

    protected override void OnSizeChanged(EventArgs e) {
      base.OnSizeChanged(e);
      cacheValid = false;
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        DisposeFonts();
        background?.Dispose();
        background = null;
        plotLayer?.Dispose();
        plotLayer = null;
      }
      base.Dispose(disposing);
    }

    private static Bitmap Reuse(Bitmap? bitmap, int width, int height, PixelFormat format) {
      width = Math.Max(1, width);
      height = Math.Max(1, height);
      if (bitmap != null && bitmap.Width == width && bitmap.Height == height && bitmap.PixelFormat == format)
        return bitmap;
      bitmap?.Dispose();
      return new Bitmap(width, height, format);
    }

    private static Size Measure(string text, Font font) {
      return TextRenderer.MeasureText(text, font, Size.Empty, Flags);
    }

    // ---- layout -------------------------------------------------------------------------

    private float Pad {
      get { return S(20); }
    }

    private int ActiveCount {
      get { return (series[0].Active ? 1 : 0) + (series[1].Active ? 1 : 0); }
    }

    private float StatisticsHeight {
      get {
        return labelFont!.Height + S(6) + Math.Max(1, ActiveCount) * (valueFont!.Height + S(8));
      }
    }

    private int PlotLeft(float labels) {
      return (int)Math.Round(Pad + labels + S(10));
    }

    private int PlotRight {
      get { return (int)Math.Round(Width - Pad); }
    }

    private void Rebuild() {
      cacheValid = true;
      int top = (int)Math.Round(Pad + StatisticsHeight + S(18));
      int bottom = (int)Math.Round(Height - Pad - axisFont!.Height - S(8));

      // The width of the value labels is only known once the data is; start
      // from the previous width, which rarely changes, and redo the buckets
      // when it did.
      Downsample(Math.Max(1, PlotRight - PlotLeft(labelWidth)));
      ComputeAxis(bottom - top);
      float measured = MeasureAxisLabels();
      if (PlotLeft(measured) != PlotLeft(labelWidth)) {
        labelWidth = measured;
        Downsample(Math.Max(1, PlotRight - PlotLeft(labelWidth)));
      }
      int left = PlotLeft(labelWidth);
      plot = new Rectangle(left, top, Math.Max(1, PlotRight - left), Math.Max(1, bottom - top));

      RenderBackground();
      RenderPlot();
      PrepareReadout();
      UpdateAccessibleDescription();
    }

    /// <summary>
    /// One bucket per pixel column, over a window aligned to whole columns, so
    /// the picture only changes when a column fills or the window moves on.
    /// </summary>
    private void Downsample(int count) {
      bucketCount = count;
      bucketTicks = Math.Max(1, range.Ticks / count);
      long end = AlignedEnd(dataTime);
      windowStart = new DateTime(end - bucketTicks * count, DateTimeKind.Utc);
      DateTime windowEnd = new DateTime(end, DateTimeKind.Utc);
      hasData = false;
      foreach (Series item in series) {
        if (!item.Active) {
          item.Statistics = default;
          continue;
        }
        EnsureCapacity(ref item.Buckets, count);
        item.Statistics = SensorHistory.Downsample(
          new ArraySegment<SensorValue>(item.Samples, 0, item.Count),
          windowStart, windowEnd, item.Buckets, count);
        hasData |= item.Statistics.HasData;
      }
    }

    private void ComputeAxis(int plotHeight) {
      if (!hasData) {
        axisMinimum = 0;
        axisMaximum = 1;
        axisStep = 1;
        axisDecimals = 0;
        return;
      }
      float low = float.MaxValue, high = float.MinValue;
      foreach (Series item in series) {
        if (!item.Active || !item.Statistics.HasData)
          continue;
        low = Math.Min(low, unit.ToDisplay(item.Statistics.Minimum));
        high = Math.Max(high, unit.ToDisplay(item.Statistics.Maximum));
      }

      double from = low, to = high;
      if (to - from < unit.MinimumSpan) {
        double center = (from + to) / 2;
        from = center - unit.MinimumSpan / 2.0;
        to = center + unit.MinimumSpan / 2.0;
      }
      double margin = (to - from) * 0.08;
      from -= margin;
      to += margin;
      if (low >= 0 && from < 0)
        from = 0;
      if (unit.IsPercent && high <= 100 && to > 100)
        to = 100;

      int guides = Math.Max(2, Math.Min(6, (int)(plotHeight / S(52))));
      double raw = (to - from) / guides;
      double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
      double normalized = raw / magnitude;
      axisStep = (normalized < 1.5 ? 1 : normalized < 3 ? 2 : normalized < 7 ? 5 : 10) * magnitude;
      axisMinimum = (float)(Math.Floor(from / axisStep + 1e-9) * axisStep);
      axisMaximum = (float)(Math.Ceiling(to / axisStep - 1e-9) * axisStep);
      if (axisMaximum <= axisMinimum)
        axisMaximum = axisMinimum + (float)axisStep;
      axisDecimals = Math.Max(0, Math.Min(4, (int)-Math.Floor(Math.Log10(axisStep) + 1e-9)));
    }

    private int GuideCount {
      get { return (int)Math.Round((axisMaximum - axisMinimum) / axisStep); }
    }

    /// <summary>The value of guide <paramref name="k"/>; the top one carries the unit.</summary>
    private string AxisLabel(int k) {
      double value = axisMinimum + k * axisStep;
      if (Math.Abs(value) < axisStep * 1e-6)
        value = 0;
      string text = value.ToString("F" + axisDecimals.ToString(CultureInfo.InvariantCulture),
        CultureInfo.CurrentCulture);
      return k == GuideCount && unit.Suffix.Length > 0 ? text + " " + unit.Suffix : text;
    }

    private float MeasureAxisLabels() {
      if (!hasData)
        return 0;
      float width = 0;
      for (int k = 0; k <= GuideCount; k++)
        width = Math.Max(width, Measure(AxisLabel(k), axisFont!).Width);
      return width;
    }

    private float Y(float display) {
      return plot.Bottom - (display - axisMinimum) / (axisMaximum - axisMinimum) * plot.Height;
    }

    // ---- painting -------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      if (Width < 16 || Height < 16) {
        g.Clear(theme.Background);
        return;
      }
      if (!cacheValid || background == null || background.Width != Width ||
        background.Height != Height)
        Rebuild();

      g.DrawImage(background!, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
      foreach (TextItem text in texts)
        if (text.Bounds.IntersectsWith(e.ClipRectangle))
          TextRenderer.DrawText(g, text.Text, text.Font, text.Bounds, text.Color, text.Flags);
      if (plotLayer != null) {
        Rectangle layer = new Rectangle(plot.X - plotMargin, plot.Y - plotMargin,
          plotLayer.Width, plotLayer.Height);
        Rectangle clip = Rectangle.Intersect(layer, e.ClipRectangle);
        if (!clip.IsEmpty)
          g.DrawImage(plotLayer, clip,
            new Rectangle(clip.X - layer.X, clip.Y - layer.Y, clip.Width, clip.Height),
            GraphicsUnit.Pixel);
      }
      DrawCrosshair(g);
    }

    private void RenderBackground() {
      texts.Clear();
      background = Reuse(background, Width, Height, PixelFormat.Format32bppRgb);
      using (Graphics g = Graphics.FromImage(background)) {
        g.Clear(theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (GraphicsPath path = Theme.RoundedRect(card, S(14)))
        using (SolidBrush surface = new SolidBrush(theme.Surface))
        using (Pen border = new Pen(theme.Border, Math.Max(1f, S(1)))) {
          g.FillPath(surface, path);
          g.DrawPath(border, path);
        }

        DrawStatistics(g);
        LayOutTimeAxis();

        if (!hasData) {
          string title = ActiveCount == 0 ? "Choose a sensor" : "No readings in this time range";
          const string detail = "Readings are kept for 24 hours while Open Hardware Monitor is running.";
          int middleY = plot.Top + plot.Height / 2;
          AddText(title, nameFont!,
            new Rectangle(plot.Left, middleY - nameFont!.Height, plot.Width, nameFont.Height),
            theme.TextSecondary, Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
          AddText(detail, labelFont!,
            new Rectangle(plot.Left, middleY + (int)S(4), plot.Width, labelFont!.Height),
            theme.TextTertiary, Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
          return;
        }

        // Faint horizontal guides with their values; no vertical grid.
        g.SmoothingMode = SmoothingMode.None;
        Color guide = Theme.Blend(theme.Surface, theme.Border, theme.IsDark ? 0.9f : 0.8f);
        using (Pen pen = new Pen(guide, Math.Max(1f, (float)Math.Floor(S(1))))) {
          for (int k = 0; k <= GuideCount; k++) {
            int y = (int)Math.Round(Y((float)(axisMinimum + k * axisStep)));
            g.DrawLine(pen, plot.Left, y, plot.Right, y);
            string label = AxisLabel(k);
            Size size = Measure(label, axisFont!);
            AddText(label, axisFont!,
              new Point(plot.Left - (int)S(10) - size.Width, y - size.Height / 2),
              theme.TextTertiary, Flags);
          }
        }
      }
    }

    /// <summary>
    /// Text is drawn at paint time over the cached bitmap rather than into it:
    /// GDI text drawn into a bitmap loses ClearType and looks heavy.
    /// </summary>
    private readonly struct TextItem {
      public TextItem(string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags) {
        Text = text;
        Font = font;
        Bounds = bounds;
        Color = color;
        Flags = flags;
      }

      public string Text { get; }
      public Font Font { get; }
      public Rectangle Bounds { get; }
      public Color Color { get; }
      public TextFormatFlags Flags { get; }
    }

    private readonly List<TextItem> texts = new List<TextItem>();

    private void AddText(string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags) {
      texts.Add(new TextItem(text, font, bounds, color, flags));
    }

    private void AddText(string text, Font font, Point location, Color color, TextFormatFlags flags) {
      Size size = Measure(text, font);
      texts.Add(new TextItem(text, font,
        new Rectangle(location, new Size(size.Width + 2, size.Height)), color, flags));
    }

    private void LayOutTimeAxis() {
      double secondsPerColumn = bucketTicks / (double)TimeSpan.TicksPerSecond;
      Size widest = Measure(FormatClock(DateTime.Today.AddHours(23.99), false), axisFont!);
      double minimumSeconds = (widest.Width + S(32)) * secondsPerColumn;
      int stepSeconds = TimeSteps[TimeSteps.Length - 1];
      foreach (int candidate in TimeSteps) {
        if (candidate >= minimumSeconds) {
          stepSeconds = candidate;
          break;
        }
      }

      // Labels on round local times.
      long offset = TimeZoneInfo.Local.GetUtcOffset(windowStart).Ticks;
      long stepTicks = stepSeconds * TimeSpan.TicksPerSecond;
      long startLocal = windowStart.Ticks + offset;
      long endLocal = startLocal + bucketTicks * bucketCount;
      int y = plot.Bottom + (int)S(8);
      for (long tick = (startLocal / stepTicks + 1) * stepTicks; tick < endLocal; tick += stepTicks) {
        float x = plot.Left + (tick - startLocal) / (float)bucketTicks;
        DateTime local = new DateTime(tick, DateTimeKind.Local);
        string label = local.TimeOfDay == TimeSpan.Zero
          ? local.ToString("MMM d", CultureInfo.CurrentCulture)
          : FormatClock(local, false);
        Size size = Measure(label, axisFont!);
        int labelX = (int)Math.Round(x - size.Width / 2f);
        if (labelX < plot.Left - S(4) || labelX + size.Width > plot.Right + S(4))
          continue;
        AddText(label, axisFont!, new Point(labelX, y), theme.TextTertiary, Flags);
      }
    }

    private static string FormatClock(DateTime local, bool seconds) {
      return local.ToString(seconds ? "T" : "t", CultureInfo.CurrentCulture);
    }

    /// <summary>"14:02", or "Sat 14:02" on another day.</summary>
    private static string FormatWhen(DateTime utc, bool seconds) {
      DateTime local = utc.ToLocalTime();
      string clock = FormatClock(local, seconds);
      return local.Date == DateTime.Today ? clock
        : local.ToString("ddd", CultureInfo.CurrentCulture) + " " + clock;
    }

    private void DrawStatistics(Graphics g) {
      float pad = Pad;
      float inner = Width - 2 * pad;
      float nameWidth = Math.Max(S(120), Math.Min(S(320), inner * 0.3f));
      float columnWidth = (inner - nameWidth) / ColumnNames.Length;
      float y = pad;

      for (int c = 0; c < ColumnNames.Length; c++)
        AddText(ColumnNames[c], labelFont!,
          new Point((int)(pad + nameWidth + c * columnWidth), (int)y), theme.TextSecondary, Flags);
      y += labelFont!.Height + S(6);

      for (int i = 0; i < series.Length; i++) {
        Series item = series[i];
        if (!item.Active)
          continue;
        float rowHeight = valueFont!.Height;
        float dot = S(8);
        using (SolidBrush brush = new SolidBrush(SeriesColor(i)))
          g.FillEllipse(brush, pad, y + (rowHeight - dot) / 2, dot, dot);
        AddText(item.Name, nameFont!,
          new Rectangle((int)(pad + dot + S(8)), (int)(y + (rowHeight - nameFont!.Height) / 2),
            (int)Math.Max(0, nameWidth - dot - S(20)), nameFont.Height),
          theme.Text, Flags | TextFormatFlags.EndEllipsis);

        HistoryStatistics statistics = item.Statistics;
        for (int c = 0; c < ColumnNames.Length; c++) {
          float value = float.NaN;
          DateTime when = default;
          if (c == 3) {
            value = item.Current;
          } else if (statistics.HasData) {
            value = c == 0 ? statistics.Minimum : c == 1 ? statistics.Average : statistics.Maximum;
            when = c == 0 ? statistics.MinimumTime : c == 2 ? statistics.MaximumTime : default;
          }
          float x = pad + nameWidth + c * columnWidth;
          string text = unit.Format(float.IsNaN(value) ? float.NaN : unit.ToDisplay(value));
          Size size = Measure(text, valueFont);
          AddText(text, valueFont, new Point((int)x, (int)y), theme.Text, Flags);
          if (when != default) {
            // When the low or the peak happened is often the point of looking.
            string time = FormatWhen(when, range <= TimeSpan.FromHours(1));
            Size timeSize = Measure(time, labelFont);
            float timeX = x + size.Width + S(6);
            if (timeX + timeSize.Width <= x + columnWidth - S(8)) {
              float baseline = y + Theme.Ascent(valueFont);
              AddText(time, labelFont,
                new Point((int)timeX, (int)(baseline - Theme.Ascent(labelFont))),
                theme.TextTertiary, Flags);
            }
          }
        }
        y += rowHeight + S(8);
      }
    }

    private void RenderPlot() {
      plotMargin = (int)Math.Ceiling(S(6));
      if (!hasData) {
        plotLayer?.Dispose();
        plotLayer = null;
        return;
      }
      plotLayer = Reuse(plotLayer, plot.Width + 2 * plotMargin, plot.Height + 2 * plotMargin,
        PixelFormat.Format32bppPArgb);
      using (Graphics g = Graphics.FromImage(plotLayer)) {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(plotMargin - plot.X, plotMargin - plot.Y);
        // The second series first, so the main one stays on top.
        for (int i = series.Length - 1; i >= 0; i--)
          if (series[i].Active && series[i].Statistics.HasData)
            DrawSeries(g, series[i], SeriesColor(i), i == 0);
      }
    }

    private void DrawSeries(Graphics g, Series item, Color color, bool primary) {
      Color bandColor = Color.FromArgb(theme.IsDark ? (primary ? 70 : 55) : (primary ? 46 : 36), color);
      float lineWidth = S(1.75f);
      HistoryBucket[] buckets = item.Buckets;
      int lastNonEmpty = -1;

      using (SolidBrush band = new SolidBrush(bandColor))
      using (Pen line = new Pen(color, lineWidth)) {
        line.LineJoin = LineJoin.Round;
        line.StartCap = LineCap.Round;
        line.EndCap = LineCap.Round;
        int index = 0;
        while (index < bucketCount) {
          if (buckets[index].IsEmpty) {
            index++;
            continue;
          }
          // A run of buckets joined without a break in recording.
          upper.Clear();
          lower.Clear();
          middle.Clear();
          int runEnd = index;
          for (int i = index; i < bucketCount; i++) {
            if (buckets[i].IsEmpty)
              continue;
            if (i != index && !buckets[i].Continues)
              break;
            float x = plot.Left + i + 0.5f;
            upper.Add(new PointF(x, Y(unit.ToDisplay(buckets[i].Maximum))));
            lower.Add(new PointF(x, Y(unit.ToDisplay(buckets[i].Minimum))));
            middle.Add(new PointF(x, Y(unit.ToDisplay(buckets[i].Average))));
            runEnd = i;
          }
          DrawRun(g, band, line, bandColor);
          lastNonEmpty = runEnd;
          index = runEnd + 1;
        }
      }

      // The latest reading, marked as on the overview cards.
      if (primary && lastNonEmpty >= bucketCount - Math.Max(2, (int)S(3))) {
        float x = plot.Left + lastNonEmpty + 0.5f;
        float y = Y(unit.ToDisplay(buckets[lastNonEmpty].Average));
        float dot = S(3.5f), ring = dot + S(1.5f);
        using (SolidBrush brush = new SolidBrush(theme.Surface))
          g.FillEllipse(brush, x - ring, y - ring, ring * 2, ring * 2);
        using (SolidBrush brush = new SolidBrush(color))
          g.FillEllipse(brush, x - dot, y - dot, dot * 2, dot * 2);
      }
    }

    private void DrawRun(Graphics g, Brush band, Pen line, Color bandColor) {
      int count = middle.Count;
      if (count == 1) {
        // An isolated reading: a short bar for its spread and a dot.
        PointF top = upper[0], bottom = lower[0], center = middle[0];
        if (bottom.Y - top.Y > 1)
          using (Pen pen = new Pen(bandColor, Math.Max(2f, line.Width)))
            g.DrawLine(pen, top, bottom);
        float r = line.Width * 0.9f;
        using (SolidBrush dot = new SolidBrush(line.Color))
          g.FillEllipse(dot, center.X - r, center.Y - r, 2 * r, 2 * r);
        return;
      }

      PointF[] outline = new PointF[count * 2];
      for (int i = 0; i < count; i++) {
        outline[i] = upper[i];
        outline[count * 2 - 1 - i] = lower[i];
      }
      g.FillPolygon(band, outline);
      g.DrawLines(line, middle.ToArray());
    }

    // ---- crosshair --------------------------------------------------------------------------

    /// <summary>The column nearest the pointer that has data, within a few pixels.</summary>
    private int SnapColumn(Series item, int column) {
      if (!item.Active || column < 0 || column >= bucketCount || item.Buckets.Length < bucketCount)
        return -1;
      int reach = (int)Math.Ceiling(S(8));
      for (int d = 0; d <= reach; d++) {
        if (column - d >= 0 && !item.Buckets[column - d].IsEmpty)
          return column - d;
        if (column + d < bucketCount && !item.Buckets[column + d].IsEmpty)
          return column + d;
      }
      return -1;
    }

    private void PrepareReadout() {
      readoutColumn = -1;
      overlayBounds = Rectangle.Empty;
      if (hoverColumn < 0 || hoverColumn >= bucketCount || !hasData)
        return;
      EnsureFonts();

      int primary = SnapColumn(series[0], hoverColumn);
      int column = primary >= 0 ? primary : hoverColumn;
      readoutColumn = column;
      float x = plot.Left + column + 0.5f;
      readoutTime = FormatWhen(windowStart.AddTicks(bucketTicks * column + bucketTicks / 2),
        range <= TimeSpan.FromHours(1));

      float padding = S(10), dotSize = S(7), lineGap = S(4);
      Size header = Measure(readoutTime, tipBoldFont!);
      float width = header.Width, height = header.Height;
      for (int i = 0; i < series.Length; i++) {
        Series item = series[i];
        readoutText[i] = "";
        readoutY[i] = float.NaN;
        if (!item.Active)
          continue;
        int at = i == 0 ? primary : SnapColumn(item, column);
        if (at < 0) {
          readoutText[i] = "No readings";
        } else {
          HistoryBucket bucket = item.Buckets[at];
          float average = unit.ToDisplay(bucket.Average);
          string minimum = unit.Format(unit.ToDisplay(bucket.Minimum));
          string maximum = unit.Format(unit.ToDisplay(bucket.Maximum));
          readoutText[i] = minimum == maximum ? unit.Format(average)
            : unit.Format(average) + "   " + minimum + " – " + maximum;
          readoutY[i] = Y(average);
        }
        width = Math.Max(width, dotSize + S(6) + Measure(readoutText[i], tipFont!).Width);
        height += lineGap + tipFont!.Height;
      }

      RectangleF bubble = new RectangleF(x + S(12), plot.Top + S(4),
        width + 2 * padding, height + 2 * padding);
      if (bubble.Right > Width - S(6))
        bubble.X = x - S(12) - bubble.Width;
      bubble.X = Math.Max(S(6), bubble.X);
      readoutBubble = bubble;

      Rectangle strip = Rectangle.FromLTRB((int)(x - S(8)) - 1, plot.Top - (int)S(8),
        (int)(x + S(8)) + 2, plot.Bottom + (int)S(8));
      overlayBounds = Rectangle.Union(strip,
        Rectangle.Inflate(Rectangle.Ceiling(bubble), 2, (int)Math.Ceiling(S(4))));
    }

    private void DrawCrosshair(Graphics g) {
      if (readoutColumn < 0)
        return;
      float x = plot.Left + readoutColumn + 0.5f;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      using (Pen pen = new Pen(Color.FromArgb(theme.IsDark ? 110 : 90, theme.TextSecondary),
        Math.Max(1f, S(1))))
        g.DrawLine(pen, x, plot.Top, x, plot.Bottom);

      for (int i = 0; i < series.Length; i++) {
        if (float.IsNaN(readoutY[i]))
          continue;
        float dot = S(4), ring = dot + S(2);
        using (SolidBrush brush = new SolidBrush(theme.Surface))
          g.FillEllipse(brush, x - ring, readoutY[i] - ring, ring * 2, ring * 2);
        using (SolidBrush brush = new SolidBrush(SeriesColor(i)))
          g.FillEllipse(brush, x - dot, readoutY[i] - dot, dot * 2, dot * 2);
      }

      RectangleF bubble = readoutBubble;
      RectangleF shadow = bubble;
      shadow.Offset(0, S(2));
      using (GraphicsPath path = Theme.RoundedRect(shadow, S(8)))
      using (SolidBrush brush = new SolidBrush(Color.FromArgb(theme.IsDark ? 70 : 24, 0, 0, 0)))
        g.FillPath(brush, path);
      using (GraphicsPath path = Theme.RoundedRect(bubble, S(8)))
      using (SolidBrush fill = new SolidBrush(theme.IsDark ? theme.SurfaceSunken : theme.Surface))
      using (Pen border = new Pen(theme.Border, Math.Max(1f, S(1)))) {
        g.FillPath(fill, path);
        g.DrawPath(border, path);
      }

      float padding = S(10), dotSize = S(7), lineGap = S(4);
      float y = bubble.Top + padding;
      TextRenderer.DrawText(g, readoutTime, tipBoldFont!,
        new Point((int)(bubble.Left + padding), (int)y), theme.Text, Flags);
      y += tipBoldFont!.Height;
      for (int i = 0; i < series.Length; i++) {
        if (!series[i].Active)
          continue;
        y += lineGap;
        using (SolidBrush brush = new SolidBrush(SeriesColor(i)))
          g.FillEllipse(brush, bubble.Left + padding, y + (tipFont!.Height - dotSize) / 2, dotSize, dotSize);
        TextRenderer.DrawText(g, readoutText[i], tipFont!,
          new Point((int)(bubble.Left + padding + dotSize + S(6)), (int)y), theme.TextSecondary, Flags);
        y += tipFont!.Height;
      }
    }

    private void SetHover(int column) {
      if (column == hoverColumn)
        return;
      Rectangle previous = overlayBounds;
      hoverColumn = column;
      if (cacheValid)
        PrepareReadout();
      if (!previous.IsEmpty)
        Invalidate(previous);
      if (!overlayBounds.IsEmpty)
        Invalidate(overlayBounds);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      SetHover(hasData && plot.Contains(e.Location) ? e.X - plot.Left : -1);
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      SetHover(-1);
    }

    private void UpdateAccessibleDescription() {
      Series item = series[0];
      if (!item.Active || !item.Statistics.HasData) {
        AccessibleDescription = "No readings";
        return;
      }
      HistoryStatistics s = item.Statistics;
      AccessibleDescription = item.Name + ": lowest " + unit.Format(unit.ToDisplay(s.Minimum)) +
        ", average " + unit.Format(unit.ToDisplay(s.Average)) +
        ", highest " + unit.Format(unit.ToDisplay(s.Maximum)) + " at " + FormatWhen(s.MaximumTime, false);
    }
  }
}
