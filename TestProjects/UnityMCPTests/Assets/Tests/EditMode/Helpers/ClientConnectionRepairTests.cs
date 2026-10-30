using System;
using System.Collections.Generic;
using System.IO;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Clients.Configurators;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Windows.Components.ClientConfig;
using MCPForUnity.External.Tommy;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Helpers
{
    [NonParallelizable]
    public class ClientConnectionRepairTests
    {
        private string directory;
        private IPathResolverService previousPaths;
        private readonly Dictionary<string, (bool exists, string value)> stringPrefs = new();
        private readonly Dictionary<string, (bool exists, bool value)> boolPrefs = new();

        private sealed class TemporaryCodex : CodexMcpConfigurator
        {
            private readonly string path;
            public TemporaryCodex(string path) : base(new McpClient { name = "Codex" }) { this.path = path; }
            public override string GetConfigPath() => path;
        }

        private sealed class NoLauncher : IPathResolverService
        {
            public string GetUvxPath() => throw new InvalidOperationException("HTTP config must not resolve uvx");
            public string GetClaudeCliPath() => null;
            public bool IsPythonDetected() => false;
            public bool IsClaudeCliDetected() => false;
            public void SetUvxPathOverride(string path) { }
            public void SetClaudeCliPathOverride(string path) { }
            public void ClearUvxPathOverride() { }
            public void ClearClaudeCliPathOverride() { }
            public bool HasUvxPathOverride => false;
            public bool HasClaudeCliPathOverride => false;
            public bool HasUvxPathFallback => false;
            public bool TryValidateUvxExecutable(string path, out string version) { version = null; return false; }
        }

        [SetUp]
        public void SetUp()
        {
            foreach (string key in new[] { EditorPrefKeys.HttpBaseUrl, EditorPrefKeys.HttpRemoteBaseUrl, EditorPrefKeys.HttpTransportScope })
                stringPrefs[key] = (EditorPrefs.HasKey(key), EditorPrefs.GetString(key));
            foreach (string key in new[] { EditorPrefKeys.UseHttpTransport, EditorPrefKeys.LockCursorConfig })
                boolPrefs[key] = (EditorPrefs.HasKey(key), EditorPrefs.GetBool(key));
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
            EditorPrefs.SetBool(EditorPrefKeys.LockCursorConfig, false);
            EditorPrefs.SetString(EditorPrefKeys.HttpBaseUrl, "http://127.0.0.1:8088");
            EditorPrefs.SetString(EditorPrefKeys.HttpRemoteBaseUrl, "");
            EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, "local");
            EditorConfigurationCache.Instance.Refresh();
            previousPaths = MCPServiceLocator.Paths;
            MCPServiceLocator.Register<IPathResolverService>(new NoLauncher());
            directory = Path.Combine(Path.GetTempPath(), "unity-mcp-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            MCPServiceLocator.Register(previousPaths);
            foreach (var entry in stringPrefs)
                if (entry.Value.exists) EditorPrefs.SetString(entry.Key, entry.Value.value); else EditorPrefs.DeleteKey(entry.Key);
            foreach (var entry in boolPrefs)
                if (entry.Value.exists) EditorPrefs.SetBool(entry.Key, entry.Value.value); else EditorPrefs.DeleteKey(entry.Key);
            EditorConfigurationCache.Instance.Refresh();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test]
        public void Configure_AlreadyConfiguredCodex_ReplacesStdioAndRemainsConfiguredOnSecondClick()
        {
            string path = Path.Combine(directory, "config.toml");
            File.WriteAllText(path, "model = 'keep-me'\n[mcp_servers.other]\ncommand = 'keep-other'\n"
                + "[mcp_servers.unity-mcp-light]\ncommand = 'uvx'\nargs = ['--transport', 'stdio']\n");
            var client = new TemporaryCodex(path);
            client.Client.SetStatus(McpStatus.Configured);
            client.Client.configuredTransport = ConfiguredTransport.Stdio;
            for (int click = 0; click < 2; click++)
            {
                // Follow the same decision as the Configure button, including its previously cached status.
                if (McpClientConfigSection.ShouldUnregister(client, ConfiguredTransport.Http)) client.Unregister();
                else client.Configure();
                Assert.AreEqual(McpStatus.Configured, client.CheckStatus(attemptAutoRewrite: false));
                Assert.AreEqual(ConfiguredTransport.Http, client.ConfiguredTransport);
            }
            string content = File.ReadAllText(path);
            Assert.IsTrue(CodexConfigHelper.TryParseCodexServer(content, out var command, out var args, out var url));
            Assert.AreEqual("http://127.0.0.1:8088/mcp", url);
            using var reader = new StringReader(content);
            var root = TOML.Parse(reader);
            Assert.AreEqual("keep-me", ((TomlString)root["model"]).Value);
            Assert.AreEqual("keep-other", ((TomlString)root["mcp_servers"]["other"]["command"]).Value);
            var unity = (TomlTable)root["mcp_servers"]["unity-mcp-light"];
            Assert.IsFalse(unity.TryGetNode("command", out _));
            Assert.IsFalse(unity.TryGetNode("args", out _));
        }

        [Test]
        public void Configure_WrongHttpPort_RewritesUrlWithoutUv()
        {
            string path = Path.Combine(directory, "config.toml");
            File.WriteAllText(path, "[mcp_servers.unity-mcp-light]\nurl = 'http://127.0.0.1:8080/mcp'\n");
            var client = new TemporaryCodex(path);
            Assert.AreNotEqual(McpStatus.Configured, client.CheckStatus(attemptAutoRewrite: false));
            client.Configure();
            Assert.AreEqual(McpStatus.Configured, client.CheckStatus(attemptAutoRewrite: false));
        }

        [Test]
        public void Configure_InvalidToml_LeavesOriginalFileIntact()
        {
            string path = Path.Combine(directory, "config.toml");
            const string invalid = "model = \"unfinished\n";
            File.WriteAllText(path, invalid);
            Assert.Throws<InvalidOperationException>(() => new TemporaryCodex(path).Configure());
            Assert.AreEqual(invalid, File.ReadAllText(path));
        }

        [Test]
        public void Configure_LockedFile_ReportsFailureWithoutChangingIt()
        {
            string path = Path.Combine(directory, "config.toml");
            const string original = "model = 'keep-me'\n";
            File.WriteAllText(path, original);
            EditorPrefs.SetBool(EditorPrefKeys.LockCursorConfig, true);
            Assert.Throws<InvalidOperationException>(() => new TemporaryCodex(path).Configure());
            Assert.AreEqual(original, File.ReadAllText(path));
        }

        [Test]
        public void ConfigureAction_JsonClientWithMismatch_DoesNotUnregister()
        {
            var client = new CursorConfigurator();
            client.Client.SetStatus(McpStatus.Configured);
            client.Client.configuredTransport = ConfiguredTransport.Stdio;
            Assert.IsFalse(McpClientConfigSection.ShouldUnregister(client, ConfiguredTransport.Http));
            Assert.IsTrue(McpClientConfigSection.ShouldUnregister(client, ConfiguredTransport.Stdio));
        }

        [Test]
        public void CodexConfigPath_UsesExplicitCodexHome()
        {
            string previous = Environment.GetEnvironmentVariable("CODEX_HOME");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", directory);
                Assert.AreEqual(Path.Combine(directory, "config.toml"), new CodexConfigurator().GetConfigPath());
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
        }
    }
}
