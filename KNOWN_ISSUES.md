# Known issues (Rhino 7 macOS)

Runtime notes for this fork. Headless build/tests are green. Anything that
needs a live Rhino 7 session is listed as unverified until `mcpstart` + smoke
has actually run.

## Build

- **RhinoCommon 7.33 is not on NuGet.** The original pin
  `7.33.23306.15001` does not exist as a stable package (7.33 is RC-only).
  NuGet was already resolving `7.34.23267.11001`. The csproj now pins 7.34
  explicitly. Still net48 / Rhino 7, not the R8 `8.x` line.
- This Mac's Rhino is **7.38.24338.17002** (x86_64, Rosetta). 7.34 refs load
  in 7.38. If a 7.38-only API ever sneaks in, bump the package pin, not the
  target framework.

## Runtime, not yet proven on this machine

- Plugin load in Rhino 7 Mac, `mcpstart` / `mcpstop`, TCP 1999.
- MVP tools: `get_document_summary`, `create_layer`, `create_object`,
  `get_object_info`, `analyze_objects`, `capture_viewport`, `undo`.
- `capture_viewport` depends on Rhino's bundled libgdiplus for PNG encoding.
  The C# side already returns a clear error if that fails.
- Grasshopper 1 tools are compiled against GH 7.34 and have not been driven
  on this install. Out of scope for the MVP smoke.
- `execute_rhinocommon_csharp_code` uses Roslyn `Microsoft.CodeAnalysis.CSharp.Scripting`
  4.8 on .NET Framework 4.8 / Rhino's Mono. Disabled for the MVP
  (`RHINO_MCP_ENABLE_CSHARP=0`). If the plugin ever fails to load, Roslyn
  is the first assembly to suspect.

## Do not do

- Do not install Package Manager **`rhinomcp`**. That yak is Rhino 8.
- Do not point Grok at `uvx rhinomcp` from PyPI while developing this fork.
  Use `server/` from this checkout so the Python side matches the plugin.
- Do not run two MCP clients against port 1999 at once.

## Grok Build

- Viewport captures are large. Project config sets `[mcp] max_output_bytes = 2000000`
  so the PNG is not truncated at the default 20 KB cap.
- `create_object` BOX is authored **centered on the origin**. The smoke script
  translates by half-size so a corner sits at `(0,0,0)`.

## Upstream

`upstream` is `https://github.com/jingcheng-chen/rhinomcp.git`. Merge
`upstream/main` periodically. Keep our net48 csproj and `plugin/Compat/`
shims. See `DEVELOPMENT.md`.
