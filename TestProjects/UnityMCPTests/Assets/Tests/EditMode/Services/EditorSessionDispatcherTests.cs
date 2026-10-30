using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Windows.Components.Connection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Runtime.Helpers;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MCPForUnityTests.Editor.Services
{
    public class EditorSessionDispatcherTests
    {
        private const string StateKey = "MCPForUnity.EditorSessions.v2";
        private const string TestCommand = "session_dispatcher_test_delayed";
        private static readonly FieldInfo StateField = typeof(EditorSessionCoordinator).GetField("state", BindingFlags.NonPublic | BindingFlags.Static);
        private object previousState;
        private string previousStored;
        private Dictionary<FieldInfo, object> previousTestStatus;
        private Dictionary<string, HandlerInfo> handlers;
        private TaskCompletionSource<object> worker;

        [SetUp]
        public void Setup()
        {
            previousState = StateField.GetValue(null);
            previousStored = SessionState.GetString(StateKey, "");
            StateField.SetValue(null, new EditorSessionState());
            previousTestStatus = typeof(TestRunStatus).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(null));
            TestRunStatus.MarkFinished();
            CommandRegistry.Initialize();
            handlers = (Dictionary<string, HandlerInfo>)typeof(CommandRegistry).GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            worker = new TaskCompletionSource<object>();
            handlers.Add(TestCommand, new HandlerInfo(TestCommand, null, _ => worker.Task));
        }

        [TearDown]
        public void Teardown()
        {
            handlers.Remove(TestCommand);
            StateField.SetValue(null, previousState);
            SessionState.SetString(StateKey, previousStored);
            foreach (var item in previousTestStatus) item.Key.SetValue(null, item.Value);
        }

        private static Task<string> Send(string command, JObject identity, string action = null, CancellationToken token = default, JObject parameters = null)
        {
            var args = new JObject { [EditorSessionCoordinator.MetadataKey] = identity.DeepClone() };
            if (action != null) args["action"] = action;
            if (parameters != null) foreach (var property in parameters.Properties()) args[property.Name] = property.Value.DeepClone();
            return TransportCommandDispatcher.ExecuteCommandJsonAsync(new JObject { ["type"] = command, ["params"] = args }.ToString(), token);
        }
        private static JObject Payload(Task<string> task) => (JObject)JObject.Parse(task.Result)["result"];

        [UnityTest]
        public IEnumerator CancelledCallerCannotUnlockHandlerThatIsStillRunning()
        {
            var a = new JObject { ["id"] = "dispatcher-a", ["name"] = "Claude" };
            var b = new JObject { ["id"] = "dispatcher-b", ["name"] = "Claude" };
            var acquire = Send("manage_editor_session", a, "acquire");
            while (!acquire.IsCompleted) yield return null;
            Assert.IsTrue(Payload(acquire).Value<bool>("success"));
            a["lease_tokens"] = new JArray(Payload(acquire)["data"]["lease_id"]);
            using var cancellation = new CancellationTokenSource();
            var request = Send(TestCommand, a, token: cancellation.Token);
            yield return null;
            Assert.AreEqual(1, EditorSessionCoordinator.Snapshot().Value<int>("in_flight"));
            cancellation.Cancel();
            Assert.IsTrue(request.IsCanceled);
            var denied = Send("manage_editor_session", b, "acquire");
            while (!denied.IsCompleted) yield return null;
            Assert.IsFalse(Payload(denied).Value<bool>("success"));
            Assert.IsFalse(EditorSessionCoordinator.ReleaseFromWindow());
            worker.SetResult(new JObject { ["success"] = true });
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (EditorSessionCoordinator.Snapshot().Value<int>("in_flight") > 0 && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.AreEqual(0, EditorSessionCoordinator.Snapshot().Value<int>("in_flight"));
            var release = Send("manage_editor_session", a, "release");
            while (!release.IsCompleted) yield return null;
            Assert.IsTrue(Payload(release).Value<bool>("success"));
            var handover = Send("manage_editor_session", b, "acquire");
            while (!handover.IsCompleted) yield return null;
            Assert.IsTrue(Payload(handover).Value<bool>("success"));
        }

        [UnityTest]
        public IEnumerator AutomaticCommandWaitsUntilCancelledCallersHandlerReallyFinishes()
        {
            const string nextCommand = "session_dispatcher_test_next";
            int invocations = 0;
            handlers.Add(nextCommand, new HandlerInfo(nextCommand, _ => {
                invocations++;
                return new JObject { ["success"] = true };
            }, null));
            try
            {
                var a = new JObject { ["id"] = "auto-a" };
                var b = new JObject { ["id"] = "auto-b" };
                using var cancellation = new CancellationTokenSource();
                var first = Send(TestCommand, a, token: cancellation.Token);
                yield return null;
                cancellation.Cancel();
                var next = Send(nextCommand, b);
                yield return null;
                Assert.AreEqual(0, invocations);
                Assert.IsFalse(next.IsCompleted);
                worker.SetResult(new JObject { ["success"] = true });
                double deadline = EditorApplication.timeSinceStartup + 5;
                while (!next.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(next.IsCompleted);
                Assert.AreEqual(1, invocations);
                Assert.IsTrue(Payload(next).Value<bool>("success"));
            }
            finally { handlers.Remove(nextCommand); }
        }

        [UnityTest]
        public IEnumerator CancelledQueuedMutationNeverStarts()
        {
            const string nextCommand = "session_dispatcher_cancelled_next";
            int invocations = 0;
            handlers.Add(nextCommand, new HandlerInfo(nextCommand, _ => { invocations++; return new JObject(); }, null));
            try
            {
                var a = new JObject { ["id"] = "queue-a" };
                var b = new JObject { ["id"] = "queue-b" };
                var first = Send(TestCommand, a);
                yield return null;
                using var cancellation = new CancellationTokenSource();
                var next = Send(nextCommand, b, token: cancellation.Token);
                yield return null;
                cancellation.Cancel();
                worker.SetResult(new JObject { ["success"] = true });
                double deadline = EditorApplication.timeSinceStartup + 5;
                while (!first.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                yield return null;
                Assert.AreEqual(0, invocations);
                Assert.IsTrue(next.IsCanceled);
            }
            finally { handlers.Remove(nextCommand); }
        }

        [UnityTest]
        public IEnumerator StaleSnapshotRejectsWholeBatchBeforeAnyCommandRuns()
        {
            const string counted = "session_dispatcher_guarded_write";
            int invocations = 0;
            handlers.Add(counted, new HandlerInfo(counted, _ => { invocations++; return new JObject { ["success"] = true }; }, null));
            var go = new GameObject("Snapshot dispatcher test");
            try
            {
                var identity = new JObject { ["id"] = "guard-agent" };
                var observation = EditorRevisionSnapshots.Capture("guard-agent", new JArray("object:" + go.GetInstanceIDCompat()));
                go.transform.position = Vector3.one;
                var request = Send("batch_execute", identity, parameters: new JObject {
                    ["expectedRevision"] = observation["data"]["revision"],
                    ["commands"] = new JArray(new JObject { ["tool"] = counted, ["params"] = new JObject() })
                });
                double deadline = EditorApplication.timeSinceStartup + 5;
                while (!request.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(request.IsCompleted);
                Assert.AreEqual("conflict", Payload(request).Value<string>("error"));
                Assert.AreEqual(0, invocations);
            }
            finally { handlers.Remove(counted); UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void PanelShowsOwnerTaskAndRecentAgentsWithoutExposingToken()
        {
            var identity = new JObject { ["id"] = "panel-test", ["name"] = "Claude", ["transport"] = "http" };
            var granted = EditorSessionCoordinator.Manage(identity, new JObject { ["action"] = "acquire", ["task"] = "Tune vehicle" });
            Assert.IsTrue(granted.Value<bool>("success"));
            var root = new VisualElement();
            new McpSessionsSection(root);
            string text = string.Join("\n", root.Query<Label>().ToList().Select(label => label.text));
            StringAssert.Contains("Owner: Claude", text);
            StringAssert.Contains("Tune vehicle", text);
            StringAssert.Contains("Open stdio sockets", text);
            StringAssert.DoesNotContain(granted["data"].Value<string>("lease_id"), text);
            Assert.IsNotNull(root.Q<VisualElement>("editor-agent-list"));
            Assert.IsTrue(root.Query<Button>().ToList().Any(button => button.text == "Copy agent instructions"));
        }
    }
}
