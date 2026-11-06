"""Local connection lifecycle commands, available even when HTTP is stopped."""

import click

from cli.connection_bootstrap import ensure_connection


@click.group()
def connection():
    """Start or recover the local bridge through an open Unity editor."""


@connection.command()
@click.option("--project", required=True, type=click.Path(exists=True, file_okay=False), help="Unity project root.")
@click.option("--server-port", default=0, type=click.IntRange(0, 65535), show_default=True, help="0 selects an available port; reuse a healthy connection.")
@click.option("--wait-seconds", default=120, type=click.IntRange(1, 600), show_default=True)
def ensure(project: str, server_port: int, wait_seconds: int):
    """Ensure a local HTTP server and verified Unity bridge; print JSON with the MCP URL."""
    import json

    try:
        result = ensure_connection(project, server_port, wait_seconds)
    except (OSError, ValueError, RuntimeError, TimeoutError) as exc:
        raise click.ClickException(str(exc)) from exc
    click.echo(json.dumps(result))
