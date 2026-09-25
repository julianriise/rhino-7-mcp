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

The agent owns the Rhino lifecycle for live smokes. Never ask Julian to open Rhino, reopen a plan, or run `mcpstart`. macOS Accessibility is available, so do it yourself.

a. Quit Rhino without saving: `pkill -x Rhinoceros` (process name confirmed with `pgrep -lf Rhino`; `CFBundleExecutable` is `Rhinoceros`). Never save the smoke .3dm files.
b. Check that the plugin at `~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/rhinomcp.rhp/rhinomcp.rhp` is the fresh build (mtime newer than the build output at `plugin/bin/Debug/net48/rhinomcp.rhp`). If the build is newer, copy it only while Rhino is quit. Equal mtimes means the installed file is that build. The `7.0/Plug-ins/rhinomcp` copy is stale.
c. Open the fixture from disk: `open -a "Rhino 7" "<absolute path>"`. App bundle confirmed with `ls /Applications | grep -i rhino`: `Rhino 7.app` (`open -a` and AppleScript use `Rhino 7`).
   - Office fixture: `/Users/jr/Downloads/forsk-rhino/office_2D.3dm`. Leave it unsaved. `office_3D.3dm` in that folder is already baked clay; do not open it for this smoke.
   - Garage fixture: there is no saved garage `.3dm`. Copy `/Applications/Rhino 7.app/Contents/Frameworks/RhCore.framework/Versions/A/Resources/en.lproj/Template Files/Large Objects - Millimeters.3dm` to `/tmp/forsk-garage-blank.3dm` and open the copy. Do not open the template in place. `scripts/garage_opening_smoke.py` draws the 6×4 m band on that blank sheet and refuses a document with more than 40 objects. Do not run it on `office_2D.3dm`.
d. Wait until the document window is up (poll with osascript/System Events, up to about 60 s), then bring Rhino to the front and type into the command line: osascript: tell application "System Events" to keystroke "mcpstart" and then key code 36 (Return).
e. Poll `nc -z localhost 1999` for up to 30 s. If it's still closed, retry step d once. If it's still closed after that, stop and report what's on screen (take a screenshot with `screencapture`).
f. Run the smoke script. Between fixtures, repeat steps a to e (full quit, fresh open, no save). Office: `RHINO_MCP_TIMEOUT=900 python3 scripts/opening_edit_smoke.py`. Garage: `RHINO_MCP_TIMEOUT=300 python3 scripts/garage_opening_smoke.py`.
g. If a native dialog blocks you (save prompt, plugin load warning), dismiss it with "Don't Save" or "Cancel" through System Events. Never click Save. On a file that already exists on disk the sheet can offer Save, Revert Changes, and Cancel. Click Revert Changes. That discards the unsaved edits.
