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
