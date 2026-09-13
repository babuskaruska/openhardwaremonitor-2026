/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2012 Prince Samuel <prince.samuel@gmail.com>
  Copyright (C) 2012-2013 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenHardwareMonitor.GUI;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.Utilities {

  /// <summary>
  /// The embedded web server: the dashboard, a JSON API, and the legacy
  /// data.json that third-party integrations consume.
  ///
  /// Changes from the 2012 original, which was reachable by anyone on the
  /// network and injectable:
  ///  - Binds to localhost by default. It used to bind http://+:port/, every
  ///    interface, from an elevated process, with no authentication at all.
  ///  - Remote connections are opt-in and need an access token.
  ///  - JSON is written with Utf8JsonWriter. It used to be string
  ///    concatenation with no escaping, and sensor names are user-editable, so
  ///    a renamed sensor could break the payload or inject script into the
  ///    page that rendered it.
  ///  - Only a fixed set of embedded files is served, under a strict
  ///    Content-Security-Policy. An unused helper that read arbitrary paths
  ///    from disk was removed.
  ///  - The sensor tree belongs to the UI thread. Snapshots used to be read
  ///    from the listener thread while the update timer mutated the tree;
  ///    they are now marshalled onto the UI thread.
  /// </summary>
  public class HttpServer {

    private const string RemoteSetting = "webServer.allowRemote";
    private const string TokenSetting = "webServer.accessToken";

    // Shorter than the join in StopHTTPListener, so a request waiting on the
    // UI thread while that thread is shutting the server down gives up first
    // instead of deadlocking.
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private const string ContentSecurityPolicy =
      "default-src 'self'; img-src 'self'; style-src 'self'; " +
      "script-src 'self'; connect-src 'self'; base-uri 'none'; " +
      "form-action 'none'; frame-ancestors 'none'";

    private static readonly string[] WebFiles = {
      "index.html", "app.js", "app.css", "images/transparent.png"
    };

    private readonly object sync = new object();
    private readonly Node root;
    private readonly Computer computer;
    private readonly ISynchronizeInvoke uiThread;
    private readonly PersistentSettings settings;
    private readonly Dictionary<string, string> resources;
    private readonly string accessToken;

    private HttpListener? listener;
    private Thread? listenerThread;
    private int listenerPort;
    private bool allowRemoteConnections;

    public HttpServer(Node root, Computer computer, ISynchronizeInvoke uiThread,
      PersistentSettings settings) {
      this.root = root;
      this.computer = computer;
      this.uiThread = uiThread;
      this.settings = settings;

      listenerPort = settings.GetValue("listenerPort", 8085);
      allowRemoteConnections = settings.GetValue(RemoteSetting, false);

      string token = settings.GetValue(TokenSetting, "");
      if (token.Length < 32) {
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
          .ToLowerInvariant();
        settings.SetValue(TokenSetting, token);
      }
      accessToken = token;

      resources = IndexResources();
    }

    public bool PlatformNotSupported {
      get { return !HttpListener.IsSupported; }
    }

    public bool IsListening {
      get {
        lock (sync)
          return listener != null && listener.IsListening;
      }
    }

    /// <summary>Why the last start attempt failed, for display.</summary>
    public string? LastError { get; private set; }

    public int ListenerPort {
      get { return listenerPort; }
      set { listenerPort = value; }
    }

    /// <summary>
    /// Whether devices other than this computer may connect. Restarts the
    /// listener if it is running; check <see cref="IsListening"/> and
    /// <see cref="LastError"/> afterwards.
    /// </summary>
    public bool AllowRemoteConnections {
      get { return allowRemoteConnections; }
      set {
        if (allowRemoteConnections == value)
          return;
        allowRemoteConnections = value;
        settings.SetValue(RemoteSetting, value);
        if (IsListening) {
          StopHTTPListener();
          StartHTTPListener();
        }
      }
    }

    public string GetUrl(string host, int port, bool includeToken) {
      string url = "http://" + host + ":" +
        port.ToString(CultureInfo.InvariantCulture) + "/";
      return includeToken ? url + "?token=" + accessToken : url;
    }

    public bool StartHTTPListener() {
      if (PlatformNotSupported) {
        LastError = "HTTP listening is not supported on this system.";
        return false;
      }

      lock (sync) {
        if (listener != null && listener.IsListening)
          return true;

        // A fresh listener each time: one whose Start failed cannot be reused.
        HttpListener candidate = new HttpListener();
        candidate.IgnoreWriteExceptions = true;
        candidate.Prefixes.Add(allowRemoteConnections
          ? "http://+:" + listenerPort.ToString(CultureInfo.InvariantCulture) + "/"
          : GetUrl("localhost", listenerPort, false));

        try {
          candidate.Start();
        } catch (HttpListenerException ex) {
          try { candidate.Close(); } catch (Exception) { }
          LastError = DescribeStartFailure(ex);
          return false;
        }

        LastError = null;
        listener = candidate;
        listenerThread = new Thread(() => HandleRequests(candidate)) {
          IsBackground = true,
          Name = "HTTP listener"
        };
        listenerThread.Start();
        return true;
      }
    }

    private string DescribeStartFailure(HttpListenerException ex) {
      const int ErrorAccessDenied = 5;
      const int ErrorSharingViolation = 32;
      const int ErrorAlreadyExists = 183;

      switch (ex.ErrorCode) {
        case ErrorAccessDenied:
          return allowRemoteConnections
            ? "Listening on the network requires running Open Hardware Monitor " +
              "as administrator. Turn off Allow Remote Connections to serve " +
              "this computer only, which needs no special rights."
            : "Access to the port was denied.";
        case ErrorSharingViolation:
        case ErrorAlreadyExists:
          return "Port " + listenerPort.ToString(CultureInfo.InvariantCulture) +
            " is already in use by another program.";
        default:
          return ex.Message;
      }
    }

    public bool StopHTTPListener() {
      HttpListener? current;
      Thread? thread;
      lock (sync) {
        current = listener;
        thread = listenerThread;
        listener = null;
        listenerThread = null;
      }

      // This used to call Thread.Abort, which throws on .NET 5 and later.
      // Closing the listener completes the pending BeginGetContext, and the
      // request loop exits once IsListening goes false.
      if (current != null) {
        try {
          current.Close();
        } catch (ObjectDisposedException) {
        }
      }
      if (thread != null && thread != Thread.CurrentThread)
        thread.Join(StopTimeout);
      return true;
    }

    public void Quit() {
      StopHTTPListener();
    }

    private void HandleRequests(HttpListener active) {
      while (active.IsListening) {
        try {
          IAsyncResult pending = active.BeginGetContext(ListenerCallback, active);
          pending.AsyncWaitHandle.WaitOne();
        } catch (Exception ex) when (ex is HttpListenerException ||
          ex is ObjectDisposedException || ex is InvalidOperationException) {
          break;
        }
      }
    }

    private void ListenerCallback(IAsyncResult result) {
      HttpListener? active = result.AsyncState as HttpListener;
      if (active == null || !active.IsListening)
        return;

      HttpListenerContext context;
      try {
        context = active.EndGetContext(result);
      } catch (Exception) {
        return;
      }

      try {
        HandleContext(context);
      } catch (Exception) {
        try {
          context.Response.Abort();
        } catch (Exception) {
        }
      }
    }

    private void HandleContext(HttpListenerContext context) {
      HttpListenerRequest request = context.Request;
      HttpListenerResponse response = context.Response;

      response.AddHeader("X-Content-Type-Options", "nosniff");
      response.AddHeader("Referrer-Policy", "no-referrer");
      response.AddHeader("X-Frame-Options", "DENY");

      bool headOnly = request.HttpMethod == "HEAD";

      // HttpListener leaves Url null for a request line it cannot turn into a
      // URI. Reject that as malformed up front; otherwise the authorization
      // check below would see no loopback URL and treat a local client as
      // remote.
      if (request.Url == null) {
        SendText(response, 400, "Bad request.", headOnly);
        return;
      }

      if (request.HttpMethod != "GET" && !headOnly) {
        response.AddHeader("Allow", "GET, HEAD");
        SendText(response, 405, "Method not allowed.", headOnly);
        return;
      }

      switch (Authorize(request)) {
        case Access.RemoteDisabled:
          SendText(response, 403, "Remote connections are disabled.", headOnly);
          return;
        case Access.TokenRequired:
          SendText(response, 401, "An access token is required.", headOnly);
          return;
      }

      string path = Uri.UnescapeDataString(request.Url?.AbsolutePath ?? "/")
        .TrimStart('/');

      switch (path) {
        case "api/v1/sensors":
          SendJson(response, BuildSensorsDocument, headOnly);
          return;
        case "data.json":
          SendJson(response, BuildLegacyDocument, headOnly);
          return;
      }

      if (resources.TryGetValue(path, out string? resourceName)) {
        SendResource(response, resourceName, path, headOnly);
        return;
      }

      SendText(response, 404, "Not found.", headOnly);
    }

    private enum Access {
      Granted,
      RemoteDisabled,
      TokenRequired
    }

    private Access Authorize(HttpListenerRequest request) {
      // Local means both a loopback peer AND a loopback Host header. Checking
      // the peer alone is not enough in remote mode: with DNS rebinding, a web
      // page the user visits can make their own browser send requests from
      // this machine under an attacker's host name.
      bool local = request.IsLocal && request.Url != null && request.Url.IsLoopback;
      if (local)
        return Access.Granted;

      if (!allowRemoteConnections)
        return Access.RemoteDisabled;

      string? supplied = request.QueryString["token"];
      if (supplied == null) {
        string? authorization = request.Headers["Authorization"];
        if (authorization != null &&
          authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
          supplied = authorization.Substring(7).Trim();
      }

      if (supplied != null && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(accessToken)))
        return Access.Granted;

      return Access.TokenRequired;
    }

    // ---- snapshots ------------------------------------------------------------

    private byte[]? OnUiThread(Func<byte[]> build) {
      if (!uiThread.InvokeRequired)
        return build();

      IAsyncResult pending;
      try {
        pending = uiThread.BeginInvoke(build, null);
      } catch (Exception ex) when (ex is InvalidOperationException ||
        ex is ObjectDisposedException) {
        return null;   // the form is closing
      }

      if (!pending.AsyncWaitHandle.WaitOne(SnapshotTimeout))
        return null;

      try {
        return uiThread.EndInvoke(pending) as byte[];
      } catch (Exception) {
        return null;
      }
    }

    /// <summary>
    /// /api/v1/sensors: a flat list of hardware, each with its sensors, as
    /// plain numbers in canonical units. This is the format to integrate with.
    /// </summary>
    private byte[] BuildSensorsDocument() {
      using (MemoryStream stream = new MemoryStream())
      using (Utf8JsonWriter w = new Utf8JsonWriter(stream)) {
        w.WriteStartObject();
        w.WriteNumber("apiVersion", 1);
        w.WriteString("application", "Open Hardware Monitor");
        w.WriteString("version", GetVersion());
        w.WriteString("machine", Environment.MachineName);
        w.WriteString("timestamp", DateTimeOffset.UtcNow);
        w.WriteString("accessTier", HardwareAccess.Tier.ToString());
        string? note = HardwareAccess.UnavailableReason;
        if (note != null)
          w.WriteString("accessNote", note);
        else
          w.WriteNull("accessNote");

        w.WriteStartArray("hardware");
        foreach (IHardware hardware in computer.Hardware)
          WriteHardware(w, hardware);
        w.WriteEndArray();

        w.WriteEndObject();
        w.Flush();
        return stream.ToArray();
      }
    }

    private static void WriteHardware(Utf8JsonWriter w, IHardware hardware) {
      w.WriteStartObject();
      w.WriteString("id", hardware.Identifier.ToString());
      w.WriteString("name", hardware.Name);
      w.WriteString("type", hardware.HardwareType.ToString());
      if (hardware.Parent != null)
        w.WriteString("parentId", hardware.Parent.Identifier.ToString());
      else
        w.WriteNull("parentId");

      w.WriteStartArray("sensors");
      foreach (ISensor sensor in hardware.Sensors) {
        w.WriteStartObject();
        w.WriteString("id", sensor.Identifier.ToString());
        w.WriteString("name", sensor.Name);
        w.WriteString("type", sensor.SensorType.ToString());
        w.WriteNumber("index", sensor.Index);
        w.WriteString("unit", GetUnit(sensor.SensorType));
        WriteReading(w, "value", sensor.Value);
        WriteReading(w, "min", sensor.Min);
        WriteReading(w, "max", sensor.Max);
        w.WriteEndObject();
      }
      w.WriteEndArray();
      w.WriteEndObject();

      foreach (IHardware subHardware in hardware.SubHardware)
        WriteHardware(w, subHardware);
    }

    private static void WriteReading(Utf8JsonWriter w, string name, float? value) {
      // Utf8JsonWriter rejects NaN and infinity, which JSON cannot represent.
      if (value.HasValue && float.IsFinite(value.Value))
        w.WriteNumber(name, Math.Round((double)value.Value, 3));
      else
        w.WriteNull(name);
    }

    /// <summary>
    /// Canonical unit per sensor type. These match what the desktop tree shows,
    /// except that temperatures are always Celsius here.
    /// </summary>
    private static string GetUnit(SensorType type) {
      switch (type) {
        case SensorType.Voltage: return "V";
        case SensorType.Clock: return "MHz";
        case SensorType.Temperature: return "°C";
        case SensorType.Load: return "%";
        case SensorType.Fan: return "RPM";
        case SensorType.Flow: return "L/h";
        case SensorType.Control: return "%";
        case SensorType.Level: return "%";
        case SensorType.Power: return "W";
        case SensorType.Data: return "GB";
        case SensorType.SmallData: return "MB";
        case SensorType.Throughput: return "MB/s";
        default: return "";
      }
    }

    private static string GetVersion() {
      Assembly assembly = typeof(HttpServer).Assembly;
      AssemblyInformationalVersionAttribute? informational =
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
      return informational?.InformationalVersion ??
        assembly.GetName().Version?.ToString() ?? "";
    }

    /// <summary>
    /// data.json, byte-for-byte the same shape as before - field names, order
    /// and preformatted strings - because Rainmeter skins, Home Assistant
    /// integrations and scripts parse it. It is now correctly escaped.
    /// </summary>
    private byte[] BuildLegacyDocument() {
      using (MemoryStream stream = new MemoryStream())
      using (Utf8JsonWriter w = new Utf8JsonWriter(stream)) {
        w.WriteStartObject();
        w.WriteNumber("id", 0);
        w.WriteString("Text", "Sensor");
        w.WriteStartArray("Children");
        int nextId = 1;
        WriteLegacyNode(w, root, ref nextId);
        w.WriteEndArray();
        w.WriteString("Min", "Min");
        w.WriteString("Value", "Value");
        w.WriteString("Max", "Max");
        w.WriteString("ImageURL", "");
        w.WriteEndObject();
        w.Flush();
        return stream.ToArray();
      }
    }

    private static void WriteLegacyNode(Utf8JsonWriter w, Node node,
      ref int nextId) {
      w.WriteStartObject();
      w.WriteNumber("id", nextId++);
      w.WriteString("Text", node.Text);
      w.WriteStartArray("Children");
      foreach (Node child in node.Nodes)
        WriteLegacyNode(w, child, ref nextId);
      w.WriteEndArray();

      SensorNode? sensorNode = node as SensorNode;
      if (sensorNode != null) {
        w.WriteString("Min", sensorNode.Min);
        w.WriteString("Value", sensorNode.Value);
        w.WriteString("Max", sensorNode.Max);
        w.WriteString("ImageURL", "images/transparent.png");
      } else {
        w.WriteString("Min", "");
        w.WriteString("Value", "");
        w.WriteString("Max", "");
        HardwareNode? hardwareNode = node as HardwareNode;
        TypeNode? typeNode = node as TypeNode;
        string image = hardwareNode != null ? GetHardwareImageFile(hardwareNode)
          : typeNode != null ? GetTypeImageFile(typeNode)
          : "computer.png";
        w.WriteString("ImageURL", "images_icon/" + image);
      }

      w.WriteEndObject();
    }

    private static string GetHardwareImageFile(HardwareNode node) {
      switch (node.Hardware.HardwareType) {
        case HardwareType.CPU: return "cpu.png";
        case HardwareType.GpuNvidia: return "nvidia.png";
        case HardwareType.GpuAti: return "ati.png";
        case HardwareType.HDD: return "hdd.png";
        case HardwareType.Heatmaster: return "bigng.png";
        case HardwareType.Mainboard: return "mainboard.png";
        case HardwareType.SuperIO: return "chip.png";
        case HardwareType.TBalancer: return "bigng.png";
        case HardwareType.RAM: return "ram.png";
        default: return "cpu.png";
      }
    }

    private static string GetTypeImageFile(TypeNode node) {
      switch (node.SensorType) {
        case SensorType.Voltage: return "voltage.png";
        case SensorType.Clock: return "clock.png";
        case SensorType.Load: return "load.png";
        case SensorType.Temperature: return "temperature.png";
        case SensorType.Fan: return "fan.png";
        case SensorType.Flow: return "flow.png";
        case SensorType.Control: return "control.png";
        case SensorType.Level: return "level.png";
        case SensorType.Power: return "power.png";
        case SensorType.Data: return "data.png";
        case SensorType.SmallData: return "data.png";
        case SensorType.Throughput: return "throughput.png";
        case SensorType.Factor: return "factor.png";
        default: return "power.png";
      }
    }

    // ---- files ----------------------------------------------------------------

    /// <summary>
    /// Maps request paths to embedded resources. Only these paths can ever be
    /// served; nothing is looked up from the request itself.
    /// </summary>
    private static Dictionary<string, string> IndexResources() {
      Dictionary<string, string> map =
        new Dictionary<string, string>(StringComparer.Ordinal);
      HashSet<string> names = new HashSet<string>(
        typeof(HttpServer).Assembly.GetManifestResourceNames(),
        StringComparer.Ordinal);

      foreach (string file in WebFiles) {
        string resource = "OpenHardwareMonitor.Resources.Web." +
          file.Replace('/', '.');
        if (names.Contains(resource))
          map[file] = resource;
      }
      if (map.TryGetValue("index.html", out string? index))
        map[""] = index;

      // Hardware and sensor type icons, e.g. Resources/cpu.png, which the
      // legacy data.json refers to as images_icon/cpu.png.
      const string iconPrefix = "OpenHardwareMonitor.Resources.";
      foreach (string name in names) {
        if (!name.StartsWith(iconPrefix, StringComparison.Ordinal) ||
          !name.EndsWith(".png", StringComparison.Ordinal))
          continue;
        string file = name.Substring(iconPrefix.Length);
        if (file.IndexOf('.') != file.Length - 4)
          continue;   // nested resource such as Web.images.transparent.png
        map["images_icon/" + file] = name;
      }

      return map;
    }

    private static void SendResource(HttpListenerResponse response,
      string resourceName, string path, bool headOnly) {
      byte[] body;
      using (Stream? stream =
        typeof(HttpServer).Assembly.GetManifestResourceStream(resourceName)) {
        if (stream == null) {
          SendText(response, 404, "Not found.", headOnly);
          return;
        }
        using (MemoryStream memory = new MemoryStream()) {
          stream.CopyTo(memory);
          body = memory.ToArray();
        }
      }

      string extension = Path.GetExtension(path.Length == 0 ? "index.html" : path)
        .ToLowerInvariant();
      string contentType;
      switch (extension) {
        case ".html": contentType = "text/html; charset=utf-8"; break;
        case ".js": contentType = "text/javascript; charset=utf-8"; break;
        case ".css": contentType = "text/css; charset=utf-8"; break;
        case ".png": contentType = "image/png"; break;
        default: contentType = "application/octet-stream"; break;
      }

      if (extension == ".html")
        response.AddHeader("Content-Security-Policy", ContentSecurityPolicy);
      response.AddHeader("Cache-Control",
        extension == ".png" ? "public, max-age=86400" : "no-cache");

      Send(response, 200, contentType, body, headOnly);
    }

    private void SendJson(HttpListenerResponse response, Func<byte[]> build,
      bool headOnly) {
      byte[]? body = OnUiThread(build);
      if (body == null) {
        SendText(response, 503, "Sensor data is temporarily unavailable.",
          headOnly);
        return;
      }
      response.AddHeader("Cache-Control", "no-store");
      Send(response, 200, "application/json; charset=utf-8", body, headOnly);
    }

    private static void SendText(HttpListenerResponse response, int status,
      string text, bool headOnly) {
      Send(response, status, "text/plain; charset=utf-8",
        Encoding.UTF8.GetBytes(text), headOnly);
    }

    private static void Send(HttpListenerResponse response, int status,
      string contentType, byte[] body, bool headOnly) {
      try {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        if (!headOnly)
          response.OutputStream.Write(body, 0, body.Length);
      } catch (Exception ex) when (ex is HttpListenerException ||
        ex is ObjectDisposedException || ex is InvalidOperationException ||
        ex is IOException) {
        // The client went away; nothing to report to.
      } finally {
        try {
          response.Close();
        } catch (Exception) {
        }
      }
    }
  }
}
