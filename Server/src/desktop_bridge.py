"""Expose the shared HTTP server to stdio-only desktop clients."""

import argparse
import os
from contextlib import asynccontextmanager
from urllib.parse import urlsplit

from services.agent_instructions import build_instructions

from fastmcp.client.transports import StreamableHttpTransport
from fastmcp.server.providers.proxy import FastMCPProxy, StatefulProxyClient


API_KEY_ENV = "UNITY_MCP_BRIDGE_API_KEY"


def http_endpoint(value: str) -> str:
    parsed = urlsplit(value)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        raise argparse.ArgumentTypeError("Supply the server's http(s) MCP endpoint.")
    if parsed.username or parsed.password or parsed.fragment:
        raise argparse.ArgumentTypeError("The MCP endpoint must not contain credentials or a fragment.")
    return value


def create_desktop_bridge(url: str) -> FastMCPProxy:
    api_key = os.environ.get(API_KEY_ENV, "")
    transport = StreamableHttpTransport(
        http_endpoint(url), headers={"X-API-Key": api_key} if api_key else None,
    )
    # A fresh upstream session per request would lose the selected editor and lease tokens.
    # Each downstream session gets its own persistent client; unrelated clients remain isolated.
    backend = StatefulProxyClient(transport)

    @asynccontextmanager
    async def lifespan(server):
        try:
            yield
        finally:
            await backend.clear()

    return FastMCPProxy(
        name="Unity MCP Light Desktop Bridge",
        client_factory=backend.new_stateful,
        lifespan=lifespan,
        instructions=build_instructions() + "\nTools are forwarded to the running Unity MCP Light HTTP server. "
        "If it is unavailable, run unity-mcp connection ensure --project <Unity-project-root> "
        "from a terminal and reconnect this client. Unity must be open with HTTP Local selected. "
        "The proxy's --project mode bootstraps the local server on client startup.",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--url", type=http_endpoint)
    target.add_argument("--project", help="Open Unity project to bootstrap automatically on an available local port.")
    parser.add_argument("--server-port", type=int, default=0, help="Project bootstrap port (0 = auto).")
    parser.add_argument("--wait-seconds", type=int, default=120, help="Project bootstrap timeout.")
    args = parser.parse_args()
    url = args.url
    if args.project:
        from cli.connection_bootstrap import ensure_connection

        try:
            url = ensure_connection(args.project, args.server_port, args.wait_seconds)["mcp_url"]
        except (OSError, ValueError, RuntimeError, TimeoutError) as exc:
            parser.exit(1, f"Unity connection bootstrap failed: {exc}\n")
    # Only MCP frames belong on stdout. FastMCP sends diagnostics to stderr.
    create_desktop_bridge(url).run(transport="stdio", show_banner=False)


if __name__ == "__main__":
    main()
