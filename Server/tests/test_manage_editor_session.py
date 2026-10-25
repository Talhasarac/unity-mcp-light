import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from transport.editor_sessions import (
    EditorAgent, EditorSessionMiddleware, METADATA_KEY, STATE_KEY, command_params, current_agent,
)
import services.tools.manage_editor_session as session_tool


def test_metadata_uses_session_identity_and_copies_tokens():
    agent = EditorAgent("a", "Claude", "http", ["lease-a"])
    token = current_agent.set(agent)
    try:
        source = {"action": "create", METADATA_KEY: {"id": "spoofed"}}
        first, second = command_params(source), command_params(source)
        assert first[METADATA_KEY]["id"] == "a"
        assert first[METADATA_KEY]["request_id"] != second[METADATA_KEY]["request_id"]
        agent.lease_tokens.append("later")
        assert first[METADATA_KEY]["lease_tokens"] == ["lease-a"]
        assert source[METADATA_KEY]["id"] == "spoofed"
    finally:
        current_agent.reset(token)
    assert METADATA_KEY not in command_params(source)


def context_for(session_id):
    return SimpleNamespace(
        fastmcp_context=SimpleNamespace(
            request_context=object(), session_id=session_id,
            session=SimpleNamespace(client_params=SimpleNamespace(clientInfo=SimpleNamespace(name="Claude"))),
            get_state=AsyncMock(return_value=[]),
        )
    )


@pytest.mark.asyncio
async def test_concurrent_requests_with_identical_client_names_are_isolated():
    middleware = EditorSessionMiddleware()

    async def next_call(context):
        before = current_agent.get().session_id
        await asyncio.sleep(0)
        assert current_agent.get().session_id == before
        assert before != context.fastmcp_context.session_id  # raw session credential is hidden
        return before

    ids = await asyncio.gather(*(middleware.on_request(context_for(sid), next_call) for sid in ["a", "b"]))
    assert ids[0] != ids[1]
    assert current_agent.get() is None


@pytest.mark.asyncio
async def test_exception_does_not_leak_identity():
    async def fail(_):
        raise ValueError("failure")
    with pytest.raises(ValueError):
        await EditorSessionMiddleware().on_request(context_for("a"), fail)
    assert current_agent.get() is None


@pytest.mark.asyncio
async def test_acquire_retains_token_for_transport_and_hides_it_from_model(monkeypatch):
    ctx = SimpleNamespace(set_state=AsyncMock())
    monkeypatch.setattr(session_tool, "get_unity_instance_from_context", AsyncMock(return_value="Project@hash"))
    send = AsyncMock(return_value={"success": True, "data": {"lease_id": "new-lease", "owner": {"id": "a"}}})
    monkeypatch.setattr(session_tool, "send_with_unity_instance", send)
    agent = EditorAgent("a", "Claude", "http", ["other-project-lease"])
    token = current_agent.set(agent)
    try:
        response = await session_tool.manage_editor_session(ctx, "acquire", "Claude - UI", "Build menu")
        assert agent.lease_tokens == ["other-project-lease", "new-lease"]
        ctx.set_state.assert_awaited_once_with(STATE_KEY, agent.lease_tokens)
        assert "lease_id" not in response["data"]
        send.return_value = {"success": True, "data": {"released_lease_id": "new-lease"}}
        await session_tool.manage_editor_session(ctx, "release")
        assert agent.lease_tokens == ["other-project-lease"]
    finally:
        current_agent.reset(token)


@pytest.mark.asyncio
async def test_denied_acquire_does_not_update_tokens(monkeypatch):
    ctx = SimpleNamespace(set_state=AsyncMock())
    monkeypatch.setattr(session_tool, "get_unity_instance_from_context", AsyncMock(return_value=None))
    monkeypatch.setattr(session_tool, "send_with_unity_instance", AsyncMock(return_value={"success": False, "error": "editor_owned"}))
    token = current_agent.set(EditorAgent("a", "Claude", "stdio"))
    try:
        assert not (await session_tool.manage_editor_session(ctx, "acquire"))["success"]
        ctx.set_state.assert_not_awaited()
    finally:
        current_agent.reset(token)


@pytest.mark.asyncio
async def test_stdio_executor_preserves_identity(monkeypatch):
    import transport.legacy.unity_connection as legacy
    seen = []
    def send(*args, **kwargs):
        seen.append(command_params({})[METADATA_KEY])
        return {"success": True}
    monkeypatch.setattr(legacy, "send_command_with_retry", send)
    agent = EditorAgent("a", "Claude", "stdio", ["lease"])
    token = current_agent.set(agent)
    try:
        await legacy.async_send_command_with_retry("read_console", {})
        assert seen[0]["id"] == "a"
        assert seen[0]["lease_tokens"] == ["lease"]
    finally:
        current_agent.reset(token)


def test_cli_session_requires_unique_agent_id():
    from click.testing import CliRunner
    from cli.main import cli
    result = CliRunner().invoke(cli, ["session", "acquire"], env={"UNITY_MCP_AGENT_ID": ""})
    assert result.exit_code != 0
    assert "agent-id" in result.output


def test_automation_releases_on_failure_and_restores_context():
    from transport.editor_sessions import automation_editor_session
    calls = []
    def send(command, params, **kwargs):
        calls.append((params["action"], current_agent.get().session_id))
        if params["action"] == "release":
            raise OSError("bridge disconnected")
        return {"success": True, "data": {"lease_id": "test-lease"}}
    with pytest.raises(ValueError, match="original failure"):
        with automation_editor_session(send, "Project@hash", "Harness", "Tests") as agent:
            assert agent.lease_tokens == ["test-lease"]
            raise ValueError("original failure")
    assert [c[0] for c in calls] == ["acquire", "release"]
    assert calls[0][1] == calls[1][1]
    assert current_agent.get() is None


def test_automation_cannot_run_after_denied_acquire():
    from transport.editor_sessions import automation_editor_session
    with pytest.raises(RuntimeError, match="ownership unavailable"):
        with automation_editor_session(lambda *a, **k: {"success": False, "error": "editor_owned"}, None, "Harness", "Tests"):
            pytest.fail("must not enter a task without owning the editor")
    assert current_agent.get() is None


def test_real_mcp_sessions_keep_identity_and_lease_across_requests():
    # Some legacy tests replace FastMCP globally. A fresh process exercises the real
    # initialize/session lifecycle and the tool's transparent token bookkeeping.
    import subprocess
    import sys
    import textwrap
    script = textwrap.dedent('''
        import asyncio
        from fastmcp import FastMCP, Client, Context
        from transport.editor_sessions import EditorSessionMiddleware, current_agent, command_params, METADATA_KEY
        import services.tools.manage_editor_session as tool

        async def fake_send(*args, **kwargs):
            return {"success": True, "data": {"lease_id": "lease-" + current_agent.get().session_id}}
        async def instance(ctx):
            return "Project@hash"
        tool.send_with_unity_instance = fake_send
        tool.get_unity_instance_from_context = instance
        server = FastMCP("session integration")
        server.add_middleware(EditorSessionMiddleware())
        server.tool(tool.manage_editor_session)
        @server.tool
        async def probe(ctx: Context):
            await asyncio.sleep(0.01)
            return command_params({})[METADATA_KEY]

        async def check():
            async with Client(server, name="Claude") as a, Client(server, name="Claude") as b:
                first, second = await asyncio.gather(a.call_tool("probe"), b.call_tool("probe"))
                aid, bid = first.data["id"], second.data["id"]
                assert aid != bid
                grant = await a.call_tool("manage_editor_session", {"action": "acquire"})
                assert "lease_id" not in grant.data["data"]
                first, second = await asyncio.gather(a.call_tool("probe"), b.call_tool("probe"))
                assert first.data["id"] == aid
                assert first.data["lease_tokens"] == ["lease-" + aid]
                assert second.data["id"] == bid and second.data["lease_tokens"] == []
            assert current_agent.get() is None
        asyncio.run(check())
    ''')
    result = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr
