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
            AddLabel(content, "Ordinary commands share the editor. Reservations protect only overlapping work.");
            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            AddLabel(content, "Coordination instructions are sent automatically when an MCP client connects. " +
                "After updating the server, reconnect clients to load the new instructions.");
            release = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Release all editor reservations?",
                    "Use this only to recover abandoned reservations. The previous agent's queued commands " +
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
            task.text = (owner?.Value<string>("task") ?? "Automatic scheduling; ordinary tasks need no lease.")
                + (busy ? "  •  Editor busy; handover is paused." : "");
            var reservations = snapshot["reservations"] as JArray;
            if (owner == null && reservations?.Count > 0)
                ownership.text = $"Shared editor · {reservations.Count} scoped reservation(s)";
            release.SetEnabled(reservations?.Count > 0 && !busy);
            agents.Clear();
            if (reservations != null)
                foreach (var reservation in reservations.OfType<JObject>())
                    AddLabel(agents, $"Reserved by {reservation.Value<string>("name")}: {string.Join(", ", reservation["resources"].Values<string>())}");
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
