# Capture recovery and a separate Game view

Composited Play Mode captures need a frame after UI overlays have rendered. Unity's
`WaitForEndOfFrame` can stop advancing when Game view is hidden behind Scene view.
Replacing it with a normal frame wait does not guarantee a complete composited image.

In v1.0.4, the shared capture helper first reuses a visible Game view. Otherwise it
reuses or creates **MCP Game** beside Inspector/Project, avoiding every Scene view dock
and the currently focused dock. When safe docking cannot be determined, it creates a
floating 960×540 window. It restores the previous focus and does not repeatedly pull
focus back during a capture. Leave that view visible, or dock it once in a separate area.
Docking and visibility detection use guarded reflection over internal Unity types;
compatibility and first-frame rendering still need user verification.

The asynchronous helper uses a 10-second editor-clock deadline. Synchronous composited
screenshots have a five-step / five-second budget. Play Mode exit, reload, cancellation,
capture failure and timeout all finish the request once and remove its hidden runner.
Late textures are discarded. A blocked Editor cannot execute a watchdog until its update
loop resumes. Camera-only RenderTexture captures do not open or focus Game view.

`manage_ui(action="render_ui")` returns `pending` and `data.capture_id`. Poll using the
original arguments plus this ID, honoring `retry_after_ms`. The ID is required even
when another caller happens to use identical arguments. Changed arguments return
`capture_parameters_changed`; unknown IDs return `capture_not_found`. Abandoned results
and textures expire 60 seconds after completion. Failures never silently become
camera-only images that omit the UI. `width`/`height` apply to edit-mode panel rendering;
Play Mode captures the rendered Game view, whose dimensions can affect responsive UI.

The default file-only `ScreenCapture.CaptureScreenshot` path still returns an async file
request; it does not promise a file already exists. The bounded request helper covers
composited inline screenshots and Play Mode `render_ui`. Arbitrary user-written probes
must use the bounded helper or their own independent editor-clock cancellation path.
Automatic MCP instructions and the skill explain this distinction.

## Source review and verification handoff — UNVERIFIED

No builds, compiles, tests, validation commands, Unity/Editor checks or Play Mode checks
were run. Event ordering was reviewed in source:

- Switching to Scene before end-of-frame leaves a separate Game view available; if it
  still does not redraw, the independent deadline ends the capture.
- Timeout before completion makes the request terminal; a later callback cannot revive
  it. Completion before timeout removes it from the active request collection.
- Play Mode exit/reload cancels runners and discards pending UI state, regardless of
  whether the request cancellation hook or UI cleanup hook runs first.
- A second request without the first request's ID cannot consume its image. Abandoned
  images are reclaimed by editor update even if nobody polls again.
- Docking avoids the previously focused dock so restoring focus cannot immediately
  hide a newly created Game view. A user-docked MCP Game behind Scene view is not
  brought forward over Scene view; a non-rendering capture can still time out.

User checks: capture with Scene view active; switch tabs during capture; hide/close MCP
Game; pause/exit Play Mode while pending; repeat two identical requests without sharing
the ID. Confirm terminal errors, no repeated focus stealing, correct image/UI dimensions,
no leftover capture runner, and successful subsequent captures. Multiple Game views and
different Unity layouts require particular attention to which view supplies ScreenCapture.
