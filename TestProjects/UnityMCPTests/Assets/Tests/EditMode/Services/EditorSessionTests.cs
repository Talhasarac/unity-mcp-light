using System.Linq;
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
        private static JObject Agent(string id) => new() { ["id"] = id, ["name"] = "Claude", ["transport"] = "http" };
        private static JObject Args(string action) => new() { ["action"] = action, ["lease_seconds"] = 30 };
        [SetUp] public void Setup() { state = new EditorSessionState(); a = Agent("a"); b = Agent("b"); }
        private JObject Acquire(JObject agent, double now = 0, params string[] resources)
        {
            var args = Args("acquire");
            if (resources.Length > 0) args["resource_keys"] = new JArray(resources);
            var result = state.Manage(agent, args, now, false);
            if (result.Value<bool>("success")) agent["lease_tokens"] = new JArray(result["data"]["lease_id"]);
            return result;
        }
        [Test] public void OrdinaryAgentsCanTakeTurnsWithoutAcquiring()
        {
            Assert.IsNull(state.Begin(a, "a1", EditorCommandAccess.Write("*"), 0, false));
            Assert.AreEqual("editor_busy", state.Begin(b, "b1", EditorCommandAccess.Write("*"), 0, false).Value<string>("error"));
            state.End("a1", 1);
            Assert.IsNull(state.Begin(b, "b1", EditorCommandAccess.Write("*"), 1, false));
        }
        [Test] public void ScopedReservationsAndRunningWorkDoNotBlockUnrelatedObjects()
        {
            Assert.IsTrue(Acquire(a, 0, "object:1/10").Value<bool>("success"));
            Assert.IsTrue(Acquire(b, 0, "object:1/20").Value<bool>("success"));
            Assert.IsNull(state.Begin(a, "a1", EditorCommandAccess.Write("object:1/10"), 1, false));
            Assert.IsNull(state.Begin(b, "b1", EditorCommandAccess.Write("object:1/20"), 1, false));
            Assert.AreEqual(2, state.InFlight);
        }
        [Test] public void ParentAndChildConflictButSiblingsDoNot()
        {
            Acquire(a, 0, "object:1/10/11");
            Assert.AreEqual("resource_reserved", state.Begin(b, "parent", EditorCommandAccess.Write("object:1/10"), 1, false).Value<string>("error"));
            Assert.IsNull(state.Begin(b, "sibling", EditorCommandAccess.Write("object:1/10/12"), 1, false));
        }
        [Test] public void ReservationAcquisitionIsAllOrNothing()
        {
            Acquire(a, 0, "asset:assets/a.mat");
            Assert.IsFalse(Acquire(b, 1, "asset:assets/b.mat", "asset:assets/a.mat").Value<bool>("success"));
            Assert.AreEqual(1, state.Reservations.Count);
        }
        [Test] public void IdleReservationsAllowInspectionButExecutingWritesBlockOverlappingReads()
        {
            Acquire(a);
            Assert.IsNull(state.Begin(b, "read", EditorCommandAccess.Read("*"), 1, false));
            state.End("read", 1);
            Assert.IsNull(state.Begin(a, "write", EditorCommandAccess.Write("*"), 2, false));
            Assert.AreEqual("editor_busy", state.Begin(b, "read2", EditorCommandAccess.Read("*"), 3, false).Value<string>("error"));
        }
        [Test] public void ExpiredAndRevokedTokensCannotBecomeAutomaticWrites()
        {
            Acquire(a);
            var old = (JObject)a.DeepClone();
            Assert.AreEqual("editor_lease_required", state.Begin(old, "stale", EditorCommandAccess.Write("*"), 31, false).Value<string>("error"));
            Acquire(a, 32);
            Assert.AreEqual("editor_lease_required", state.Begin(old, "stale2", EditorCommandAccess.Write("*"), 33, false).Value<string>("error"));
            Assert.IsTrue(state.ReleaseFromWindow(34, false));
            Assert.AreEqual("editor_lease_required", state.Begin(a, "revoked", EditorCommandAccess.Write("*"), 35, false).Value<string>("error"));
            Assert.IsNull(state.Begin(b, "fresh", EditorCommandAccess.Write("*"), 36, false));
        }
        [Test] public void CallerTimeoutOrLeaseExpiryCannotUnlockRunningWork()
        {
            Acquire(a);
            state.Begin(a, "work", EditorCommandAccess.Write("*"), 1, false);
            Assert.IsFalse(Acquire(b, 1000).Value<bool>("success"));
            Assert.IsFalse(state.ReleaseFromWindow(1000, false));
            Assert.IsFalse(state.Manage(a, Args("release"), 1000, false).Value<bool>("success"));
            state.End("work", 1000);
            state.End("work", 1001); // Late duplicate completion cannot release a newer command.
            Assert.IsTrue(state.Manage(a, Args("release"), 1001, false).Value<bool>("success"));
            Assert.IsTrue(Acquire(b, 1002).Value<bool>("success"));
        }
        [Test] public void CompilationPreventsHandoverEvenAfterExpiry()
        {
            Acquire(a);
            Assert.IsFalse(state.Manage(b, Args("acquire"), 90, true).Value<bool>("success"));
            Assert.IsFalse(state.ReleaseFromWindow(90, true));
            Assert.IsTrue(Acquire(b, 91).Value<bool>("success"));
        }
        [Test] public void ReservationsPersistAcrossReloadButExecutionsDoNot()
        {
            Acquire(a, 0, "asset:assets/a.mat");
            state.Begin(a, "work", EditorCommandAccess.Write("asset:assets/a.mat"), 1, false);
            state = JsonConvert.DeserializeObject<EditorSessionState>(JsonConvert.SerializeObject(state));
            Assert.AreEqual(0, state.InFlight);
            Assert.AreEqual(1, state.Reservations.Count);
            Assert.IsNull(state.Begin(a, "next", EditorCommandAccess.Write("asset:assets/a.mat"), 2, false));
        }
        [Test] public void ForeignSessionCannotRenewOrReleaseAndTokensAreNotInStatus()
        {
            Acquire(a);
            Assert.IsFalse(state.Manage(b, Args("release"), 1, false).Value<bool>("success"));
            Assert.IsFalse(state.Manage(b, Args("renew"), 1, false).Value<bool>("success"));
            StringAssert.DoesNotContain(state.Reservations.Single().Token, state.Snapshot(1, false).ToString());
        }
        [Test] public void DiagnosticActivityCannotResurrectExpiredIdleReservation()
        {
            Acquire(a);
            state.Observe(a, "read_console", 20, false);
            Assert.AreEqual(50, state.Reservations.Single().ExpiresAt);
            state.Observe(a, "read_console", 60, false);
            Assert.IsFalse(state.Manage(a, Args("renew"), 60, false).Value<bool>("success"));
        }
        [Test] public void UnknownClientsNeedIdentityButNotOwnership()
        {
            Assert.AreEqual("agent_identity_required", state.Begin(null, "work", EditorCommandAccess.Write("*"), 0, false).Value<string>("error"));
            Assert.IsNull(state.Begin(a, "work", EditorCommandAccess.Write("*"), 0, false));
        }
        [Test] public void PresenceStaysBoundedWithoutEvictingReservationHolders()
        {
            state.Touch(a, "acquire", 0);
            Acquire(a);
            for (int i = 0; i < 100; i++) state.Touch(Agent("other" + i), "read_console", i);
            Assert.AreEqual(64, state.Agents.Count);
            Assert.IsTrue(state.Agents.Any(agent => agent.Id == "a"));
        }
    }
}
