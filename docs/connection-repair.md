# Repairing a client connection

On **Connect**, select the affected client under **Client Configuration**, choose the desired server transport and URL, then click **Repair Connection** in the **Server** section. The button remains available after the mismatch banner disappears, including when a server launch failed and needs another attempt.

The repair always applies configuration; it never toggles registration off. It reads the client configuration back, starts the local HTTP server if needed, and connects/checks Unity's bridge. An existing healthy server is reused. A stale Unity session can be reconnected without stopping the shared server. Remote mode only connects to the supplied server; it does not launch one.

The result distinguishes a configuration failure, a connection failure after configuration was saved, and a ready Unity bridge. A ready bridge does **not** prove that an already-running agent has reloaded its MCP configuration. Restart that client or use its reconnect action. The repair does not close client apps or interrupt agent conversations.

Codex HTTP configuration no longer depends on finding uv/uvx; launching the local Python server still requires its dependencies. Codex's **Configure** button now writes even when its cached status is already Configured, instead of invoking the unimplemented unregister operation. Malformed or unreadable Codex TOML is left intact and reported. The Codex config path respects `CODEX_HOME` inherited by Unity.

## Scope and remaining overrides

The repair updates the selected configurator's file/scope. It does not rewrite every possible project override, profile, managed policy, or a separately launched client's environment. Codex can load project configuration over its user configuration ([official configuration documentation](https://learn.chatgpt.com/docs/config-file/config-basic)). If Unity reports success but the client still uses old settings after reconnecting, inspect that client's effective configuration and the config path shown in Unity.

Starting in v1.0.1, **Claude Desktop** supports HTTP through the bundled `mcp-for-unity-proxy` command. Desktop still launches a stdio process, but that process forwards tools/resources/prompts to the selected shared HTTP endpoint. The UI reports **Configured (HTTP bridge)**. Repair writes this compatible command and starts/connects the HTTP server, without changing the global transport. Restart Claude Desktop to load the new command. The proxy uses the existing Python package dependencies; it does not require Node.js.

Each Desktop MCP session keeps its own upstream session, so selected Unity instances and reservation credentials persist between calls and are isolated from other clients. Tool definitions come from the running backend. Closing the client closes its proxy session; the shared server stays running.

For native HTTP in the local **Code tab**, **Claude Code** remains an option. A same-named Desktop registration takes precedence there ([Anthropic documentation](https://code.claude.com/docs/en/desktop)). Both configurations must target the intended endpoint. Clear a Claude Code project-directory override before repairing this project's bridge.

## Verification handoff — UNVERIFIED

No compiles, tests, or live Unity/client checks were run for this change.

- Reproduce the screenshot: Codex configured for stdio, HTTP Local at port 8088, server stopped. Click Repair Connection: the Codex URL should become `http://127.0.0.1:8088/mcp`, the server should start, and Unity should connect. Reconnect Codex separately.
- Click repair again with a healthy server: registration must remain present and the shared server must remain running.
- Repeat with the correct transport but an old HTTP port, and with an invalid or locked Codex config. Invalid/locked configs must remain unchanged and the reason must be displayed.
- Remove uv from the configured paths: HTTP config writing must still work; a local launch without its dependencies must report the remaining failure.
- Retry after a failed launch; close/reopen the MCP window during a slow launch; change settings in another window during a repair. No late result should claim success for changed settings.
- Regression coverage is in `ClientConnectionRepairTests.cs`. It uses temporary config files and restores preferences and registered services; it never needs a real client app or server.

Additional v1.0.1 handoff: reproduce the Claude Desktop screenshot with HTTP Local at port 8088. Repair should replace the old direct stdio server command with `mcp-for-unity-proxy --url http://127.0.0.1:8088/mcp`, display Configured (HTTP bridge), and connect Unity. After restarting Desktop, inspect then modify through successive calls, confirming the selected editor persists. Repeating Repair must keep the registration. Switching back to stdio must remove the proxy command. No tests or live checks have been run for this patch.
