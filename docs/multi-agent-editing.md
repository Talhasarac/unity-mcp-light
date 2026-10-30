# Multiple agents in one Unity Editor

MCP initialization delivers coordination instructions automatically. Native HTTP,
direct stdio and the Desktop HTTP bridge share `Server/src/services/agent_instructions.py`.
No copy/paste or skill installation is needed for this guidance. Reconnect clients
after updating/restarting the server; existing conversations may retain older guidance.
The optional skill adds a [shared-editor reference](../unity-mcp-skill/references/shared-editor.md).
User/project rules, including restrictions on verification, take precedence.

Ordinary calls no longer require `acquire` or `release`. Each request is scheduled on
Unity's main thread, and protection ends when the actual handler completes, not when
an agent finishes thinking about its task. Session identity and retry deduplication remain.

## Normal workflow

1. Select the intended instance when multiple editors are connected.
2. Inspect the relevant objects/assets, then issue small, focused commands.
3. Use instance IDs for scene object edits. Unrelated agents can continue between calls.
4. For an edit based on an observation that must remain current, use a guarded batch below.

Conflicting calls wait in Unity's queue for up to 10 seconds. Older executable conflicts
precede later conflicts; unrelated work can pass a request blocked by a reservation.
`editor_queue_timeout` with `executed=false` means the handler never started and can be
resubmitted. A transport timeout does NOT make that guarantee. Cancellation removes queued
work, but never unlocks an executing handler. Diagnostics remain available during busy work.

This is concurrent task progress, not multithreaded Unity API execution. Batch entries
always execute sequentially, even when `parallel=true` is supplied.

## Guard an observation against stale edits

Take a snapshot **before** reading the state used to decide your edit:

```python
observation = manage_editor_session(
    action="snapshot", resources=["object:12345", "asset:Assets/Materials/Body.mat"]
)
# Now read the object/components/material through their normal tools/resources.
batch_execute(
    expected_revision=observation["data"]["revision"],
    fail_fast=True,
    commands=[{"tool": "manage_gameobject", "params": {
        "action": "modify", "target": 12345, "search_method": "by_id",
        "position": [1, 0, 0]
    }}]
)
```

The dispatcher reserves the batch's effects and its snapshot read dependencies, checks
fingerprints, then executes. A changed/deleted resource returns `conflict` before any
batch command runs. Snapshot tokens belong to one session, are single-use, expire after
10 minutes, and are invalidated by domain reload. Keep at most a small number outstanding;
the cache holds 128 observations. Include every object/asset your decision depends on.

Object fingerprints cover the subtree's serialized objects/components and ancestor
transforms. Asset fingerprints cover the exact file, `.meta`, imported dependency hash
and live serialized asset values. They do not cover arbitrary static/runtime state,
unlisted referenced assets, filesystem changes after the check, or human edits during
an asynchronous batch. A guarded batch provides no rollback: later command failure can
leave earlier commands applied. A snapshot is optional; unguarded calls do not detect
an earlier stale observation.

## Reserve resources for a short sequence

```python
manage_editor_session(action="acquire", agent_name="Vehicle agent", task="Adjust pose",
                      resources=["object:12345"], lease_seconds=60)
# Re-read, edit, then release. Do not wait for human input while holding a reservation.
manage_editor_session(action="release")
```

Selectors are `object:<instance ID>` or `asset:Assets/<exact path>`, 1–32 per reservation.
Acquisition is all-or-nothing. Each session may hold one reservation per editor; release
before changing its resource set. An object scope includes descendants, so parent/child
edits conflict and separate branches do not. Idle reservations prevent overlapping writes;
inspections can read between operations but do not see an isolated transaction snapshot.
In-flight writes also block overlapping reads until the actual operation completes.

The first audited narrow write actions are plain `manage_gameobject modify` transform
edits by instance ID and `manage_material set_material_color/set_material_shader_property`
with exact `Assets/...mat` paths. Object/component resources have narrow read scopes.
Other reads use conservative editor-wide read scopes. Structural edits, arbitrary component
setters, asset imports/saves, custom code/tools and unclassified actions use editor-wide
write scopes for their **operation**, since their side effects may cross resource boundaries.
Consequently a broad operation may wait for a scoped reservation even if its apparent
target differs. Keep reservations short and prefer guarded batches when possible.

Acquire **without** resources only for a sequence requiring exclusive editor state,
such as enter Play Mode → inspect runtime → stop, or open a Prefab Stage → edit → close.
The old exclusive workflow remains supported, but is not the default. Compilation,
asset updating, queued/running builds, light baking, tests and Play Mode transitions still block unsafe execution.
Compilation requests also reserve a bounded start grace before the pipeline becomes active.
Build/bake status and cancellation remain available while those jobs run.
Run compilation, build/test starts, baking and Play Mode transitions as standalone calls, not batch entries. A reservation
expires after 30–900 seconds of inactivity (default 300); running work prevents expiry.
Use `renew` during long work. Reload preserves reservations with a 60-second recovery grace.
Stale tokens cannot silently turn into automatic writes: reacquire, reread, then release.

## Visibility and recovery

`manage_editor_session(action="status")` and **Connect → Agents & editor ownership** show
recent sessions, reservations, executing operations and editor readiness. Connection counts
are not agent counts. Human recovery can release abandoned reservations only while idle;
it revokes tokens, without cancelling operations or undoing changes.

Independent agents should use independent MCP sessions. Shared-session callers are still
serialized when their operation footprints conflict, but share explicit reservations and
snapshot credentials; their parent must coordinate any cross-call workflow.

## CLI, REST and migration

Update the Python server, Unity package and bundled skill together while Unity is idle;
restart clients to discard old schemas/tokens. This version uses a new reservation state
format, so release old leases before installing. No automatic deployment is performed.

CLI callers still supply a unique `--agent-id` / `UNITY_MCP_AGENT_ID`. Ordinary commands
need no lease. Explicit reservations return a token for `--lease-id` / `UNITY_MCP_LEASE_ID`:

```sh
unity-mcp --agent-id vehicle-agent session acquire --resource object:12345 --lease-seconds 60
unity-mcp --agent-id vehicle-agent session snapshot --resource object:12345
unity-mcp --agent-id vehicle-agent batch run edits.json --expected-revision REVISION
```

REST `/api/command` uses the same `agent_id`/`lease_id` envelope and parameters. MCP clients
manage reservation credentials automatically. Snapshots require one stable agent identity.

MCP does not intercept terminal/file edits, Git, project callbacks or human Unity actions.
Coordinate shared file writes separately. Separate workspaces remain appropriate for code
preparation; two branches in a shared working directory do not isolate files. Saving a scene
or all dirty assets saves shared changes, not just the caller's contribution. Undo history
is shared too; use an exclusive sequence when those semantics matter.

## Verification handoff (not run)

The added state/dispatcher tests cover automatic calls, scoped conflicts, hierarchy overlap,
stale fencing, cancellation, expiry, reload persistence and snapshot conflicts. Run the Python
session/batch tests and Unity coordination EditMode tests in a disposable test project, then
the multi-agent smoke harness. No builds, tests, validation commands or live Unity checks
were run while implementing this change.
