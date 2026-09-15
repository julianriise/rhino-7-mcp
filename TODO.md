# TODO — Rhino 7 line (`ktkt40208/rhinomcp-r7`)

Continuation checklist for another session / PC. **Setup, build, and upstream-sync: see
[`DEVELOPMENT.md`](./DEVELOPMENT.md).** Companion repo: `ktkt40208/RhinoMCP` (the Rhino 8 line).
Strategy background lives in the "RhinoMCP fork" note in Notion (not in-repo).

_Status as of 2026-06-17. `main` is the working line._

## Done

- [x] Retarget plugin to **net48 / Rhino 7** (csproj → RhinoCommon & Grasshopper 7.34, because
      7.33.23306.15001 is unpublished on NuGet; framework refs for Drawing/WinForms;
      `Compat/IsExternalInit.cs` + `Compat/StringCompat.cs` shims; Serializer R8-only API swaps).
      Builds clean to `plugin/bin/Debug/net48/rhinomcp.rhp`.
- [x] Mac install path, Grok Build MCP config, live smoke script, KNOWN_ISSUES.md. Plugin staged
      to `~/Library/Application Support/McNeel/Rhinoceros/7.0/Plug-ins/rhinomcp/`. Still needs a
      first-time drag of the `.rhp` onto Rhino 7, then `mcpstart`.
- [x] Confirmed already-merged-upstream in the fork point (no action): TCP framing (#31/#32),
      PRs #28 (capture_viewport), #29 (jsonschema referencing), #30 (validate_response).
- [x] Cherry-picked `blazingphoenix7:fix/delete-object-null-name`.
- [x] Verified **without Rhino**: net48 build 0 errors; `pytest` (server) = 166 passed;
      contract schema tests = 12 passed.

## Verify loop (no Rhino needed — run after any change)

```bash
# C# plugin (net48)
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet build plugin/rhinomcp.csproj
# Python server (socket-mocked; no Rhino)
cd server && uv run --python 3.12 --extra dev pytest -q && cd ..
# JSON contracts
uv run --python 3.12 --with pytest --with jsonschema --with referencing pytest contracts/test_schemas.py -q
```

## Pending — needs a real Rhino 7 (cannot be done headless)

- [ ] Load `rhinomcp.rhp` in Rhino 7, run `mcpstart`, start the Python server, smoke-test a few tools.
- [ ] Confirm the Grasshopper (GH1) tools actually drive Grasshopper 7 (compiled vs 7.33, runtime unverified).
- [ ] Confirm `execute_rhinocommon_csharp_code` (Roslyn `Microsoft.CodeAnalysis.CSharp.Scripting`) runs on .NET Framework 4.8.

## Pending — optional features from `blazingphoenix7` (decide, then cherry-pick + verify)

Not pulled yet — opinionated additions. To take one: add the remote, cherry-pick the branch tip,
then run the verify loop above.

```bash
git remote add bphoenix https://github.com/blazingphoenix7/rhinomcp.git && git fetch bphoenix
git cherry-pick bphoenix/<branch>
```

- [ ] `feat/perception-stage3-spatial` — `measure_objects` (clash / bbox-gap). **Recommended (standalone).**
- [ ] `feat/perception-stage4-capabilities` — `describe_capabilities` (MCP self-description). **Recommended (standalone).**
- [ ] `feat/perception-stage2-health` — geometry-health block on mutate responses.
- [ ] `feat/perception-stage1-change-delta` — cap change-delta id lists for large operations.

## Notes for whoever continues

- Python server is **unchanged from upstream** and version-agnostic; only the C# plugin is R7-specific.
- Keep the project targeting **net48** when merging upstream (`git merge upstream/main` will conflict
  on `plugin/rhinomcp.csproj` — keep our net48 / RhinoCommon-7 target, take upstream's other changes).
- New upstream tools generally just need the project to still target net48; re-run the verify loop.
