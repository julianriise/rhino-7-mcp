# Agent instructions — rhino-7-mcp (coding agents)

**This file is standing project instructions.** Open it at the start of every coding session on this repo.

Product sibling: `/Users/jr/Documents/hobby/forsk` — full ship rules in that repo’s [`GROK_BUILD.md`](https://github.com/julianriise/forsk/blob/main/GROK_BUILD.md) and [`AGENTS.md`](https://github.com/julianriise/forsk/blob/main/AGENTS.md).

---

## Mandatory before any code change

1. Work only on **`main`** in **`julianriise/rhino-7-mcp`** (and `julianriise/forsk` when touching product docs/prompts).
2. **Never** create feature branches, git worktrees, or pull requests.
3. Commit on `main` as you go; when the headless gates are green (`contracts` + `pytest` + `dotnet build`), `git push origin main`. A change that needs a live Rhino check waits for Julian's green smoke (see below).
4. Never push or open anything against `jingcheng-chen/rhinomcp` or any non-`julianriise` remote. This clone’s default remote must not become the PR target.
5. Report **main commit SHAs** only (no PR links).
6. Typed MCP tools only for geometry (no `execute_*` for modeling). Poteto mode on every brief (see “Poteto mode” below).
7. Delete any local/remote `feat/*` you created once `main` has the commits.

---

## Poteto mode

1. **One job.** One slice per session, no scope creep. List extra findings in the report instead of fixing them, unless they are clearly dead code.
2. **Evidence first.** Read the code, logs and smoke output, and reproduce the problem before changing anything. Name the root cause, not the symptom.
3. **Small verified steps.** Each change goes through the headless gates before the next one.
4. **Subtract before add.** Delete dead, duplicated or proven-useless paths before adding code. Keep one source of truth.
5. **Prove it.** A headless test fails before the fix and passes after. Live proof comes from Julian's smoke.
6. **Report honestly.** Say what is unverified, what failed and what is still open. Never claim green without output.
7. **Same failure twice: stop.** No third approach. Report the options and wait for Julian.

---

## Live Rhino testing: Julian runs it

Full rule: forsk [`AGENTS.md`](https://github.com/julianriise/forsk/blob/main/AGENTS.md) “Live Rhino testing: Julian runs it”.

- The coding agent never launches, drives, screenshots, or waits on Rhino, and never runs the live smokes. No macOS Accessibility GUI driving.
- Gates are headless only: `dotnet build` with 0 warnings, `SoftParam.Tests`, `pytest -q`, contracts, plus any check that does not need a running Rhino.
- A change that needs a live check is committed locally, not pushed. End the session with a smoke handoff of at most 5 lines: the forsk commands (`./scripts/smoke_office.sh`, `./scripts/smoke_garage.sh`, or a specific one), what should pass, and which 1000 px PNGs in `/tmp` to glance at. Julian runs them and pastes the output. Push `main` only after green; on red, fix from the output and the `/tmp` log.
- At most one office smoke per slice, and only when the office path changed.
- Smoke scripts stay one command, no prompts, stdout at most 25 lines, exit 0/1, and list the image and 3dm paths they wrote. They may quit and reopen Rhino and create blank fixtures themselves.
- Changes that need no live check (docs, pure Python/contracts, tests) push after green headless gates.

---

## Token budget

- Live smokes run through forsk `scripts/smoke_office.sh` and `scripts/smoke_garage.sh`, and Julian runs them (see above). The coding agent never drives smoke steps itself.
- While iterating, run only the affected tests with quiet output (`pytest -q`, `dotnet test --verbosity quiet`, print failures only). Full headless gates run once before the push.
- The coding agent does not open preview PNGs. The smoke handoff names the 1000 px copies in `/tmp` for Julian to glance at. Commit the full-res PNGs after his green run.
- Do not read forsk `docs/archive/` or `docs/smoke/HISTORY.md` unless the task names them.
- Same failure twice with the same symptom: stop and report. No third approach.
- One slice per session. Spikes get their own session.
- Final report 15 lines max: numbers, commit SHAs, link to forsk [`docs/SMOKE.md`](https://github.com/julianriise/forsk/blob/main/docs/SMOKE.md).
- Poteto-mode code quality stays unchanged. The budget is about reading, looping, and output, not code quality.
