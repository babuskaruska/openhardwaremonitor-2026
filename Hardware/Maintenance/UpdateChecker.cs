/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>A published release, as far as the update check needs to know.</summary>
  public sealed class ReleaseInfo {

    public ReleaseInfo(string tag, SemanticVersion version, string name, string pageUrl) {
      Tag = tag ?? throw new ArgumentNullException(nameof(tag));
      Version = version ?? throw new ArgumentNullException(nameof(version));
      Name = name ?? "";
      PageUrl = pageUrl ?? throw new ArgumentNullException(nameof(pageUrl));
    }

    /// <summary>The Git tag, for example "v0.11.0".</summary>
    public string Tag { get; }
    public SemanticVersion Version { get; }

    /// <summary>The release title; the tag when the release has none.</summary>
    public string Name { get; }

    /// <summary>The release page on github.com. Always an https github.com address of this repository.</summary>
    public string PageUrl { get; }
  }

  public enum UpdateCheckOutcome {
    /// <summary>A newer release exists and has not been skipped.</summary>
    UpdateAvailable,
    /// <summary>A newer release exists, but the user skipped that version.</summary>
    UpdateSkipped,
    UpToDate,
    /// <summary>No request was made: the last check is too recent.</summary>
    NotDue,
    /// <summary>No request was made: automatic checks are turned off.</summary>
    Disabled,
    Failed,
    /// <summary>The caller cancelled, for example because the application is closing.</summary>
    Cancelled
  }

  public sealed class UpdateCheckResult {

    internal UpdateCheckResult(UpdateCheckOutcome outcome, ReleaseInfo? latest,
      string? error) {
      Outcome = outcome;
      Latest = latest;
      Error = error;
    }

    public UpdateCheckOutcome Outcome { get; }

    /// <summary>The newest release known, from this check or an earlier one.</summary>
    public ReleaseInfo? Latest { get; }

    /// <summary>Why the last check failed, in a sentence; null after a successful check.</summary>
    public string? Error { get; }
  }

  /// <summary>
  /// Asks GitHub whether a newer release exists. It only ever reads the
  /// latest release's tag, name and page address: nothing is downloaded or
  /// installed, and the user decides whether to open the release page.
  ///
  /// Requests are rare by design. Automatic checks happen at most once per
  /// <see cref="CheckInterval"/>, and a failed check counts, so a PC without
  /// internet access or a GitHub outage never causes repeated requests. The
  /// time of the last check, the newest release seen and a skipped version
  /// are kept in the settings, so a restart neither checks again early nor
  /// forgets an available update. "Check now" ignores the interval but not a
  /// short <see cref="ManualCheckSpacing"/>, and only one request runs at a time.
  ///
  /// Version comparison (<see cref="IsNewer"/>): a release is offered when its
  /// semantic version is higher than the running one. The exception is a
  /// "-dev" build, which is what every build that is not made from a release
  /// tag reports (see Directory.Build.props). A "0.10.0-dev" build is usually
  /// newer code than the 0.10.0 release, so development builds compare only
  /// major, minor and patch: 0.10.0-dev is not offered 0.10.0, but is offered
  /// 0.10.1. Other pre-releases follow SemVer: 0.11.0-beta.1 is offered 0.11.0.
  ///
  /// The HTTP handler is injectable so tests run without a network.
  /// </summary>
  public sealed class UpdateChecker {

    public const string Repository = "babuskaruska/openhardwaremonitor-2026";
    public const string LatestReleaseApiUrl =
      "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/" + Repository + "/releases";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan ManualCheckSpacing = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    // A release JSON is a few kilobytes, more with long release notes.
    internal const int MaxResponseBytes = 1024 * 1024;

    internal const string AutomaticKey = "updates.automatic";
    internal const string LastCheckKey = "updates.lastCheck";
    internal const string SkippedVersionKey = "updates.skippedVersion";
    internal const string LatestTagKey = "updates.latestTag";
    internal const string LatestNameKey = "updates.latestName";
    internal const string LatestUrlKey = "updates.latestUrl";

    private readonly ISettings settings;
    private readonly HttpMessageHandler? handler;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new object();
    private Task<UpdateCheckResult>? running;
    private DateTimeOffset? lastManualCheck;
    private ReleaseInfo? latest;
    private string? lastError;

    /// <param name="runningVersion">The application's informational version,
    /// for example "0.10.0" or "0.10.0-dev+852d066".</param>
    /// <param name="handler">Sends the request; null uses a new
    /// <see cref="SocketsHttpHandler"/> per check. An injected handler is not
    /// disposed.</param>
    public UpdateChecker(ISettings settings, string? runningVersion,
      HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null) {
      this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
      this.handler = handler;
      this.clock = clock ?? (() => DateTimeOffset.UtcNow);
      SemanticVersion.TryParse(runningVersion, out SemanticVersion? parsed);
      RunningVersion = parsed;
      UserAgent = "OpenHardwareMonitor/" + (parsed?.ToString() ?? "unknown") +
        " (+https://github.com/" + Repository + ")";
      latest = LoadCachedRelease();
    }

    /// <summary>Null when the running version could not be parsed; no update is then ever offered.</summary>
    public SemanticVersion? RunningVersion { get; }

    public string UserAgent { get; }

    /// <summary>The whole check, including reading the answer, must finish within this.</summary>
    public TimeSpan RequestTimeout { get; internal set; } = DefaultTimeout;

    /// <summary>
    /// Raised when a check starts or finishes and when a setting here changes.
    /// Raised on the thread that made the change, which for a check is a
    /// thread-pool thread.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>Whether <see cref="CheckIfDueAsync"/> may make requests. On by default.</summary>
    public bool AutomaticChecks {
      get { return settings.GetValue(AutomaticKey, "true") != "false"; }
      set {
        if (value == AutomaticChecks)
          return;
        settings.SetValue(AutomaticKey, value ? "true" : "false");
        OnStateChanged();
      }
    }

    /// <summary>When the last check was made, successful or not.</summary>
    public DateTimeOffset? LastCheck {
      get {
        string text = settings.GetValue(LastCheckKey, "");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
          DateTimeStyles.RoundtripKind, out DateTimeOffset value)
          ? value : (DateTimeOffset?)null;
      }
    }

    /// <summary>The version the user chose to skip, or null.</summary>
    public SemanticVersion? SkippedVersion {
      get {
        SemanticVersion.TryParse(settings.GetValue(SkippedVersionKey, ""),
          out SemanticVersion? version);
        return version;
      }
    }

    public bool IsChecking {
      get {
        lock (sync)
          return running != null;
      }
    }

    /// <summary>Why the last check in this session failed; null if it succeeded or none was made.</summary>
    public string? LastError {
      get {
        lock (sync)
          return lastError;
      }
    }

    /// <summary>The newest release seen by any check, possibly in an earlier session.</summary>
    public ReleaseInfo? LatestRelease {
      get {
        lock (sync)
          return latest;
      }
    }

    /// <summary>The newest release when it is newer than this version and not skipped.</summary>
    public ReleaseInfo? AvailableUpdate {
      get {
        lock (sync)
          return Classify(latest) == UpdateCheckOutcome.UpdateAvailable ? latest : null;
      }
    }

    /// <summary>True when the last check is at least <see cref="CheckInterval"/> ago, or there was none.</summary>
    public bool IsCheckDue {
      get {
        DateTimeOffset? last = LastCheck;
        if (!last.HasValue)
          return true;
        DateTimeOffset now = clock();
        // A check "in the future" means the clock was changed; do not let
        // that postpone checks for however long the difference is.
        if (last.Value > now + TimeSpan.FromMinutes(5))
          return true;
        return now - last.Value >= CheckInterval;
      }
    }

    /// <summary>
    /// True when <paramref name="latest"/> should be offered to someone
    /// running <paramref name="running"/>. See the class remarks for -dev builds.
    /// </summary>
    public static bool IsNewer(SemanticVersion latest, SemanticVersion? running) {
      if (latest == null || running == null)
        return false;
      if (running.IsDevelopmentBuild)
        return latest.Core.CompareTo(running.Core) > 0;
      return latest.CompareTo(running) > 0;
    }

    /// <summary>The automatic check: makes a request only when enabled and due. Never throws.</summary>
    public Task<UpdateCheckResult> CheckIfDueAsync(
      CancellationToken cancellationToken = default) {
      lock (sync) {
        if (running != null)
          return running;
        if (!AutomaticChecks)
          return Task.FromResult(CurrentResult(UpdateCheckOutcome.Disabled));
        if (!IsCheckDue)
          return Task.FromResult(CurrentResult(UpdateCheckOutcome.NotDue));
        return Start(cancellationToken);
      }
    }

    /// <summary>"Check now": ignores the daily interval and the automatic setting. Never throws.</summary>
    public Task<UpdateCheckResult> CheckNowAsync(
      CancellationToken cancellationToken = default) {
      lock (sync) {
        if (running != null)
          return running;
        DateTimeOffset now = clock();
        if (lastManualCheck.HasValue && now >= lastManualCheck.Value &&
          now - lastManualCheck.Value < ManualCheckSpacing)
          return Task.FromResult(CurrentResult(UpdateCheckOutcome.NotDue));
        lastManualCheck = now;
        return Start(cancellationToken);
      }
    }

    /// <summary>Stops offering <paramref name="release"/>. A later version is offered again.</summary>
    public void SkipVersion(ReleaseInfo release) {
      if (release == null)
        throw new ArgumentNullException(nameof(release));
      settings.SetValue(SkippedVersionKey, release.Version.ToString());
      ApplicationLog.Info("Update check: version " + release.Version + " skipped.");
      OnStateChanged();
    }

    private Task<UpdateCheckResult> Start(CancellationToken cancellationToken) {
      // The caller holds the lock, so the check cannot clear 'running' before
      // it has been assigned, even if it completes at once.
      running = Task.Run(() => RunAsync(cancellationToken));
      return running;
    }

    private async Task<UpdateCheckResult> RunAsync(CancellationToken cancellationToken) {
      UpdateCheckResult result;
      try {
        OnStateChanged();
        result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
      } finally {
        lock (sync)
          running = null;
      }
      OnStateChanged();
      return result;
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken) {
      DateTimeOffset started = clock();
      try {
        ReleaseInfo release = await FetchLatestAsync(cancellationToken).ConfigureAwait(false);
        UpdateCheckOutcome outcome;
        lock (sync) {
          latest = release;
          lastError = null;
          settings.SetValue(LatestTagKey, release.Tag);
          settings.SetValue(LatestNameKey, release.Name);
          settings.SetValue(LatestUrlKey, release.PageUrl);
          settings.SetValue(LastCheckKey, FormatTime(started));
          outcome = Classify(release);
        }
        switch (outcome) {
          case UpdateCheckOutcome.UpdateAvailable:
            ApplicationLog.Info("Update check: version " + release.Version +
              " is available (running " + RunningVersion + ").");
            break;
          case UpdateCheckOutcome.UpdateSkipped:
            ApplicationLog.Info("Update check: version " + release.Version +
              " is available but was skipped.");
            break;
          default:
            ApplicationLog.Info("Update check: the latest release is " +
              release.Version + " (running " + RunningVersion + ").");
            break;
        }
        return new UpdateCheckResult(outcome, release, null);
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        // Not recorded as a check: nothing was learned.
        return CurrentResult(UpdateCheckOutcome.Cancelled);
      } catch (Exception ex) {
        // An update check is never worth an error dialog or a crash.
        string message = DescribeFailure(ex);
        lock (sync) {
          lastError = message;
          settings.SetValue(LastCheckKey, FormatTime(started));
        }
        ApplicationLog.Warning("Update check failed: " + message);
        return new UpdateCheckResult(UpdateCheckOutcome.Failed, LatestRelease, message);
      }
    }

    private async Task<ReleaseInfo> FetchLatestAsync(CancellationToken cancellationToken) {
      using (CancellationTokenSource timeout =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
        timeout.CancelAfter(RequestTimeout);
        CancellationToken token = timeout.Token;

        HttpMessageHandler messageHandler = handler ?? new SocketsHttpHandler {
          AutomaticDecompression = DecompressionMethods.All
        };
        using (HttpClient client = new HttpClient(messageHandler, handler == null) {
          Timeout = System.Threading.Timeout.InfiniteTimeSpan
        })
        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
          LatestReleaseApiUrl)) {
          // GitHub rejects API requests without a User-Agent.
          request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
          request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
          request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

          using (HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false)) {
            int status = (int)response.StatusCode;
            if (status == 404)
              throw new UpdateCheckException("No release has been published yet.");
            if (status == 403 || status == 429)
              throw new UpdateCheckException(
                "GitHub is limiting requests from this network. The next check will try again.");
            if (!response.IsSuccessStatusCode)
              throw new UpdateCheckException("GitHub answered with an error (HTTP " +
                status.ToString(CultureInfo.InvariantCulture) + ").");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
              throw new UpdateCheckException(TooLarge);

            byte[] body = await ReadLimitedAsync(response.Content, token).ConfigureAwait(false);
            return ParseRelease(body);
          }
        }
      }
    }

    private const string TooLarge = "The answer from GitHub was unexpectedly large.";
    private const string Unreadable = "The answer from GitHub could not be read.";

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content,
      CancellationToken token) {
      using (Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false))
      using (MemoryStream buffer = new MemoryStream()) {
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0) {
          if (buffer.Length + read > MaxResponseBytes)
            throw new UpdateCheckException(TooLarge);
          buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
      }
    }

    internal static ReleaseInfo ParseRelease(string json) {
      return ParseRelease(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Reads tag_name, name and html_url from a GitHub release object.</summary>
    internal static ReleaseInfo ParseRelease(byte[] json) {
      try {
        using (JsonDocument document = JsonDocument.Parse(json)) {
          JsonElement root = document.RootElement;
          if (root.ValueKind != JsonValueKind.Object)
            throw new UpdateCheckException(Unreadable);

          string? tag = GetString(root, "tag_name")?.Trim();
          if (string.IsNullOrEmpty(tag) || tag.Length > 100)
            throw new UpdateCheckException("The latest release has no version tag.");
          if (!SemanticVersion.TryParse(tag, out SemanticVersion? version))
            throw new UpdateCheckException("The latest release tag \"" +
              Clean(tag, 40) + "\" is not a version number.");

          string name = Clean(GetString(root, "name"), 120);
          if (name.Length == 0)
            name = tag;
          return new ReleaseInfo(tag, version!, name,
            TrustedPageUrl(GetString(root, "html_url"), tag));
        }
      } catch (JsonException) {
        throw new UpdateCheckException(Unreadable);
      }
    }

    private static string? GetString(JsonElement element, string property) {
      return element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// The address comes from the network and is opened in a browser, so it
    /// must be a release page of this repository on github.com. Anything else
    /// falls back to the page built from the tag.
    /// </summary>
    internal static string TrustedPageUrl(string? url, string tag) {
      if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/" + Repository + "/releases/",
          StringComparison.OrdinalIgnoreCase))
        return uri.AbsoluteUri;
      return ReleasesPageUrl + "/tag/" + Uri.EscapeDataString(tag);
    }

    private static string Clean(string? text, int maxLength) {
      if (string.IsNullOrEmpty(text))
        return "";
      StringBuilder s = new StringBuilder(Math.Min(text.Length, maxLength));
      foreach (char c in text) {
        if (s.Length >= maxLength)
          break;
        s.Append(char.IsControl(c) ? ' ' : c);
      }
      return s.ToString().Trim();
    }

    private string DescribeFailure(Exception ex) {
      if (ex is UpdateCheckException)
        return ex.Message;
      if (ex is OperationCanceledException)
        return "GitHub did not answer within " +
          RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
          " seconds.";
      if (ex is HttpRequestException)
        return "GitHub could not be reached. Check the internet connection.";
      return "The check failed (" + ex.GetType().Name + ": " + Clean(ex.Message, 200) + ").";
    }

    private UpdateCheckOutcome Classify(ReleaseInfo? release) {
      if (release == null || !IsNewer(release.Version, RunningVersion))
        return UpdateCheckOutcome.UpToDate;
      SemanticVersion? skipped = SkippedVersion;
      return skipped != null && skipped.Equals(release.Version)
        ? UpdateCheckOutcome.UpdateSkipped : UpdateCheckOutcome.UpdateAvailable;
    }

    private UpdateCheckResult CurrentResult(UpdateCheckOutcome outcome) {
      lock (sync)
        return new UpdateCheckResult(outcome, latest, lastError);
    }

    private ReleaseInfo? LoadCachedRelease() {
      string tag = settings.GetValue(LatestTagKey, "");
      if (!SemanticVersion.TryParse(tag, out SemanticVersion? version))
        return null;
      string name = Clean(settings.GetValue(LatestNameKey, ""), 120);
      return new ReleaseInfo(tag, version!, name.Length == 0 ? tag : name,
        TrustedPageUrl(settings.GetValue(LatestUrlKey, ""), tag));
    }

    private static string FormatTime(DateTimeOffset time) {
      return time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }

    private void OnStateChanged() {
      try {
        StateChanged?.Invoke(this, EventArgs.Empty);
      } catch (Exception ex) {
        // A faulty subscriber must not break the check itself.
        ApplicationLog.Error("An update status handler failed.", ex);
      }
    }
  }

  /// <summary>A failure with a message fit to show the user.</summary>
  internal sealed class UpdateCheckException : Exception {
    public UpdateCheckException(string message) : base(message) { }
  }
}
