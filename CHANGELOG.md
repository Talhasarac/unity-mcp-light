# Unity MCP Light changelog

Light uses independent semantic versioning starting with v1.0.0. Earlier 10.x numbers identify the upstream MCP for Unity baseline; they are not earlier releases in Light's v1 sequence. Original upstream attribution and license notices remain intact.

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
