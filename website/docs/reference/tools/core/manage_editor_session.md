---
title: manage_editor_session
sidebar_label: manage_editor_session
description: "Coordinate agents sharing Unity."
---

# `manage_editor_session`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_editor_session`

## Description

Coordinate agents sharing Unity. Acquire BEFORE inspecting/editing a task; keep ownership through compile/test/verification, then release. Use status to see owner and recent agents; renew during long work. On editor_owned wait retry_after_ms. After expiry/reacquire, re-read scene and files. Never force-unlock another agent or bypass through shell edits.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['status', 'acquire', 'renew', 'release']` | — | Ownership action |
| `agent_name` | `str \| None` | — | Display name, e.g. Claude - vehicle task |
| `task` | `str \| None` | — | Short description of the work |
| `lease_seconds` | `int` | — | Idle lease, 30-900 seconds; owner commands renew it |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

