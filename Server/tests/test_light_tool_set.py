"""Guard the Light fork's tool set when merging upstream releases."""
from pathlib import Path

import subprocess
import sys
import textwrap

from services.registry import DEFAULT_ENABLED_GROUPS, get_registered_tools
from utils.module_discovery import discover_modules
import services.tools as tools_package


def test_upstream_sync_preserves_light_tools():
    list(discover_modules(Path(tools_package.__file__).parent, tools_package.__package__))
    names = {tool["name"] for tool in get_registered_tools()}
    assert {"inspect_prefab", "blender_bridge", "import_model_file", "manage_animation"} <= names
    assert names.isdisjoint({
        "generate_image", "generate_model", "generate_audio", "import_model",
        "manage_probuilder", "manage_profiler", "manage_vfx",
        "manage_packages", "unity_docs", "debug_request_context", "manage_script_capabilities",
    })
    assert len(names) == 39


def test_animation_is_an_optional_registered_group():
    list(discover_modules(Path(tools_package.__file__).parent, tools_package.__package__))
    tool = next(t for t in get_registered_tools() if t["name"] == "manage_animation")
    assert tool["group"] == "animation"
    assert "group:animation" in tool["kwargs"]["tags"]
    assert "animation" not in DEFAULT_ENABLED_GROUPS


def test_animation_visibility_and_compact_schema():
    # Integration conftest installs global MCP stubs; exercise the real client in isolation.
    script = textwrap.dedent("""
        import asyncio
        from fastmcp import Client, FastMCP
        from core.config import config
        from services.tools import register_all_tools

        async def check():
            config.transport_mode = "http"
            mcp = FastMCP("animation visibility test")
            register_all_tools(mcp)
            async with Client(mcp) as client:
                assert "manage_animation" not in {t.name for t in await client.list_tools()}
                await client.call_tool("manage_tools", {"action": "activate", "group": "animation"})
                tool = next(t for t in await client.list_tools() if t.name == "manage_animation")
                assert tool.inputSchema["properties"]["target"]["type"] == "string"
                assert "default" not in tool.inputSchema["properties"]["target"]
                await client.call_tool("manage_tools", {"action": "deactivate", "group": "animation"})
                assert "manage_animation" not in {t.name for t in await client.list_tools()}

        asyncio.run(check())
    """)
    result = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr
