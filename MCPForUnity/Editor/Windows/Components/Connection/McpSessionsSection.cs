using System;
using System.Linq;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Connection
{
    internal sealed class McpSessionsSection
    {
        private readonly Label connections;
        private readonly Label ownership;
        private readonly Label task;
        private readonly VisualElement agents;
        private readonly Button release;

        internal const string AgentInstructions =
            "Before working in this shared Unity project, call manage_editor_session(action='acquire', " +
            "agent_name='your role', task='short description'). Read current scene/files only after acquisition. " +
            "Keep ownership through edit, compile, test, verify and save. Commands renew the 300-second idle " +
            "lease; use action='renew' during long work. If editor_owned/editor_busy, wait retry_after_ms " +
            "and check status. After expiry or reconnect, acquire and re-read everything relevant. " +
            "Release with action='release' when done or after a failure once Unity is idle. " +
            "Do not bypass ownership through shell edits, bridge restarts or force-unlocks. " +
            "Other agents can research/review or prepare changes in separate workspaces. " +
            "Human editor actions and direct filesystem writes are not intercepted by this lock.";

        internal McpSessionsSection(VisualElement parent)
        {
            var section = new VisualElement { name = "editor-sessions-section" };
            section.AddToClassList("section");
            var title = new Label("Agents & editor ownership");
            title.AddToClassList("section-title");
            section.Add(title);
            var content = new VisualElement();
            content.AddToClassList("section-content");
            section.Add(content);
            connections = AddLabel(content, "");
            ownership = AddLabel(content, "");
            ownership.style.unityFontStyleAndWeight = FontStyle.Bold;
            task = AddLabel(content, "");
            AddLabel(content, "One agent owns the editor through read, edit and verification. Other agents wait.");
            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = AgentInstructions)
                { text = "Copy agent instructions" };
            buttons.Add(copy);
            release = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Release editor ownership?",
                    "Use this only to recover an abandoned task. The previous agent's queued commands " +
                    "will be rejected. This does not undo edits or cancel running operations.", "Release", "Cancel")) return;
                if (!EditorSessionCoordinator.ReleaseFromWindow())
                    EditorUtility.DisplayDialog("Editor still busy", "Wait for the current operation, compilation or tests to finish.", "OK");
                Refresh();
            }) { text = "Release ownership" };
            buttons.Add(release);
            content.Add(buttons);
            agents = new VisualElement { name = "editor-agent-list" };
            content.Add(agents);
            AddLabel(content, "Agent rows show recent MCP activity; an idle HTTP session is not proof of an open connection. " +
                "Agents appear after their first Unity request. Direct file edits and human editor actions are outside this lock.");
            parent.Add(section);
            Refresh();
        }

        private static Label AddLabel(VisualElement parent, string text)
        {
            var label = new Label(text) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 5;
            parent.Add(label);
            return label;
        }

        internal void Refresh()
        {
            var http = MCPServiceLocator.TransportManager.GetState(TransportMode.Http);
            var endpoints = StdioBridgeHost.GetConnectedEndpoints();
            connections.text = $"HTTP bridge: {(http.IsConnected ? "connected" : "disconnected")}  |  Open stdio sockets: {endpoints.Length}";
            connections.tooltip = endpoints.Length == 0 ? "No open stdio sockets" : string.Join("\n", endpoints);
            var snapshot = EditorSessionCoordinator.Snapshot();
            var owner = snapshot["owner"] as JObject;
            bool busy = snapshot.Value<bool>("editor_busy") || snapshot.Value<int>("in_flight") > 0;
            ownership.text = owner == null ? "Editor available" :
                $"Owner: {owner.Value<string>("name")}  ·  {owner.Value<int>("seconds_remaining")}s idle lease remaining";
            task.text = (owner?.Value<string>("task") ?? "Acquire ownership before starting a task.")
                + (busy ? "  •  Editor busy; handover is paused." : "");
            release.SetEnabled(owner != null && !busy);
            agents.Clear();
            var rows = snapshot["agents"] as JArray;
            if (rows == null || rows.Count == 0) AddLabel(agents, "No agent activity observed yet.");
            else foreach (var row in rows.OfType<JObject>())
            {
                var card = new VisualElement();
                card.style.marginTop = 7;
                card.style.paddingTop = 5;
                card.style.borderTopWidth = 1;
                card.style.borderTopColor = new Color(0.5f, 0.5f, 0.5f, 0.25f);
                var name = AddLabel(card, $"{row.Value<string>("name")}  ·  {row.Value<string>("status")}  ·  {row.Value<string>("transport")}");
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.tooltip = "Session " + row.Value<string>("id");
                AddLabel(card, $"{row.Value<string>("task")}\nLast request: {row.Value<string>("last_command")} · {row.Value<int>("seconds_since_seen")}s ago");
                agents.Add(card);
            }
        }
    }
}
