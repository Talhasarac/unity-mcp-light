"""Regression coverage for the advertised API and guarded batch forwarding (not a live editor test)."""
import ast
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

import services.tools.batch_execute as batch_tool

ROOT = Path(__file__).resolve().parents[2]


@pytest.mark.asyncio
async def test_guarded_batch_forwards_the_observation_token(monkeypatch):
    monkeypatch.setattr(batch_tool, "get_unity_instance_from_context", AsyncMock(return_value="Game@hash"))
    monkeypatch.setattr(batch_tool, "_get_max_commands_from_editor_state", AsyncMock(return_value=25))
    send = AsyncMock(return_value={"success": False, "error": "conflict"})
    monkeypatch.setattr(batch_tool, "send_with_unity_instance", send)
    commands = [{"tool": "manage_gameobject", "params": {"action": "modify", "target": 123, "position": [1, 0, 0]}}]
    result = await batch_tool.batch_execute(SimpleNamespace(), commands, expected_revision="observed", fail_fast=True)
    assert send.call_args.args[3]["expectedRevision"] == "observed"
    assert send.call_args.args[3]["failFast"] is True
    assert result == {"success": False, "error": "conflict"}
    assert "expectedRevision" not in commands[0]["params"]


def test_distribution_skill_copies_match():
    for relative in ("SKILL.md", "references/tools-reference.md", "references/workflows.md", "references/resources-reference.md", "references/shared-editor.md"):
        assert (ROOT / "unity-mcp-skill" / relative).read_text() == (ROOT / ".claude/skills/unity-mcp-skill" / relative).read_text()


def test_known_stale_actions_do_not_return_to_executable_examples():
    import re
    for relative in ("SKILL.md", "references/tools-reference.md", "references/workflows.md"):
        text = (ROOT / "unity-mcp-skill" / relative).read_text()
        for block in re.findall(r"```python\n(.*?)```", text, re.DOTALL):
            try:
                tree = ast.parse(block)
            except SyntaxError:
                continue  # Some reference snippets deliberately contain placeholders.
            for node in ast.walk(tree):
                if not isinstance(node, ast.Call) or not isinstance(node.func, ast.Name):
                    continue
                action = next((kw.value.value for kw in node.keywords if kw.arg == "action" and isinstance(kw.value, ast.Constant)), None)
                assert (node.func.id, action) not in {
                    ("manage_scene", "screenshot"), ("manage_script", "update"),
                    ("manage_gameobject", "get_components"),
                }, (relative, node.func.id, action)
                assert node.func.id not in {"manage_packages", "manage_probuilder", "manage_profiler", "unity_docs"}


def test_startup_prompt_no_longer_teaches_removed_component_action():
    source = (ROOT / "Server/src/services/agent_instructions.py").read_text()
    assert 'manage_gameobject(action="get_components")' not in source
    assert "Shared editor ownership (required, even for one agent)" not in source
