# Install (Rhino 7 on macOS)

This fork talks to **Rhino 7** on a Mac. It does not use the Package Manager
`rhinomcp` yak. That package is the Rhino 8 line and will not load here.

Stack:

```
Grok Build --MCP stdio--> Python server (server/)
  --TCP 127.0.0.1:1999--> C# plugin (plugin/, net48) --> Rhino 7 + GH1
```

## What this machine already has

Checked on this checkout:

| Thing | Status |
|---|---|
| Rhino 7 | `/Applications/Rhino 7.app` (7.38, x86_64, runs under Rosetta on Apple Silicon) |
| uv | Homebrew `uv 0.12.3` |
| .NET 8 SDK | user-local install at `~/.dotnet` (8.0.425). No sudo. |

If `dotnet` is missing in a new terminal, add this to `~/.config/fish/config.fish`:

```fish
set -gx DOTNET_ROOT $HOME/.dotnet
fish_add_path $HOME/.dotnet
```

Or in bash/zsh:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

## 1. Build and stage the plugin

From the repo root:

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
./plugin/install.sh
```

That builds `plugin/bin/Debug/net48/rhinomcp.rhp` and copies it plus its
dependent dlls to:

```
~/Library/Application Support/McNeel/Rhinoceros/7.0/Plug-ins/rhinomcp/
```

Equivalent one-liner without the script:

```bash
dotnet build plugin/rhinomcp.csproj -p:CopyToRhinoPluginDir=true -p:RhinoVersion=7.0
```

## 2. Register the plugin in Rhino 7 (first time only)

Rhino for Mac does not auto-load a new `.rhp` just because it sits in that
folder. Register it once:

1. Quit Rhino 7 if it is open.
2. Open Rhino 7.
3. Drag `~/Library/Application Support/McNeel/Rhinoceros/7.0/Plug-ins/rhinomcp/rhinomcp.rhp`
   onto the viewport.
4. Accept the load dialog.

Later rebuilds overwrite the same files. Restart Rhino to pick them up. You do
not need to drag again.

## 3. Start the TCP bridge

In the Rhino command line:

```
mcpstart
```

You should see `RhinoMCP server started on 127.0.0.1:1999`. `mcpstop` ends it.
Run `mcpstart` once per Rhino session.

If `mcpstart` is unknown, the `.rhp` did not load. Check `Tools → Options →
Plug-ins` (or drag the file again) and look at the Rhino command history for a
load error.

## 4. Point Grok Build at the local Python server

This repo already has a project MCP config at `.grok/config.toml`. It launches
the server from **this checkout**, not from PyPI, with the dangerous tools off:

- `run_command`
- `execute_rhinoscript_python_code`
- `execute_rhinocommon_csharp_code`

Restart Grok Build in this directory, or open `/mcps` and press `r` to reload.

To add it by hand:

```bash
grok mcp add --scope project rhino \
  -e RHINO_MCP_HOST=127.0.0.1 \
  -e RHINO_MCP_PORT=1999 \
  -e RHINO_MCP_ENABLE_RUN_COMMAND=0 \
  -e RHINO_MCP_ENABLE_RHINOSCRIPT=0 \
  -e RHINO_MCP_ENABLE_CSHARP=0 \
  -- uv run --directory /ABS/PATH/rhino-7-mcp/server rhinomcp
```

Only one MCP client should hold the Rhino TCP connection at a time.

## 5. Smoke test (no Grok required)

With Rhino open and `mcpstart` running:

```bash
python3 scripts/mvp_smoke.py
```

That hits the plugin directly and writes `scripts/smoke_output/capture_viewport.png`
on success.

## Verify loop (no Rhino needed)

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet build plugin/rhinomcp.csproj
cd server && uv run --python 3.12 --extra dev pytest -q && cd ..
uv run --python 3.12 --with pytest --with jsonschema --with referencing \
  pytest contracts/test_schemas.py -q
```

## Out of scope for this MVP

Grasshopper tools, `execute_*` code, Layout/PDF, multi-sheet. Layer convention
for tilbygg work (`A-WALL`, `A-OPEN`, `A-STRU`, `A-ANNO`, `X-EXIST`) comes after
the smoke is green.
