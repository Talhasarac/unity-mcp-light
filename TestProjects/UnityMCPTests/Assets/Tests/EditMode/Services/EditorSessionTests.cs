using MCPForUnity.Editor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class EditorSessionTests
    {
        private EditorSessionState state;
        private JObject a, b;
        private static JObject Args(string action) => new JObject { ["action"] = action, ["lease_seconds"] = 30 };
        private static JObject Agent(string id) => new JObject { ["id"] = id, ["name"] = "Claude", ["transport"] = "http" };

        [SetUp] public void Setup() { state = new EditorSessionState(); a = Agent("a"); b = Agent("b"); }

        private JObject Acquire(JObject agent, double now = 0)
        {
            var result = state.Manage(agent, Args("acquire"), now, false);
            if (result.Value<bool>("success")) agent["lease_tokens"] = new JArray(result["data"]["lease_id"]);
            return result;
        }

        [Test] public void IdenticalNamesCannotShareOwnership()
        {
            Assert.IsTrue(Acquire(a).Value<bool>("success"));
            Assert.AreEqual("editor_owned", Acquire(b).Value<string>("error"));
            Assert.AreEqual("editor_owned", state.Begin(b, "manage_scene", 1, false).Value<string>("error"));
            Assert.IsNull(state.Begin(a, "manage_scene", 1, false));
        }

        [Test] public void UnknownOrLegacyClientCannotBypassLease()
        {
            Assert.AreEqual("editor_lease_required", state.Begin(null, "custom_tool", 0, false).Value<string>("error"));
            Assert.AreEqual("agent_identity_required", state.Manage(null, Args("acquire"), 0, false).Value<string>("error"));
        }

        [Test] public void ExpiredQueuedCommandRequiresNewAcquisitionAndToken()
        {
            Acquire(a);
            var old = (JObject)a.DeepClone();
            Assert.AreEqual("editor_lease_required", state.Begin(old, "manage_scene", 31, false).Value<string>("error"));
            Acquire(a, 32);
            Assert.AreEqual("editor_lease_required", state.Begin(old, "manage_scene", 33, false).Value<string>("error"));
            Assert.IsNull(state.Begin(a, "manage_scene", 33, false));
        }

        [Test] public void SameSessionCannotOverlapCommandsOrReleaseExecutingWork()
        {
            Acquire(a);
            Assert.IsNull(state.Begin(a, "batch_execute", 1, false));
            Assert.AreEqual("editor_busy", state.Begin(a, "manage_scene", 2, false).Value<string>("error"));
            Assert.IsFalse(state.Manage(a, Args("release"), 2, false).Value<bool>("success"));
            Assert.IsFalse(state.ReleaseFromWindow(2, false));
            // Caller timeout does not invoke End. Even after TTL, another agent cannot acquire.
            Assert.IsFalse(Acquire(b, 1000).Value<bool>("success"));
            state.End(1000);
            Assert.IsTrue(state.Manage(a, Args("release"), 1001, false).Value<bool>("success"));
            Assert.IsTrue(Acquire(b, 1002).Value<bool>("success"));
        }

        [Test] public void CompileOrTestsPreventExpiredLeaseHandover()
        {
            Acquire(a);
            Assert.IsFalse(state.Manage(b, Args("acquire"), 90, true).Value<bool>("success"));
            Assert.IsFalse(state.ReleaseFromWindow(90, true));
            Assert.IsTrue(Acquire(b, 91).Value<bool>("success"));
        }

        [Test] public void OwnershipAndTokenSurviveSerializationForDomainReload()
        {
            Acquire(a);
            state.Begin(a, "refresh_unity", 1, false);
            state = JsonConvert.DeserializeObject<EditorSessionState>(JsonConvert.SerializeObject(state));
            Assert.AreEqual(0, state.InFlight);
            Assert.IsFalse(Acquire(b, 2).Value<bool>("success"));
            Assert.IsNull(state.Begin(a, "manage_scene", 2, false));
        }

        [Test] public void ManualRecoveryFencesOutPreviousOwnersQueuedCommands()
        {
            Acquire(a);
            Assert.IsTrue(state.ReleaseFromWindow(1, false));
            Acquire(b, 2);
            Assert.AreEqual("editor_owned", state.Begin(a, "manage_scene", 3, false).Value<string>("error"));
        }

        [Test] public void StatusNeverExposesFencingTokenAndCannotReleaseOtherOwner()
        {
            Acquire(a);
            Assert.IsFalse(state.Manage(b, Args("release"), 1, false).Value<bool>("success"));
            Assert.IsFalse(state.Manage(b, Args("renew"), 1, false).Value<bool>("success"));
            StringAssert.DoesNotContain(state.LeaseId, state.Manage(b, Args("status"), 1, false).ToString());
        }

        [Test] public void ActivityRenewsLiveLeaseButDoesNotResurrectExpiredIdleLease()
        {
            Acquire(a);
            state.Observe(a, "read_console", 20, false);
            Assert.AreEqual(50, state.ExpiresAt);
            state.Observe(a, "manage_scene", 60, false);
            Assert.AreEqual(50, state.ExpiresAt);
            Assert.IsFalse(state.Manage(a, Args("renew"), 60, false).Value<bool>("success"));
        }

        [TestCase("batch_execute", null, false)]
        [TestCase("unknown_custom_tool", null, false)]
        [TestCase("read_console", "clear", false)]
        [TestCase("read_console", "get", true)]
        [TestCase("get_test_job", null, true)]
        public void OnlyExplicitDiagnosticsBypassOwnership(string command, string action, bool expected)
        {
            Assert.AreEqual(expected, EditorSessionCoordinator.IsDiagnostic(command, new JObject { ["action"] = action }, false));
        }

        [Test] public void PresenceIsBoundedWithoutEvictingOwner()
        {
            Acquire(a);
            for (int i = 0; i < 100; i++) state.Touch(Agent("other" + i), "read_console", i);
            Assert.AreEqual(64, state.Agents.Count);
            Assert.IsTrue(state.Agents.Exists(agent => agent.Id == "a"));
        }
    }
}
