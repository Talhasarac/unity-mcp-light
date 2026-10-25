using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    // Pure state machine: the dispatcher calls it on Unity's main thread. Keeping time and
    // editor readiness explicit makes ownership, expiry and handover independently testable.
    internal sealed class EditorSessionState
    {
        internal sealed class Agent
        {
            public string Id;
            public string Name;
            public string Transport;
            public string Task;
            public string LastCommand;
            public string Status;
            public double LastSeen;
        }

        public List<Agent> Agents = new();
        public string Owner;
        public string LeaseId;
        public double ExpiresAt;
        public int LeaseSeconds = 300;
        // Execution belongs to the editor, not the request lifetime. Never persist a count
        // across a domain reload: the old managed handlers cannot continue in the new domain.
        [JsonIgnore] public int InFlight;

        public void Touch(JObject identity, string command, double now)
        {
            var id = identity?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id)) return;
            var agent = Agents.FirstOrDefault(a => a.Id == id);
            if (agent == null)
            {
                if (Agents.Count >= 64)
                    Agents.Remove(Agents.Where(a => a.Id != Owner).OrderBy(a => a.LastSeen).First());
                agent = new Agent { Id = id, Name = Clip(identity.Value<string>("name"), 80),
                    Transport = Clip(identity.Value<string>("transport"), 16) };
                Agents.Add(agent);
            }
            agent.LastSeen = now;
            agent.LastCommand = command;
        }

        private static string Clip(string value, int length) =>
            string.IsNullOrEmpty(value) ? "" : new string(value.Where(c => !char.IsControl(c)).Take(length).ToArray());

        private void Expire(double now, bool editorBusy)
        {
            if (Owner != null && now >= ExpiresAt && InFlight == 0 && !editorBusy)
                ClearOwner();
        }

        private void ClearOwner()
        {
            var owner = Agents.FirstOrDefault(a => a.Id == Owner);
            if (owner != null) owner.Status = "idle";
            Owner = null;
            LeaseId = null;
            ExpiresAt = 0;
        }

        private bool HasLease(JObject identity) => identity?.Value<string>("id") == Owner && Owner != null
            && identity["lease_tokens"] is JArray tokens && tokens.Values<string>().Contains(LeaseId);

        public JObject Snapshot(double now, bool editorBusy, bool includeAgents = true)
        {
            Expire(now, editorBusy);
            var owner = Agents.FirstOrDefault(a => a.Id == Owner);
            var snapshot = new JObject
            {
                ["owner"] = owner == null ? null : new JObject { ["id"] = owner.Id,
                    ["name"] = owner.Name, ["task"] = owner.Task,
                    ["seconds_remaining"] = Math.Max(0, (int)Math.Ceiling(ExpiresAt - now)) },
                ["in_flight"] = InFlight,
                ["editor_busy"] = editorBusy
            };
            if (includeAgents)
            {
                snapshot["agents"] = new JArray(Agents.OrderByDescending(a => a.LastSeen).Select(a => new JObject
                {
                    ["id"] = a.Id, ["name"] = a.Name, ["transport"] = a.Transport,
                    ["task"] = a.Task, ["last_command"] = a.LastCommand,
                    ["seconds_since_seen"] = Math.Max(0, (int)(now - a.LastSeen)),
                    ["status"] = a.Id == Owner ? "owner" : now - a.LastSeen > 300 ? "inactive" : a.Status ?? "idle"
                }));
                snapshot["presence_note"] = "Agents are observed MCP sessions, not a count of open HTTP sockets.";
            }
            return snapshot;
        }

        private JObject Fail(string code, string message, double now, bool busy) => new JObject
        {
            ["success"] = false, ["error"] = code, ["code"] = code, ["message"] = message,
            ["data"] = new JObject { ["retry_after_ms"] = 2000, ["session"] = Snapshot(now, busy, false) }
        };

        public JObject Manage(JObject identity, JObject args, double now, bool busy)
        {
            Touch(identity, "manage_editor_session", now);
            Expire(now, busy);
            string action = args.Value<string>("action") ?? "status";
            if (action == "status") return new JObject { ["success"] = true, ["data"] = Snapshot(now, busy) };
            var id = identity?.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
                return Fail("agent_identity_required", "Update both the Python server and Unity package. CLI clients must supply an agent ID.", now, busy);
            var agent = Agents.First(a => a.Id == id);
            if (action == "acquire")
            {
                if (Owner != null && Owner != id)
                {
                    agent.Status = "waiting";
                    return Fail("editor_owned", "Another agent owns Unity. Wait, then acquire and re-read scene/files before editing.", now, busy);
                }
                if (InFlight > 0 || busy)
                    return Fail("editor_busy", "Wait for the current operation, compilation or tests to finish before acquiring.", now, busy);
                int seconds = args.Value<int?>("lease_seconds") ?? 300;
                if (seconds < 30 || seconds > 900)
                    return Fail("invalid_lease", "lease_seconds must be 30-900.", now, busy);
                // Rotate only on a new ownership period. Retried acquires by the same session
                // are idempotent, but queued calls carrying an old period's token are fenced out.
                if (Owner == null) LeaseId = Guid.NewGuid().ToString("N");
                Owner = id;
                LeaseSeconds = seconds;
                ExpiresAt = now + seconds;
                if (args.Value<string>("agent_name") is string name) agent.Name = Clip(name, 80);
                if (args.Value<string>("task") is string task) agent.Task = Clip(task, 200);
                agent.Status = "owner";
                var data = Snapshot(now, busy, false);
                data["lease_id"] = LeaseId;
                data["instruction"] = "Re-read relevant scene/files now. Keep the lease through edit, compile, verify and save; release when finished.";
                return new JObject { ["success"] = true, ["data"] = data };
            }
            if (action != "renew" && action != "release")
                return Fail("invalid_action", "Use status, acquire, renew or release.", now, busy);
            if (!HasLease(identity))
                return Fail("editor_lease_required", "Your lease is missing or expired. Acquire and re-read current state before continuing.", now, busy);
            if (action == "renew")
            {
                ExpiresAt = now + LeaseSeconds;
                return new JObject { ["success"] = true, ["data"] = Snapshot(now, busy, false) };
            }
            if (InFlight > 0 || busy)
                return Fail("editor_busy", "Cannot hand over while an operation, compilation or tests are running. Poll state and release afterward.", now, busy);
            string released = LeaseId;
            ClearOwner();
            var releasedData = Snapshot(now, busy, false);
            releasedData["released_lease_id"] = released;
            return new JObject { ["success"] = true, ["data"] = releasedData };
        }

        public JObject Begin(JObject identity, string command, double now, bool busy)
        {
            Touch(identity, command, now);
            Expire(now, busy);
            if (Owner != null && identity?.Value<string>("id") != Owner)
            {
                var other = Agents.FirstOrDefault(a => a.Id == identity?.Value<string>("id"));
                if (other != null) other.Status = "waiting";
                return Fail("editor_owned", "Another agent owns Unity. Wait and acquire before inspecting/editing your task.", now, busy);
            }
            if (!HasLease(identity))
                return Fail("editor_lease_required", "Call manage_editor_session(action='acquire', agent_name=..., task=...) BEFORE inspecting/editing. Re-read state after acquisition.", now, busy);
            if (InFlight > 0 || busy)
                return Fail("editor_busy", "An operation, compilation or test is still running. Poll editor state and retry after it finishes.", now, busy);
            InFlight++;
            ExpiresAt = now + LeaseSeconds;
            return null;
        }

        public void End(double now)
        {
            InFlight = Math.Max(0, InFlight - 1);
            if (Owner != null) ExpiresAt = now + LeaseSeconds;
        }

        public void Observe(JObject identity, string command, double now, bool busy)
        {
            Touch(identity, command, now);
            // Polling diagnostics during compilation/tests keeps a living owner present, but
            // an expired idle lease must not be resurrected by a stale command.
            if (HasLease(identity) && (now < ExpiresAt || busy || InFlight > 0))
                ExpiresAt = now + LeaseSeconds;
        }

        public bool ReleaseFromWindow(double now, bool busy)
        {
            if (InFlight > 0 || busy) return false;
            ClearOwner();
            return true;
        }
    }

    [InitializeOnLoad]
    internal static class EditorSessionCoordinator
    {
        internal const string MetadataKey = "_mcp_editor_session";
        private const string StateKey = "MCPForUnity.EditorSessions.v1";
        private static EditorSessionState state;
        private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        internal static bool IsBusy => EditorStateCache.GetActualIsCompiling() || EditorApplication.isUpdating
            || TestRunStatus.IsRunning || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying;

        static EditorSessionCoordinator()
        {
            try { state = JsonConvert.DeserializeObject<EditorSessionState>(SessionState.GetString(StateKey, "")); }
            catch { /* A prior package version's state cannot authorize new commands. */ }
            state ??= new EditorSessionState();
            // Reserve recovery time across reload even when an idle lease was about to expire.
            if (state.Owner != null) state.ExpiresAt = Math.Max(state.ExpiresAt, Now + 60);
            Save();
        }

        private static void Save() => SessionState.SetString(StateKey, JsonConvert.SerializeObject(state));
        internal static JObject Snapshot() { var result = state.Snapshot(Now, IsBusy); Save(); return result; }
        internal static JObject Manage(JObject identity, JObject args)
        { var result = state.Manage(identity, args, Now, IsBusy); Save(); return result; }
        internal static void Observe(JObject identity, string command) { state.Observe(identity, command, Now, IsBusy); Save(); }
        internal static JObject Begin(JObject identity, string command)
        { var result = state.Begin(identity, command, Now, IsBusy); Save(); return result; }
        internal static void End() { state.End(Now); Save(); }
        internal static bool ReleaseFromWindow()
        { bool result = state.ReleaseFromWindow(Now, IsBusy); Save(); return result; }

        internal static bool IsDiagnostic(string command, JObject args, bool resource)
        {
            // Conservative allowlist. Unknown/custom commands and entire batches need ownership;
            // nested handlers execute under the outer command's lease, without self-deadlocking.
            if (resource) return true;
            if (command == "ping" || command == "get_test_job" || command == "get_tool_states") return true;
            return command == "read_console" && (args.Value<string>("action") ?? "get") == "get";
        }
    }
}
