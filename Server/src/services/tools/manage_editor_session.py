from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.editor_sessions import STATE_KEY, current_agent
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    annotations=ToolAnnotations(title="Manage Editor Session", readOnlyHint=False, destructiveHint=False),
    description=(
        "Coordinate agents sharing Unity. Acquire BEFORE inspecting/editing a task; "
        "keep ownership through compile/test/verification, then release. "
        "Use status to see owner and recent agents; renew during long work. "
        "On editor_owned wait retry_after_ms. After expiry/reacquire, re-read scene and files. "
        "Never force-unlock another agent or bypass through shell edits."
    ),
)
async def manage_editor_session(
    ctx: Context,
    action: Annotated[Literal["status", "acquire", "renew", "release"], "Ownership action"] = "status",
    agent_name: Annotated[str | None, "Display name, e.g. Claude - vehicle task"] = None,
    task: Annotated[str | None, "Short description of the work"] = None,
    lease_seconds: Annotated[int, "Idle lease, 30-900 seconds; owner commands renew it"] = 300,
) -> dict[str, Any]:
    if not 30 <= lease_seconds <= 900:
        return {"success": False, "error": "lease_seconds must be between 30 and 900"}
    instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry, instance, "manage_editor_session",
        {"action": action, "agent_name": agent_name, "task": task, "lease_seconds": lease_seconds},
    )
    agent = current_agent.get()
    if agent is not None and isinstance(response, dict) and response.get("success"):
        data = response.get("data") or {}
        lease = data.get("lease_id")
        released = data.get("released_lease_id")
        if lease and lease not in agent.lease_tokens:
            agent.lease_tokens = [*agent.lease_tokens, lease][-32:]
        if released:
            agent.lease_tokens = [t for t in agent.lease_tokens if t != released]
        if lease or released:
            await ctx.set_state(STATE_KEY, agent.lease_tokens)
        # Fencing tokens are transport bookkeeping, not instructions for the model.
        data.pop("lease_id", None)
        data.pop("released_lease_id", None)
    return response
