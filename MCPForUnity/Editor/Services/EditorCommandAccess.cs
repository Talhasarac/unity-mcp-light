using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    // Resource keys describe effects, not execution threads. All handlers still enter on
    // Unity's main thread. An unclassified action conservatively writes the whole editor.
    internal sealed class EditorCommandAccess
    {
        public string[] Reads = Array.Empty<string>();
        public string[] Writes = Array.Empty<string>();
        public bool Diagnostic;
        public bool BypassBusy;
        public bool IsWrite => Writes.Length != 0;
        public static EditorCommandAccess Read(params string[] keys) => new() { Reads = keys };
        public static EditorCommandAccess Write(params string[] keys) => new() { Writes = keys };
        public static bool Overlaps(string a, string b) => a == "*" || b == "*" || a == b
            || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);
        public bool Conflicts(EditorCommandAccess other) => !Diagnostic && !other.Diagnostic &&
            (Writes.Any(a => other.Reads.Concat(other.Writes).Any(b => Overlaps(a, b)))
            || Reads.Any(a => other.Writes.Any(b => Overlaps(a, b))));

        internal static string ObjectKey(GameObject go)
        {
            var ids = new List<int>();
            for (var t = go.transform; t != null; t = t.parent) ids.Add(t.gameObject.GetInstanceIDCompat());
            ids.Reverse();
            return "object:" + go.scene.handle + "/" + string.Join("/", ids);
        }

        internal static string AssetPath(string value)
        {
            string path = (value ?? "").Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Split('/').Any(p => p == ".." || p == "." || p == ""))
                throw new ArgumentException("Use an exact Assets/... path without empty, '.' or '..' segments.");
            return path;
        }
        internal static string AssetKey(string path) => "asset:" + AssetPath(path).ToLowerInvariant();

        internal static string ResolveResource(string selector)
        {
            if (selector != null && selector.StartsWith("object:", StringComparison.Ordinal)
                && int.TryParse(selector.Substring(7), out int id))
            {
                var go = GameObjectLookup.FindById(id);
                if (go == null || !go.scene.IsValid()) throw new ArgumentException("Scene object no longer exists: " + selector);
                return ObjectKey(go);
            }
            if (selector != null && selector.StartsWith("asset:", StringComparison.Ordinal)) return AssetKey(selector.Substring(6));
            throw new ArgumentException("Resources must be object:<instance ID> or asset:Assets/<path>.");
        }

        internal static string[] ResolveResources(JToken token)
        {
            if (token is not JArray array || array.Count == 0 || array.Count > 32 || array.Any(t => t.Type != JTokenType.String))
                throw new ArgumentException("resources must contain 1-32 object:/asset: selectors.");
            return array.Values<string>().Select(ResolveResource).Distinct().ToArray();
        }

        internal static bool IsDiagnostic(string command, JObject args) =>
            command == "ping" || command == "get_editor_state" || command == "get_test_job" || command == "get_tool_states"
            || (command == "manage_build" && args.Value<string>("action") == "status")
            || (command == "manage_graphics" && args.Value<string>("action") == "bake_status")
            || (command == "read_console" && (args.Value<string>("action") ?? "get") == "get");

        internal static EditorCommandAccess Describe(string command, JObject args, bool resource = false, int depth = 0)
        {
            if (IsDiagnostic(command, args)) return new EditorCommandAccess { Diagnostic = true };
            string action = (args.Value<string>("action") ?? "").ToLowerInvariant();
            if ((command == "manage_build" && action == "cancel") || (command == "manage_graphics" && action == "bake_cancel")
                || (command == "manage_editor" && action == "stop"))
                return new EditorCommandAccess { Writes = new[] { "*" }, BypassBusy = true };
            if (command == "batch_execute")
            {
                if (depth > 8) throw new ArgumentException("Batches may nest at most eight levels.");
                if (args["commands"] is not JArray commands || commands.Count == 0 || commands.Count > 100)
                    throw new ArgumentException("A batch requires 1-100 commands.");
                var accesses = new List<EditorCommandAccess>();
                foreach (var item in commands)
                {
                    if (item is not JObject entry || entry["tool"]?.Type != JTokenType.String)
                        throw new ArgumentException("Each batch entry needs a tool name.");
                    string tool = entry.Value<string>("tool");
                    var parameters = entry["params"] as JObject ?? new JObject();
                    if (tool == "manage_editor_session" || parameters["expected_revision"] != null || parameters["expectedRevision"] != null)
                        throw new ArgumentException("Session management and revision tokens belong outside nested batch entries.");
                    string nestedAction = (parameters.Value<string>("action") ?? "").ToLowerInvariant();
                    if (tool == "refresh_unity" || tool == "run_tests"
                        || (tool == "manage_script" && nestedAction != "read" && nestedAction != "get_sha")
                        || (tool == "manage_build" && (nestedAction == "build" || nestedAction == "batch" || nestedAction == "build_profile"))
                        || (tool == "manage_graphics" && nestedAction == "bake_start")
                        || (tool == "manage_editor" && (nestedAction == "play" || nestedAction == "stop")))
                        throw new ArgumentException("Compilation, build, test, bake and Play Mode transitions must be standalone calls, not batch entries.");
                    bool isResource = MCPServiceLocator.ResourceDiscovery.GetResourceMetadata(tool) != null;
                    accesses.Add(Describe(tool, parameters, isResource, depth + 1));
                }
                return new EditorCommandAccess
                {
                    Reads = accesses.SelectMany(a => a.Reads).Distinct().ToArray(),
                    Writes = accesses.SelectMany(a => a.Writes).Distinct().ToArray()
                };
            }
            // Scoped writes require an unambiguous instance ID. Other selectors still work,
            // but use the conservative whole-editor operation until their effects are audited.
            var target = args["target"] ?? args["instanceID"] ?? args["instance_id"] ?? args["id"];
            var method = args.Value<string>("searchMethod") ?? args.Value<string>("search_method");
            GameObject go = null;
            if (int.TryParse(target?.ToString(), out int instanceId) && (method == null || method == "by_id"))
                go = GameObjectLookup.FindById(instanceId);
            if (resource)
            {
                if (go != null && (command == "get_gameobject" || command == "get_gameobject_components" || command == "get_gameobject_component"))
                    return Read(ObjectKey(go));
                return Read("*");
            }
            if ((command == "manage_script" && (action == "read" || action == "get_sha"))
                || (command == "manage_prefabs" && (action == "get_info" || action == "get_hierarchy"))
                || command == "find_gameobjects" || command == "unity_reflect" || command == "inspect_prefab"
                || (command == "manage_scene" && (action == "get_hierarchy" || action == "get_active" || action == "get_build_settings" || action == "get_loaded_scenes"))
                || (command == "manage_asset" && (action == "search" || action == "get_info" || action == "get_components")))
                return Read("*");
            if (command == "manage_material" && action == "get_material_info") return Read("*");
            if (command == "manage_material" && (action == "set_material_color" || action == "set_material_shader_property"))
            {
                string path = args.Value<string>("materialPath") ?? args.Value<string>("material_path");
                if (path != null && path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    return Write(AssetKey(path));
            }
            // Arbitrary component setters can follow references to shared assets or run
            // project callbacks. Only plain transform edits have a bounded footprint here.
            if (command == "manage_gameobject" && action == "modify" && go != null && go.scene.IsValid()
                && PrefabStageUtility.GetCurrentPrefabStage() == null)
            {
                var safe = new HashSet<string> { "action", "target", "searchMethod", "search_method", "position", "rotation", "scale" };
                if (args.Properties().All(p => safe.Contains(p.Name))) return Write(ObjectKey(go));
            }
            return Write("*");
        }
    }
}
