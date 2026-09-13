/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>A setup step failed; the message is meant for the user.</summary>
  public sealed class PawnIoSetupException : Exception {
    public PawnIoSetupException(string message) : base(message) {
    }

    public PawnIoSetupException(string message, Exception innerException)
      : base(message, innerException) {
    }
  }

  /// <summary>The downloadable module archive of a PawnIO.Modules release.</summary>
  public sealed class PawnIoReleaseAsset {
    internal PawnIoReleaseAsset(string releaseTag, string name, Uri downloadUrl, long size) {
      ReleaseTag = releaseTag;
      Name = name;
      DownloadUrl = downloadUrl;
      Size = size;
    }

    public string ReleaseTag { get; }
    public string Name { get; }
    public Uri DownloadUrl { get; }
    public long Size { get; }
  }

  public sealed class PawnIoModuleInstallResult {
    internal PawnIoModuleInstallResult(string releaseTag,
      IReadOnlyList<string> installedModules, string directory) {
      ReleaseTag = releaseTag;
      InstalledModules = installedModules;
      Directory = directory;
    }

    public string ReleaseTag { get; }
    public IReadOnlyList<string> InstalledModules { get; }
    public string Directory { get; }
  }

  /// <summary>
  /// Downloads the latest signed modules from the PawnIO.Modules releases on
  /// GitHub and installs the known ones into the per-user modules folder.
  ///
  /// No signature is checked here: the PawnIO driver verifies every module's
  /// signature when it is loaded and refuses anything not signed by the
  /// PawnIO project, so a tampered file can at worst fail to load. What this
  /// class does guard against is the archive itself: only files named like a
  /// known module are taken, directory components in entry names are ignored
  /// so nothing is written outside the modules folder, sizes are bounded, and
  /// every module is written to a temporary file first and then moved over the
  /// old one, so a module file is never left half-written.
  /// </summary>
  public sealed class PawnIoModuleInstaller : IDisposable {

    public const string LatestReleaseUrl =
      "https://api.github.com/repos/namazso/PawnIO.Modules/releases/latest";

    /// <summary>Upper bound for the release archive; 0.2.11 is well under 1 MB.</summary>
    public const long MaxDownloadBytes = 20 * 1024 * 1024;

    internal const long MaxReleaseInfoBytes = 1024 * 1024;

    // The largest module in 0.2.11 is about 55 KB.
    internal const long MaxModuleBytes = 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Module names (without ".bin") published in PawnIO.Modules 0.2.11. A
    /// module added in a later release is ignored until it is listed here.
    /// </summary>
    internal static readonly string[] KnownModules = {
      "AMDFamily0F", "AMDFamily10", "AMDFamily17", "AMDReset", "ARMMSR",
      "DellSMM", "Echo", "IntelMCHBAR", "IntelMSR", "IntelOOBMSM",
      "IntelPCHThermal", "IsaBridgeEC", "LedsValve", "LpcACPIEC", "LpcCrOSEC",
      "LpcIO", "Nvidia", "RyzenSMU", "SmbusI801", "SmbusIntelSkylakeIMC",
      "SmbusNCT6793", "SmbusPIIX4", "ZhaoxinMSR"
    };

    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    public PawnIoModuleInstaller()
      : this(new SocketsHttpHandler {
          AllowAutoRedirect = true,
          MaxAutomaticRedirections = 5,
          AutomaticDecompression = DecompressionMethods.All
        }, DefaultTimeout) {
    }

    /// <summary>
    /// For tests: all network traffic goes through <paramref name="handler"/>,
    /// which the installer then owns.
    /// </summary>
    internal PawnIoModuleInstaller(HttpMessageHandler handler, TimeSpan timeout) {
      // The whole operation, response body included, is bounded by the
      // timeout below; HttpClient's own timeout ends at the headers.
      client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
      this.timeout = timeout;
    }

    public void Dispose() {
      client.Dispose();
    }

    /// <exception cref="PawnIoSetupException">Any failure, explained.</exception>
    /// <exception cref="OperationCanceledException">Cancelled by the caller.</exception>
    public async Task<PawnIoModuleInstallResult> InstallLatestAsync(string directory,
      IProgress<string>? progress, CancellationToken cancellationToken) {

      using (CancellationTokenSource limit =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
        limit.CancelAfter(timeout);
        try {
          progress?.Report("Looking up the latest PawnIO modules on GitHub…");
          byte[] releaseInfo = await DownloadAsync(new Uri(LatestReleaseUrl),
            "application/vnd.github+json", MaxReleaseInfoBytes, null, limit.Token)
            .ConfigureAwait(false);
          PawnIoReleaseAsset asset = SelectAsset(releaseInfo);

          byte[] archive = await DownloadAsync(asset.DownloadUrl,
            "application/octet-stream", MaxDownloadBytes,
            (received, total) => progress?.Report("Downloading " + asset.Name + ": " +
              FormatSize(received) + (total.HasValue ? " of " + FormatSize(total.Value) : "") + "…"),
            limit.Token).ConfigureAwait(false);

          progress?.Report("Installing modules…");
          IReadOnlyList<string> installed;
          using (MemoryStream stream = new MemoryStream(archive, false))
            installed = ExtractModules(stream, directory);
          return new PawnIoModuleInstallResult(asset.ReleaseTag, installed, directory);
        } catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) {
          throw new PawnIoSetupException("GitHub did not respond in time. Check your " +
            "internet connection and try again.", ex);
        } catch (HttpRequestException ex) {
          throw new PawnIoSetupException("GitHub could not be reached. Check your " +
            "internet connection and try again.", ex);
        }
      }
    }

    /// <summary>
    /// Fetches <paramref name="url"/> over HTTPS into memory, refusing bodies
    /// larger than <paramref name="maxBytes"/> whether or not the server
    /// announces a length.
    /// </summary>
    internal async Task<byte[]> DownloadAsync(Uri url, string accept, long maxBytes,
      Action<long, long?>? onProgress, CancellationToken cancellationToken) {

      RequireHttps(url);
      using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url)) {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenHardwareMonitor",
          typeof(PawnIoModuleInstaller).Assembly.GetName().Version?.ToString() ?? "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using (HttpResponseMessage response = await client.SendAsync(request,
          HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)) {

          // Redirects must not leave HTTPS either.
          Uri? final = response.RequestMessage?.RequestUri;
          if (final != null)
            RequireHttps(final);

          if (!response.IsSuccessStatusCode)
            throw new PawnIoSetupException(DescribeStatus(response.StatusCode));

          long? length = response.Content.Headers.ContentLength;
          if (length > maxBytes)
            throw TooLarge();

          using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false))
          using (MemoryStream buffer = new MemoryStream()) {
            byte[] chunk = new byte[81920];
            long received = 0;
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0) {
              received += read;
              if (received > maxBytes)
                throw TooLarge();
              buffer.Write(chunk, 0, read);
              onProgress?.Invoke(received, length);
            }
            return buffer.ToArray();
          }
        }
      }
    }

    private static PawnIoSetupException TooLarge() {
      return new PawnIoSetupException("The PawnIO modules download is much larger " +
        "than expected, so it was stopped. Try again later.");
    }

    private static void RequireHttps(Uri url) {
      if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        throw new PawnIoSetupException("The PawnIO modules download did not use a " +
          "secure connection, so it was stopped.");
    }

    private static string DescribeStatus(HttpStatusCode status) {
      switch (status) {
        case HttpStatusCode.Forbidden:
        case HttpStatusCode.TooManyRequests:
          return "GitHub is limiting downloads from your network right now. Try " +
            "again in an hour.";
        case HttpStatusCode.NotFound:
          return "No PawnIO modules release was found on GitHub. Try again later.";
        default:
          return "GitHub returned an error (" + (int)status + "). Try again later.";
      }
    }

    /// <summary>
    /// Picks the module archive from a GitHub "latest release" response: a
    /// .zip asset, preferring one named "release…" over debug builds.
    /// </summary>
    internal static PawnIoReleaseAsset SelectAsset(byte[] releaseJson) {
      try {
        using (JsonDocument document = JsonDocument.Parse(releaseJson)) {
          JsonElement root = document.RootElement;
          string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) &&
            tagElement.ValueKind == JsonValueKind.String ? tagElement.GetString() ?? "" : "";

          PawnIoReleaseAsset? chosen = null;
          bool chosenIsRelease = false;
          if (root.TryGetProperty("assets", out JsonElement assets) &&
            assets.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement asset in assets.EnumerateArray()) {
              string? name = GetString(asset, "name");
              string? url = GetString(asset, "browser_download_url");
              if (name == null || url == null ||
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;
              bool isRelease = name.StartsWith("release", StringComparison.OrdinalIgnoreCase);
              if (chosen != null && (chosenIsRelease || !isRelease))
                continue;
              if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                continue;
              long size = asset.TryGetProperty("size", out JsonElement sizeElement) &&
                sizeElement.ValueKind == JsonValueKind.Number &&
                sizeElement.TryGetInt64(out long value) ? value : 0;
              chosen = new PawnIoReleaseAsset(tag, name, uri, size);
              chosenIsRelease = isRelease;
            }
          }

          if (chosen == null)
            throw new PawnIoSetupException("The latest PawnIO modules release has no " +
              "download for Windows yet. Try again later.");
          RequireHttps(chosen.DownloadUrl);
          if (chosen.Size > MaxDownloadBytes)
            throw TooLarge();
          return chosen;
        }
      } catch (JsonException ex) {
        throw new PawnIoSetupException("GitHub sent an unexpected answer. Try again later.", ex);
      }
    }

    private static string? GetString(JsonElement element, string property) {
      return element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Installs every known module in <paramref name="zip"/> into
    /// <paramref name="directory"/> and returns their names. All modules are
    /// extracted to temporary files first; existing modules are replaced only
    /// once the whole archive has been read successfully.
    /// </summary>
    internal static IReadOnlyList<string> ExtractModules(Stream zip, string directory) {
      List<(string Module, string Temp, string Target)> staged =
        new List<(string Module, string Temp, string Target)>();
      try {
        Directory.CreateDirectory(directory);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (ZipArchive archive = new ZipArchive(zip, ZipArchiveMode.Read, true)) {
          foreach (ZipArchiveEntry entry in archive.Entries) {
            string? module = GetModuleName(entry.FullName);
            // A second entry with the same name, say in another folder of the
            // archive, must not silently replace the first.
            if (module == null || !seen.Add(module))
              continue;
            if (entry.Length > MaxModuleBytes)
              throw TooLarge();

            string target = Path.Combine(directory, module + ".bin");
            string temp = Path.Combine(directory, module + ".bin." +
              Guid.NewGuid().ToString("N") + ".tmp");
            staged.Add((module, temp, target));
            using (Stream input = entry.Open())
            using (FileStream output = new FileStream(temp, FileMode.CreateNew,
              FileAccess.Write, FileShare.None)) {
              CopyBounded(input, output, MaxModuleBytes);
              output.Flush(true);
            }
          }
        }

        if (staged.Count == 0)
          throw new PawnIoSetupException("The download did not contain any PawnIO " +
            "modules. Try again later.");

        List<string> installed = new List<string>();
        foreach ((string module, string temp, string target) in staged) {
          File.Move(temp, target, true);
          installed.Add(module);
        }
        return installed;
      } catch (InvalidDataException ex) {
        throw new PawnIoSetupException("The PawnIO modules download is damaged. Try again.", ex);
      } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
        throw new PawnIoSetupException("The modules could not be saved to " + directory +
          ". " + ex.Message, ex);
      } finally {
        foreach ((string _, string temp, string _) in staged) {
          try {
            if (File.Exists(temp))
              File.Delete(temp);
          } catch (Exception) {
            // A leftover .tmp file is never loaded as a module.
          }
        }
      }
    }

    /// <summary>
    /// The canonical module name for an archive entry, or null. Only the
    /// part after the last slash or backslash counts, and it must be exactly
    /// a known module name followed by ".bin".
    /// </summary>
    internal static string? GetModuleName(string entryName) {
      int separator = Math.Max(entryName.LastIndexOf('/'), entryName.LastIndexOf('\\'));
      string fileName = entryName.Substring(separator + 1);
      if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        return null;
      string baseName = fileName.Substring(0, fileName.Length - 4);
      foreach (string known in KnownModules)
        if (string.Equals(known, baseName, StringComparison.OrdinalIgnoreCase))
          return known;
      return null;
    }

    // Entry sizes in the archive header can lie; the bytes are counted.
    private static void CopyBounded(Stream input, Stream output, long maxBytes) {
      byte[] buffer = new byte[16384];
      long total = 0;
      int read;
      while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
        total += read;
        if (total > maxBytes)
          throw TooLarge();
        output.Write(buffer, 0, read);
      }
    }

    private static string FormatSize(long bytes) {
      return bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
    }
  }
}
