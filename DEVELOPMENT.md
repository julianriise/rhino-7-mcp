# DEVELOPMENT — fork notes (Rhino 7 line)

This is a personal fork of **jingcheng-chen/rhinomcp**, retargeted to **Rhino 7 /
.NET Framework 4.8**. It is the **Rhino 7** line of a two-repo setup:

| Line | Repo | Base | Target |
|------|------|------|--------|
| **Rhino 7** (this repo) | `ktkt40208/rhinomcp-r7` | jingcheng-chen/rhinomcp | net48 / Rhino 7 |
| **Rhino 8** | `ktkt40208/RhinoMCP` | mcneel/RhinoMCP | net8.0 / Rhino 8+ |

Why this fork exists: the official mcneel/RhinoMCP (the Rhino 8 line) is net8.0 and cannot run
on Rhino 7. jingcheng-chen/rhinomcp has a lighter architecture — a separate Python MCP server
talking over a TCP socket to a thin C# plugin — so only the **C# plugin** needs retargeting to
net48; the Python server is unchanged and version-agnostic.

## Where the work is

The retarget is on **`main`** (this fork's working line; `main` tracks our work, not a pristine
mirror of upstream — see "Syncing upstream" below). Changes vs the upstream fork point:

- `plugin/rhinomcp.csproj` — net8.0 → **net48**; RhinoCommon & Grasshopper 8.17 → **7.34**
  (7.33.23306.15001 is not published on NuGet; 7.34 is the oldest stable 7.x after that);
  dropped the net8-only `System.Drawing.Common` + `Microsoft.WindowsDesktop.App.Ref` NuGets in
  favour of framework references; added `Microsoft.NETFramework.ReferenceAssemblies` (lets net48
  build on a machine without a full .NET Framework dev pack, e.g. macOS); `LangVersion` 11.
- `plugin/Compat/IsExternalInit.cs` — polyfill so `record`/`init` compile on net48.
- `plugin/Compat/StringCompat.cs` — polyfill `string.Contains(string|char, StringComparison)`,
  absent on net48 (global-namespace extension; fixes 15 call sites in the Grasshopper code).
- `plugin/Serializers/Serializer.cs` — replaced the Rhino-8-only `Curve.ControlPolygon()` and
  `PolylineCurve.ToArray()` with R7-compatible NURBS/sampling and `Point(i)` loops.

Builds clean to `plugin/bin/Debug/net48/rhinomcp.rhp`. Live smoke on Rhino 7.38
Mac (2026-09-15) passed. See [KNOWN_ISSUES.md](KNOWN_ISSUES.md).

## Build (macOS or Windows)

Any recent .NET SDK works (the `Microsoft.NETFramework.ReferenceAssemblies` package supplies the
net48 reference assemblies, so no Visual Studio / .NET Framework dev pack is required):

```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet build plugin/rhinomcp.csproj
# output: plugin/bin/Debug/net48/rhinomcp.rhp

# install as a MacPlugIns *.rhp package (macOS) in one step:
./plugin/install.sh
# or:
dotnet build plugin/rhinomcp.csproj -p:CopyToRhinoPluginDir=true
```

A user-local .NET 8 SDK at `~/.dotnet` is enough. The Homebrew `dotnet-sdk` cask needs sudo;
the Microsoft `dotnet-install.sh` script does not. See [INSTALL.md](INSTALL.md).

(If your only SDK is an old Homebrew `dotnet@8`, that's fine here — unlike the Rhino 8 repo, this
build has no Roslyn source generator, so the SDK version doesn't matter.)

## Run

1. `./plugin/install.sh` writes a Mac plugin package under
   `~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/rhinomcp.rhp/`.
   Quit and reopen Rhino 7, then run `mcpstart`. The plugin opens a TCP server
   on `127.0.0.1:1999`. Do not drag the raw `.rhp` onto the viewport.
2. Point Grok Build at the **local** `server/` from this checkout (`.grok/config.toml`), not
   `uvx rhinomcp` from PyPI. Dangerous tools are off for the MVP.
3. With `mcpstart` running, `python3 scripts/mvp_smoke.py` hits the plugin without Grok.

## Syncing upstream

**Pull-only** fork: `main` carries the R7 retarget; we don't PR back to jingcheng. The `upstream`
remote points at jingcheng-chen/rhinomcp; the tag `upstream-fork-point` marks the fork commit.

```bash
git fetch upstream
git merge upstream/main             # pull upstream's new tools/fixes into our R7 main
git diff upstream-fork-point..main  # the R7 retarget delta
```

Expect recurring conflicts in `plugin/rhinomcp.csproj` when merging (upstream is net8.0, we are
net48) — keep our net48/RhinoCommon-7 target and take upstream's other changes. New upstream tools
generally just work once the project still targets net48.

## Already handled upstream (present in `main`)

Between the survey and this fork, jingcheng merged blazingphoenix7's fixes, so the fork point
already includes them — no action needed:
- **TCP message framing (issue #31 / PR #32):** 4-byte length-prefix framing both directions, with
  backward-compat sniffing for un-framed clients (`server/src/rhinomcp/server.py`).
- **PR #28** capture_viewport view-restore, **#29** jsonschema `referencing` (RefResolver removed),
  **#30** `validate_response` wired behind `RHINO_MCP_VALIDATE`.
- Plus cherry-picked `fix/delete-object-null-name` (delete_object name for unnamed objects).

Validated without Rhino: net48 plugin builds clean; `pytest` (server) = 166 passed; contract
schema tests = 12 passed.

## Still to verify / do on the R7 line

- **Runtime on real Rhino 7:** plugin loads, `mcpstart`, MVP smoke OK (2026-09-15). Still open:
  GH1 tools actually driving Grasshopper 7, and `execute_rhinocommon_csharp_code` (Roslyn) on
  .NET Framework 4.8 / Mono.
- **Optional features available from `blazingphoenix7`** (not yet pulled — opinionated additions):
  `feat/perception-stage1-change-delta`, `stage2-health` (geometry-health in mutate responses),
  `stage3-spatial` (`measure_objects` clash/gap), `stage4-capabilities` (`describe_capabilities`).

## Note on the tool surface

Unlike the Rhino 8 repo (where geometry tools were hand-ported onto mcneel), jingcheng's plugin
**already includes** the full typed tool surface (create/modify/analyze/undo/redo, layer &
attribute CRUD, and ~27 Grasshopper tools incl. `gh_build_graph`/`gh_mutate_graph`). After this
retarget they are available on Rhino 7 as-is — no re-porting needed.
