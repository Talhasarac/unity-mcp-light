using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using MCPForUnity.Editor.Tools.Build;

namespace MCPForUnity.Editor.Services
{
    internal sealed class EditorSessionState
    {
        internal sealed class Agent
        {
            public string Id, Name, Transport, Task, LastCommand, Status;
            public double LastSeen;
        }
        internal sealed class Reservation
        {
            public string Owner, Token;
            public string[] Resources;
            public double ExpiresAt;
            public int Seconds;
        }
        internal sealed class Execution
        {
            public string Owner;
            public EditorCommandAccess Access;
        }
        public string EditorId = Guid.NewGuid().ToString("N");
        public List<Agent> Agents = new();
        public List<Reservation> Reservations = new();
        [JsonIgnore] public Dictionary<string, Execution> Executions = new();
        [JsonIgnore] public int InFlight => Executions.Count;

        public void Touch(JObject identity, string command, double now)
        {
            var id = identity?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return;
            var agent = Agents.FirstOrDefault(a => a.Id == id);
            if (agent == null)
            {
                if (Agents.Count >= 64)
                {
                    var oldest = Agents.Where(a => !Reservations.Any(r => r.Owner == a.Id)
                        && !Executions.Values.Any(e => e.Owner == a.Id)).OrderBy(a => a.LastSeen).FirstOrDefault();
                    if (oldest == null) return;
                    Agents.Remove(oldest);
                }
                agent = new Agent { Id = id, Name = Clip(identity.Value<string>("name"), 80),
                    Transport = Clip(identity.Value<string>("transport"), 16) };
                Agents.Add(agent);
            }
            agent.LastSeen = now;
            agent.LastCommand = command;
        }
        private static string Clip(string value, int length) => string.IsNullOrEmpty(value) ? "" :
            new string(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
        private static bool HasToken(JObject identity, Reservation r) => identity?.Value<string>("id") == r.Owner
            && identity["lease_tokens"] is JArray tokens && tokens.Values<string>().Contains(r.Token);
        private bool HasOldToken(JObject identity) => identity?["lease_tokens"] is JArray tokens
            && tokens.Values<string>().Any(t => t != null && t.StartsWith(EditorId + ":", StringComparison.Ordinal));
        private bool Running(Reservation r) => Executions.Values.Any(e =>
            EditorCommandAccess.Write(r.Resources).Conflicts(e.Access));
        private void Expire(double now, bool busy)
        {
            if (!busy) Reservations.RemoveAll(r => now >= r.ExpiresAt && !Running(r));
        }
        private JObject Describe(Reservation r, double now)
        {
            var owner = Agents.FirstOrDefault(a => a.Id == r.Owner);
            return new JObject { ["id"] = r.Owner, ["name"] = owner?.Name, ["task"] = owner?.Task,
                ["resources"] = new JArray(r.Resources), ["seconds_remaining"] = Math.Max(0, (int)Math.Ceiling(r.ExpiresAt - now)) };
        }
        public JObject Snapshot(double now, bool busy, bool includeAgents = true)
        {
            Expire(now, busy);
            var exclusive = Reservations.FirstOrDefault(r => r.Resources.Contains("*"));
            var result = new JObject {
                ["mode"] = "automatic", ["owner"] = exclusive == null ? null : Describe(exclusive, now),
                ["reservations"] = new JArray(Reservations.Select(r => Describe(r, now))),
                ["in_flight"] = InFlight, ["editor_busy"] = busy
            };
            if (includeAgents)
                result["agents"] = new JArray(Agents.OrderByDescending(a => a.LastSeen).Select(a => new JObject {
                    ["id"] = a.Id, ["name"] = a.Name, ["task"] = a.Task, ["transport"] = a.Transport,
                    ["last_command"] = a.LastCommand, ["seconds_since_seen"] = Math.Max(0, (int)(now - a.LastSeen)),
                    ["status"] = Executions.Values.Any(e => e.Owner == a.Id) ? "executing" :
                        Reservations.Any(r => r.Owner == a.Id) ? "reserved" : now - a.LastSeen > 300 ? "inactive" : a.Status ?? "idle"
                }));
            return result;
        }
        private JObject Fail(string code, string message, double now, bool busy) => new() {
            ["success"] = false, ["error"] = code, ["code"] = code, ["message"] = message,
            ["data"] = new JObject { ["retry_after_ms"] = 1000, ["session"] = Snapshot(now, busy, false) }
        };

        public JObject Manage(JObject identity, JObject args, double now, bool busy)
        {
            Touch(identity, "manage_editor_session", now);
            Expire(now, busy);
            string action = args.Value<string>("action") ?? "status";
            if (action == "status") return new JObject { ["success"] = true, ["data"] = Snapshot(now, busy) };
            string id = identity?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return Fail("agent_identity_required", "Supply a distinct agent/session identity.", now, busy);
            var own = Reservations.FirstOrDefault(r => r.Owner == id);
            if (action == "acquire")
            {
                // The facade resolves selectors. The pure state machine receives canonical keys.
                var resources = args["resource_keys"]?.Values<string>().Distinct().ToArray() ?? new[] { "*" };
                if (resources.Length == 0) return Fail("invalid_resources", "A reservation needs resources.", now, busy);
                int seconds = args.Value<int?>("lease_seconds") ?? 300;
                if (seconds < 30 || seconds > 900) return Fail("invalid_lease", "lease_seconds must be 30-900.", now, busy);
                if (own != null && !new HashSet<string>(own.Resources).SetEquals(resources))
                    return Fail("reservation_change_required", "Release your existing reservation before changing its scope.", now, busy);
                var access = EditorCommandAccess.Write(resources);
                if (busy || Executions.Values.Any(e => access.Conflicts(e.Access)))
                    return Fail("editor_busy", "An overlapping operation or editor transition is running.", now, busy);
                if (Reservations.Any(r => r.Owner != id && access.Conflicts(EditorCommandAccess.Write(r.Resources))))
                    return Fail("resource_reserved", "Another session has reserved overlapping resources.", now, busy);
                if (Reservations.Count >= 32 && own == null) return Fail("reservation_limit", "Too many active reservations.", now, busy);
                if (own == null)
                {
                    own = new Reservation { Owner = id, Token = EditorId + ":" + Guid.NewGuid().ToString("N"), Resources = resources };
                    Reservations.Add(own);
                }
                own.Seconds = seconds;
                own.ExpiresAt = now + seconds;
                var agent = Agents.FirstOrDefault(a => a.Id == id);
                if (agent != null)
                {
                    if (args.Value<string>("agent_name") is string name) agent.Name = Clip(name, 80);
                    if (args.Value<string>("task") is string task) agent.Task = Clip(task, 200);
                }
                var data = Snapshot(now, busy, false);
                data["lease_id"] = own.Token;
                data["instruction"] = "Re-read reserved resources, perform the short sequence, then release. Ordinary commands need no reservation.";
                return new JObject { ["success"] = true, ["data"] = data };
            }
            if (action != "renew" && action != "release") return Fail("invalid_action", "Use status, snapshot, acquire, renew or release.", now, busy);
            if (own == null || !HasToken(identity, own))
                return Fail("editor_lease_required", "Reservation expired or was revoked. Acquire again and re-read before continuing.", now, busy);
            if (action == "renew")
            {
                own.ExpiresAt = now + own.Seconds;
                return new JObject { ["success"] = true, ["data"] = Snapshot(now, busy, false) };
            }
            if (busy || Running(own)) return Fail("editor_busy", "Cannot release resources used by running work.", now, busy);
            Reservations.Remove(own);
            var released = Snapshot(now, busy, false);
            released["released_lease_id"] = own.Token;
            return new JObject { ["success"] = true, ["data"] = released };
        }

        public JObject Begin(JObject identity, string operation, EditorCommandAccess access, double now, bool busy)
        {
            Expire(now, busy);
            if (access.Diagnostic) return null;
            string id = identity?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return Fail("agent_identity_required", "Update both sides; CLI callers must supply --agent-id.", now, busy);
            if (access.IsWrite)
            {
                var own = Reservations.FirstOrDefault(r => r.Owner == id);
                if ((own != null && !HasToken(identity, own)) || (own == null && HasOldToken(identity)))
                    return Fail("editor_lease_required", "Old reservation token. Reacquire, re-read, and release; stale calls cannot run automatically.", now, busy);
                if (Reservations.Any(r => r.Owner != id && access.Conflicts(EditorCommandAccess.Write(r.Resources))))
                    return Fail("resource_reserved", "Another session reserved resources this operation may change.", now, busy);
            }
            if ((busy && !access.BypassBusy) || Executions.Values.Any(e => access.Conflicts(e.Access)))
                return Fail("editor_busy", "Waiting for overlapping execution or an editor transition.", now, busy);
            Executions.Add(operation, new Execution { Owner = id, Access = access });
            return null;
        }
        public void End(string operation, double now)
        {
            if (!Executions.TryGetValue(operation, out var execution)) return;
            Executions.Remove(operation);
            var own = Reservations.FirstOrDefault(r => r.Owner == execution.Owner);
            if (own != null) own.ExpiresAt = now + own.Seconds;
        }
        public void Observe(JObject identity, string command, double now, bool busy)
        {
            Touch(identity, command, now);
            var own = Reservations.FirstOrDefault(r => HasToken(identity, r));
            if (own != null && (now < own.ExpiresAt || busy || Running(own))) own.ExpiresAt = now + own.Seconds;
        }
        public bool ReleaseFromWindow(double now, bool busy)
        {
            if (busy || InFlight > 0) return false;
            Reservations.Clear();
            return true;
        }
    }

    [InitializeOnLoad]
    internal static class EditorSessionCoordinator
    {
        internal const string MetadataKey = "_mcp_editor_session";
        private const string StateKey = "MCPForUnity.EditorSessions.v2";
        private static EditorSessionState state;
        private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        private static double compilationRequestedUntil;
        // RequestScriptCompilation and debounced imports start on a later editor tick.
        // Cover that gap before isCompiling becomes true; use the same 10s start grace
        // as RefreshUnity. Actual compilation/build/bake state takes over afterward.
        internal static void MarkCompilationRequested() => compilationRequestedUntil = Now + 10;
        internal static bool IsBusy => Now < compilationRequestedUntil || BuildJobStore.HasActiveJobs
            || BuildPipeline.isBuildingPlayer || Lightmapping.isRunning || EditorStateCache.GetActualIsCompiling() || EditorApplication.isUpdating
            || TestRunStatus.IsRunning || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying;
        static EditorSessionCoordinator()
        {
            try { state = JsonConvert.DeserializeObject<EditorSessionState>(SessionState.GetString(StateKey, "")); }
            catch { }
            state ??= new EditorSessionState();
            foreach (var r in state.Reservations) r.ExpiresAt = Math.Max(r.ExpiresAt, Now + 60);
            CompilationPipeline.compilationStarted += _ => compilationRequestedUntil = 0;
            Save();
        }
        private static void Save() => SessionState.SetString(StateKey, JsonConvert.SerializeObject(state));
        internal static JObject Snapshot() { var r = state.Snapshot(Now, IsBusy); Save(); return r; }
        internal static JObject Manage(JObject identity, JObject args)
        {
            try
            {
                args = (JObject)args.DeepClone();
                args.Remove("resource_keys"); // Clients cannot forge resolved resource keys.
                if (args.Value<string>("action") == "snapshot")
                {
                    var access = EditorCommandAccess.Read(EditorCommandAccess.ResolveResources(args["resources"]));
                    string operation = Guid.NewGuid().ToString("N");
                    var denied = Begin(identity, operation, access);
                    if (denied != null) return denied;
                    try { return EditorRevisionSnapshots.Capture(identity.Value<string>("id"), (JArray)args["resources"]); }
                    finally { End(operation); }
                }
                if (args["resources"] != null && args["resources"].Type != JTokenType.Null)
                    args["resource_keys"] = new JArray(EditorCommandAccess.ResolveResources(args["resources"]));
                var result = state.Manage(identity, args, Now, IsBusy); Save(); return result;
            }
            catch (ArgumentException e) { return new JObject { ["success"] = false, ["error"] = "invalid_resources", ["message"] = e.Message }; }
        }
        internal static void Observe(JObject identity, string command) { state.Observe(identity, command, Now, IsBusy); Save(); }
        internal static JObject Begin(JObject identity, string operation, EditorCommandAccess access)
        { var r = state.Begin(identity, operation, access, Now, IsBusy); Save(); return r; }
        internal static void End(string operation) { state.End(operation, Now); Save(); }
        internal static bool ReleaseFromWindow() { bool r = state.ReleaseFromWindow(Now, IsBusy); Save(); return r; }
    }
}
