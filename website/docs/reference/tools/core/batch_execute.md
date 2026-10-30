---
title: batch_execute
sidebar_label: batch_execute
description: "Executes multiple MCP commands in a single batch for dramatically better performance."
---

# `batch_execute`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.batch_execute`

## Description

Execute commands sequentially in one Unity request, reducing round trips. Default limit 25; configurable up to 100. For stale-edit protection, take manage_editor_session(action='snapshot', resources=[...]) BEFORE inspecting, then pass its revision as expected_revision. A conflict runs no commands. Session management and compilation/build/test/bake/Play Mode transitions must be standalone calls. A batch is non-interleaved for conflicting MCP operations, but has no rollback on failure.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `commands` | `list[dict[str, Any]]` | yes | List of commands with 'tool' and 'params' keys. |
| `parallel` | `bool \| None` | — | Legacy hint only; Unity executes batch commands sequentially |
| `fail_fast` | `bool \| None` | — | Stop processing after the first failure |
| `max_parallelism` | `int \| None` | — | Hint for the maximum number of parallel workers |
| `expected_revision` | `str \| None` | — | Single-use snapshot revision checked immediately before execution. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Why batch?

A loop of 10 individual `manage_gameobject` calls pays 10 round trips to Unity. `batch_execute` pays one. This reduces transport overhead; the speedup depends on the work. Use them whenever the next call doesn't need the previous call's return value.

### Spawn three colored cubes in one round trip

> Create a red, blue, and yellow cube at x = -1, 0, 1.

```json
{
  "commands": [
    { "tool": "manage_gameobject", "params": {
        "action": "create", "name": "RedCube",
        "primitive_type": "Cube", "position": [-1, 0, 0]
    }},
    { "tool": "manage_gameobject", "params": {
        "action": "create", "name": "BlueCube",
        "primitive_type": "Cube", "position": [0, 0, 0]
    }},
    { "tool": "manage_gameobject", "params": {
        "action": "create", "name": "YellowCube",
        "primitive_type": "Cube", "position": [1, 0, 0]
    }},
    { "tool": "manage_material", "params": {
        "action": "create", "material_path": "Materials/Red.mat",
        "shader": "Standard", "properties": { "_Color": [1, 0, 0, 1] }
    }},
    { "tool": "manage_material", "params": {
        "action": "create", "material_path": "Materials/Blue.mat",
        "shader": "Standard", "properties": { "_Color": [0, 0, 1, 1] }
    }},
    { "tool": "manage_material", "params": {
        "action": "create", "material_path": "Materials/Yellow.mat",
        "shader": "Standard", "properties": { "_Color": [1, 1, 0, 1] }
    }},
    { "tool": "manage_material", "params": {
        "action": "assign_material_to_renderer", "target": "RedCube",
        "search_method": "by_name", "material_path": "Materials/Red.mat"
    }},
    { "tool": "manage_material", "params": {
        "action": "assign_material_to_renderer", "target": "BlueCube",
        "search_method": "by_name", "material_path": "Materials/Blue.mat"
    }},
    { "tool": "manage_material", "params": {
        "action": "assign_material_to_renderer", "target": "YellowCube",
        "search_method": "by_name", "material_path": "Materials/Yellow.mat"
    }}
  ]
}
```

### Mix tools freely

A batch can mix supported Unity handlers (not server-only tools or Python-only aliases), as long as ordering inside the batch doesn't depend on a previous call's *return value*. If you need the response of step N to feed step N+1, split into two batches.

### Stop on first failure vs continue

Set `fail_fast: true` to abort the rest on the first failed step. Pass `fail_fast: false` to attempt every operation and collect per-step results, useful for "best-effort cleanup" patterns.

### Parallel reads

`parallel` and `max_parallelism` are legacy hints. Unity executes all batch commands sequentially on its main thread.

### Limits

Batch size is configurable in the Unity MCP Tools window (default 25, hard max 100). Past the limit, split into multiple batches — round-trip cost is still amortized.
<!-- examples:end -->

