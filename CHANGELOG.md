# Unity MCP Light changelog

Light uses independent semantic versioning starting with v1.0.0. Earlier 10.x numbers identify the upstream MCP for Unity baseline; they are not earlier releases in Light's v1 sequence. Original upstream attribution and license notices remain intact.

## v1.0.4 — Capture recovery without replacing Scene view — 2026-10-31

- Reuse a visible Game view or prepare a separate MCP Game view beside Inspector/Project when safe, otherwise floating. Restore the previously focused window without repeatedly switching tabs.
- Bound composited end-of-frame captures with an editor-clock deadline and clean up on timeout, Play Mode exit, reload, cancellation and failure. Do not silently substitute camera-only images for Play Mode UI captures.
- Require the returned `capture_id` and original arguments when polling `render_ui`, preventing identical requests from collecting another capture. Reclaim abandoned images after 60 seconds.
- Update MCP initialization instructions, tool schemas, distributed skills and UI tool reference with the capture workflow and custom-probe guidance.
- Source-only event-order review completed; no compiles, tests or live checks run. See [capture recovery](docs/capture-recovery.md) for limits and user verification handoff.

## v1.0.3 — Automatic agent guidance — 2026-10-31

- Share one instruction builder across native HTTP/stdio and the Claude Desktop bridge, supplying coordination guidance during MCP initialization without copy/paste.
- Remove the Copy agent instructions button; explain that clients receive guidance on connection and must reconnect after a server update.
- Update the distributed skill and shared-editor reference with independent sessions, scoped reservations, snapshot-before-read workflows, timeout recovery and direct-file ownership.
- Make project/user restrictions take precedence over compilation and verification examples.
- Add an in-memory regression for delivery of Desktop instructions during initialization. No builds, compiles, tests or live checks run; user verification pending.

## v1.0.2 — Unity compile fix — 2026-10-31

- Fix CS7036 in Claude Desktop transport detection by using `Array.IndexOf` for the command argument lookup. The previous `Contains` call required a missing LINQ import and resolved to an incompatible span overload.
- No compilation or tests run; user verification pending.

## v1.0.1 — Claude Desktop connection repair — 2026-10-31

- Repair Connection now supports Claude Desktop with HTTP Local/Remote through a bundled stdio-to-HTTP bridge, preserving the shared server transport.
- Keep upstream sessions stable across Desktop calls, with separate state per client and cleanup on disconnect; discover the backend's live tools, resources and prompts.
- Detect the bridge's target URL in client status, including changed ports, and display Configured (HTTP bridge).
- Pass remote API keys through environment configuration rather than command arguments; remove stale credentials when switching transports.
- Refuse to replace unreadable or malformed JSON configuration.
- Include the Connect tab section reorder: Server, Client Configuration, Agents & editor ownership.
- Package, server, CLI and bundle versions are 1.0.1. Verification remains pending; tests were added but not run.

## v1.0.0 — Rewrite — 2026-10-31

### Multiple agents in one editor

- Schedule ordinary commands automatically instead of retaining a global editor lease while an agent thinks or edits files.
- Add optional object/asset reservations and single-use revision snapshots for guarded batches.
- Keep conservative protection for global editor changes and overlapping writes. A disconnected caller does not release an operation still executing in Unity.

### Connection repair

- Add **Repair Connection** in the Server section to apply the selected client's configuration, start the local HTTP server when needed, and reconnect Unity.
- Report configuration failures, connection failures, and required client restarts separately.
- Fix Configure/unregister routing so repeated configuration does not silently do nothing for Codex or unregister Claude Code.
- Allow Codex HTTP configuration without resolving uv/uvx, respect `CODEX_HOME`, and preserve malformed or unreadable TOML rather than replacing it.

### Tool capabilities

- Correct removed or unsupported action examples in agent instructions and tool documentation.
- Describe batch execution as sequential and expose revision guards through MCP and the CLI.

### Upgrade notes

- Unity package, Python server, MCP bundle, and CLI identify this rewrite as **1.0.0**. Existing package IDs, executable names, and the `unity-mcp-light` client registration name remain stable.
- Because 1.0.0 is numerically below the inherited 10.x version, migrate by selecting the rewrite's Git revision/tag rather than relying on an older installation's semantic-version update prompt.
- Release existing editor reservations before upgrading. Reconfigure/reconnect clients after installing both the Unity package and matching server.
- Distributed through the `v1.0.0` Git tag. The Unity package pins its default Python server source to the same tag. Existing installations must update their package reference explicitly.
- Verification is pending: no compiles, builds, tests, or live Unity/client checks were run. See [multi-agent editing](docs/multi-agent-editing.md) and [connection repair](docs/connection-repair.md) for the verification handoff.
