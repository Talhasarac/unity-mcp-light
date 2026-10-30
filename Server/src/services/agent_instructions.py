"""Shared MCP initialization guidance for the server and Desktop compatibility bridge."""


def build_instructions(project_scoped_tools: bool | None = None) -> str:
    # The Desktop proxy does not know the backend's project-scoped-tools setting.
    if project_scoped_tools is None:
        custom_tools_note = (
            "Discover tools/resources from this connection. If mcpforunity://custom-tools is advertised, "
            "read it for project-specific capabilities and use execute_custom_tool. Otherwise, use "
            "the custom tools advertised as ordinary tools."
        )
    elif project_scoped_tools:
        custom_tools_note = (
            "I have a dynamic tool system. When project-specific capabilities are needed, check mcpforunity://custom-tools "
            "to see what special capabilities are available for the current project."
        )
    else:
        custom_tools_note = (
            "Custom tools are registered as standard tools when Unity connects. "
            "No project-scoped custom tools resource is available."
        )

    return f"""
This server provides tools to interact with the Unity Game Engine Editor.
These instructions arrive automatically during MCP initialization; no pasted agent instructions or installed skill are required for coordination.
User instructions and project rules take precedence over the workflows below. Do not run compilation, tests, builds, validation, console checks, screenshots or other verification when the user/project forbids them. Tools that edit scripts can trigger import/compilation; choose an allowed file-edit workflow instead when those side effects are forbidden.

{custom_tools_note}

Targeting Unity instances:
- Use the resource mcpforunity://instances to list active Unity sessions (Name@hash).
- When multiple instances are connected, call set_active_instance with the exact Name@hash before using tools/resources to pin routing for the whole session. The server will error if multiple are connected and no active instance is set.
- Alternatively, pass unity_instance as a parameter on any individual tool call to route just that call (e.g. unity_instance="MyGame@abc123", unity_instance="abc" for a hash prefix, or unity_instance="6401" for a port number in stdio mode). This does not change the session default.

Important Workflows:

Shared editor coordination:
- Use an independent MCP session per independent agent and select the intended Unity instance. A parent and children sharing a session also share routing, reservations and snapshot credentials; the parent must coordinate them.
- Divide work by objects/assets and assign separate files for direct code edits. Do not ask the user to copy coordination instructions into each agent conversation.
- Ordinary tools are automatically scheduled on Unity's main thread. Do not acquire ownership for an entire task.
- Unrelated work can progress between calls. Plain transform modifications by unambiguous instance ID and material color/shader-property edits by exact Assets/...mat path have narrow scopes; unknown or globally affecting actions are exclusive only while executing. Compilation/tests/editor transitions still gate unsafe work.
- For a short multi-call sequence, use manage_editor_session(action="acquire", resources=["object:<instance ID>", "asset:Assets/<path>"]), re-read the reserved targets, then release promptly when finished. Do not hold a reservation while planning or waiting for user input. Omit resources only when the entire editor must remain exclusive.
- To protect an edit based on an earlier observation: take manage_editor_session(action="snapshot", resources=[...]) BEFORE inspecting, then use batch_execute(expected_revision=<snapshot data.revision>, commands=[...]). Include every read dependency. A revision conflict runs no commands: take a fresh snapshot and re-read before deciding the edit again. Snapshots expire after 10 minutes or a domain reload. Batches have no rollback.
- On editor_queue_timeout with executed=false, no handler ran. A transport timeout is different: the handler might still be executing; check actual state before retrying a mutation.
- After scene changes or domain reload, re-resolve instance IDs and discard old snapshot tokens. Expired/revoked reservations require reacquisition and fresh reads.
- Human edits, terminal/file writes and Git are not intercepted. Coordinate shared file writes separately; separate Git branches in the same working directory do not isolate files. Scene/asset saves and Undo can include other agents' changes. Never force-unlock running work.

Available capabilities:
- Use the active tool schema as the authority for tool names, action values and parameter names. Old upstream examples are not this Light fork's API.
- Use manage_tools(action="list_groups") to discover the installed tool names and optional groups. Activating a group does not restore tools removed from Light.
- Screenshots use manage_camera(action="screenshot" or "screenshot_multiview"), not manage_scene.
- manage_script accepts create/read/delete. Edit with apply_text_edits or script_apply_edits, not manage_script(action="update").
- Batch entries execute Unity-side handlers, not arbitrary Python-only tools. Session management, group activation and instance routing belong outside a batch. Batch commands always execute sequentially; parallel is a legacy hint. Compilation, build/test/bake starts and Play Mode transitions must be standalone calls.

Resources vs Tools:
- Use RESOURCES to read editor state (mcpforunity://editor/state, mcpforunity://project/info, mcpforunity://project/tags, mcpforunity://tests, etc)
- Use TOOLS to perform actions and mutations (manage_editor for play mode control, tag/layer management, etc)
- Always check related resources before modifying the engine state with tools

Reading resources (read this before using ANY resource named below):
- Resources are addressed by URI, never by name. A resource's name and URI are NOT interchangeable: names use underscores (e.g. editor_state) while URIs use slashes (e.g. mcpforunity://editor/state). Do NOT build a URI by swapping separators in the name — you will 404.
- These instructions always spell resources as full mcpforunity:// URIs — read one exactly as written. If you only have a name (from resources/list or another tool's output), look its URI up in resources/list rather than guessing it.
- Resource payloads are wrapped: the content lives under a top-level `data` object, so field paths are `data.<section>.<field>` (e.g. `data.advice.ready_for_tools`), not bare top-level fields.
- The mcpforunity:// URI names the resource, not the server. Some clients take a separate server key on a resource read — in Codex, tools are exposed as mcp__unity-mcp-light__* but resources/read wants server: "unity-mcp-light". If a read fails with an unknown-server error, list resources first and use the key exactly as returned.

Script Management (only when project/user rules allow compilation and verification):
- After creating or modifying scripts (by your own tools or the `manage_script` tool) use `read_console` to check for compilation errors before proceeding
- Only after successful compilation can new components/types be used
- You can poll mcpforunity://editor/state and read `data.compilation.is_compiling` to check if the domain reload is complete, or `data.advice.ready_for_tools` for overall readiness

Scene Setup:
- Always include a Camera and main Light (Directional Light) in new scenes
- Create a prefab from a scene object with `manage_prefabs(action="create_from_gameobject")`
- Use `manage_scene` to load, save, and query scene information

Path Conventions:
- Unless specified otherwise, all paths are relative to the project's `Assets/` folder
- Use forward slashes (/) in paths for cross-platform compatibility

Console Monitoring (only when permitted and relevant to this task):
- Use small, error-only `read_console` requests. Do not poll routinely or fetch warnings/logs unless requested.

Menu Items:
- Use `execute_menu_item` when you have read the mcpforunity://menu-items resource
- This lets you interact with Unity's menu system and third-party tools

Unity API Verification (when permitted; requires 'docs' tool group):
- When the 'docs' tool group is active, use `unity_reflect` to verify Unity API details before answering questions or writing C# code. LLM training data frequently contains incorrect, outdated, or hallucinated Unity APIs.
- BEFORE answering Unity API questions: search the project's assets (`manage_asset`) and reflect the API (`unity_reflect`) to verify. Do NOT rely on training data alone.
- Common hallucination areas: shaders and materials (always search assets for actual shader names), package-specific APIs (Input System, Cinemachine, ProBuilder, NavMesh, URP/HDRP), and APIs that changed between Unity versions.
- Workflow: `unity_reflect search` → `unity_reflect get_type` → `unity_reflect get_member`.
- For shader/material questions: use `manage_asset(action="search", filter_type="Shader")` to find actual shaders in the project before recommending one.

Payload sizing & paging (important):
- Many Unity queries can return very large JSON. Prefer **paged + summary-first** calls.
- `manage_scene(action="get_hierarchy")`:
  - Use `page_size` + `cursor` and follow `next_cursor` until null.
  - `page_size` is **items per page**; recommended starting point: **50**.
- Read `mcpforunity://scene/gameobject/{{instance_id}}/components` (substitute the actual instance ID):
  - Start with `include_properties=false` (metadata-only) and small `page_size` (e.g. **10-25**).
  - Only request `include_properties=true` when needed; keep `page_size` small (e.g. **3-10**) to bound payloads.
- `manage_asset(action="search")`:
  - Use paging (`page_size`, `page_number`) and keep `page_size` modest (e.g. **25-50**) to avoid token-heavy responses.
  - Keep `generate_preview=false` unless you explicitly need thumbnails (previews may include large base64 payloads).
"""
