# Model Import — Manual Verification Checklist

The `asset_gen` group now contains only the model import tools (`import_model` and
`import_model_file`). They download from Sketchfab or read local files and write real
assets into a licensed Unity Editor, so they **cannot be covered headlessly**. Run this
checklist by hand in an interactive Editor before shipping.

## Prerequisites

- [ ] A licensed Unity Editor with the package installed and the bridge connected.
- [ ] Enable the group: `manage_tools` → enable `asset_gen` (it is off by default).
- [ ] Open **Window → MCP for Unity → Generative** tab to enter the Sketchfab token
      (stored in the OS secure store — Keychain / Windows Credential Manager / libsecret).

## Sketchfab (`import_model`)

- [ ] Enter the Sketchfab token.
- [ ] `import_model(action=search, query="wooden chair")`, then
      `import_model(action=import, uid=<from search>)`, and poll `import_model(action=status, job_id=...)`.
- [ ] Confirm the downloaded zip extracts and the model imports.
- [ ] Confirm the **path-traversal guard** holds (no files written outside the target dir).

## Local files (`import_model_file`)

- [ ] `import_model_file(source_path="/path/to/model.fbx")` imports under `Assets/`.
- [ ] Repeat with `.obj` and a `.zip`.
- [ ] Install **glTFast** from the **Deps** tab, then import a `.glb` and confirm there is no missing-importer error.

## Security spot-check

- [ ] Confirm no key value ever appears in MCP tool output.
- [ ] Confirm no key value appears in logs.
- [ ] Confirm no key value appears in the job `status` payload.
- [ ] Confirm no key value is committed to git.
