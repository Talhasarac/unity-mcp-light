using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Server;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>Local, project-scoped startup requests that do not depend on MCP being online.</summary>
    [InitializeOnLoad]
    internal static class AgentConnectionBootstrap
    {
        private const string LaunchPendingKey = "MCPForUnity.AgentHttpLaunchPending";
        private static double _nextPoll;
        private static Task _pending;

        static AgentConnectionBootstrap()
        {
            if (Application.isBatchMode && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH"))) return;
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll || (_pending != null && !_pending.IsCompleted)) return;
            _nextPoll = EditorApplication.timeSinceStartup + 0.5;
            if (HttpBridgeReloadHandler.IsEditorBusy() || HttpBridgeReloadHandler.IsResumePending ||
                SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false)) return;

            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "MCPForUnity", "ConnectionRequests");
            if (!Directory.Exists(directory)) return;
            try
            {
                string path = Directory.EnumerateFiles(directory, "*.request.json")
                    .Where(p => Guid.TryParseExact(Path.GetFileName(p).Replace(".request.json", ""), "N", out _))
                    .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                if (path != null) _pending = ProcessRequestAsync(path, EnsureAsync);
            }
            catch (IOException) { } // The CLI may cancel/remove a request while Unity polls.
        }

        // Keep the request until its response is committed. A domain reload can then retry it.
        internal static async Task ProcessRequestAsync(string path, Func<int, double, Task<JObject>> ensure)
        {
            string id = Path.GetFileName(path).Replace(".request.json", "");
            if (!Guid.TryParseExact(id, "N", out _)) return;
            JObject response;
            try
            {
                var request = JObject.Parse(File.ReadAllText(path));
                if ((string)request["id"] != id) throw new InvalidOperationException("Request ID does not match its filename.");
                int port = (int?)request["port"] ?? -1;
                double expires = (double?)request["expires_at"] ?? 0;
                if (port < 0 || port > 65535) throw new InvalidOperationException("Invalid server port.");
                if (double.IsNaN(expires) || double.IsInfinity(expires) || expires <= UnixNow() || expires > UnixNow() + 601)
                    throw new InvalidOperationException("Connection request expired or has an invalid deadline; retry.");
                response = await ensure(port, expires);
            }
            catch (Exception ex)
            {
                response = new JObject { ["success"] = false, ["error"] = ex.Message };
            }

            // A timed-out CLI removes its request: don't leave an orphan response behind.
            if (!File.Exists(path)) return;
            response["id"] = id;
            string destination = Path.Combine(Path.GetDirectoryName(path), id + ".response.json");
            string temporary = destination + ".tmp";
            try
            {
                File.WriteAllText(temporary, response.ToString());
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);
                File.Delete(path);
            }
            catch (IOException) { }
        }

        private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        internal static int AvailablePort(int preferredPort, bool automatic)
        {
            var listener = new TcpListener(IPAddress.Loopback, preferredPort);
            listener.ExclusiveAddressUse = true;
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (SocketException) when (automatic && preferredPort != 0)
            {
                return AvailablePort(0, false);
            }
            finally { listener.Stop(); }
        }

        private static void RequireLocalHttp()
        {
            if (!EditorConfigurationCache.Instance.UseHttpTransport || HttpEndpointUtility.IsRemoteScope())
                throw new InvalidOperationException("Connection bootstrap requires HTTP Local. The selected stdio/remote transport was left intact.");
        }

        internal static async Task<JObject> EnsureAsync(int requestedPort, double deadline)
        {
            RequireLocalHttp();
            var bridge = MCPServiceLocator.Bridge;
            var server = MCPServiceLocator.Server;
            string originalUrl = HttpEndpointUtility.GetLocalBaseUrl();
            var original = new Uri(originalUrl);
            if (!HttpEndpointUtility.IsLoopbackHost(original.Host))
                throw new InvalidOperationException("Agent bootstrap requires a loopback HTTP Local URL.");

            if (bridge.IsRunning && (await bridge.VerifyAsync()).Success)
            {
                if (requestedPort != 0 && requestedPort != original.Port)
                    throw new InvalidOperationException($"A healthy bridge is already using port {original.Port}; it was left running.");
                return Ready(originalUrl, true);
            }

            // Reuse an existing server only after checking its HTTP identity, not just an open TCP port.
            int preferred = requestedPort == 0 ? original.Port : requestedPort;
            string url = requestedPort == 0 ? originalUrl : $"http://127.0.0.1:{preferred}";
            bool healthy = await IsMcpServerAsync(url);
            bool pendingLaunch = url == originalUrl && (server.IsManagedServerLaunchProcessAlive() ||
                double.TryParse(SessionState.GetString(LaunchPendingKey, ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double launchTime) && UnixNow() - launchTime < 600 &&
                !LaunchProcessExited(preferred));
            if (!healthy && !pendingLaunch)
            {
                int port = AvailablePort(preferred, requestedPort == 0);
                url = $"http://127.0.0.1:{port}";
            }

            RequireLocalHttp();
            if (HttpEndpointUtility.GetLocalBaseUrl() != originalUrl)
                throw new InvalidOperationException("HTTP settings changed during bootstrap; retry.");
            if (UnixNow() >= deadline)
                throw new TimeoutException("Connection request expired before startup; retry.");
            if (bridge.IsRunning) await bridge.StopAsync();
            // SessionState is per editor and survives reloads; saved machine-wide URLs stay intact.
            SessionState.SetString(HttpEndpointUtility.SessionLocalUrlKey, url);
            if (!healthy && !pendingLaunch)
            {
                SessionState.SetString(LaunchPendingKey, UnixNow().ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!server.StartLocalHttpServer(quiet: true, restartExisting: false))
                {
                    SessionState.EraseString(LaunchPendingKey);
                    throw new InvalidOperationException("Local server launch failed. Check uv and the server source in Advanced Settings.");
                }
            }

            while (UnixNow() < deadline)
            {
                RequireLocalHttp();
                if (HttpEndpointUtility.GetLocalBaseUrl() != url)
                    throw new InvalidOperationException("HTTP settings changed during bootstrap; retry.");
                if (healthy || await IsMcpServerAsync(url))
                {
                    SessionState.EraseString(LaunchPendingKey);
                    if ((bridge.IsRunning || await bridge.StartAsync()) && (await bridge.VerifyAsync()).Success)
                    {
                        // A WebSocket alone is not ready: wait for the hub to acknowledge registration.
                        string session = MCPServiceLocator.TransportManager.GetState(TransportMode.Http).SessionId;
                        if (!string.IsNullOrEmpty(session) && session != "pending") return Ready(url, healthy);
                    }
                }
                if (server.HasManagedServerLaunchHandle && !server.IsManagedServerLaunchProcessAlive())
                {
                    SessionState.EraseString(LaunchPendingKey);
                    throw new InvalidOperationException($"Server process exited. Launch log: {server.GetLocalHttpServerLaunchLogPath()}");
                }
                await Task.Delay(500);
                healthy = false;
            }
            throw new TimeoutException($"Timed out waiting for the server and Unity bridge at {url}. Retry after dependencies finish installing.");
        }

        private static JObject Ready(string url, bool reused) => new JObject
        {
            ["success"] = true, ["transport"] = "http", ["port"] = new Uri(url).Port,
            ["mcp_url"] = HttpEndpointUtility.AppendPathSegment(url, "mcp"),
            ["instance"] = ProjectIdentityUtility.GetProjectHash(), ["reused"] = reused
        };

        private static bool LaunchProcessExited(int port)
        {
            var pidFiles = new PidFileManager();
            if (!pidFiles.TryReadPid(pidFiles.GetPidFilePath(port), out int pid)) return false;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException) { return true; }
        }

        private static async Task<bool> IsMcpServerAsync(string url)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
                var body = JObject.Parse(await client.GetStringAsync(HttpEndpointUtility.AppendPathSegment(url, "health")));
                return (string)body["status"] == "healthy" && (string)body["message"] == "MCP for Unity server is running";
            }
            catch (Exception) { return false; }
        }
    }
}
