"""Desktop proxy contracts; uses in-memory MCP servers, not Unity or a live HTTP endpoint."""
import argparse
from contextlib import asynccontextmanager

import pytest
from fastmcp import Client, Context, FastMCP

import desktop_bridge


@pytest.mark.asyncio
async def test_bridge_retains_session_state_and_isolates_other_clients(monkeypatch):
    active_connections = 0

    @asynccontextmanager
    async def lifespan(server):
        nonlocal active_connections
        active_connections += 1
        try:
            yield
        finally:
            active_connections -= 1

    backend = FastMCP("Test Unity backend", lifespan=lifespan)

    @backend.tool
    async def select_editor(value: str, ctx: Context) -> str:
        await ctx.set_state("editor", value)
        return value

    @backend.tool
    async def read_editor(ctx: Context) -> str:
        return await ctx.get_state("editor") or "none"

    monkeypatch.setattr(desktop_bridge, "StreamableHttpTransport", lambda *a, **kw: backend)
    proxy = desktop_bridge.create_desktop_bridge("http://127.0.0.1:8088/mcp")
    async with Client(proxy) as first, Client(proxy) as second:
        await first.call_tool("select_editor", {"value": "project-a"})
        assert (await first.call_tool("read_editor")).data == "project-a"
        assert (await second.call_tool("read_editor")).data == "none"
        await second.call_tool("select_editor", {"value": "project-b"})
        assert (await first.call_tool("read_editor")).data == "project-a"
        assert (await second.call_tool("read_editor")).data == "project-b"
    assert active_connections == 0


@pytest.mark.asyncio
async def test_bridge_discovers_backend_tools_and_forwards_structured_results(monkeypatch):
    backend = FastMCP("Live capabilities")

    @backend.tool
    def inspect_editor() -> dict:
        return {"success": True, "objects": [123]}

    monkeypatch.setattr(desktop_bridge, "StreamableHttpTransport", lambda *a, **kw: backend)
    proxy = desktop_bridge.create_desktop_bridge("http://127.0.0.1:8088/mcp")
    async with Client(proxy) as client:
        assert {tool.name for tool in await client.list_tools()} == {"inspect_editor"}
        result = await client.call_tool("inspect_editor")
        assert result.structured_content == {"success": True, "objects": [123]}


@pytest.mark.parametrize("url", ["file:///tmp/editor", "http://user:password@localhost/mcp", "http://localhost/mcp#fragment"])
def test_invalid_endpoint_is_rejected(url):
    with pytest.raises(argparse.ArgumentTypeError):
        desktop_bridge.http_endpoint(url)


def test_api_key_is_forwarded_as_header(monkeypatch):
    captured = {}
    backend = FastMCP("Header test")

    def transport(url, **kwargs):
        captured.update(url=url, **kwargs)
        return backend

    monkeypatch.setattr(desktop_bridge, "StreamableHttpTransport", transport)
    monkeypatch.setenv(desktop_bridge.API_KEY_ENV, "sample-test-key")
    desktop_bridge.create_desktop_bridge("https://example.test/mcp")
    assert captured == {"url": "https://example.test/mcp", "headers": {"X-API-Key": "sample-test-key"}}
