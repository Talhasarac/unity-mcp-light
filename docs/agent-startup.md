# Agent-controlled local startup

An agent can start the local HTTP server and connect Unity without clicking **Start
Server** or **Repair Connection**. The Unity project must be open with the updated
package, and its selected transport must be **HTTP Local**. Python and uv are still
required. This does not launch Unity or install missing dependencies for the user.

## From a terminal

From a source checkout with the matching Python server installed:

```sh
unity-mcp connection ensure --project "/path/to/UnityProject"
```

During source development, use `uv run --directory Server unity-mcp connection ensure
--project "/path/to/UnityProject"` from the repository root. No running MCP server
is required for this command. The output is JSON:

```json
{"success":true,"transport":"http","port":8080,"mcp_url":"http://127.0.0.1:8080/mcp","instance":"project-hash","reused":false,"id":"request-id"}
```

Use the returned `mcp_url`, never an assumed default port. `--server-port 8099`
requests an explicit port; the default `0` reuses a healthy bridge/server, otherwise
tries the configured port and falls back to an available loopback port. An occupied
explicit port fails rather than terminating its owner. A healthy bridge on a different
port stays running and the command reports the conflict. Independent agents targeting
the same editor share its server; they do not each need a separate HTTP port.

`--wait-seconds 120` controls the wait (1–600 seconds). Startup can take longer on a
fresh machine while uv downloads the server. Retry with a longer wait if necessary.
Expired requests cannot initiate a later launch. A launch that already began may
finish after the caller times out; the next request reuses it.

## Start automatically when an MCP client connects

The existing stdio-to-HTTP proxy also accepts a project path instead of a fixed URL:

```sh
mcp-for-unity-proxy --project "/path/to/UnityProject"
```

Configure this command once as the client's stdio MCP server. It asks Unity to ensure
the server is ready before forwarding MCP traffic to the returned URL. For local
source development, a client registration can use:

```json
{
  "mcpServers": {
    "unity-mcp-light": {
      "command": "uv",
      "args": ["run", "--directory", "/path/to/unity-mcp-light/Server", "mcp-for-unity-proxy", "--project", "/path/to/UnityProject"]
    }
  }
}
```

Optional `--server-port` and `--wait-seconds` arguments work here too. Existing
`--url http://127.0.0.1:8080/mcp` registrations keep their fixed-endpoint behavior.
Use a single registration for this package to avoid connecting to an older server
under another name. Restart/reconnect the client after changing its registration.

## Recovery and scope

When the server or Unity connection is lost, rerun `connection ensure` and use its
returned URL, or reconnect a project-based proxy. Unity already retries WebSocket
connections after temporary interruptions and resumes them after domain reloads.
The bootstrap can restart a dead server as well. A running proxy is not a server
supervisor; reconnect it after recovering a failed backend.

Requests and responses live under `Library/MCPForUnity/ConnectionRequests`. Unity
polls while idle, serializes requests from concurrent agents, and writes each response
atomically. The request remains available across domain reloads until it completes.
This is a local filesystem control path with the same trust boundary as agents that
can already edit the project. It opens no extra network listener.

The chosen URL is stored in per-editor `SessionState`, so it survives domain reloads
without changing another project's saved port. Editing the local URL in Unity clears
that override. It resets when Unity exits. Native HTTP clients must use the returned
URL; a project-based proxy discovers it again on every client startup. The bootstrap
does not rewrite client configuration, switch stdio/remote transports, or stop an
existing healthy bridge. In batch mode the file watcher requires
`UNITY_MCP_ALLOW_BATCH=1`, matching the existing bridge startup policy.
