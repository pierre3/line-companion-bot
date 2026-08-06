# Review — Tutorial restructure + user-secrets enablement (2026-08-06)

3-role review gate (per `docs/REVIEW-WORKFLOW.md`) run before committing the tutorial rework and
the small source change that enables `dotnet user-secrets`.

## Scope reviewed

Uncommitted working-tree changes:

- **Source (2 files):**
  - `src/LineCompanionBot/Program.cs` — `BuildCompanionConfiguration` adds
    `AddUserSecrets(typeof(Program).Assembly, optional: true)` in the `Development` environment only,
    placed before `AddEnvironmentVariables()` (so env vars still override). Applies to both the web
    host and the `setup` verb via the shared helper.
  - `src/LineCompanionBot/LineCompanionBot.csproj` — added `<UserSecretsId>`.
- **Tooling / config:** `.gitignore` flipped from blanket `.vscode/` ignore to `.vscode/*` +
  allow-list of `launch.json`/`tasks.json`/`extensions.json`; new `.vscode/` run-debug config.
- **Docs:** monolithic `docs/manual/{en,ja}/tutorial.md` split into per-chapter files
  (`00`–`09` + `README.md` index), final-state rewrite folding the earlier trailing refactor
  sections into their chapters, VS Code (F5) + user-secrets oriented; JA reworded to a narrative
  "developer blog" register; `README.md` / `README_ja.md` / `CLAUDE.md` references updated.

## Verdicts

| Role | Verdict |
|---|---|
| code-reviewer | **PASS** (2 LOW, non-blocking) |
| security-reviewer | **PASS** (no findings) |
| test-arch-reviewer | **PASS** (2 non-blocking observations) |

No blocking findings. Human go/no-go: proceed to commit (push deferred — no remote configured).

## What was confirmed

- **user-secrets wiring correct & idiomatic:** Development-only gate, provider precedence keeps env
  vars winning, `typeof(Program).Assembly` resolves under top-level statements (build + 29 tests
  pass), `<UserSecretsId>` present so the attribute binds. The deliberate `AddCommandLine()`
  exclusion is preserved.
- **No secret material committed:** `.vscode/*` and docs contain only `ASPNETCORE_ENVIRONMENT=Development`
  and placeholder values; `secrets.json` resolves to `%APPDATA%\Microsoft\UserSecrets\<id>\` outside
  the repo. `.gitignore` allow-list verified via `git check-ignore` / `git add -n` to track only the
  three shared files (personal `.vscode/settings.json` stays ignored). `UserSecretsId` is an
  identifier, not a secret.
- **setup verb path:** defaults to `Production` when `ASPNETCORE_ENVIRONMENT` is truly unset, but
  `launchSettings.json`, the F5 `launch.json`, and the `setup-richmenu` task all set `Development`,
  so user-secrets load on every documented path.
- **Docs ↔ implementation 1:1:** chapter code matches the real source; the F5/user-secrets flow
  claims match `.vscode/` and `launchSettings.json`.
- **Test scope:** no new test warranted — the only new logic delegates to the framework's provider;
  a wiring test would test the framework or reach a non-public seam, contrary to this project's
  intentionally minimal test scope.

## Non-blocking findings — disposition

Applied before commit (small accuracy/consistency fixes):

1. `Program.cs` header comment now lists user secrets among the config sources (code LOW #1).
2. `Program.cs` Development-gate `if` braced to match the file's style (code LOW #2).
3. `docs/manual/{en,ja}/00-getting-started.md`: clarified that `dotnet user-secrets init` generates a
   GUID `UserSecretsId` and that the exact value is just a stable identifier (test-arch observation).

Not actioned (intentional): chapter 1's sample `Program.cs` using-block omits later-chapter
namespaces — expected for that chapter's scope, not a mismatch.
