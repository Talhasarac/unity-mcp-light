---
title: manage_editor_session
sidebar_label: manage_editor_session
description: "Coordinate agents sharing Unity."
---

# `manage_editor_session`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_editor_session`

## Description

Ordinary Unity calls are scheduled automatically; do not acquire for a whole task. Use status for agents/reservations. For a short multi-call sequence, acquire resources (object:<instance ID> or asset:Assets/<path>), then release. Omitting resources requests exclusive editor ownership. Snapshot resources BEFORE inspecting and pass the returned revision as batch_execute.expected_revision to reject stale edits. On conflict re-snapshot/re-read; on editor_queue_timeout no command ran.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['status', 'snapshot', 'acquire', 'renew', 'release']` | — | Status, optimistic snapshot, or explicit reservation |
| `agent_name` | `str \| None` | — | Display name, e.g. Claude - vehicle task |
| `task` | `str \| None` | — | Short description of the work |
| `lease_seconds` | `int` | — | Idle lease, 30-900 seconds; owner commands renew it |
| `resources` | `list[str] \| None` | — | 1–32 object:/asset: selectors. Required for snapshot; omitted acquire reserves the whole editor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

