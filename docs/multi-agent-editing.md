# Working with multiple agents in one Unity Editor

Unity has one active editor state. Separate client connections do not provide separate
scenes, Play Mode, compilation or Undo histories. A task owns the editor from its first
inspection through the final verification/save. A command queue alone cannot protect that
sequence.

## Agent workflow

1. Select the intended instance using `set_active_instance` when multiple editors are open.
2. Call `manage_editor_session(action="acquire", agent_name="Claude - vehicles", task="Tune steering")`.
3. Read current scene/files and readiness resources. Do not reuse an observation from before acquisition.
4. Edit, compile, check console, test, verify, and save. Keep ownership throughout.
5. Call `manage_editor_session(action="release")` even if the task failed, once Unity is idle.

The default lease expires after 300 seconds without an authorized request. Choose 30–900
seconds with `lease_seconds` on acquisition; use `action="renew"` during long work. Do not
hold the editor while waiting for human input. In-flight operations, compilation, asset
updating and tests prevent handover. Domain reload preserves ownership and gives the owner
at least 60 seconds to reconnect. Server-side tokens are attached automatically to MCP calls.

| Response | Agent action |
|---|---|
| `editor_owned` | Another session owns Unity. Wait `data.retry_after_ms`; use `status` and back off. |
| `editor_busy` | Wait for the ongoing operation, compilation or tests; poll editor state. |
| `editor_lease_required` | Acquire again, then re-read scene/files and discard stale object IDs. |
| `agent_identity_required` | Update/restart both sides; CLI callers must supply an agent ID. |

Never work around a denial with shell edits, a second bridge or a force-unlock. Unknown
custom tools and entire batches are protected by default. Registered resources, status,
console reads and test-job polling remain available as diagnostics. Their observations can
change while another agent works and do not authorize edits.

Each independent agent needs its own MCP session. Agents/subagents sharing one session are
one owner; their parent must serialize their workflows. The dispatcher also rejects
overlapping protected commands from the same owner. For multiple operations use a batch;
its nested handlers execute under the outer lease.

## Unity window

Open **Window → Unity MCP Light → Connect** and find **Agents & editor ownership**:

- HTTP bridge connection and actual open stdio socket count; hover for remote endpoints.
- Current owner's display name, task and remaining idle lease time.
- Up to 64 recently observed sessions, their transport, waiting/idle/inactive state and last request.
- **Copy agent instructions** to paste into another agent's conversation.
- **Release ownership** for human recovery of an abandoned task, disabled during running work.

HTTP agents are MCP sessions, not individual bridge sockets. Unity sees their identity on
the first command forwarded to it. Last-seen time does not prove a client is still connected.
Idle leases expire automatically. Recovery revokes the old token; it does not cancel a
running operation or undo changes. A genuinely hung handler must be diagnosed rather than
granting another agent simultaneous write access.

## Migration

Update the Python server and Unity package together, restart the MCP clients, and use
**Install Skills** again. Older agents receive an actionable lease-required error. HTTP is
recommended for several agents, with one shared server. Stdio now keeps multiple clients
open; a new socket no longer disconnects the previous one. Identical commands from two
agents are distinct requests. A reconnect resend is recognized only by session ID,
request ID and the identical payload.

Do not simply increase timeouts: that cannot repair competing scene edits. Avoid starting
two HTTP servers on the same port. Separate Unity copies/worktrees and explicit instance
routing remain an alternative for independent work.

## CLI and REST

Use a unique `--agent-id` / `UNITY_MCP_AGENT_ID` for each agent. Acquire with:

```sh
unity-mcp --agent-id vehicle-agent --format json session acquire --name "Vehicle agent" --task "Tune steering"
```

Use the returned `data.lease_id` as `--lease-id` / `UNITY_MCP_LEASE_ID` for subsequent
commands, renewals and release. Do not share IDs or tokens between agents. REST
`/api/command` accepts `agent_id` and `lease_id` alongside `type`, `params` and
`unity_instance`. No token is needed for `session status`. MCP clients handle token
bookkeeping automatically; CLI tokens deliberately are not written to a shared global file.

## Boundaries

This is coordination among trusted local/project clients, not a security sandbox. MCP cannot
intercept filesystem writes from Claude's shell, a different IDE, Git or a human editing
Unity. The owner must also coordinate shared-folder writes. Let other agents research/review
or prepare patches in separate workspaces, then integrate them under ownership. Two Git
branches in one working directory do not isolate files. Re-read state after human edits.

A timeout means an operation may still be running or may have completed. Check the actual
state before repeating a non-idempotent mutation; request deduplication is not a durable
transaction log. Ownership does not provide rollback, distributed file locks or scene merges.
