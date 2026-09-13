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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.LowLevel;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// Guides the user to full sensor access: install PawnIO, download its
  /// modules, restart as administrator. One button always does the next
  /// thing. The restart itself belongs to the main window, so the dialog only
  /// reports <see cref="RestartRequested"/>.
  /// </summary>
  internal sealed class PawnIoSetupDialog : Form {

    private static readonly PawnIoSetupStep[] Steps = {
      PawnIoSetupStep.InstallDriver, PawnIoSetupStep.InstallModules,
      PawnIoSetupStep.RestartAsAdministrator
    };

    private readonly Theme theme;
    private readonly PawnIoSetupRunner runner;
    private readonly Func<PawnIoSetupStatus> captureStatus;
    private readonly PageHeader header;
    private readonly SetupStepView[] stepViews;
    private readonly SetupNoteView note;
    private readonly ModernButton actionButton;
    private readonly ModernButton closeButton;
    private readonly System.Windows.Forms.Timer spinner;

    private PawnIoSetupStatus status;
    private CancellationTokenSource? cancellation;
    private bool busy, closeRequested;
    private PawnIoSetupStep workingStep;
    private PawnIoSetupStep? failedStep;
    private string? error, progressText;

    public PawnIoSetupDialog(Theme theme)
      : this(theme, new PawnIoSetupRunner(), PawnIoSetupStatus.Capture) {
    }

    public PawnIoSetupDialog(Theme theme, PawnIoSetupRunner runner,
      Func<PawnIoSetupStatus> captureStatus) {
      this.theme = theme;
      this.runner = runner;
      this.captureStatus = captureStatus;
      status = captureStatus();

      Text = "Full sensor access";
      FormBorderStyle = FormBorderStyle.FixedDialog;
      MaximizeBox = false;
      MinimizeBox = false;
      ShowIcon = false;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.CenterParent;
      AutoScaleMode = AutoScaleMode.None;
      BackColor = theme.Background;
      ForeColor = theme.Text;

      header = new PageHeader {
        Theme = theme,
        Title = "Set up full sensor access",
        Subtitle = "Three steps unlock processor temperatures and power, and " +
          "motherboard fans, voltages and temperatures. Everything else already works."
      };
      stepViews = new[] {
        new SetupStepView(theme, 1, "Install the PawnIO driver",
          "PawnIO is a small, signed kernel driver that gives limited access to " +
          "sensor chips. It is installed with winget, and Windows asks for your permission."),
        new SetupStepView(theme, 2, "Download the sensor modules",
          "Signed modules from the PawnIO.Modules project on GitHub, saved in your " +
          "user folder. PawnIO checks each module's signature before using it."),
        new SetupStepView(theme, 3, "Restart as administrator",
          "PawnIO only works for apps running as administrator. Open Hardware " +
          "Monitor restarts, and Windows asks for your permission.")
      };
      note = new SetupNoteView(theme);
      actionButton = new ModernButton { Theme = theme, Kind = ButtonKind.Primary };
      closeButton = new ModernButton { Theme = theme, Kind = ButtonKind.Secondary, Text = "Not now" };
      actionButton.Click += delegate { OnAction(); };
      closeButton.Click += delegate { Close(); };

      spinner = new System.Windows.Forms.Timer { Interval = 40 };
      spinner.Tick += delegate {
        foreach (SetupStepView view in stepViews)
          if (view.State == SetupStepState.Working)
            view.AdvanceSpinner();
      };

      Controls.Add(header);
      Controls.AddRange(stepViews);
      Controls.Add(note);
      Controls.Add(closeButton);
      Controls.Add(actionButton);
      RefreshView();
    }

    /// <summary>True when the user chose to restart as administrator.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>A one-sentence state of the setup, for the settings page.</summary>
    internal static string Summarize(PawnIoSetupStatus status) {
      if (status.IsComplete)
        return "Done. PawnIO " + status.DriverVersion + " and its modules are in use.";
      List<string> steps = new List<string>();
      foreach (PawnIoSetupStep step in status.RemainingSteps) {
        switch (step) {
          case PawnIoSetupStep.InstallDriver: steps.Add("install PawnIO"); break;
          case PawnIoSetupStep.InstallModules: steps.Add("download its modules"); break;
          default: steps.Add(status.IsElevated ? "restart" : "restart as administrator"); break;
        }
      }
      string list = steps.Count == 1 ? steps[0] :
        string.Join(", ", steps.GetRange(0, steps.Count - 1)) + " and " + steps[steps.Count - 1];
      return (steps.Count == 1 ? "One step left: " : steps.Count + " steps left: ") + list +
        ". A guided setup does this for you.";
    }

    private int S(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      theme.ApplyWindowChrome(this);
      LayoutContent();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) {
      base.OnDpiChanged(e);
      LayoutContent();
    }

    // ---- state ------------------------------------------------------------------

    private void RefreshView() {
      if (!busy)
        status = captureStatus();

      bool installPending = !status.IsDone(PawnIoSetupStep.InstallDriver) ||
        !status.IsDone(PawnIoSetupStep.InstallModules);
      for (int i = 0; i < Steps.Length; i++) {
        PawnIoSetupStep step = Steps[i];
        SetupStepView view = stepViews[i];
        if (busy && step == workingStep) {
          view.State = SetupStepState.Working;
          view.Detail = progressText ?? "Working…";
        } else if (!busy && failedStep == step && error != null) {
          view.State = SetupStepState.Failed;
          view.Detail = error;
        } else if (status.IsDone(step)) {
          view.State = SetupStepState.Done;
          view.Detail = DoneDetail(step);
        } else {
          view.State = SetupStepState.Pending;
          view.Detail = PendingDetail(step);
        }
      }
      spinner.Enabled = busy;

      if (busy) {
        note.Text = "You can keep using Open Hardware Monitor while this runs.";
        note.Warning = false;
        actionButton.Text = "Working…";
        actionButton.Enabled = false;
        closeButton.Text = "Cancel";
        closeButton.Visible = true;
      } else if (status.IsComplete) {
        note.Text = null;
        actionButton.Text = "Done";
        actionButton.Enabled = true;
        closeButton.Visible = false;
      } else if (installPending) {
        // The consent the winget command gives on the user's behalf is stated
        // before the button that gives it.
        note.Text = !status.DriverInstalled
          ? "Install adds PawnIO, a signed kernel driver, and accepts the winget " +
            "source and package agreements on your behalf."
          : "Install downloads the modules from github.com.";
        note.Warning = !status.DriverInstalled;
        actionButton.Text = failedStep != null ? "Try again" : "Install";
        actionButton.Enabled = true;
        closeButton.Text = "Not now";
        closeButton.Visible = true;
      } else {
        note.Text = null;
        actionButton.Text = status.IsElevated ? "Restart" : "Restart as administrator";
        actionButton.Enabled = true;
        closeButton.Text = "Not now";
        closeButton.Visible = true;
      }
      LayoutContent();
    }

    private string? DoneDetail(PawnIoSetupStep step) {
      switch (step) {
        case PawnIoSetupStep.InstallDriver:
          return "Installed, version " + status.DriverVersion + ".";
        case PawnIoSetupStep.InstallModules:
          return string.Join(" and ", status.RequiredModules) + " are ready.";
        default:
          return "Running as administrator with full sensor access.";
      }
    }

    private string? PendingDetail(PawnIoSetupStep step) {
      switch (step) {
        case PawnIoSetupStep.InstallModules:
          return status.MissingModules.Count < status.RequiredModules.Count
            ? "Missing: " + string.Join(", ", status.MissingModules) + "." : null;
        case PawnIoSetupStep.RestartAsAdministrator:
          if (!status.IsElevated || !status.DriverInstalled || !status.ModulesInstalled)
            return null;
          // Everything is in place and elevated, yet not loaded: either it was
          // installed during this session or PawnIO refused something.
          return HardwareAccess.Tier == AccessTier.Deep && HardwareAccess.UnavailableReason != null
            ? HardwareAccess.UnavailableReason
            : "Restart once so Open Hardware Monitor can load PawnIO.";
        default:
          return null;
      }
    }

    private async void OnAction() {
      if (busy)
        return;
      if (status.IsComplete) {
        DialogResult = DialogResult.OK;
        return;
      }
      if (!status.IsDone(PawnIoSetupStep.InstallDriver) ||
        !status.IsDone(PawnIoSetupStep.InstallModules)) {
        await RunInstallAsync();
        return;
      }
      RestartRequested = true;
      DialogResult = DialogResult.OK;
    }

    private async Task RunInstallAsync() {
      busy = true;
      failedStep = null;
      error = null;
      progressText = null;
      workingStep = status.DriverInstalled ? PawnIoSetupStep.InstallModules : PawnIoSetupStep.InstallDriver;
      cancellation = new CancellationTokenSource();
      RefreshView();

      // Progress<T> captures the UI thread, so reports arrive there.
      Progress<PawnIoSetupProgress> progress = new Progress<PawnIoSetupProgress>(report => {
        if (!busy || IsDisposed)
          return;
        workingStep = report.Step;
        progressText = report.Message;
        RefreshView();
      });

      try {
        PawnIoSetupRunResult result = await runner.RunAsync(progress, cancellation.Token);
        failedStep = result.FailedStep;
        error = result.Error;
      } catch (OperationCanceledException) {
        failedStep = workingStep;
        error = workingStep == PawnIoSetupStep.InstallDriver
          ? "Stopped waiting for winget. PawnIO may still finish installing; choose Try again to check."
          : "The download was cancelled.";
      } catch (Exception ex) {
        failedStep = workingStep;
        error = "Something went wrong: " + ex.Message + " Try again, or follow the " +
          "manual steps in Settings › Hardware access.";
      } finally {
        busy = false;
        progressText = null;
        cancellation.Dispose();
        cancellation = null;
      }

      if (IsDisposed)
        return;
      if (closeRequested) {
        Close();
        return;
      }
      RefreshView();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
      if (busy) {
        // Close once the running step has stopped, so nothing is left writing
        // files behind a closed window.
        e.Cancel = true;
        closeRequested = true;
        progressText = "Stopping…";
        cancellation?.Cancel();
        RefreshView();
      }
      base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
      if (keyData == Keys.Escape) {
        Close();
        return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- layout -------------------------------------------------------------------

    private void LayoutContent() {
      int pad = S(24);
      int width = S(560);
      int inner = width - 2 * pad;
      SuspendLayout();

      header.Bounds = new Rectangle(0, 0, width, header.Height);
      int y = header.Height;
      foreach (SetupStepView view in stepViews) {
        int height = view.MeasureHeight(inner);
        view.Bounds = new Rectangle(pad, y, inner, height);
        y += height + S(10);
      }

      note.Visible = !string.IsNullOrEmpty(note.Text);
      if (note.Visible) {
        int height = note.MeasureHeight(inner);
        note.Bounds = new Rectangle(pad, y + S(2), inner, height);
        y += height + S(2);
      }

      y += S(14);
      Size action = actionButton.GetPreferredSize(Size.Empty);
      Size close = closeButton.GetPreferredSize(Size.Empty);
      actionButton.Bounds = new Rectangle(width - pad - action.Width, y, action.Width, action.Height);
      closeButton.Bounds = new Rectangle(actionButton.Left - S(8) - close.Width, y,
        close.Width, close.Height);
      y += action.Height + pad;

      Size client = new Size(width, y);
      if (ClientSize != client)
        ClientSize = client;
      ResumeLayout(false);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        spinner.Dispose();
        cancellation?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  internal enum SetupStepState {
    Pending,
    Working,
    Done,
    Failed
  }

  /// <summary>One numbered step on a card, with its state and a detail line.</summary>
  internal sealed class SetupStepView : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

    private readonly Theme theme;
    private readonly int number;
    private readonly string title;
    private readonly string body;
    private SetupStepState state;
    private string? detail;
    private float spinnerAngle;
    private int fontDpi;
    private Font? titleFont, bodyFont;

    public SetupStepView(Theme theme, int number, string title, string body) {
      this.theme = theme;
      this.number = number;
      this.title = title;
      this.body = body;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      BackColor = theme.Background;
      AccessibleRole = AccessibleRole.StaticText;
      AccessibleName = "Step " + number + ": " + title;
    }

    public SetupStepState State {
      get { return state; }
      set {
        if (state == value)
          return;
        state = value;
        UpdateAccessibility();
        Invalidate();
      }
    }

    public string? Detail {
      get { return detail; }
      set {
        if (detail == value)
          return;
        detail = value;
        UpdateAccessibility();
        Invalidate();
      }
    }

    public void AdvanceSpinner() {
      spinnerAngle = (spinnerAngle + 18) % 360;
      Invalidate();
    }

    private void UpdateAccessibility() {
      string text = state == SetupStepState.Done ? "Done" :
        state == SetupStepState.Working ? "In progress" :
        state == SetupStepState.Failed ? "Failed" : "Not done";
      AccessibleDescription = string.IsNullOrEmpty(detail) ? text : text + ". " + detail;
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      titleFont?.Dispose();
      bodyFont?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 10.5f, FontStyle.Regular, DeviceDpi);
      bodyFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
    }

    private int TextLeft {
      get { return (int)(S(18) + S(26) + S(14)); }
    }

    private int TextWidth(int width) {
      return Math.Max(80, width - TextLeft - (int)S(18));
    }

    private int Measure(string text, int width) {
      return TextRenderer.MeasureText(text, bodyFont!, new Size(width, int.MaxValue), Flags).Height;
    }

    public int MeasureHeight(int width) {
      EnsureFonts();
      int textWidth = TextWidth(width);
      int height = (int)S(16) + titleFont!.Height + (int)S(3) + Measure(body, textWidth);
      if (!string.IsNullOrEmpty(detail))
        height += (int)S(8) + Measure(detail, textWidth);
      return height + (int)S(16);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;

      RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      Color border = state == SetupStepState.Working ? theme.Accent
        : state == SetupStepState.Failed ? theme.Hot : theme.Border;
      using (GraphicsPath path = Theme.RoundedRect(card, S(12)))
      using (SolidBrush surface = new SolidBrush(theme.Surface))
      using (Pen pen = new Pen(border, Math.Max(1f, S(1)))) {
        g.FillPath(surface, path);
        g.DrawPath(pen, path);
      }

      float top = S(16);
      DrawIndicator(g, S(18) + S(13), top + titleFont!.Height / 2f);

      int textLeft = TextLeft;
      int textWidth = TextWidth(Width);
      TextRenderer.DrawText(g, title, titleFont, new Point(textLeft, (int)top), theme.Text,
        Flags & ~TextFormatFlags.WordBreak);
      int y = (int)top + titleFont.Height + (int)S(3);
      int bodyHeight = Measure(body, textWidth);
      TextRenderer.DrawText(g, body, bodyFont!, new Rectangle(textLeft, y, textWidth, bodyHeight),
        theme.TextSecondary, Flags);
      y += bodyHeight;

      if (!string.IsNullOrEmpty(detail)) {
        y += (int)S(8);
        Color color = state == SetupStepState.Failed ? theme.Hot
          : state == SetupStepState.Working ? theme.Accent
          : state == SetupStepState.Done ? theme.Good : theme.TextSecondary;
        TextRenderer.DrawText(g, detail, bodyFont!,
          new Rectangle(textLeft, y, textWidth, Measure(detail, textWidth)), color, Flags);
      }
    }

    private void DrawIndicator(Graphics g, float cx, float cy) {
      float r = S(13);
      RectangleF circle = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
      switch (state) {
        case SetupStepState.Done:
          using (SolidBrush brush = new SolidBrush(theme.Good))
            g.FillEllipse(brush, circle);
          using (Pen pen = new Pen(Color.White, S(2.2f)) {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
          })
            g.DrawLines(pen, new[] {
              new PointF(cx - S(5.5f), cy + S(0.5f)),
              new PointF(cx - S(1.5f), cy + S(4.5f)),
              new PointF(cx + S(6), cy - S(4.5f))
            });
          break;
        case SetupStepState.Failed:
          using (SolidBrush brush = new SolidBrush(theme.Hot))
            g.FillEllipse(brush, circle);
          using (Pen pen = new Pen(Color.White, S(2.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, cx, cy - S(6), cx, cy + S(1.5f));
          using (SolidBrush dot = new SolidBrush(Color.White))
            g.FillEllipse(dot, cx - S(1.5f), cy + S(4), S(3), S(3));
          break;
        case SetupStepState.Working:
          RectangleF ring = RectangleF.Inflate(circle, -S(1.5f), -S(1.5f));
          using (Pen track = new Pen(theme.SurfaceSunken, S(3)))
            g.DrawEllipse(track, ring);
          using (Pen arc = new Pen(theme.Accent, S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, ring, spinnerAngle, 100);
          break;
        default:
          using (Pen pen = new Pen(theme.TextTertiary, Math.Max(1f, S(1.5f))))
            g.DrawEllipse(pen, RectangleF.Inflate(circle, -S(0.75f), -S(0.75f)));
          TextRenderer.DrawText(g, number.ToString(System.Globalization.CultureInfo.CurrentCulture),
            titleFont!, Rectangle.Round(circle), theme.TextSecondary,
            TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
          break;
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        titleFont?.Dispose();
        bodyFont?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  /// <summary>A short wrapped note under the steps, plain or as a caution.</summary>
  internal sealed class SetupNoteView : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

    private readonly Theme theme;
    private int fontDpi;
    private Font? font;
    private bool warning;

    public SetupNoteView(Theme theme) {
      this.theme = theme;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      BackColor = theme.Background;
      AccessibleRole = AccessibleRole.StaticText;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text {
      get { return base.Text; }
      set {
        base.Text = value;
        AccessibleName = value;
        Invalidate();
      }
    }

    public bool Warning {
      get { return warning; }
      set {
        warning = value;
        Invalidate();
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void EnsureFont() {
      if (fontDpi == DeviceDpi && font != null)
        return;
      font?.Dispose();
      fontDpi = DeviceDpi;
      font = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
    }

    public int MeasureHeight(int width) {
      EnsureFont();
      return TextRenderer.MeasureText(Text, font!,
        new Size(Math.Max(80, width - (int)S(18)), int.MaxValue), Flags).Height + (int)S(2);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFont();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      float dot = S(7);
      using (SolidBrush brush = new SolidBrush(warning ? theme.Warm : theme.TextTertiary))
        g.FillEllipse(brush, S(2), font!.Height / 2f - dot / 2, dot, dot);
      TextRenderer.DrawText(g, Text, font,
        new Rectangle((int)S(18), 0, Math.Max(80, Width - (int)S(18)), Height),
        warning ? Theme.Blend(theme.Warm, theme.Text, theme.IsDark ? 0.35f : 0.25f) : theme.TextSecondary,
        Flags);
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        font?.Dispose();
      base.Dispose(disposing);
    }
  }
}
