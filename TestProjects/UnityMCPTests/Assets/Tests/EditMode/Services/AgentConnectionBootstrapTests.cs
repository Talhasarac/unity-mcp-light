using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Services
{
    public class AgentConnectionBootstrapTests
    {
        private string _directory;

        [SetUp]
        public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, true);

        private string Request(int port = 0, double? expires = null)
        {
            string id = Guid.NewGuid().ToString("N");
            string path = Path.Combine(_directory, id + ".request.json");
            File.WriteAllText(path, new JObject
            {
                ["id"] = id, ["port"] = port,
                ["expires_at"] = expires ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30
            }.ToString());
            return path;
        }

        [Test]
        public void RequestRoundTrip_CommitsResponseAndRemovesRequest()
        {
            string path = Request(8099);
            AgentConnectionBootstrap.ProcessRequestAsync(path, (port, deadline) =>
            {
                Assert.AreEqual(8099, port);
                return Task.FromResult(new JObject { ["success"] = true, ["port"] = port });
            }).GetAwaiter().GetResult();
            Assert.IsFalse(File.Exists(path));
            var response = JObject.Parse(File.ReadAllText(path.Replace(".request.json", ".response.json")));
            Assert.IsTrue((bool)response["success"]);
            Assert.AreEqual(Path.GetFileName(path).Replace(".request.json", ""), (string)response["id"]);
        }

        [TestCase(-1, false)]
        [TestCase(65536, false)]
        [TestCase(0, true)]
        public void InvalidRequests_DoNotLaunch(int port, bool expired)
        {
            string path = Request(port, expired ? 1 : (double?)null);
            AgentConnectionBootstrap.ProcessRequestAsync(path, (_, __) => throw new Exception("must not run")).GetAwaiter().GetResult();
            var response = JObject.Parse(File.ReadAllText(path.Replace(".request.json", ".response.json")));
            Assert.IsFalse((bool)response["success"]);
            StringAssert.DoesNotContain("must not run", (string)response["error"]);
        }

        [Test]
        public void CanceledRequest_DoesNotLeaveResponse()
        {
            string path = Request();
            AgentConnectionBootstrap.ProcessRequestAsync(path, (_, __) =>
            {
                File.Delete(path);
                return Task.FromResult(new JObject { ["success"] = true });
            }).GetAwaiter().GetResult();
            Assert.IsEmpty(Directory.GetFiles(_directory));
        }

        [Test]
        public void OccupiedPort_AutoSelectsAnotherWithoutClosingListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.ExclusiveAddressUse = true;
            listener.Start();
            try
            {
                int occupied = ((IPEndPoint)listener.LocalEndpoint).Port;
                int selected = AgentConnectionBootstrap.AvailablePort(occupied, true);
                Assert.AreNotEqual(occupied, selected);
                Assert.Throws<SocketException>(() => AgentConnectionBootstrap.AvailablePort(occupied, false));
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, occupied);
            }
            finally { listener.Stop(); }
        }

        private sealed class HealthyBridge : IBridgeControlService
        {
            public bool IsRunning => true;
            public int CurrentPort => 8099;
            public bool IsAutoConnectMode => false;
            public TransportMode? ActiveMode => TransportMode.Http;
            public Task<bool> StartAsync() => throw new Exception("A healthy bridge must not restart");
            public Task StopAsync() => throw new Exception("A healthy bridge must not stop");
            public BridgeVerificationResult Verify(int port) => new BridgeVerificationResult { Success = true };
            public Task<BridgeVerificationResult> VerifyAsync() => Task.FromResult(Verify(8099));
        }

        [Test]
        public void HealthyBridge_IsReusedAndConflictingPortIsRejected()
        {
            var savedBridge = MCPServiceLocator.Bridge;
            string savedSessionUrl = SessionState.GetString(HttpEndpointUtility.SessionLocalUrlKey, "");
            bool savedHttp = EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true);
            string savedScope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, "local");
            bool pinnedStdio = SessionState.GetBool(EditorConfigurationCache.SessionKeyForceStdio, false);
            try
            {
                EditorConfigurationCache.Instance.UnpinStdioForSession();
                EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
                EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, "local");
                EditorConfigurationCache.Instance.Refresh();
                SessionState.SetString(HttpEndpointUtility.SessionLocalUrlKey, "http://127.0.0.1:8099");
                MCPServiceLocator.Register<IBridgeControlService>(new HealthyBridge());
                var response = AgentConnectionBootstrap.EnsureAsync(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30).GetAwaiter().GetResult();
                Assert.IsTrue((bool)response["reused"]);
                Assert.AreEqual("http://127.0.0.1:8099/mcp", (string)response["mcp_url"]);
                Assert.ThrowsAsync<InvalidOperationException>(() => AgentConnectionBootstrap.EnsureAsync(8100, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30));
            }
            finally
            {
                MCPServiceLocator.Register(savedBridge);
                SessionState.SetString(HttpEndpointUtility.SessionLocalUrlKey, savedSessionUrl);
                EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, savedHttp);
                EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, savedScope);
                if (pinnedStdio) EditorConfigurationCache.Instance.PinStdioForSession();
                EditorConfigurationCache.Instance.Refresh();
            }
        }
    }
}
