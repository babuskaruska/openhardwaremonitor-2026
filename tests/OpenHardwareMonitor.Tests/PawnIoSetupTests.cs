/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.LowLevel;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// The guided PawnIO setup: the status model, module download and
  /// extraction, the winget driver installer and the step runner. Nothing
  /// here touches the network, winget or the browser; every side effect is
  /// injected, and files are written only below the test output folder.
  /// </summary>
  public class PawnIoSetupTests : IDisposable {

    private const string AssetUrl =
      "https://github.com/namazso/PawnIO.Modules/releases/download/0.2.11/release_0_2_11.zip";

    private readonly string root = Path.Combine(AppContext.BaseDirectory,
      "PawnIoSetupTests", Guid.NewGuid().ToString("N"));

    public void Dispose() {
      if (Directory.Exists(root))
        Directory.Delete(root, true);
    }

    private string ModulesDirectory {
      get { return Path.Combine(root, "modules"); }
    }

    // ---- helpers --------------------------------------------------------------

    private static byte[] Zip(params (string Name, byte[] Data)[] entries) {
      using (MemoryStream stream = new MemoryStream()) {
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
          foreach ((string name, byte[] data) in entries) {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using (Stream output = entry.Open())
              output.Write(data, 0, data.Length);
          }
        }
        return stream.ToArray();
      }
    }

    private static byte[] Bytes(string text) {
      return Encoding.ASCII.GetBytes(text);
    }

    private static byte[] ReleaseJson(params (string Name, string Url)[] assets) {
      string list = string.Join(",", assets.Select(asset =>
        "{\"name\":\"" + asset.Name + "\",\"browser_download_url\":\"" + asset.Url +
        "\",\"size\":1234}"));
      return Bytes("{\"tag_name\":\"0.2.11\",\"assets\":[" + list + "]}");
    }

    private sealed class Collector<T> : IProgress<T> {
      public List<T> Items { get; } = new List<T>();
      public void Report(T value) {
        Items.Add(value);
      }
    }

    private sealed class FakeHandler : HttpMessageHandler {
      private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

      public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, token) => Task.FromResult(respond(request))) {
      }

      public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) {
        this.respond = respond;
      }

      public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) {
        Requests.Add(request);
        return respond(request, cancellationToken);
      }
    }

    private sealed class UnknownLengthContent : HttpContent {
      private readonly byte[] data;

      public UnknownLengthContent(byte[] data) {
        this.data = data;
      }

      protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) {
        return stream.WriteAsync(data, 0, data.Length);
      }

      protected override bool TryComputeLength(out long length) {
        length = 0;
        return false;
      }
    }

    private static HttpResponseMessage Ok(byte[] body) {
      return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private static PawnIoSetupStatus Status(bool driver, bool modules, bool elevated = false,
      AccessTier tier = AccessTier.Base, bool active = false, bool amd = false) {
      return PawnIoSetupStatus.Create(driver ? 0x020200u : 0, amd, new[] { "modules" },
        path => modules, "modules", elevated, tier, active);
    }

    // ---- status ---------------------------------------------------------------

    [Fact]
    public void RequiredModulesFollowTheProcessorVendor() {
      Assert.Equal(new[] { "IntelMSR", "LpcIO" }, PawnIoSetupStatus.GetRequiredModules(false));
      Assert.Equal(new[] { "AMDFamily17", "LpcIO" }, PawnIoSetupStatus.GetRequiredModules(true));
    }

    [Fact]
    public void NothingInstalledLeavesEveryStepInOrder() {
      PawnIoSetupStatus status = Status(false, false);
      Assert.False(status.DriverInstalled);
      Assert.Null(status.DriverVersion);
      Assert.Equal(new[] { "IntelMSR", "LpcIO" }, status.MissingModules);
      Assert.Equal(new[] {
        PawnIoSetupStep.InstallDriver, PawnIoSetupStep.InstallModules,
        PawnIoSetupStep.RestartAsAdministrator
      }, status.RemainingSteps);
      Assert.False(status.IsComplete);
    }

    [Fact]
    public void ModulesAreFoundInAnySearchDirectory() {
      string[] directories = { @"C:\app\PawnIOModules", @"C:\user\PawnIOModules" };
      HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        @"C:\app\PawnIOModules\LpcIO.bin", @"C:\user\PawnIOModules\AMDFamily17.bin"
      };
      PawnIoSetupStatus status = PawnIoSetupStatus.Create(0x020200, true, directories,
        files.Contains, @"C:\user\PawnIOModules", false, AccessTier.Base, false);

      Assert.Equal("2.2.0", status.DriverVersion);
      Assert.True(status.ModulesInstalled);
      Assert.Equal(new[] { PawnIoSetupStep.RestartAsAdministrator }, status.RemainingSteps);
    }

    [Fact]
    public void OnlyTheMissingModuleIsReported() {
      PawnIoSetupStatus status = PawnIoSetupStatus.Create(0x020200, false,
        new[] { "broken", "modules" },
        path => path.StartsWith("broken", StringComparison.Ordinal)
          ? throw new IOException("unreadable")
          : path.EndsWith("IntelMSR.bin", StringComparison.Ordinal),
        "modules", true, AccessTier.Deep, false);

      Assert.Equal(new[] { "LpcIO" }, status.MissingModules);
      Assert.True(status.IsDone(PawnIoSetupStep.InstallDriver));
      Assert.False(status.IsDone(PawnIoSetupStep.InstallModules));
    }

    [Fact]
    public void InstallingDuringASessionStillNeedsARestart() {
      PawnIoSetupStatus status = Status(true, true, elevated: true, tier: AccessTier.Base);
      Assert.Equal(new[] { PawnIoSetupStep.RestartAsAdministrator }, status.RemainingSteps);
    }

    [Fact]
    public void DeepTierWithoutElevationIsNotComplete() {
      // Not a real state today, but the restart step must never be skipped
      // without administrator rights.
      PawnIoSetupStatus status = Status(true, true, elevated: false, tier: AccessTier.Deep, active: true);
      Assert.False(status.IsComplete);
    }

    [Fact]
    public void ElevatedDeepTierWithEveryModuleIsComplete() {
      PawnIoSetupStatus status = Status(true, true, elevated: true, tier: AccessTier.Deep, active: true);
      Assert.True(status.IsComplete);
      Assert.Empty(status.RemainingSteps);
      Assert.True(status.FullAccessActive);
    }

    [Fact]
    public void DeepTierMissingOneCapabilityStillOffersTheRestart() {
      PawnIoSetupStatus status = Status(true, true, elevated: true, tier: AccessTier.Deep, active: false);
      Assert.Equal(new[] { PawnIoSetupStep.RestartAsAdministrator }, status.RemainingSteps);
    }

    // ---- module names and asset selection -------------------------------------

    [Theory]
    [InlineData("IntelMSR.bin", "IntelMSR")]
    [InlineData("release/IntelMSR.bin", "IntelMSR")]
    [InlineData(@"..\..\LpcIO.bin", "LpcIO")]
    [InlineData("../../../Windows/System32/AMDFamily17.bin", "AMDFamily17")]
    [InlineData("intelmsr.BIN", "IntelMSR")]
    [InlineData("IntelMSR.bin.exe", null)]
    [InlineData("IntelMSR.exe", null)]
    [InlineData("IntelMSR .bin", null)]
    [InlineData("C:IntelMSR.bin", null)]
    [InlineData("evil.bin", null)]
    [InlineData("IntelMSR.bin/", null)]
    [InlineData(".bin", null)]
    [InlineData("", null)]
    public void OnlyKnownModuleFileNamesAreAccepted(string entry, string? expected) {
      Assert.Equal(expected, PawnIoModuleInstaller.GetModuleName(entry));
    }

    [Fact]
    public void TheReleaseArchiveIsPreferredOverOtherZips() {
      PawnIoReleaseAsset asset = PawnIoModuleInstaller.SelectAsset(ReleaseJson(
        ("Source.tar.gz", "https://github.com/x/source.tar.gz"),
        ("debug_0_2_11.zip", "https://github.com/x/debug_0_2_11.zip"),
        ("release_0_2_11.zip", AssetUrl)));

      Assert.Equal("release_0_2_11.zip", asset.Name);
      Assert.Equal(new Uri(AssetUrl), asset.DownloadUrl);
      Assert.Equal("0.2.11", asset.ReleaseTag);
      Assert.Equal(1234, asset.Size);
    }

    [Fact]
    public void AnyZipIsUsedWhenNoneIsNamedRelease() {
      PawnIoReleaseAsset asset = PawnIoModuleInstaller.SelectAsset(ReleaseJson(
        ("modules.zip", "https://github.com/x/modules.zip")));
      Assert.Equal("modules.zip", asset.Name);
    }

    [Fact]
    public void AReleaseWithoutAZipIsRejected() {
      Assert.Throws<PawnIoSetupException>(() => PawnIoModuleInstaller.SelectAsset(
        ReleaseJson(("notes.txt", "https://github.com/x/notes.txt"))));
      Assert.Throws<PawnIoSetupException>(() => PawnIoModuleInstaller.SelectAsset(Bytes("{}")));
    }

    [Fact]
    public void AnInsecureAssetUrlIsRejected() {
      Assert.Throws<PawnIoSetupException>(() => PawnIoModuleInstaller.SelectAsset(
        ReleaseJson(("release_0_2_11.zip", "http://github.com/x/release_0_2_11.zip"))));
    }

    [Fact]
    public void AnOversizedAssetIsRejected() {
      byte[] json = Bytes("{\"assets\":[{\"name\":\"release.zip\",\"browser_download_url\":\"" +
        AssetUrl + "\",\"size\":999999999}]}");
      Assert.Throws<PawnIoSetupException>(() => PawnIoModuleInstaller.SelectAsset(json));
    }

    [Fact]
    public void MalformedReleaseInformationIsExplained() {
      PawnIoSetupException ex = Assert.Throws<PawnIoSetupException>(
        () => PawnIoModuleInstaller.SelectAsset(Bytes("<html>")));
      Assert.Contains("unexpected", ex.Message);
    }

    // ---- extraction -----------------------------------------------------------

    [Fact]
    public void OnlyKnownModulesAreExtractedAndNothingEscapesTheFolder() {
      byte[] zip = Zip(
        ("release/IntelMSR.bin", Bytes("msr")),
        ("release/sub/LpcIO.bin", Bytes("lpc")),
        ("../AMDFamily17.bin", Bytes("amd")),
        ("readme.txt", Bytes("text")),
        ("evil.bin", Bytes("evil")),
        ("debug/IntelMSR.bin", Bytes("second copy")));

      IReadOnlyList<string> installed;
      using (MemoryStream stream = new MemoryStream(zip))
        installed = PawnIoModuleInstaller.ExtractModules(stream, ModulesDirectory);

      Assert.Equal(new[] { "IntelMSR", "LpcIO", "AMDFamily17" }, installed);
      Assert.Equal(new[] { "AMDFamily17.bin", "IntelMSR.bin", "LpcIO.bin" },
        Directory.GetFiles(ModulesDirectory).Select(Path.GetFileName).OrderBy(n => n,
          StringComparer.Ordinal));
      Assert.Equal("msr", File.ReadAllText(Path.Combine(ModulesDirectory, "IntelMSR.bin")));
      Assert.False(File.Exists(Path.Combine(root, "AMDFamily17.bin")));
      Assert.Empty(Directory.GetDirectories(ModulesDirectory));
    }

    [Fact]
    public void AnExistingModuleIsReplaced() {
      Directory.CreateDirectory(ModulesDirectory);
      File.WriteAllText(Path.Combine(ModulesDirectory, "IntelMSR.bin"), "old");

      using (MemoryStream stream = new MemoryStream(Zip(("IntelMSR.bin", Bytes("new")))))
        PawnIoModuleInstaller.ExtractModules(stream, ModulesDirectory);

      Assert.Equal("new", File.ReadAllText(Path.Combine(ModulesDirectory, "IntelMSR.bin")));
      Assert.Single(Directory.GetFiles(ModulesDirectory));
    }

    [Fact]
    public void AFailingArchiveChangesNoModule() {
      Directory.CreateDirectory(ModulesDirectory);
      File.WriteAllText(Path.Combine(ModulesDirectory, "IntelMSR.bin"), "old");
      byte[] zip = Zip(
        ("LpcIO.bin", Bytes("lpc")),
        ("IntelMSR.bin", new byte[PawnIoModuleInstaller.MaxModuleBytes + 1]));

      using (MemoryStream stream = new MemoryStream(zip))
        Assert.Throws<PawnIoSetupException>(
          () => PawnIoModuleInstaller.ExtractModules(stream, ModulesDirectory));

      Assert.Equal(new[] { "IntelMSR.bin" },
        Directory.GetFiles(ModulesDirectory).Select(Path.GetFileName));
      Assert.Equal("old", File.ReadAllText(Path.Combine(ModulesDirectory, "IntelMSR.bin")));
    }

    [Fact]
    public void AnArchiveWithoutModulesIsRejected() {
      using (MemoryStream stream = new MemoryStream(Zip(("readme.txt", Bytes("text")))))
        Assert.Throws<PawnIoSetupException>(
          () => PawnIoModuleInstaller.ExtractModules(stream, ModulesDirectory));
    }

    [Fact]
    public void ADamagedArchiveIsExplained() {
      using (MemoryStream stream = new MemoryStream(Bytes("this is not a zip file")))
        Assert.Throws<PawnIoSetupException>(
          () => PawnIoModuleInstaller.ExtractModules(stream, ModulesDirectory));
    }

    // ---- download -------------------------------------------------------------

    [Fact]
    public async Task TheLatestModulesAreDownloadedAndInstalled() {
      byte[] zip = Zip(("IntelMSR.bin", Bytes("msr")), ("LpcIO.bin", Bytes("lpc")));
      FakeHandler handler = new FakeHandler(request =>
        request.RequestUri!.AbsoluteUri == PawnIoModuleInstaller.LatestReleaseUrl
          ? Ok(ReleaseJson(("release_0_2_11.zip", AssetUrl)))
          : request.RequestUri.AbsoluteUri == AssetUrl
            ? Ok(zip)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
      Collector<string> progress = new Collector<string>();

      PawnIoModuleInstallResult result;
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30)))
        result = await installer.InstallLatestAsync(ModulesDirectory, progress, CancellationToken.None);

      Assert.Equal("0.2.11", result.ReleaseTag);
      Assert.Equal(new[] { "IntelMSR", "LpcIO" }, result.InstalledModules);
      Assert.Equal("lpc", File.ReadAllText(Path.Combine(ModulesDirectory, "LpcIO.bin")));
      Assert.Equal(2, handler.Requests.Count);
      Assert.All(handler.Requests, request => {
        Assert.Equal(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
        Assert.Contains("OpenHardwareMonitor", request.Headers.UserAgent.ToString());
      });
      Assert.Contains(progress.Items, message => message.StartsWith("Downloading release_0_2_11.zip"));
      Assert.Equal("Installing modules…", progress.Items.Last());
    }

    [Fact]
    public async Task AnAnnouncedOversizedBodyIsRefused() {
      FakeHandler handler = new FakeHandler(request => {
        HttpResponseMessage response = Ok(new byte[10]);
        response.Content.Headers.ContentLength = PawnIoModuleInstaller.MaxDownloadBytes + 1;
        return response;
      });
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30)))
        await Assert.ThrowsAsync<PawnIoSetupException>(() => installer.DownloadAsync(
          new Uri(AssetUrl), "application/octet-stream", PawnIoModuleInstaller.MaxDownloadBytes,
          null, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnannouncedOversizedBodyIsCutOff() {
      FakeHandler handler = new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new UnknownLengthContent(new byte[200000])
      });
      List<long> received = new List<long>();
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30)))
        await Assert.ThrowsAsync<PawnIoSetupException>(() => installer.DownloadAsync(
          new Uri(AssetUrl), "application/octet-stream", 100000,
          (bytes, total) => received.Add(bytes), CancellationToken.None));
      Assert.All(received, bytes => Assert.True(bytes <= 100000));
    }

    [Fact]
    public async Task InsecureUrlsAreNeverRequested() {
      FakeHandler handler = new FakeHandler(request => Ok(new byte[1]));
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30)))
        await Assert.ThrowsAsync<PawnIoSetupException>(() => installer.DownloadAsync(
          new Uri("http://github.com/release.zip"), "application/octet-stream", 1000, null,
          CancellationToken.None));
      Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARedirectAwayFromHttpsIsRefused() {
      FakeHandler handler = new FakeHandler(request => {
        HttpResponseMessage response = Ok(new byte[1]);
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.invalid/release.zip");
        return response;
      });
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30)))
        await Assert.ThrowsAsync<PawnIoSetupException>(() => installer.DownloadAsync(
          new Uri(AssetUrl), "application/octet-stream", 1000, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limiting")]
    [InlineData(HttpStatusCode.NotFound, "No PawnIO modules release")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task ServerErrorsAreExplained(HttpStatusCode status, string expected) {
      FakeHandler handler = new FakeHandler(request => new HttpResponseMessage(status));
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30))) {
        PawnIoSetupException ex = await Assert.ThrowsAsync<PawnIoSetupException>(
          () => installer.InstallLatestAsync(ModulesDirectory, null, CancellationToken.None));
        Assert.Contains(expected, ex.Message);
      }
    }

    [Fact]
    public async Task ANetworkFailureIsExplained() {
      FakeHandler handler = new FakeHandler(request => throw new HttpRequestException("offline"));
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30))) {
        PawnIoSetupException ex = await Assert.ThrowsAsync<PawnIoSetupException>(
          () => installer.InstallLatestAsync(ModulesDirectory, null, CancellationToken.None));
        Assert.Contains("internet connection", ex.Message);
      }
    }

    [Fact]
    public async Task ASlowServerTimesOut() {
      FakeHandler handler = new FakeHandler(async (request, token) => {
        await Task.Delay(Timeout.Infinite, token);
        return Ok(new byte[1]);
      });
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromMilliseconds(50))) {
        PawnIoSetupException ex = await Assert.ThrowsAsync<PawnIoSetupException>(
          () => installer.InstallLatestAsync(ModulesDirectory, null, CancellationToken.None));
        Assert.Contains("in time", ex.Message);
      }
    }

    [Fact]
    public async Task CancellingIsNotReportedAsAnError() {
      using (CancellationTokenSource cancel = new CancellationTokenSource()) {
        FakeHandler handler = new FakeHandler(async (request, token) => {
          cancel.Cancel();
          await Task.Delay(Timeout.Infinite, token);
          return Ok(new byte[1]);
        });
        using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller(handler, TimeSpan.FromSeconds(30)))
          await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallLatestAsync(ModulesDirectory, null, cancel.Token));
      }
      Assert.False(Directory.Exists(ModulesDirectory));
    }

    // ---- driver ---------------------------------------------------------------

    private sealed class DriverRig {
      public bool Installed;
      public string? Winget = @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\winget.exe";
      public int ExitCode;
      public bool InstallsDriver = true;
      public Exception? StartFailure;
      public Exception? OpenFailure;
      public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();
      public List<string> Opened { get; } = new List<string>();

      public PawnIoDriverInstaller Create() {
        return new PawnIoDriverInstaller(() => Winget,
          (info, token) => {
            Started.Add(info);
            if (StartFailure != null)
              throw StartFailure;
            if (InstallsDriver)
              Installed = true;
            return Task.FromResult(ExitCode);
          },
          url => {
            Opened.Add(url);
            if (OpenFailure != null)
              throw OpenFailure;
          },
          () => Installed);
      }
    }

    [Fact]
    public async Task WingetInstallsTheDriverWithTheAgreementsAccepted() {
      DriverRig rig = new DriverRig();
      Collector<string> progress = new Collector<string>();
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(progress, CancellationToken.None);

      Assert.True(result.Succeeded);
      ProcessStartInfo info = Assert.Single(rig.Started);
      Assert.Equal(rig.Winget, info.FileName);
      Assert.False(info.UseShellExecute);
      Assert.Equal(new[] {
        "install", "-e", "--id", "namazso.PawnIO",
        "--accept-source-agreements", "--accept-package-agreements"
      }, info.ArgumentList);
      Assert.Empty(rig.Opened);
      Assert.Single(progress.Items);
    }

    [Fact]
    public async Task AnInstalledDriverIsLeftAlone() {
      DriverRig rig = new DriverRig { Installed = true };
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(null, CancellationToken.None);
      Assert.True(result.Succeeded);
      Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task WithoutWingetThePawnIoWebsiteIsOpened() {
      DriverRig rig = new DriverRig { Winget = null };
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(null, CancellationToken.None);

      Assert.Equal(PawnIoDriverInstallOutcome.WingetMissing, result.Outcome);
      Assert.Equal(new[] { "https://pawnio.eu" }, rig.Opened);
      Assert.Empty(rig.Started);
    }

    [Fact]
    public async Task AWingetThatCannotStartCountsAsMissing() {
      DriverRig rig = new DriverRig { StartFailure = new Win32Exception(2), OpenFailure = new Win32Exception(5) };
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(null, CancellationToken.None);

      Assert.Equal(PawnIoDriverInstallOutcome.WingetMissing, result.Outcome);
      Assert.Contains("https://pawnio.eu", result.Message);
    }

    [Fact]
    public async Task AFailedInstallReportsTheExitCode() {
      DriverRig rig = new DriverRig { InstallsDriver = false, ExitCode = unchecked((int)0x8A15000F) };
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(null, CancellationToken.None);

      Assert.Equal(PawnIoDriverInstallOutcome.Failed, result.Outcome);
      Assert.Equal(unchecked((int)0x8A15000F), result.ExitCode);
      Assert.Contains("0x8A15000F", result.Message);
    }

    [Theory]
    [InlineData(1223)]
    [InlineData(unchecked((int)0x800704C7))]
    public async Task ADeclinedPermissionPromptIsRecognised(int exitCode) {
      DriverRig rig = new DriverRig { InstallsDriver = false, ExitCode = exitCode };
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(null, CancellationToken.None);
      Assert.Equal(PawnIoDriverInstallOutcome.Declined, result.Outcome);
    }

    [Fact]
    public async Task ANonZeroExitWithTheDriverPresentIsSuccess() {
      // winget returns an error when the package is already installed.
      DriverRig rig = new DriverRig { ExitCode = unchecked((int)0x8A15002B) };
      PawnIoDriverInstallResult result = await rig.Create().InstallAsync(null, CancellationToken.None);
      Assert.True(result.Succeeded);
    }

    [Fact]
    public void WingetIsFoundOnAbsolutePathEntriesOrAsTheAppAlias() {
      HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        @"tools\winget.exe", @"D:\Tools\winget.exe",
        @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\winget.exe"
      };

      Assert.Equal(@"D:\Tools\winget.exe", PawnIoDriverInstaller.FindWinget(
        @"tools;;""D:\Tools""", @"C:\Users\test\AppData\Local", files.Contains));
      Assert.Equal(@"C:\Users\test\AppData\Local\Microsoft\WindowsApps\winget.exe",
        PawnIoDriverInstaller.FindWinget(@"tools;C:\Windows", @"C:\Users\test\AppData\Local",
          files.Contains));
      Assert.Null(PawnIoDriverInstaller.FindWinget(null, null, files.Contains));
    }

    // ---- runner ---------------------------------------------------------------

    private sealed class RunnerRig {
      public bool Driver;
      public bool Modules;
      public bool ModulesAppear = true;
      public PawnIoDriverInstallResult? DriverResult;
      public Exception? ModuleFailure;
      public List<string> Calls { get; } = new List<string>();

      public PawnIoSetupRunner Create() {
        return new PawnIoSetupRunner(() => Status(Driver, Modules),
          (progress, token) => {
            Calls.Add("driver");
            progress?.Report("driver working");
            if (DriverResult == null) {
              Driver = true;
              return Task.FromResult(new DriverRig { Installed = true }.Create()
                .InstallAsync(null, token).Result);
            }
            return Task.FromResult(DriverResult);
          },
          (directory, progress, token) => {
            Calls.Add("modules:" + directory);
            progress?.Report("modules working");
            if (ModuleFailure != null)
              throw ModuleFailure;
            Modules = ModulesAppear;
            return Task.FromResult<PawnIoModuleInstallResult>(null!);
          });
      }
    }

    [Fact]
    public async Task TheRunnerInstallsTheDriverThenTheModules() {
      RunnerRig rig = new RunnerRig();
      Collector<PawnIoSetupProgress> progress = new Collector<PawnIoSetupProgress>();
      PawnIoSetupRunResult result = await rig.Create().RunAsync(progress, CancellationToken.None);

      Assert.Null(result.FailedStep);
      Assert.Null(result.Error);
      Assert.Equal(new[] { "driver", "modules:modules" }, rig.Calls);
      Assert.Equal(new[] { PawnIoSetupStep.RestartAsAdministrator }, result.Status.RemainingSteps);
      Assert.Equal(new[] { PawnIoSetupStep.InstallDriver, PawnIoSetupStep.InstallModules },
        progress.Items.Select(item => item.Step));
    }

    [Fact]
    public async Task TheRunnerSkipsFinishedSteps() {
      RunnerRig rig = new RunnerRig { Driver = true, Modules = true };
      PawnIoSetupRunResult result = await rig.Create().RunAsync(null, CancellationToken.None);
      Assert.Empty(rig.Calls);
      Assert.Null(result.FailedStep);
    }

    [Fact]
    public async Task TheRunnerStopsWhenTheDriverFails() {
      RunnerRig rig = new RunnerRig {
        DriverResult = await new DriverRig { Winget = null }.Create().InstallAsync(null, CancellationToken.None)
      };
      PawnIoSetupRunResult result = await rig.Create().RunAsync(null, CancellationToken.None);

      Assert.Equal(PawnIoSetupStep.InstallDriver, result.FailedStep);
      Assert.Equal(rig.DriverResult.Message, result.Error);
      Assert.Equal(new[] { "driver" }, rig.Calls);
    }

    [Fact]
    public async Task TheRunnerReportsAModuleFailure() {
      RunnerRig rig = new RunnerRig { Driver = true, ModuleFailure = new PawnIoSetupException("GitHub is down.") };
      PawnIoSetupRunResult result = await rig.Create().RunAsync(null, CancellationToken.None);
      Assert.Equal(PawnIoSetupStep.InstallModules, result.FailedStep);
      Assert.Equal("GitHub is down.", result.Error);
    }

    [Fact]
    public async Task TheRunnerNamesModulesTheReleaseLacks() {
      RunnerRig rig = new RunnerRig { Driver = true, ModulesAppear = false };
      PawnIoSetupRunResult result = await rig.Create().RunAsync(null, CancellationToken.None);
      Assert.Equal(PawnIoSetupStep.InstallModules, result.FailedStep);
      Assert.Contains("IntelMSR and LpcIO", result.Error);
    }
  }
}
