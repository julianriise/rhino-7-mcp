# Agent instructions — rhino-7-mcp (Grok Build / coding agents)

**This file is standing project instructions.** Open it at the start of every coding session on this repo.

Product sibling: `/Users/jr/Documents/hobby/forsk` — full ship rules in that repo’s [`GROK_BUILD.md`](https://github.com/julianriise/forsk/blob/main/GROK_BUILD.md) and [`AGENTS.md`](https://github.com/julianriise/forsk/blob/main/AGENTS.md).

---

## Mandatory before any code change

1. Work only on **`main`** in **`julianriise/rhino-7-mcp`** (and `julianriise/forsk` when touching product docs/prompts).
2. **Never** create feature branches, git worktrees, or pull requests.
3. Commit on `main` as you go; when green (`contracts` + `pytest` + `dotnet build`), `git push origin main`.
4. Never push or open anything against `jingcheng-chen/rhinomcp` or any non-`julianriise` remote. This clone’s default remote must not become the PR target.
5. Report **main commit SHAs** only (no PR links).
6. Poteto-mode: one job per brief, typed MCP tools only for geometry (no `execute_*` for modeling).
7. Delete any local/remote `feat/*` you created once `main` has the commits.

---

## Live smoke: Rhino lifecycle

The agent owns the Rhino lifecycle for live smokes. macOS Accessibility is available. Create blank-file fixtures yourself. Do not ask Julian to open Rhino, open a file, create a blank file, or run `mcpstart`.

1. App is `/Applications/Rhino 7.app`. `open -a` and AppleScript use **`Rhino 7`**. The process name is **`Rhinoceros`**.
2. Quit without saving (`Cmd+Q` / `tell application "Rhino 7" to quit`). If a save dialog appears, click **Don't Save**. On a file that already exists on disk the sheet can instead offer Save, **Revert Changes**, and Cancel. Click **Revert Changes**. That discards the unsaved edits. Never save a smoke `.3dm`.
3. Office fixture, leave unsaved: `/Users/jr/Downloads/forsk-rhino/office_2D.3dm`. `office_3D.3dm` in that folder is already baked clay.
4. Millimetre template, and the Rhino default (`MRDefaultTemplateFilename`): `/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources/en.lproj/Template Files/Large Objects - Millimeters.3dm`. For a blank sheet, copy that template to a temp `.3dm` and open the copy. Do not open the template in place.
5. `scripts/garage_opening_smoke.py` refuses a document with more than 40 objects. Run it on that blank copy, not on `office_2D.3dm`.
6. Type `mcpstart` through System Events into the Rhino command line. Poll `nc -z localhost 1999` until the port accepts.
7. Rhino loads `~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/rhinomcp.rhp/rhinomcp.rhp`. Copy `plugin/bin/Debug/net48/rhinomcp.rhp` there only while Rhino is quit. The `7.0/Plug-ins/rhinomcp` copy is stale.
