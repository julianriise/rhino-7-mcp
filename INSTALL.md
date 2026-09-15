# Install (Rhino 7 on macOS)

This fork talks to **Rhino 7** on a Mac. It does not use the Package Manager
`rhinomcp` yak. That package is the Rhino 8 line and will not load here.

Stack:

```
Grok Build --MCP stdio--> Python server (server/)
  --TCP 127.0.0.1:1999--> C# plugin (plugin/, net48) --> Rhino 7 + GH1
```

## What this machine already has

Checked on this checkout (build machine):

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

That builds `plugin/bin/Debug/net48/rhinomcp.rhp` and copies the whole output
into a **Mac plugin package**:

```
~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/rhinomcp.rhp/
  rhinomcp.rhp          # the assembly (a renamed DLL)
  Newtonsoft.Json.dll
  ...
```

Rhino 7 on Mac scans `MacPlugIns` for folders named `*.rhp` and loads the
assembly of the same name inside. That is not the Windows path
`.../7.0/Plug-ins/`, and it is not a raw `.rhp` dropped on the viewport.

The script also writes `plugin/bin/Debug/rhinomcp.macrhi` if you would rather
drag an installer onto the Rhino 7 **Dock icon**.

## 2. Load it in Rhino 7

1. **Quit Rhino 7** (Cmd+Q), then open it again. Mac plugins are picked up at
   launch from `MacPlugIns`.
2. Optional fallback: drag `plugin/bin/Debug/rhinomcp.macrhi` onto the Rhino 7
   icon in the Dock, click OK, then quit and restart.

Do **not** drag the raw `rhinomcp.rhp` onto the viewport. That Open / Import
dialog means Rhino thought it was a model file. The plugin never loaded, so
`mcpstart` stays unknown.

## 3. Start the TCP bridge

In the Rhino command line:

```
mcpstart
```

You should see `RhinoMCP server started on 127.0.0.1:1999`. `mcpstop` ends it.
Run `mcpstart` once per Rhino session.

If `mcpstart` is still unknown after a restart, open **Rhinoceros → Settings →
Plug-ins** and look for `rhinomcp`, and read the command history for a load
error.

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
