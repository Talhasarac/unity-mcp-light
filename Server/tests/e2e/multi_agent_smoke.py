"""Exercise real MCP session isolation and Unity lease handover against a disposable editor.

Run with UNITY_MCP_STATUS_DIR pointing at the test editor, from Server:
    uv run python tests/e2e/multi_agent_smoke.py
Creates and deletes two temporary GameObjects. Never target a user's working scene.
"""
import asyncio
from pathlib import Path
import sys
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))

from fastmcp import Client, FastMCP
from core.config import config
from services.tools import register_all_tools
from transport.editor_sessions import EditorSessionMiddleware
from transport.unity_instance_middleware import UnityInstanceMiddleware


async def run():
    config.transport_mode = "stdio"
    server = FastMCP("live multi-agent regression")
    server.add_middleware(EditorSessionMiddleware())
    server.add_middleware(UnityInstanceMiddleware())
    register_all_tools(server)
    names = ["MCP_Lease_Test_" + uuid4().hex[:8] for _ in range(2)]

    async def call(client, tool_name, **args):
        result = await client.call_tool(tool_name, args)
        assert isinstance(result.data, dict), result
        return result.data

    async def acquire(client, name):
        return await call(client, "manage_editor_session", action="acquire", agent_name=name,
                          task="Live lease regression")

    async with Client(server, name="Claude") as a, Client(server, name="Claude") as b:
        owner = None
        try:
            # No ownership handshake is needed for unrelated ordinary commands.
            created = await asyncio.gather(*(
                call(client, "manage_gameobject", action="create", name=name)
                for client, name in zip((a, b), names)
            ))
            assert all(result["success"] for result in created), created
            grant = await acquire(a, "Claude - A")
            assert grant["success"], grant
            owner = a
            denied = await acquire(b, "Claude - B")
            assert denied.get("error") == "resource_reserved", denied
            blocked = await call(b, "manage_gameobject", action="create", name=names[1])
            assert not blocked["success"] and blocked.get("error") == "editor_queue_timeout" and blocked.get("executed") is False, blocked
            status = await call(b, "manage_editor_session", action="status")
            assert len(status["data"]["agents"]) >= 2, status
            assert (await call(a, "manage_editor_session", action="release"))["success"]
            owner = None
            assert (await acquire(b, "Claude - B"))["success"]
            owner = b
            stale = await call(a, "manage_gameobject", action="create", name=names[1])
            assert not stale["success"], stale
            print("PASS: distinct Claude sessions, rejected competing mutation, handover, stale owner rejection")
        finally:
            if owner is not None:
                for name in names:
                    await call(owner, "manage_gameobject", action="delete", target=name, search_method="by_name")
                result = await call(owner, "manage_editor_session", action="release")
                assert result["success"], result


if __name__ == "__main__":
    asyncio.run(run())
