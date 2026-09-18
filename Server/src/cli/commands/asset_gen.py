"""Model import CLI commands (Sketchfab marketplace and local model files).

Thin pass-through to Unity over HTTP: these commands carry NO API keys and NO
file bytes. The C# side reads provider keys from the OS secure store, performs
the provider call, and imports the result.
"""

import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import format_output, print_info
from cli.utils.connection import run_command, handle_unity_errors


@click.group(name="asset-gen")
def asset_gen():
    """Model import - import Sketchfab marketplace models and local model files."""
    pass


def _emit(result, config, verb):
    """Echo the command result, then (on success with a job_id) print the status-poll hint."""
    click.echo(format_output(result, config.format))
    if result.get("success"):
        job_id = (result.get("data") or {}).get("job_id")
        if job_id:
            print_info(f"{verb} started. Poll with: unity-mcp asset-gen status --job-id {job_id}")


@asset_gen.command("import-model")
@click.option("--uid", required=True, help="Sketchfab model uid to import.")
@click.option("--target-size", default=None, type=float, help="Normalize largest dimension (meters).")
@click.option("--name", default=None, help="Base name for the imported asset.")
@click.option("--output-folder", default=None, help="Destination folder under Assets/.")
@handle_unity_errors
def import_model(
    uid: str,
    target_size: Optional[float],
    name: Optional[str],
    output_folder: Optional[str],
):
    """Import a 3D model from the Sketchfab marketplace by uid.

    \b
    Examples:
        unity-mcp asset-gen import-model --uid abc123
        unity-mcp asset-gen import-model --uid abc123 --name MyProp --output-folder Assets/Props
    """
    config = get_config()

    params: dict[str, Any] = {"action": "import", "uid": uid}
    optional = {
        "targetSize": target_size,
        "name": name,
        "outputFolder": output_folder,
    }
    params.update({k: v for k, v in optional.items() if v is not None})

    result = run_command("import_model", params, config)
    _emit(result, config, "Import")


@asset_gen.command("import-model-file")
@click.option("--source-path", "source_path", required=True,
              help="Path to a local model file (.fbx/.obj/.glb/.gltf/.zip).")
@click.option("--name", default=None, help="Base name for the imported asset.")
@click.option("--output-folder", default=None, help="Destination folder under Assets/.")
@click.option("--target-size", default=None, type=float, help="Normalize largest dimension (meters).")
@click.option("--animation-type", "animation_type", default=None,
              type=click.Choice(["none", "generic", "humanoid", "legacy"]),
              help="FBX/OBJ rig mode: generic/humanoid surface animation clips; "
                   "legacy selects Unity's legacy Animation system (glTF ignores this).")
@handle_unity_errors
def import_model_file(source_path, name, output_folder, target_size, animation_type):
    """Import a local 3D model file (e.g. a Blender export) into the Unity project."""
    config = get_config()
    params = {
        "sourcePath": source_path,
        "name": name,
        "outputFolder": output_folder,
        "targetSize": target_size,
        "animationType": animation_type,
    }
    params = {k: v for k, v in params.items() if v is not None}
    result = run_command("import_model_file", params, config)
    click.echo(format_output(result, config.format))


@asset_gen.command("status")
@click.option("--job-id", "job_id", required=True, help="Job id returned by import-model.")
@handle_unity_errors
def status(job_id: str):
    """Check the status of a model import job.

    \b
    Examples:
        unity-mcp asset-gen status --job-id abc123
    """
    config = get_config()
    result = run_command("import_model", {"action": "status", "jobId": job_id}, config)
    click.echo(format_output(result, config.format))
