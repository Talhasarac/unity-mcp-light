"""Bootstrap through the project's Library, without a working MCP connection."""

import json
import os
from pathlib import Path
import time
import uuid


def ensure_connection(project: str | Path, port: int = 0, timeout: float = 120) -> dict:
    project = Path(project).expanduser().resolve()
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings").is_dir():
        raise ValueError(f"Not a Unity project: {project}")
    if not 0 <= port <= 65535:
        raise ValueError("Server port must be 0 (auto) or 1–65535.")
    if not 1 <= timeout <= 600:
        raise ValueError("Wait timeout must be between 1 and 600 seconds.")

    inbox = project / "Library" / "MCPForUnity" / "ConnectionRequests"
    inbox.mkdir(parents=True, exist_ok=True)
    request_id = uuid.uuid4().hex
    request = inbox / f"{request_id}.request.json"
    temporary = inbox / f"{request_id}.tmp"
    response = inbox / f"{request_id}.response.json"
    deadline = time.monotonic() + timeout
    try:
        temporary.write_text(json.dumps({
            "id": request_id, "port": port, "expires_at": time.time() + timeout,
        }), encoding="utf-8")
        os.replace(temporary, request)
        while time.monotonic() < deadline:
            if response.exists():
                result = json.loads(response.read_text(encoding="utf-8-sig"))
                if result.get("id") != request_id:
                    raise RuntimeError("Unity returned a mismatched connection response.")
                if not result.get("success"):
                    raise RuntimeError(result.get("error", "Unity could not start the connection."))
                return result
            time.sleep(0.2)
        raise TimeoutError(
            f"Unity did not complete the connection request within {timeout:g}s. "
            "Open this project in Unity with the updated Unity MCP Light package; "
            "let compilation/import finish, then retry."
        )
    finally:
        for path in (temporary, request, response):
            path.unlink(missing_ok=True)
