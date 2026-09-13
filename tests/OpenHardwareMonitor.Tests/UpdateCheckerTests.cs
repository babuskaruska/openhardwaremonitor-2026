/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Maintenance;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// The update check without a network: parsing, version comparison, the
  /// daily limit, skipped versions and every way a request can fail.
  /// </summary>
  public class UpdateCheckerTests {

    internal sealed class MemorySettings : ISettings {
      private readonly Dictionary<string, string> values = new Dictionary<string, string>();

      public bool Contains(string name) {
        lock (values)
          return values.ContainsKey(name);
      }

      public void SetValue(string name, string value) {
        lock (values)
          values[name] = value;
      }

      public string GetValue(string name, string value) {
        lock (values)
          return values.TryGetValue(name, out string? stored) ? stored : value;
      }

      public void Remove(string name) {
        lock (values)
          values.Remove(name);
      }
    }

    private sealed class FakeHandler : HttpMessageHandler {
      private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

      public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) {
        this.respond = respond;
      }

      public int Requests;
      public HttpRequestMessage? LastRequest;

      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) {
        Interlocked.Increment(ref Requests);
        LastRequest = request;
        return respond(request, cancellationToken);
      }
    }

    private sealed class UnseekableStream : MemoryStream {
      public UnseekableStream(byte[] buffer) : base(buffer) { }
      public override bool CanSeek {
        get { return false; }
      }
    }

    private static string Release(string tag, string? url = null) {
      return "{\"tag_name\":\"" + tag + "\",\"name\":\"Open Hardware Monitor 2026 " + tag +
        "\",\"html_url\":\"" + (url ?? "https://github.com/babuskaruska/openhardwaremonitor-2026/releases/tag/" + tag) +
        "\",\"draft\":false,\"prerelease\":false,\"body\":\"Notes\"}";
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) {
      return new HttpResponseMessage(status) {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
      };
    }

    private static FakeHandler Answer(string json) {
      return new FakeHandler((request, token) => Task.FromResult(Json(json)));
    }

    private DateTimeOffset now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private UpdateChecker Checker(ISettings settings, FakeHandler handler,
      string running = "0.10.0") {
      return new UpdateChecker(settings, running, handler, () => now);
    }

    // ---- parsing ----------------------------------------------------------------

    [Fact]
    public void ParsesTheLatestRelease() {
      ReleaseInfo release = UpdateChecker.ParseRelease(Release("v0.11.0"));
      Assert.Equal("v0.11.0", release.Tag);
      Assert.Equal("0.11.0", release.Version.ToString());
      Assert.Equal("Open Hardware Monitor 2026 v0.11.0", release.Name);
      Assert.Equal("https://github.com/babuskaruska/openhardwaremonitor-2026/releases/tag/v0.11.0",
        release.PageUrl);
    }

    [Theory]
    [InlineData("https://evil.example/babuskaruska/openhardwaremonitor-2026/releases/tag/v0.11.0")]
    [InlineData("http://github.com/babuskaruska/openhardwaremonitor-2026/releases/tag/v0.11.0")]
    [InlineData("https://github.com/someone-else/openhardwaremonitor-2026/releases/tag/v0.11.0")]
    [InlineData("https://github.com:8443/babuskaruska/openhardwaremonitor-2026/releases/tag/v0.11.0")]
    [InlineData("javascript:alert(1)")]
    public void OpensOnlyThisRepositorysReleasePages(string url) {
      ReleaseInfo release = UpdateChecker.ParseRelease(Release("v0.11.0", url));
      Assert.Equal(UpdateChecker.ReleasesPageUrl + "/tag/v0.11.0", release.PageUrl);
    }

    [Fact]
    public void UsesTheTagWhenTheReleaseHasNoName() {
      ReleaseInfo release = UpdateChecker.ParseRelease("{\"tag_name\":\"v1.0.0\",\"name\":null}");
      Assert.Equal("v1.0.0", release.Name);
    }

    [Theory]
    [InlineData("{\"name\":\"No tag\"}")]
    [InlineData("{\"tag_name\":\"nightly\"}")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void RejectsUnusableAnswers(string json) {
      Assert.Throws<UpdateCheckException>(() => UpdateChecker.ParseRelease(json));
    }

    // ---- comparison ---------------------------------------------------------------

    [Theory]
    [InlineData("0.11.0", "0.10.0", true)]
    [InlineData("0.10.0", "0.10.0", false)]
    [InlineData("0.9.9", "0.10.0", false)]
    [InlineData("1.0.0", "0.99.0", true)]
    // Development builds compare major, minor and patch only.
    [InlineData("0.10.0", "0.10.0-dev", false)]
    [InlineData("0.10.0", "0.10.0-dev+852d066", false)]
    [InlineData("0.10.1", "0.10.0-dev+852d066", true)]
    [InlineData("0.9.0", "0.10.0-dev", false)]
    // Other pre-releases follow SemVer.
    [InlineData("0.11.0-beta.1", "0.10.0", true)]
    [InlineData("0.11.0", "0.11.0-beta.1", true)]
    [InlineData("0.11.0-beta.2", "0.11.0-beta.1", true)]
    [InlineData("0.11.0-beta.1", "0.11.0", false)]
    public void OffersOnlyNewerVersions(string latest, string running, bool expected) {
      Assert.Equal(expected, UpdateChecker.IsNewer(SemanticVersion.Parse(latest),
        SemanticVersion.Parse(running)));
    }

    [Fact]
    public async Task NeverOffersAnUpdateToAnUnknownVersion() {
      UpdateChecker checker = Checker(new MemorySettings(), Answer(Release("v9.0.0")), "custom build");
      UpdateCheckResult result = await checker.CheckNowAsync();
      Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
      Assert.Null(checker.AvailableUpdate);
    }

    // ---- checking -----------------------------------------------------------------

    [Fact]
    public async Task AsksGitHubPolitely() {
      FakeHandler handler = Answer(Release("v0.11.0"));
      UpdateChecker checker = Checker(new MemorySettings(), handler);

      UpdateCheckResult result = await checker.CheckIfDueAsync();

      Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
      Assert.Equal(1, handler.Requests);
      HttpRequestMessage request = handler.LastRequest!;
      Assert.Equal(HttpMethod.Get, request.Method);
      Assert.Equal(UpdateChecker.LatestReleaseApiUrl, request.RequestUri!.AbsoluteUri);
      Assert.StartsWith("OpenHardwareMonitor/0.10.0", string.Join(" ",
        request.Headers.GetValues("User-Agent")));
      Assert.Contains("application/vnd.github+json", string.Join(",",
        request.Headers.GetValues("Accept")));
      Assert.Equal("0.11.0", checker.AvailableUpdate!.Version.ToString());
      Assert.Equal(now, checker.LastCheck);
      Assert.Null(checker.LastError);
      Assert.False(checker.IsChecking);
    }

    [Fact]
    public async Task ChecksAtMostOncePerDay() {
      FakeHandler handler = Answer(Release("v0.10.0"));
      UpdateChecker checker = Checker(new MemorySettings(), handler);

      Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckIfDueAsync()).Outcome);
      now = now.AddHours(23);
      Assert.Equal(UpdateCheckOutcome.NotDue, (await checker.CheckIfDueAsync()).Outcome);
      Assert.Equal(1, handler.Requests);

      now = now.AddHours(1);
      Assert.True(checker.IsCheckDue);
      Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckIfDueAsync()).Outcome);
      Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task MakesNoRequestWhenTurnedOff() {
      FakeHandler handler = Answer(Release("v0.11.0"));
      UpdateChecker checker = Checker(new MemorySettings(), handler);
      checker.AutomaticChecks = false;

      Assert.Equal(UpdateCheckOutcome.Disabled, (await checker.CheckIfDueAsync()).Outcome);
      Assert.Equal(0, handler.Requests);
      // On unless turned off.
      Assert.True(new UpdateChecker(new MemorySettings(), "0.10.0").AutomaticChecks);
    }

    [Fact]
    public async Task CheckNowIgnoresTheDailyLimitButNotRepeatedClicks() {
      FakeHandler handler = Answer(Release("v0.10.0"));
      UpdateChecker checker = Checker(new MemorySettings(), handler);
      checker.AutomaticChecks = false;

      await checker.CheckNowAsync();
      Assert.Equal(UpdateCheckOutcome.NotDue, (await checker.CheckNowAsync()).Outcome);
      Assert.Equal(1, handler.Requests);

      now = now.Add(UpdateChecker.ManualCheckSpacing);
      await checker.CheckNowAsync();
      Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task RunsOneRequestAtATime() {
      TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      FakeHandler handler = new FakeHandler(async (request, token) => {
        await gate.Task;
        return Json(Release("v0.11.0"));
      });
      UpdateChecker checker = Checker(new MemorySettings(), handler);

      Task<UpdateCheckResult> first = checker.CheckNowAsync();
      Task<UpdateCheckResult> second = checker.CheckIfDueAsync();
      Assert.Same(first, second);
      Assert.True(checker.IsChecking);

      gate.SetResult(true);
      await first;
      Assert.Equal(1, handler.Requests);
      Assert.False(checker.IsChecking);
    }

    [Fact]
    public async Task ReportsStartAndFinish() {
      UpdateChecker checker = Checker(new MemorySettings(), Answer(Release("v0.11.0")));
      int changes = 0;
      checker.StateChanged += delegate { Interlocked.Increment(ref changes); };
      await checker.CheckNowAsync();
      Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotBreakTheCheck() {
      UpdateChecker checker = Checker(new MemorySettings(), Answer(Release("v0.11.0")));
      checker.StateChanged += delegate { throw new InvalidOperationException("Handler bug"); };
      UpdateCheckResult result = await checker.CheckNowAsync();
      Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task RemembersTheResultAcrossRestarts() {
      MemorySettings settings = new MemorySettings();
      await Checker(settings, Answer(Release("v0.11.0"))).CheckIfDueAsync();

      FakeHandler handler = Answer(Release("v0.12.0"));
      UpdateChecker restarted = Checker(settings, handler);
      Assert.Equal("0.11.0", restarted.AvailableUpdate!.Version.ToString());
      Assert.Equal(now, restarted.LastCheck);
      Assert.False(restarted.IsCheckDue);
      Assert.Equal(UpdateCheckOutcome.NotDue, (await restarted.CheckIfDueAsync()).Outcome);
      Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task IgnoresACachedPageAddressThatWasTamperedWith() {
      MemorySettings settings = new MemorySettings();
      await Checker(settings, Answer(Release("v0.11.0"))).CheckIfDueAsync();
      settings.SetValue("updates.latestUrl", "https://evil.example/download");

      UpdateChecker restarted = Checker(settings, Answer(Release("v0.11.0")));
      Assert.Equal(UpdateChecker.ReleasesPageUrl + "/tag/v0.11.0",
        restarted.AvailableUpdate!.PageUrl);
    }

    [Fact]
    public async Task ChecksAgainWhenTheClockWasSetBack() {
      UpdateChecker checker = Checker(new MemorySettings(), Answer(Release("v0.10.0")));
      await checker.CheckIfDueAsync();
      now = now.AddDays(-2);
      Assert.True(checker.IsCheckDue);
    }

    [Fact]
    public async Task SkippedVersionIsNotOfferedButALaterOneIs() {
      MemorySettings settings = new MemorySettings();
      UpdateChecker checker = Checker(settings, Answer(Release("v0.11.0")));
      await checker.CheckIfDueAsync();
      checker.SkipVersion(checker.AvailableUpdate!);

      Assert.Null(checker.AvailableUpdate);
      Assert.Equal("0.11.0", checker.SkippedVersion!.ToString());

      now = now.AddDays(1);
      Assert.Equal(UpdateCheckOutcome.UpdateSkipped, (await checker.CheckIfDueAsync()).Outcome);

      now = now.AddDays(1);
      UpdateChecker later = Checker(settings, Answer(Release("v0.12.0")));
      Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await later.CheckIfDueAsync()).Outcome);
      Assert.Equal("0.12.0", later.AvailableUpdate!.Version.ToString());
    }

    // ---- failures -----------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.Forbidden, "limiting")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting")]
    [InlineData(HttpStatusCode.NotFound, "No release")]
    public async Task ReportsHttpErrors(HttpStatusCode status, string message) {
      FakeHandler handler = new FakeHandler((request, token) =>
        Task.FromResult(Json("{\"message\":\"error\"}", status)));
      UpdateChecker checker = Checker(new MemorySettings(), handler);

      UpdateCheckResult result = await checker.CheckIfDueAsync();

      Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
      Assert.Contains(message, result.Error);
      Assert.Equal(result.Error, checker.LastError);
      // A failure counts as the day's check, so an outage causes no retries.
      Assert.Equal(UpdateCheckOutcome.NotDue, (await checker.CheckIfDueAsync()).Outcome);
      Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ReportsANetworkFailure() {
      FakeHandler handler = new FakeHandler((request, token) =>
        throw new HttpRequestException("No such host is known."));
      UpdateCheckResult result = await Checker(new MemorySettings(), handler).CheckNowAsync();
      Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
      Assert.Contains("could not be reached", result.Error);
    }

    [Fact]
    public async Task GivesUpAfterTheTimeout() {
      FakeHandler handler = new FakeHandler(async (request, token) => {
        await Task.Delay(Timeout.Infinite, token);
        return Json(Release("v0.11.0"));
      });
      UpdateChecker checker = Checker(new MemorySettings(), handler);
      checker.RequestTimeout = TimeSpan.FromMilliseconds(100);

      UpdateCheckResult result = await checker.CheckNowAsync();

      Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
      Assert.Contains("did not answer", result.Error);
      Assert.NotNull(checker.LastCheck);
    }

    [Fact]
    public async Task CancellingIsNotRecordedAsACheck() {
      FakeHandler handler = new FakeHandler(async (request, token) => {
        await Task.Delay(Timeout.Infinite, token);
        return Json(Release("v0.11.0"));
      });
      UpdateChecker checker = Checker(new MemorySettings(), handler);
      using (CancellationTokenSource cancel = new CancellationTokenSource()) {
        Task<UpdateCheckResult> check = checker.CheckIfDueAsync(cancel.Token);
        cancel.CancelAfter(50);
        Assert.Equal(UpdateCheckOutcome.Cancelled, (await check).Outcome);
      }
      Assert.Null(checker.LastCheck);
      Assert.True(checker.IsCheckDue);
    }

    [Fact]
    public async Task ReportsAnUnreadableAnswer() {
      UpdateCheckResult result = await Checker(new MemorySettings(),
        Answer("<html>Unicorn!</html>")).CheckNowAsync();
      Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
      Assert.Contains("could not be read", result.Error);
    }

    [Fact]
    public async Task RefusesAnOversizedAnswerWithOrWithoutALength() {
      byte[] huge = new byte[UpdateChecker.MaxResponseBytes + 1];
      Array.Fill(huge, (byte)' ');

      FakeHandler declared = new FakeHandler((request, token) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(huge) }));
      UpdateCheckResult result = await Checker(new MemorySettings(), declared).CheckNowAsync();
      Assert.Contains("unexpectedly large", result.Error);

      FakeHandler streamed = new FakeHandler((request, token) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) {
          Content = new StreamContent(new UnseekableStream(huge))
        }));
      result = await Checker(new MemorySettings(), streamed).CheckNowAsync();
      Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
      Assert.Contains("unexpectedly large", result.Error);
    }

    [Fact]
    public async Task KeepsTheKnownReleaseWhenALaterCheckFails() {
      MemorySettings settings = new MemorySettings();
      await Checker(settings, Answer(Release("v0.11.0"))).CheckIfDueAsync();

      now = now.AddDays(1);
      UpdateChecker checker = Checker(settings, new FakeHandler((request, token) =>
        throw new HttpRequestException("Offline")));
      UpdateCheckResult result = await checker.CheckIfDueAsync();

      Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
      Assert.Equal("0.11.0", result.Latest!.Version.ToString());
      Assert.NotNull(checker.AvailableUpdate);
    }
  }
}
