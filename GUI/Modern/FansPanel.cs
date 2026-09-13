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
using System.Linq;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI.Modern {

  public enum FanTuningState {
    Idle,
    /// <summary>Queued behind another fan in the running detection.</summary>
    Waiting,
    Active
  }

  /// <summary>What the Fans page needs from the application.</summary>
  public interface IFanControlHost {

    /// <summary>A sentence about missing access, or null when all is well.</summary>
    string? AccessNote { get; }

    FanMode GetMode(ISensor control);

    /// <summary>For example "CPU Package · 5 points".</summary>
    string? DescribeCurve(ISensor control);

    void SetAutomatic(ISensor control);

    /// <summary>Applies a fixed duty, raised to the fan's calibrated minimum.</summary>
    void SetFixed(ISensor control, float percent);

    /// <summary>Opens the curve dialog; returns once it closes. The page itself edits curves in place.</summary>
    void EditCurve(ISensor control);

    FanCurve? GetCurve(ISensor control);

    void SetCurve(ISensor control, FanCurve curve);

    /// <summary>A starting curve from calibration, or null without a temperature sensor.</summary>
    FanCurve? CreateDefaultCurve(ISensor control);

    IReadOnlyList<ISensor> GetTemperatureSensors();

    /// <summary>False for graphics card fans, which are only paired.</summary>
    bool CanCalibrate(ISensor control);

    FanPairing? GetPairing(ISensor control);

    FanCalibration? GetCalibration(ISensor control);

    /// <summary>The running or most recent detection, or null.</summary>
    FanTuningProgress? TuningProgress { get; }

    FanTuningState GetTuningState(ISensor control);

    /// <summary>What the most recent detection reported for this fan.</summary>
    FanTuningResult? GetTuningResult(ISensor control);

    /// <summary>False when detection is already running.</summary>
    bool StartTuning(IReadOnlyList<ISensor> controls);

    void CancelTuning();

    FanProfileKind? ActiveProfile { get; }

    void ApplyProfile(FanProfileKind profile);

    void SaveProfile(FanProfileKind profile);
  }

  /// <summary>
  /// One card per controllable fan: live speed, a clear choice between
  /// automatic control, a fixed speed and a temperature curve edited in place,
  /// and detection of the fan's speed sensor and minimum speed. Profiles in
  /// the header switch every fan at once.
  /// </summary>
  public sealed class FansPanel : UserControl {

    private const float MoveSeconds = 0.28f;

    private readonly IComputer computer;
    private readonly IFanControlHost host;
    private readonly PageHeader header;
    private readonly FansToolbar toolbar;
    private readonly Panel scroller;
    private readonly List<FanCard> cards = new List<FanCard>();
    private readonly Dictionary<string, ISensor> sensors =
      new Dictionary<string, ISensor>(StringComparer.Ordinal);
    private readonly Dictionary<FanCard, (Rectangle From, Rectangle To, double Start)> moves =
      new Dictionary<FanCard, (Rectangle, Rectangle, double)>();
    private string signature = "";
    private Theme theme = Theme.Current;

    public FansPanel(IComputer computer, IFanControlHost host) {
      this.computer = computer;
      this.host = host;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

      header = new PageHeader {
        Dock = DockStyle.Top,
        Title = "Fans",
        Subtitle = "Choose how each fan is driven. Fans always return to automatic control when Open Hardware Monitor exits."
      };
      toolbar = new FansToolbar { Dock = DockStyle.Top, Visible = false };
      toolbar.ProfileSelected += OnProfileSelected;
      toolbar.SaveRequested += OnSaveProfile;
      toolbar.DetectAllClicked += OnDetectAll;
      scroller = new BufferedScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
      scroller.Resize += delegate { LayoutCards(false); };
      Controls.Add(scroller);
      Controls.Add(toolbar);
      Controls.Add(header);
      ApplyTheme();
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        ApplyTheme();
      }
    }

    private void ApplyTheme() {
      BackColor = theme.Background;
      scroller.BackColor = theme.Background;
      header.Theme = theme;
      toolbar.Theme = theme;
      foreach (FanCard card in cards)
        card.Theme = theme;
      Invalidate(true);
    }

    private int S(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    /// <summary>Call after each sensor update, on the UI thread.</summary>
    public void UpdateValues() {
      List<ISensor> controls = new List<ISensor>();
      sensors.Clear();
      computer.Accept(new SensorVisitor(sensor => {
        sensors[sensor.Identifier.ToString()] = sensor;
        if (sensor.SensorType == SensorType.Control && sensor.Control != null)
          controls.Add(sensor);
      }));

      string current = string.Join("|", controls.Select(c => c.Identifier.ToString()));
      if (current != signature) {
        signature = current;
        Rebuild(controls);
      }

      header.Note = host.AccessNote;
      header.Invalidate();
      FanTuningProgress? progress = host.TuningProgress;
      toolbar.Sync(host.ActiveProfile, progress?.IsRunning == true, cards.Count > 0);
      foreach (FanCard card in cards)
        card.RefreshValues(progress);
    }

    private ISensor? Resolve(string identifier) {
      return sensors.TryGetValue(identifier, out ISensor? sensor) ? sensor : null;
    }

    private void Rebuild(List<ISensor> controls) {
      scroller.SuspendLayout();
      moves.Clear();
      foreach (FanCard card in cards) {
        scroller.Controls.Remove(card);
        card.Dispose();
      }
      cards.Clear();
      foreach (ISensor control in controls) {
        FanCard card = new FanCard(control, host, Resolve) { Theme = theme };
        card.HeightChanged += delegate { LayoutCards(true); };
        card.DetectRequested += delegate { StartTuning(new[] { card.ControlSensor }); };
        cards.Add(card);
        scroller.Controls.Add(card);
      }
      scroller.ResumeLayout(false);
      header.EmptyMessage = cards.Count == 0
        ? "No controllable fans were found. Graphics card fans need administrator rights; motherboard fans also need PawnIO."
        : null;
      LayoutCards(false);
    }

    // ---- actions --------------------------------------------------------------------

    private DialogResult Ask(string text, string caption) {
      Form? owner = FindForm();
      return owner != null
        ? MessageBox.Show(owner, text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
        : MessageBox.Show(text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
    }

    private void StartTuning(IReadOnlyList<ISensor> controls) {
      if (controls.Count == 0 || host.TuningProgress?.IsRunning == true)
        return;
      bool calibrates = controls.Any(host.CanCalibrate);
      string subject = controls.Count == 1 ? controls[0].Name : "each fan in turn";
      string text = calibrates
        ? "The speed of " + subject + " will change for one to two minutes" +
          (controls.Count > 1 ? " per fan" : "") + ", to find its speed sensor and the " +
          "slowest speed it keeps turning at. A motherboard fan may stop for a few seconds."
        : "The speed of " + subject + " will change for about 15 seconds" +
          (controls.Count > 1 ? " per fan" : "") + ", to find its speed sensor.";
      text += "\n\nAfterwards each fan returns to its previous setting. If a processor or " +
        "graphics temperature passes 80 °C, or anything unexpected happens, the fan goes " +
        "back to automatic control at once. You can cancel at any time." +
        "\n\nFor accurate results, close demanding programs first.";
      if (Ask(text, "Detect and calibrate fans") != DialogResult.OK)
        return;
      if (!host.StartTuning(controls))
        MessageBox.Show(FindForm(), "Fan detection is already running.", "Detect and calibrate fans",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
      UpdateValues();
    }

    private void OnDetectAll(object? sender, EventArgs e) {
      if (host.TuningProgress?.IsRunning == true) {
        host.CancelTuning();
        UpdateValues();
        return;
      }
      StartTuning(cards.Select(c => c.ControlSensor).ToList());
    }

    private void OnProfileSelected(object? sender, FanProfileKind profile) {
      if (host.TuningProgress?.IsRunning == true &&
        Ask("Switching profiles stops fan detection. The fan being tested returns to automatic " +
          "control first.", "Fan profile") != DialogResult.OK) {
        UpdateValues();
        return;
      }
      host.ApplyProfile(profile);
      toolbar.Status = null;
      UpdateValues();
    }

    private void OnSaveProfile(object? sender, FanProfileKind profile) {
      host.SaveProfile(profile);
      toolbar.Status = "Saved as " + FanProfileStore.DisplayName(profile) + ".";
      UpdateValues();
    }

    // ---- layout ---------------------------------------------------------------------

    /// <summary>
    /// Two columns at most. A card that grows or shrinks - for example when
    /// the curve editor opens - pushes the cards below it smoothly.
    /// </summary>
    private void LayoutCards(bool animate) {
      if (!animate)
        moves.Clear();
      int margin = S(24), gap = S(16), minimum = S(380);
      int available = Math.Max(minimum, scroller.ClientSize.Width - 2 * margin);
      int columns = Math.Max(1, Math.Min(2, (available + gap) / (minimum + gap)));
      int width = (available - (columns - 1) * gap) / columns;
      Point scroll = scroller.AutoScrollPosition;
      int[] heights = new int[columns];
      scroller.SuspendLayout();
      for (int i = 0; i < cards.Count; i++) {
        FanCard card = cards[i];
        int column = i % columns;
        int height = card.PreferredHeight;
        Rectangle bounds = new Rectangle(margin + column * (width + gap) + scroll.X,
          S(4) + heights[column] + scroll.Y, width, height);
        bool glide = animate && Animator.Enabled && Animator.IsOnScreen(card) &&
          card.Width == bounds.Width;
        if (glide && card.Bounds != bounds)
          MoveTo(card, bounds);
        else if (!moves.ContainsKey(card) && card.Bounds != bounds)
          card.Bounds = bounds;
        heights[column] += height + gap;
      }
      scroller.AutoScrollMargin = new Size(0, margin);
      scroller.ResumeLayout(true);
    }

    private void MoveTo(FanCard card, Rectangle target) {
      if (moves.TryGetValue(card, out var existing) && existing.To == target)
        return;
      bool idle = moves.Count == 0;
      moves[card] = (card.Bounds, target, Animator.Now);
      if (idle)
        Animator.Run(MoveFrame);
    }

    private bool MoveFrame() {
      if (IsDisposed) {
        moves.Clear();
        return false;
      }
      foreach (KeyValuePair<FanCard, (Rectangle From, Rectangle To, double Start)> entry in moves.ToList()) {
        var move = entry.Value;
        float t = Easing.OutCubic((float)((Animator.Now - move.Start) / MoveSeconds));
        entry.Key.Bounds = new Rectangle(
          (int)Math.Round(move.From.X + (move.To.X - move.From.X) * t),
          (int)Math.Round(move.From.Y + (move.To.Y - move.From.Y) * t),
          (int)Math.Round(move.From.Width + (move.To.Width - move.From.Width) * t),
          (int)Math.Round(move.From.Height + (move.To.Height - move.From.Height) * t));
        if (t >= 1)
          moves.Remove(entry.Key);
      }
      return moves.Count > 0;
    }
  }

  /// <summary>Profile choice, saving and detection of every fan, under the page title.</summary>
  internal sealed class FansToolbar : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private readonly SegmentedControl profiles;
    private readonly ModernButton save;
    private readonly ModernButton detectAll;
    private Theme theme = Theme.Current;
    private bool syncing;
    private string? status;
    private int fontDpi;
    private Font? labelFont;

    public FansToolbar() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      profiles = new SegmentedControl {
        Items = FanProfileStore.All.Select(FanProfileStore.DisplayName).ToArray(),
        AccessibleName = "Fan profile"
      };
      save = new ModernButton { Text = "Save current settings as…", Kind = ButtonKind.Subtle };
      detectAll = new ModernButton { Text = "Detect and calibrate all", Kind = ButtonKind.Secondary };

      profiles.SelectedIndexChanged += delegate {
        if (!syncing && profiles.SelectedIndex >= 0)
          ProfileSelected?.Invoke(this, FanProfileStore.All[profiles.SelectedIndex]);
      };
      save.Click += delegate { ShowSaveMenu(); };
      detectAll.Click += delegate { DetectAllClicked?.Invoke(this, EventArgs.Empty); };

      Controls.Add(profiles);
      Controls.Add(save);
      Controls.Add(detectAll);
    }

    public event EventHandler<FanProfileKind>? ProfileSelected;
    public event EventHandler<FanProfileKind>? SaveRequested;
    public event EventHandler? DetectAllClicked;

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        foreach (ModernControl child in new ModernControl[] { profiles, save, detectAll }) {
          child.Theme = value;
          child.SurfaceColor = theme.Background;
        }
        Invalidate(true);
      }
    }

    public string? Status {
      get { return status; }
      set {
        status = value;
        Invalidate();
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    public void Sync(FanProfileKind? active, bool detecting, bool anyFans) {
      syncing = true;
      try {
        profiles.SelectedIndex = active.HasValue ? (int)active.Value : -1;
      } finally {
        syncing = false;
      }
      string text = detecting ? "Cancel detection" : "Detect and calibrate all";
      if (detectAll.Text != text) {
        detectAll.Text = text;
        PerformLayout();
      }
      save.Enabled = !detecting;
      if (Visible != anyFans) {
        Visible = anyFans;
        // The height follows the layout, which does not run while hidden.
        PerformLayout();
      }
    }

    private void ShowSaveMenu() {
      ContextMenuStrip menu = new ContextMenuStrip();
      foreach (FanProfileKind kind in FanProfileStore.All) {
        FanProfileKind profile = kind;
        menu.Items.Add(new ToolStripMenuItem(FanProfileStore.DisplayName(kind), null,
          delegate { SaveRequested?.Invoke(this, profile); }));
      }
      menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
      menu.Show(save, new Point(0, save.Height));
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && labelFont != null)
        return;
      labelFont?.Dispose();
      fontDpi = DeviceDpi;
      labelFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
    }

    private int LabelWidth {
      get {
        EnsureFonts();
        return TextRenderer.MeasureText("Profile", labelFont!, Size.Empty, Flags).Width;
      }
    }

    // Items flow left to right and wrap on narrow windows; detection sits at
    // the right end of whichever row it lands on.
    protected override void OnLayout(LayoutEventArgs levent) {
      base.OnLayout(levent);
      int margin = (int)S(24), gap = (int)S(10), rowGap = (int)S(10);
      int right = Math.Max(margin + (int)S(200), Width - margin);
      Size profileSize = profiles.GetPreferredSize(Size.Empty);
      Size saveSize = save.GetPreferredSize(Size.Empty);
      Size detectSize = detectAll.GetPreferredSize(Size.Empty);
      int rowHeight = Math.Max(profileSize.Height, saveSize.Height);

      int x = margin + LabelWidth + gap, y = 0;
      profiles.Bounds = new Rectangle(new Point(x, y), profileSize);
      x = profiles.Right + gap;
      if (x + saveSize.Width > right) {
        x = margin;
        y += rowHeight + rowGap;
      }
      save.Bounds = new Rectangle(new Point(x, y + (rowHeight - saveSize.Height) / 2), saveSize);
      x = save.Right + gap * 2;
      if (x + detectSize.Width > right) {
        y += rowHeight + rowGap;
      }
      detectAll.Bounds = new Rectangle(new Point(right - detectSize.Width,
        y + (rowHeight - detectSize.Height) / 2), detectSize);

      int height = y + rowHeight + (int)S(16);
      if (Height != height)
        Height = height;
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      int margin = (int)S(24);
      TextRenderer.DrawText(g, "Profile", labelFont!,
        new Rectangle(margin, profiles.Top, LabelWidth, profiles.Height), theme.TextSecondary,
        Flags | TextFormatFlags.VerticalCenter);
      if (!string.IsNullOrEmpty(status)) {
        int left = save.Right + (int)S(8);
        int limit = detectAll.Top == save.Top ? detectAll.Left - (int)S(12) : Width - margin;
        if (limit - left > S(60))
          TextRenderer.DrawText(g, status, labelFont!,
            new Rectangle(left, save.Top, limit - left, save.Height), theme.Good,
            Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        labelFont?.Dispose();
      base.Dispose(disposing);
    }
  }

  /// <summary>A card for one fan output.</summary>
  internal sealed class FanCard : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private readonly ISensor control;
    private readonly IFanControlHost host;
    private readonly Func<string, ISensor?> resolve;
    private readonly SegmentedControl mode;
    private readonly ModernSlider slider;
    private readonly FanCurveEditor editor;
    private readonly ModernButton detect;
    private Theme theme = Theme.Current;
    private bool syncing;
    private int fontDpi;
    private Font? titleFont, subtitleFont, bigFont, labelFont, valueFont, strongFont;
    private FanMode shownMode = FanMode.Automatic;
    private int lastHeight;
    private string? loadedCurve;
    private FanCurve? rememberedCurve;
    private string? notice;
    private FanTuningProgress? progress;
    private FanTuningState tuningState;

    public FanCard(ISensor control, IFanControlHost host, Func<string, ISensor?> resolve) {
      this.control = control;
      this.host = host;
      this.resolve = resolve;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

      mode = new SegmentedControl { Items = new[] { "Automatic", "Fixed speed", "Curve" } };
      slider = new ModernSlider { AccessibleName = "Fixed speed" };
      editor = new FanCurveEditor { Visible = false, TemperatureSensors = host.GetTemperatureSensors };
      detect = new ModernButton { Kind = ButtonKind.Subtle };

      IControl hardwareControl = control.Control;
      slider.Minimum = hardwareControl.MinSoftwareValue;
      slider.Maximum = hardwareControl.MaxSoftwareValue;
      slider.Value = hardwareControl.SoftwareValue > 0
        ? hardwareControl.SoftwareValue
        : Math.Max(hardwareControl.MinSoftwareValue, control.Value ?? 50);
      editor.MinimumDuty = hardwareControl.MinSoftwareValue;
      editor.MaximumDuty = hardwareControl.MaxSoftwareValue;

      mode.SelectedIndexChanged += OnModeChanged;
      slider.ValueChanged += delegate { Invalidate(); };
      slider.ValueCommitted += delegate {
        host.SetFixed(control, slider.Value);
        Sync();
      };
      editor.CurveCommitted += delegate {
        FanCurve? curve = editor.BuildCurve();
        if (curve == null)
          return;
        rememberedCurve = curve;
        loadedCurve = curve.Serialize();
        host.SetCurve(control, curve);
      };
      detect.Click += delegate {
        if (tuningState == FanTuningState.Active)
          host.CancelTuning();
        else
          DetectRequested?.Invoke(this, EventArgs.Empty);
      };

      Controls.Add(mode);
      Controls.Add(slider);
      Controls.Add(editor);
      Controls.Add(detect);
      AccessibleName = control.Name;
      Sync();
      lastHeight = PreferredHeight;
    }

    public event EventHandler? HeightChanged;

    public event EventHandler? DetectRequested;

    public ISensor ControlSensor {
      get { return control; }
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        editor.BackColor = theme.Surface;
        editor.Theme = theme;
        foreach (ModernControl child in new ModernControl[] { mode, slider, detect }) {
          child.Theme = theme;
          child.SurfaceColor = theme.Surface;
        }
        Invalidate(true);
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private float Pad {
      get { return S(20); }
    }

    private int RowY {
      get { return (int)Math.Round(Pad + S(62)) + mode.GetPreferredSize(Size.Empty).Height + (int)S(16); }
    }

    private int RowHeight {
      get { return shownMode == FanMode.Curve ? editor.PreferredHeight : (int)S(28); }
    }

    private int InfoY {
      get { return RowY + RowHeight + (int)S(20); }
    }

    private int InfoHeight {
      get { return (int)S(40); }
    }

    public int PreferredHeight {
      get { return InfoY + InfoHeight + (int)Math.Round(Pad); }
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      DisposeFonts();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 10.5f, FontStyle.Regular, DeviceDpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      bigFont = Theme.CreateFont(Theme.DisplayFamily, 18f, FontStyle.Regular, DeviceDpi);
      labelFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      valueFont = Theme.CreateFont(Theme.SemiboldFamily, 10f, FontStyle.Regular, DeviceDpi);
      strongFont = Theme.CreateFont(Theme.SemiboldFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    private void DisposeFonts() {
      foreach (Font? font in new[] { titleFont, subtitleFont, bigFont, labelFont, valueFont, strongFont })
        font?.Dispose();
    }

    private void OnModeChanged(object? sender, EventArgs e) {
      if (syncing)
        return;
      notice = null;
      switch ((FanMode)mode.SelectedIndex) {
        case FanMode.Automatic:
          host.SetAutomatic(control);
          break;
        case FanMode.Fixed:
          host.SetFixed(control, slider.Value);
          break;
        case FanMode.Curve:
          FanCurve? curve = host.GetCurve(control) ?? rememberedCurve ?? host.CreateDefaultCurve(control);
          if (curve == null)
            notice = "No temperature sensor is available for a curve.";
          else
            host.SetCurve(control, curve);
          break;
      }
      Sync();
    }

    /// <summary>Brings the card in line with what is actually in effect.</summary>
    private void Sync() {
      FanMode current = host.GetMode(control);
      bool detecting = progress?.IsRunning == true;
      tuningState = host.GetTuningState(control);
      IControl hardwareControl = control.Control;
      FanCalibration? calibration = host.GetCalibration(control);
      bool calibrates = host.CanCalibrate(control);

      syncing = true;
      try {
        mode.SelectedIndex = (int)current;
        // Detection reads every fan; changes elsewhere would disturb it.
        mode.Enabled = !detecting;
        slider.Enabled = !detecting;
        editor.Enabled = !detecting;
        slider.Visible = current == FanMode.Fixed;
        editor.Visible = current == FanMode.Curve;

        // A fixed speed below what starts the fan would leave it standing.
        float floor = calibration != null
          ? Math.Min(hardwareControl.MaxSoftwareValue,
              Math.Max(hardwareControl.MinSoftwareValue, calibration.StartDuty))
          : hardwareControl.MinSoftwareValue;
        if (!slider.Capture && slider.Minimum != floor)
          slider.Minimum = floor;
        editor.RunningMinimum = calibrates ? calibration?.MinRunningDuty : null;

        if (current == FanMode.Curve) {
          FanCurve? curve = host.GetCurve(control);
          if (curve != null) {
            rememberedCurve = curve;
            string text = curve.Serialize();
            if (text != loadedCurve && !editor.IsEditing) {
              loadedCurve = text;
              ISensor? source = resolve(curve.SourceSensorIdentifier);
              editor.Load(curve, source != null ? FanCurveEditor.SensorName(source) : null);
            }
          }
        }

        string label = tuningState == FanTuningState.Active ? "Cancel"
          : calibrates ? "Detect and calibrate" : "Detect speed sensor";
        if (detect.Text != label) {
          detect.Text = label;
          detect.Kind = tuningState == FanTuningState.Active ? ButtonKind.Secondary : ButtonKind.Subtle;
          PerformLayout();
        }
        detect.Enabled = tuningState == FanTuningState.Active || !detecting;
      } finally {
        syncing = false;
      }

      if (current != shownMode) {
        shownMode = current;
        PerformLayout();
      }
      int height = PreferredHeight;
      if (height != lastHeight) {
        lastHeight = height;
        HeightChanged?.Invoke(this, EventArgs.Empty);
      }
      Invalidate();
    }

    public void RefreshValues(FanTuningProgress? latest) {
      progress = latest;
      if (!slider.Capture && !editor.IsEditing)
        Sync();
      else
        Invalidate();

      if (shownMode == FanMode.Curve && rememberedCurve != null) {
        ISensor? source = resolve(rememberedCurve.SourceSensorIdentifier);
        editor.SetLive(source?.Value, control.Value);
      }
    }

    private ISensor? GuessSpeedSensor() {
      ISensor? byName = null, byIndex = null;
      foreach (ISensor sensor in control.Hardware.Sensors) {
        if (sensor.SensorType != SensorType.Fan)
          continue;
        if (string.Equals(sensor.Name, control.Name, StringComparison.OrdinalIgnoreCase))
          byName = sensor;
        else if (sensor.Index == control.Index)
          byIndex = sensor;
      }
      return byName ?? byIndex;
    }

    /// <summary>The detected sensor when there is one; otherwise a guess by name, then index.</summary>
    private ISensor? FindSpeedSensor() {
      string? paired = host.GetPairing(control)?.SpeedSensorIdentifier;
      return (paired != null ? resolve(paired) : null) ?? GuessSpeedSensor();
    }

    private string PairingText() {
      FanPairing? pairing = host.GetPairing(control);
      if (pairing == null) {
        ISensor? guess = GuessSpeedSensor();
        return guess != null
          ? "Speed sensor " + guess.Name + " (a guess, not detected yet)"
          : "Speed sensor not detected yet";
      }
      if (pairing.SpeedSensorIdentifier == null)
        return "No speed sensor responded to this fan";
      ISensor? sensor = resolve(pairing.SpeedSensorIdentifier);
      string certainty = pairing.Confidence == FanPairingConfidence.High ? "detected"
        : pairing.Confidence == FanPairingConfidence.Medium ? "detected, fairly sure" : "uncertain";
      return "Speed sensor " + (sensor?.Name ?? "not reporting now") + " (" + certainty + ")";
    }

    private (string Text, bool Warning) CalibrationText() {
      FanTuningResult? result = host.GetTuningResult(control);
      if (result != null && result.Outcome != FanTuningOutcome.Completed)
        return (result.Message, true);
      if (!host.CanCalibrate(control))
        return ("The graphics driver sets this fan's minimum speed", false);
      FanCalibration? calibration = host.GetCalibration(control);
      if (calibration == null)
        return ("Minimum speed not calibrated yet", false);
      if (!calibration.Stops)
        return (string.Format(CultureInfo.CurrentCulture,
          "Keeps turning at every speed, down to {0:0} %", calibration.MinRunningDuty), false);
      return (string.Format(CultureInfo.CurrentCulture,
        "Keeps turning down to {0:0} %, starts at {1:0} %",
        calibration.MinRunningDuty, calibration.StartDuty), false);
    }

    protected override void OnLayout(LayoutEventArgs e) {
      base.OnLayout(e);
      float pad = Pad;
      Size modeSize = mode.GetPreferredSize(Size.Empty);
      mode.Bounds = new Rectangle((int)pad, (int)Math.Round(pad + S(62)), modeSize.Width, modeSize.Height);
      int rowY = RowY;
      slider.Bounds = new Rectangle((int)(pad - S(10)), rowY,
        (int)(Width - 2 * pad - S(64)), (int)S(28));
      editor.Bounds = new Rectangle((int)pad, rowY, (int)(Width - 2 * pad), editor.PreferredHeight);
      Size detectSize = detect.GetPreferredSize(Size.Empty);
      detect.Bounds = new Rectangle((int)(Width - pad - detectSize.Width),
        InfoY + (InfoHeight - detectSize.Height) / 2, detectSize.Width, detectSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      using (GraphicsPath path = Theme.RoundedRect(card, S(14)))
      using (SolidBrush surface = new SolidBrush(theme.Surface))
      using (Pen border = new Pen(theme.Border, Math.Max(1f, S(1)))) {
        g.FillPath(surface, path);
        g.DrawPath(border, path);
      }

      float pad = Pad;
      Image icon = ModernIcons.ForSensorType(SensorType.Fan);
      float iconSize = S(32);
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.DrawImage(icon, pad, pad + S(2), iconSize, iconSize);

      float textX = pad + iconSize + S(12);
      float rightWidth = S(150);
      int textWidth = (int)(Width - textX - pad - rightWidth);
      TextRenderer.DrawText(g, control.Name, titleFont!,
        new Rectangle((int)textX, (int)pad, textWidth, titleFont!.Height),
        theme.Text, Flags | TextFormatFlags.EndEllipsis);
      TextRenderer.DrawText(g, control.Hardware.Name, subtitleFont!,
        new Rectangle((int)textX, (int)pad + titleFont.Height, textWidth, subtitleFont!.Height),
        theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);

      // Live readings, right-aligned.
      ISensor? speed = FindSpeedSensor();
      string rpm = speed?.Value != null
        ? speed.Value.Value.ToString("N0", CultureInfo.CurrentCulture) + " RPM" : "–";
      string output = control.Value.HasValue
        ? "Output " + control.Value.Value.ToString("0", CultureInfo.CurrentCulture) + " %"
        : "Output managed by hardware";
      Rectangle right = new Rectangle((int)(Width - pad - rightWidth), (int)(pad - S(4)),
        (int)rightWidth, bigFont!.Height);
      TextRenderer.DrawText(g, rpm, bigFont, right, theme.Text,
        Flags | TextFormatFlags.Right);
      TextRenderer.DrawText(g, output, labelFont!,
        new Rectangle(right.X - (int)S(40), right.Bottom, right.Width + (int)S(40), labelFont!.Height),
        theme.TextSecondary, Flags | TextFormatFlags.Right);

      // The row below the mode selector explains or adjusts the choice.
      int rowY = RowY;
      Rectangle rowText = new Rectangle((int)pad, rowY + (int)S(6), (int)(Width - 2 * pad), labelFont.Height);
      if (notice != null) {
        TextRenderer.DrawText(g, notice, labelFont, rowText, theme.Warm, Flags | TextFormatFlags.EndEllipsis);
      } else if (shownMode == FanMode.Automatic) {
        TextRenderer.DrawText(g, control.Hardware.HardwareType == HardwareType.GpuNvidia ||
          control.Hardware.HardwareType == HardwareType.GpuAti
            ? "The graphics driver controls this fan."
            : "The motherboard controls this fan using its own settings.",
          labelFont, rowText, theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
      } else if (shownMode == FanMode.Fixed) {
        TextRenderer.DrawText(g, slider.Value.ToString("0", CultureInfo.CurrentCulture) + " %",
          valueFont!, new Rectangle((int)(Width - pad - S(56)), rowY + (int)S(5), (int)S(56), valueFont!.Height),
          theme.Text, Flags | TextFormatFlags.Right);
      }

      PaintDetection(g);
    }

    private void PaintDetection(Graphics g) {
      float pad = Pad;
      int infoY = InfoY;
      using (Pen divider = new Pen(theme.Border, Math.Max(1f, S(1))))
        g.DrawLine(divider, pad, infoY - S(10), Width - pad, infoY - S(10));

      int width = Math.Max(0, detect.Left - (int)pad - (int)S(12));
      Rectangle first = new Rectangle((int)pad, infoY + (int)S(2), width, strongFont!.Height);
      Rectangle second = new Rectangle((int)pad, first.Bottom + (int)S(3), width, labelFont!.Height);

      FanTuningProgress? current = progress;
      if (tuningState == FanTuningState.Active && current != null && current.IsRunning) {
        TextRenderer.DrawText(g, current.Description, strongFont, first, theme.Text,
          Flags | TextFormatFlags.EndEllipsis);
        List<string> details = new List<string>();
        if (current.Duty.HasValue)
          details.Add("Duty " + current.Duty.Value.ToString("0", CultureInfo.CurrentCulture) + " %");
        if (current.Rpm.HasValue)
          details.Add(current.Rpm.Value.ToString("N0", CultureInfo.CurrentCulture) + " RPM");
        if (current.FanCount > 1)
          details.Add(string.Format(CultureInfo.CurrentCulture, "Fan {0} of {1}",
            current.FanIndex + 1, current.FanCount));
        TextRenderer.DrawText(g, string.Join("  ·  ", details), labelFont, second,
          theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);

        RectangleF track = new RectangleF(pad, infoY + InfoHeight - S(2), width, S(3));
        using (GraphicsPath path = Theme.RoundedRect(track, track.Height / 2))
        using (SolidBrush brush = new SolidBrush(theme.SurfaceSunken))
          g.FillPath(brush, path);
        RectangleF filled = new RectangleF(track.X, track.Y,
          Math.Max(track.Height, track.Width * current.Fraction), track.Height);
        using (GraphicsPath path = Theme.RoundedRect(filled, track.Height / 2))
        using (SolidBrush brush = new SolidBrush(theme.Accent))
          g.FillPath(brush, path);
        return;
      }

      if (tuningState == FanTuningState.Waiting) {
        TextRenderer.DrawText(g, "Waiting for its turn", strongFont, first, theme.Text,
          Flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "Another fan is being detected", labelFont, second,
          theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
        return;
      }

      TextRenderer.DrawText(g, PairingText(), strongFont, first, theme.Text,
        Flags | TextFormatFlags.EndEllipsis);
      (string text, bool warning) = CalibrationText();
      TextRenderer.DrawText(g, text, labelFont, second,
        warning ? Theme.Blend(theme.Warm, theme.Text, theme.IsDark ? 0.2f : 0.25f) : theme.TextSecondary,
        Flags | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        DisposeFonts();
      base.Dispose(disposing);
    }
  }

  /// <summary>Title, explanation and an optional note at the top of a page.</summary>
  public sealed class PageHeader : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private Theme theme = Theme.Current;
    private int fontDpi;
    private Font? titleFont, subtitleFont, noteFont;

    public PageHeader() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    private string? title, subtitle, note, emptyMessage;

    public string Title {
      get { return title ?? ""; }
      set { Update(ref title, value); }
    }

    public string Subtitle {
      get { return subtitle ?? ""; }
      set { Update(ref subtitle, value); }
    }

    public string? Note {
      get { return note; }
      set { Update(ref note, value); }
    }

    public string? EmptyMessage {
      get { return emptyMessage; }
      set { Update(ref emptyMessage, value); }
    }

    private void Update(ref string? field, string? value) {
      if (field == value)
        return;
      field = value;
      UpdateHeight();
      Invalidate();
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        Invalidate();
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      foreach (Font? font in new[] { titleFont, subtitleFont, noteFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.DisplayFamily, 17f, FontStyle.Regular, DeviceDpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
      noteFont = Theme.CreateFont(Theme.SemiboldFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    // The height follows the text, so it is kept current whenever the text or
    // the width changes rather than being corrected during painting.
    private void UpdateHeight() {
      int height = MeasureHeight();
      if (Height != height)
        Height = height;
    }

    protected override void OnSizeChanged(EventArgs e) {
      base.OnSizeChanged(e);
      UpdateHeight();
    }

    private int MeasureHeight() {
      EnsureFonts();
      int width = Math.Max(100, Width - (int)S(48));
      float y = S(22) + titleFont!.Height + S(6);
      y += TextRenderer.MeasureText(Subtitle, subtitleFont!, new Size(width, int.MaxValue),
        Flags | TextFormatFlags.WordBreak).Height;
      if (!string.IsNullOrEmpty(Note))
        y += S(12) + noteFont!.Height + S(10);
      if (!string.IsNullOrEmpty(EmptyMessage))
        y += S(24) + TextRenderer.MeasureText(EmptyMessage, subtitleFont!,
          new Size(width, int.MaxValue), Flags | TextFormatFlags.WordBreak).Height;
      return (int)Math.Ceiling(y + S(14));
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      int margin = (int)S(24);
      int width = Math.Max(100, Width - 2 * margin);
      float y = S(22);
      TextRenderer.DrawText(g, Title, titleFont!, new Point(margin, (int)y), theme.Text,
        Flags | TextFormatFlags.SingleLine);
      y += titleFont!.Height + S(6);
      Size subtitle = TextRenderer.MeasureText(Subtitle, subtitleFont!, new Size(width, int.MaxValue),
        Flags | TextFormatFlags.WordBreak);
      TextRenderer.DrawText(g, Subtitle, subtitleFont!, new Rectangle(margin, (int)y, width, subtitle.Height),
        theme.TextSecondary, Flags | TextFormatFlags.WordBreak);
      y += subtitle.Height;

      if (!string.IsNullOrEmpty(Note)) {
        y += S(12);
        Size text = TextRenderer.MeasureText(Note, noteFont!, Size.Empty, Flags | TextFormatFlags.SingleLine);
        RectangleF chip = new RectangleF(margin, y, S(26) + text.Width + S(12), noteFont!.Height + S(10));
        chip.Width = Math.Min(chip.Width, width);
        using (GraphicsPath path = Theme.RoundedRect(chip, chip.Height / 2))
        using (SolidBrush brush = new SolidBrush(Theme.Blend(theme.Background, theme.Warm,
          theme.IsDark ? 0.18f : 0.12f)))
          g.FillPath(brush, path);
        float dot = S(7);
        using (SolidBrush brush = new SolidBrush(theme.Warm))
          g.FillEllipse(brush, chip.Left + S(11), chip.Top + (chip.Height - dot) / 2, dot, dot);
        TextRenderer.DrawText(g, Note, noteFont!,
          new Rectangle((int)(chip.Left + S(24)), (int)(chip.Top + S(5)), (int)(chip.Width - S(30)), noteFont!.Height),
          Theme.Blend(theme.Warm, theme.Text, theme.IsDark ? 0.35f : 0.25f),
          Flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        y += chip.Height;
      }

      if (!string.IsNullOrEmpty(EmptyMessage)) {
        y += S(24);
        TextRenderer.DrawText(g, EmptyMessage, subtitleFont!,
          new Rectangle(margin, (int)y, width, (int)S(200)), theme.TextSecondary,
          Flags | TextFormatFlags.WordBreak);
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        foreach (Font? font in new[] { titleFont, subtitleFont, noteFont })
          font?.Dispose();
      base.Dispose(disposing);
    }
  }

  internal sealed class BufferedScrollPanel : Panel {
    public BufferedScrollPanel() {
      DoubleBuffered = true;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
  }
}
