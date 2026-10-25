"""Request-scoped agent identity; Unity remains the authority for editor ownership."""
from contextvars import ContextVar
from contextlib import contextmanager
from dataclasses import dataclass, field
import hashlib
import logging
from uuid import uuid4

from fastmcp.server.middleware import Middleware

from core.config import config

STATE_KEY = "unity_editor_lease_tokens"
METADATA_KEY = "_mcp_editor_session"


@dataclass
class EditorAgent:
    session_id: str
    client_name: str
    transport: str
    lease_tokens: list[str] = field(default_factory=list)


current_agent: ContextVar[EditorAgent | None] = ContextVar("unity_editor_agent", default=None)


def command_params(params: dict) -> dict:
    """Attach a fresh request ID once, before transport retries; never mutate caller data."""
    agent = current_agent.get()
    result = dict(params)
    # Tool parameters cannot impersonate a different MCP session.
    result.pop(METADATA_KEY, None)
    if agent is not None:
        result[METADATA_KEY] = {
            "id": agent.session_id,
            "name": agent.client_name,
            "transport": agent.transport,
            "lease_tokens": list(agent.lease_tokens),
            "request_id": uuid4().hex,
        }
    return result


class EditorSessionMiddleware(Middleware):
    async def on_request(self, context, call_next):
        ctx = context.fastmcp_context
        if ctx is None or ctx.request_context is None:
            return await call_next(context)
        # Hash the actual MCP session ID, not the peer-supplied client_id or client name.
        # The raw HTTP session credential must never reach Unity's UI or logs.
        session_id = hashlib.sha256(ctx.session_id.encode()).hexdigest()[:32]
        client_params = ctx.session.client_params
        name = client_params.clientInfo.name if client_params else "MCP client"
        leases = await ctx.get_state(STATE_KEY) or []
        agent = EditorAgent(session_id, name[:80], config.transport_mode, list(leases))
        token = current_agent.set(agent)
        try:
            return await call_next(context)
        finally:
            current_agent.reset(token)


def acquire_for_automation(send, instance_id, agent, task):
    """Use the same lease protocol for repository test harnesses as for MCP agents."""
    from models.unity_response import normalize_unity_response
    response = normalize_unity_response(send("manage_editor_session", {
        "action": "acquire", "agent_name": agent.client_name, "task": task,
        "lease_seconds": 300,
    }, instance_id=instance_id))
    if not isinstance(response, dict) or not response.get("success"):
        raise RuntimeError(f"Editor ownership unavailable: {response}")
    lease = response.get("data", {}).get("lease_id")
    if not lease:
        raise RuntimeError("Unity package does not support editor leases; update both sides")
    agent.lease_tokens.append(lease)


@contextmanager
def automation_editor_session(send, instance_id, name, task):
    from models.unity_response import normalize_unity_response
    agent = EditorAgent(uuid4().hex, name, "stdio")
    token = current_agent.set(agent)
    acquired = False
    try:
        acquire_for_automation(send, instance_id, agent, task)
        acquired = True
        yield agent
    finally:
        try:
            if acquired:
                response = normalize_unity_response(send("manage_editor_session", {"action": "release"}, instance_id=instance_id))
                if not isinstance(response, dict) or not response.get("success"):
                    logging.getLogger(__name__).warning("Editor lease retained until idle expiry: %s", response)
        except Exception as exc:
            # A lost connection during cleanup must not hide the original test failure.
            logging.getLogger(__name__).warning("Could not release editor lease; idle expiry will recover it: %s", exc)
        finally:
            current_agent.reset(token)
