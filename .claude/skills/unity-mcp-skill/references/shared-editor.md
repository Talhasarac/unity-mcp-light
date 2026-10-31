# Multiple agents, one project

Coordination guidance is delivered automatically in the MCP initialization response.
Native HTTP, direct stdio and the Desktop stdio-to-HTTP bridge use the same instruction
builder. This skill supplies examples; copying instructions from Unity is unnecessary.
Reconnect clients after updating/restarting the server. An existing conversation may
retain old guidance until its MCP connection or session is restarted.

## Default workflow

1. Give independent agents independent MCP sessions. Select the intended editor with
   `set_active_instance` when multiple editors are connected. Shared-session children
   share routing, reservations and snapshot credentials; their parent coordinates them.
2. Divide work by object, asset and source file. Inspect the specific target and use
   instance IDs and exact asset paths. Ordinary MCP calls need no acquire/release.
3. Keep commands small. Unity starts handlers on its main thread; concurrent agent
   progress does not mean parallel Unity API execution. Protection ends when the
   actual handler finishes, even if the caller disconnects or cancels.
4. Respect project/user restrictions. Do not run compilation, tests, builds, console
   checks, screenshots or other verification when forbidden. Script tools can trigger
   compilation; use permitted direct file edits instead and leave verification to the user.

Only audited plain transform modifications by instance ID and material color/shader
property changes by exact `.mat` path currently have narrow write scopes. A parent
object scope includes its descendants. Structural edits, component setters, custom
code and unclassified mutations use editor-wide access for that operation. Different
apparent targets therefore do not always imply independent work. Compilation, imports,
builds, baking, tests and Play Mode transitions gate unsafe operations globally.

## Edits based on an observation

Take the snapshot **before** reading the state used to decide the edit. Include every
read dependency, not only the object being changed:

```python
observed = manage_editor_session(action="snapshot", resources=["object:12345"])
# Now read mcpforunity://scene/gameobject/12345 and decide the new position.
batch_execute(
    expected_revision=observed["data"]["revision"],
    fail_fast=True,
    commands=[{"tool": "manage_gameobject", "params": {
        "action": "modify", "target": 12345, "search_method": "by_id",
        "position": [1, 0, 0]
    }}]
)
```

A revision conflict rejects the batch before its first command. Take a new snapshot,
reread and reconsider the edit; do not simply attach a fresh token to the old decision.
Tokens are session-bound, single-use, expire after 10 minutes and are invalidated on
domain reload. A batch is sequential and has no rollback. Unlisted dependencies,
arbitrary runtime state and human/file edits during execution are not fully protected.

## Short reservations, only when needed

For a multi-call sequence requiring stable ownership, acquire explicit resources,
reread, edit and release promptly:

```python
manage_editor_session(action="acquire", agent_name="Vehicle agent",
                      task="Adjust pose", resources=["object:12345"], lease_seconds=60)
# Reread and perform the short sequence.
manage_editor_session(action="release")
```

Use 1–32 selectors: `object:<instance ID>` or `asset:Assets/<exact path>`.
Omitting resources reserves the whole editor; use that only for an authorized sequence
that needs exclusive global state. Do not hold reservations while thinking, editing
unrelated files or waiting for the user. Idle reservations allow reads between writes;
they are not isolated transactions. Expired/revoked reservations need reacquisition and
fresh reads. Do not release another agent's reservation as a routine retry strategy.

## Waiting and recovery

- Conflicts queue for up to 10 seconds; unrelated executable requests can pass a blocked
  request. `editor_queue_timeout` with `executed=false` means no handler started.
- A transport timeout does **not** prove non-execution. Inspect actual state before
  resubmitting a mutation, especially creates, deletes or additive changes.
- Use `manage_editor_session(action="status")` for a specific coordination problem;
  avoid continuous polling. After a scene change/reload, resolve IDs again.
- Start compilation, builds/tests/bakes and Play Mode transitions as standalone calls,
  only when permitted. Do not put them inside a batch.

## Source files and shared editor state

MCP does not lock shell/file edits, Git operations or human Unity actions. Assign
different files to agents; coordinate a handoff for shared files. Separate branches
in one working directory do not isolate edits. Separate worktrees can isolate code
preparation, but the live Unity Editor still uses its own project directory.
Never overwrite or revert another agent's changes. Scene saves, saving all dirty
assets and Undo can affect work from multiple agents.

## Captures and frame waits

Only capture or run probes when the user/project permits them. Play Mode composited
captures reuse a visible Game view, otherwise prepare a reusable **MCP Game** window
beside Inspector/Project when those docks are separate from Scene view and the focused
window. A floating window is the fallback; the user can dock it once in a separate area.
MCP restores the previously focused window and does not repeatedly pull focus back.
Keep Game view visible while it renders. Docking is best-effort across Unity versions;
view size can change image dimensions and responsive UI layout.

For UI Toolkit capture, the initial request returns `pending` and `data.capture_id`.
Poll with **that ID and all the original parameters**, following `retry_after_ms`.
Do not start a new capture for each poll. Without the ID, identical parameters do not
identify the original caller and cannot collect its result. Results expire 60 seconds
after completion if unclaimed. A changed parameter set returns `capture_parameters_changed`.

```python
capture_args = dict(action="render_ui", target="UICanvas", include_image=True)
started = manage_ui(**capture_args)
# If data.pending is true, wait the returned retry_after_ms, then:
result = manage_ui(**capture_args, capture_id=started["data"]["capture_id"])
# Repeat only while pending, using the same ID. Stop on success or a terminal error.
```

The asynchronous wait has a 10-second editor-clock deadline; synchronous composited
camera screenshots have a smaller five-step / five-second budget. The deadline is
processed when the Editor can update, not by the stalled frame coroutine. A completely
blocked Editor cannot service any watchdog until it resumes. Timeout, capture failure,
Play Mode exit and script reload cancel the request and clean up the capture runner.
Do not silently swap to a camera-only image if the task needs UI overlays. A new Game
view may need another request before it has rendered its first frame.

For custom agent probes, use a normal frame/editor-update wait when only another
logic update is needed. `yield return null` does **not** establish that compositing
finished. If real end-of-frame capture is needed, use the bounded screenshot helper or
an editor-clock deadline outside that coroutine, cancellation and cleanup. Never leave
`WaitForEndOfFrame` or a scaled-time timeout as the only way a probe can finish.
