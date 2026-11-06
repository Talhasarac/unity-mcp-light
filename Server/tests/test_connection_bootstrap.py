import json
from concurrent.futures import ThreadPoolExecutor
import time

from click.testing import CliRunner
import pytest

from cli.connection_bootstrap import ensure_connection
from cli.main import cli


@pytest.fixture
def project(tmp_path):
    (tmp_path / "Assets").mkdir()
    (tmp_path / "ProjectSettings").mkdir()
    return tmp_path


def respond(project, count=1, *, success=True):
    inbox = project / "Library" / "MCPForUnity" / "ConnectionRequests"
    seen = set()
    deadline = time.monotonic() + 5
    while len(seen) < count and time.monotonic() < deadline:
        for request in inbox.glob("*.request.json"):
            if request.name in seen:
                continue
            body = json.loads(request.read_text())
            assert body["expires_at"] > time.time()
            assert body["port"] in (0, 8099)
            seen.add(request.name)
            response = {"id": body["id"], "success": success, "mcp_url": "http://127.0.0.1:8099/mcp"}
            if not success:
                response["error"] = "uv was not found"
            destination = inbox / request.name.replace(".request.json", ".response.json")
            temporary = destination.with_suffix(".tmp")
            temporary.write_text(json.dumps(response), encoding="utf-8")
            temporary.replace(destination)
        time.sleep(0.01)
    assert len(seen) == count


def test_request_and_response_work_without_a_running_http_server(project):
    with ThreadPoolExecutor() as pool:
        editor = pool.submit(respond, project)
        result = ensure_connection(project, port=8099, timeout=3)
        editor.result()
    assert result["mcp_url"] == "http://127.0.0.1:8099/mcp"
    assert not list((project / "Library/MCPForUnity/ConnectionRequests").iterdir())


def test_simultaneous_agents_get_separate_responses(project):
    with ThreadPoolExecutor() as pool:
        editor = pool.submit(respond, project, 2)
        agents = [pool.submit(ensure_connection, project, 0, 3) for _ in range(2)]
        results = [agent.result() for agent in agents]
        editor.result()
    assert results[0]["id"] != results[1]["id"]
    assert results[0]["mcp_url"] == results[1]["mcp_url"]


def test_editor_failure_reaches_agent(project):
    with ThreadPoolExecutor() as pool:
        editor = pool.submit(respond, project, success=False)
        with pytest.raises(RuntimeError, match="uv was not found"):
            ensure_connection(project, timeout=3)
        editor.result()


def test_timeout_removes_request_so_it_cannot_start_a_server_later(project):
    with pytest.raises(TimeoutError, match="updated Unity MCP Light"):
        ensure_connection(project, timeout=1)
    assert not list((project / "Library/MCPForUnity/ConnectionRequests").iterdir())


@pytest.mark.parametrize("port,timeout", [(-1, 1), (65536, 1), (0, 0), (0, 601)])
def test_invalid_options_do_not_write_requests(project, port, timeout):
    with pytest.raises(ValueError):
        ensure_connection(project, port, timeout)
    assert not (project / "Library").exists()


def test_non_project_is_rejected(tmp_path):
    with pytest.raises(ValueError, match="Not a Unity project"):
        ensure_connection(tmp_path)


def test_cli_prints_machine_readable_endpoint(project):
    with ThreadPoolExecutor() as pool:
        editor = pool.submit(respond, project)
        result = CliRunner().invoke(cli, ["connection", "ensure", "--project", str(project), "--wait-seconds", "3"])
        editor.result()
    assert result.exit_code == 0, result.output
    assert json.loads(result.output)["mcp_url"].endswith("/mcp")


def test_proxy_project_bootstraps_before_start_and_keeps_stdout_clean(monkeypatch, capsys):
    import desktop_bridge
    import cli.connection_bootstrap as bootstrap

    events = []
    def ensure(project, port, timeout):
        events.append((project, port, timeout))
        return {"mcp_url": "http://127.0.0.1:8099/mcp"}
    class Proxy:
        def run(self, **kwargs):
            events.append(kwargs)
    monkeypatch.setattr(bootstrap, "ensure_connection", ensure)
    monkeypatch.setattr(desktop_bridge, "create_desktop_bridge", lambda url: events.append(url) or Proxy())
    monkeypatch.setattr("sys.argv", ["proxy", "--project", "/project", "--server-port", "8099"])
    desktop_bridge.main()
    assert events == [("/project", 8099, 120), "http://127.0.0.1:8099/mcp", {"transport": "stdio", "show_banner": False}]
    assert capsys.readouterr().out == ""
