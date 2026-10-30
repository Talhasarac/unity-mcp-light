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
        "Ordinary Unity calls are scheduled automatically; do not acquire for a whole task. "
        "Use status for agents/reservations. For a short multi-call sequence, acquire resources "
        "(object:<instance ID> or asset:Assets/<path>), then release. Omitting resources requests "
        "exclusive editor ownership. Snapshot resources BEFORE inspecting and pass the returned "
        "revision as batch_execute.expected_revision to reject stale edits. "
        "On conflict re-snapshot/re-read; on editor_queue_timeout no command ran."
    ),
)
async def manage_editor_session(
    ctx: Context,
    action: Annotated[Literal["status", "snapshot", "acquire", "renew", "release"], "Status, optimistic snapshot, or explicit reservation"] = "status",
    agent_name: Annotated[str | None, "Display name, e.g. Claude - vehicle task"] = None,
    task: Annotated[str | None, "Short description of the work"] = None,
    lease_seconds: Annotated[int, "Idle lease, 30-900 seconds; owner commands renew it"] = 300,
    resources: Annotated[list[str] | None, "1-32 object:<instance ID> or asset:Assets/<path> selectors. Required for snapshot; omitted acquire reserves the whole editor."] = None,
) -> dict[str, Any]:
    if resources is not None and (not 1 <= len(resources) <= 32 or not all(isinstance(r, str) for r in resources)):
        return {"success": False, "error": "resources must contain 1-32 selectors"}
    if action == "snapshot" and not resources:
        return {"success": False, "error": "snapshot requires resources"}
    if not 30 <= lease_seconds <= 900:
        return {"success": False, "error": "lease_seconds must be between 30 and 900"}
    instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry, instance, "manage_editor_session",
        {"action": action, "agent_name": agent_name, "task": task, "lease_seconds": lease_seconds, "resources": resources},
    )
    agent = current_agent.get()
    if agent is not None and isinstance(response, dict) and response.get("success"):
        data = response.get("data") or {}
        lease = data.get("lease_id")
        released = data.get("released_lease_id")
        if lease and lease not in agent.lease_tokens:
            prefix = lease.split(":", 1)[0] + ":" if ":" in lease else None
            retained = [t for t in agent.lease_tokens if prefix is None or not t.startswith(prefix)]
            agent.lease_tokens = [*retained, lease][-32:]
        if released:
            agent.lease_tokens = [t for t in agent.lease_tokens if t != released]
        if lease or released:
            await ctx.set_state(STATE_KEY, agent.lease_tokens)
        # Fencing tokens are transport bookkeeping, not instructions for the model.
        data.pop("lease_id", None)
        data.pop("released_lease_id", None)
    return response
