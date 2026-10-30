using MCPForUnity.Editor.Services;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Services
{
    public class EditorCoordinationTests
    {
        private GameObject a, b;
        [SetUp] public void Setup() { a = new GameObject("Coordination A"); b = new GameObject("Coordination B"); }
        [TearDown] public void Teardown() { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        private JArray Resources() => new("object:" + a.GetInstanceIDCompat());
        private string Capture() => EditorRevisionSnapshots.Capture("a", Resources())["data"].Value<string>("revision");

        [Test] public void UnrelatedChangesDoNotInvalidateSnapshot()
        {
            string token = Capture();
            b.transform.position = Vector3.one;
            Assert.IsNull(EditorRevisionSnapshots.Consume("a", token));
        }
        [Test] public void HumanTransformEditInvalidatesSnapshot()
        {
            string token = Capture();
            a.transform.position = Vector3.one;
            Assert.AreEqual("conflict", EditorRevisionSnapshots.Consume("a", token).Value<string>("error"));
        }
        [Test] public void AncestorMovementInvalidatesWorldPoseObservation()
        {
            a.transform.SetParent(b.transform);
            string token = Capture();
            b.transform.position = Vector3.one;
            Assert.IsNotNull(EditorRevisionSnapshots.Consume("a", token));
        }
        [Test] public void SnapshotsCannotBeSharedOrReused()
        {
            string token = Capture();
            Assert.IsNotNull(EditorRevisionSnapshots.Consume("b", token));
            Assert.IsNull(EditorRevisionSnapshots.Consume("a", token));
            Assert.IsNotNull(EditorRevisionSnapshots.Consume("a", token));
        }
        [Test] public void DeletedResourcesConflictInsteadOfAuthorizingOldIds()
        {
            string token = Capture();
            Object.DestroyImmediate(a);
            Assert.IsNotNull(EditorRevisionSnapshots.Consume("a", token));
        }
        [Test] public void OnlyBoundedTransformChangesUseObjectScope()
        {
            var args = new JObject { ["action"] = "modify", ["target"] = a.GetInstanceIDCompat(), ["position"] = new JArray(1, 2, 3) };
            var bounded = EditorCommandAccess.Describe("manage_gameobject", args);
            CollectionAssert.DoesNotContain(bounded.Writes, "*");
            args["componentProperties"] = new JObject();
            CollectionAssert.Contains(EditorCommandAccess.Describe("manage_gameobject", args).Writes, "*");
            CollectionAssert.Contains(EditorCommandAccess.Describe("unknown_custom_tool", new JObject()).Writes, "*");
        }
        [Test] public void InspectionsAreReadsAndConsoleClearIsNotDiagnostic()
        {
            Assert.IsFalse(EditorCommandAccess.Describe("find_gameobjects", new JObject()).IsWrite);
            Assert.IsTrue(EditorCommandAccess.Describe("read_console", new JObject { ["action"] = "get" }).Diagnostic);
            Assert.IsFalse(EditorCommandAccess.Describe("read_console", new JObject { ["action"] = "clear" }).Diagnostic);
        }
        [Test] public void BusyJobStatusAndCancellationHaveExplicitPolicies()
        {
            Assert.IsTrue(EditorCommandAccess.Describe("manage_build", new JObject { ["action"] = "status" }).Diagnostic);
            Assert.IsTrue(EditorCommandAccess.Describe("manage_graphics", new JObject { ["action"] = "bake_status" }).Diagnostic);
            var cancel = EditorCommandAccess.Describe("manage_build", new JObject { ["action"] = "cancel" });
            Assert.IsTrue(cancel.BypassBusy);
            Assert.IsTrue(cancel.IsWrite); // Still obeys conflicting execution and reservation fences.
        }

        [Test] public void ACompilationCannotBeginHalfwayThroughABatch()
        {
            var batch = new JObject { ["commands"] = new JArray(new JObject {
                ["tool"] = "refresh_unity", ["params"] = new JObject { ["compile"] = "request" }
            }) };
            Assert.Throws<System.ArgumentException>(() => EditorCommandAccess.Describe("batch_execute", batch));
        }

        [Test] public void NestedSessionManagementCannotBypassScheduler()
        {
            var batch = new JObject { ["commands"] = new JArray(new JObject {
                ["tool"] = "manage_editor_session", ["params"] = new JObject { ["action"] = "acquire" }
            }) };
            Assert.Throws<System.ArgumentException>(() => EditorCommandAccess.Describe("batch_execute", batch));
        }
    }
}
