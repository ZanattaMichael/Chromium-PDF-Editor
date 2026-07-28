# Claude Code Agent Plan — Working the Issue Backlog

This describes how to use Claude Code sessions/agents to execute the tiers defined in
`ACTION_PLAN.md`. It covers sequencing, session scoping, branch/PR strategy, and
review checkpoints so multiple issues can be worked through safely and in parallel
where dependencies allow.

## Principles

- **One issue (or tightly-coupled pair) per session/branch.** Keep diffs reviewable
  and bisectable. Don't let a session wander into unrelated tiers.
- **Respect the dependency order in `ACTION_PLAN.md`.** Don't start Tier 3+ work
  until the Tier 0 stabilization bugs and Tier 2 safety net (tests, export
  validation) are merged — later tiers assume those exist.
- **Draft PR per issue, referencing the issue number.** Branch name convention:
  `claude/issue-<n>-<short-slug>` (e.g. `claude/issue-18-js-execution`).
- **A session should end with either a pushed fix + draft PR, or a written note on
  why the issue is blocked** (e.g. needs a product decision) — never silently drop it.

## Sequencing (mirrors ACTION_PLAN.md tiers)

### Phase 1 — Stabilize (serial, high care)
Run one session at a time; each of these touches core pipelines and later phases
depend on them being correct.
1. #18 JS execution engine → #22 form JS notification (same session, since #22 is
   trivial once #18 lands)
2. #20 JPEG/OCR image corruption → #21 OCR zoom (same subsystem, one session)
3. #24 links menu stale state (independent, can run in parallel with 1–2)
4. #23 highlight click-and-drag (independent, can run in parallel)

### Phase 2 — Safety net (partly parallel; #52 first)
5. **#52 export pipeline validation — do this first.** It yields the structural
   validator that #53 and #54 both want as their assertion oracle; running it last
   means the other two invent their own. *(PR #75, open.)*
6. #54 fuzz/regression testing — independent of #52, can run concurrently with it.
   *(PR #76, open.)*
7. #53 golden-file regression suite — after #52, so it asserts with the validator.
   Note the corpus must be **generated in-repo**: no network access, and real-world
   PDFs carry licensing questions. Build on `CorruptPdfs.cs` (#52) and
   `Fuzz/RawPdf.cs` (#54).
8. #19 async link parser + loading indicator
9. #56 SonarCube cleanup — split into 2–3 sub-PRs by rule category (bugs, code
   smells, security hotspots); run as background/low-priority sessions since it's
   pure debt paydown, not urgent.

Do not start Phase 3 until #52–#54 are merged — they're the regression net every
later phase should run against.

### Phase 3 — Text & font fidelity (serial within phase; shared subsystem)
10. #29 → #28 → #34 → #35 → #32 → #33 → #31
    Run these as one continuous thread of sessions (same branch lineage or stacked
    PRs) since each builds on the semantic text model introduced by #32.

### Phase 4 — Rendering & geometry (pair up related issues per session)
11. #36 (preview/export parity) — do first, establishes verification harness
12. #38 + #40 (clipping/crop-box + hit-testing) — one session, shared transform math
13. #37 (transparency/blend modes)
14. #39 (nested Form XObjects)
15. #45 (accessibility/structure preservation) — last, depends on 11–14 being stable

### Phase 5 — Forms (serial)
16. #42 → #43 → #44 → #17

### Phase 6 — Redaction & undo (serial, high care — security-sensitive)
17. #49 (recovery-resistance verification) before #48 (audit report) — don't report
    "redacted" until recovery-resistance is proven
18. #51 (undo/redo atomicity)

### Phase 7 — Diff tooling
19. #46 → #47

### Phase 8 — Performance
20. #50 (cancellation + progressive processing)

### Phase 9 — New features (parallelizable, low coupling)
21. #25, #26 (+ #55 HEIC alongside #26), #27, #30, #41 — any order, can run several
    sessions concurrently since they don't share subsystems with each other.

## Running it with Claude Code

For each issue:
1. Start a session (or spawn a subagent — see `issue-resolver` below) with a prompt
   that includes: the issue number/title/body, the relevant ACTION_PLAN.md tier
   context, and the target branch name.
2. The session should: reproduce the bug/confirm the gap, implement the fix, add
   tests **and confirm they fail without it**, run the full suite, commit and push.
3. **Opening the PR is the calling session's job.** A subagent has no GitHub API
   access — `gh` is absent and `/repos/...` returns 403 — so it can push a branch but
   cannot file the PR. Have it write the PR body to a file and report the path; three
   agent runs in one session ended with finished work and no PR because of this.
4. Before opening the PR, **rebase the branch onto current `main`** and re-run the
   suites. Agent branches go stale quickly when several land in a session, and a
   diff against a moved `main` shows other people's merges as deletions.
5. **Verify the agent's central claim yourself** rather than relaying it. For a fix,
   that means reverting it and watching the new test fail; for a detector, stubbing it
   out and watching the suite go red. Both Phase 2 agents' headline claims held up
   under that check — but the check is what makes the claim worth repeating.
6. Human review gate: PRs from Phase 1 and Phase 6 (security-sensitive) get a manual
   review before merge; later phases can use lighter review if CI is green and the
   regression suite (Phase 2) passes.

### Parallelism guidance
- Within a phase marked "independent" or "parallelizable" above, multiple sessions
  can run concurrently.
- Across phases, respect the dependency arrows — e.g. don't parallelize Phase 3 work
  with Phase 1, since Phase 3's font-size fix (#29) assumes JS/OCR pipelines aren't
  simultaneously changing underneath it.

### Subagent
A repo-specific subagent (`.claude/agents/issue-resolver.md`) is provided to
standardize how any session picks up a single issue from this plan: it reads the
issue, checks `ACTION_PLAN.md` for context/dependencies, implements the fix on a
correctly-named branch, and opens a draft PR. Invoke it per-issue rather than
re-deriving this process from scratch each time.
