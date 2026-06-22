# ADR-011: CI Quality Gates — Qodana + CodeRabbit

**Date**: 2026-06-22
**Status**: Active
**Affects**: `.github/workflows/ci.yml`, `qodana.yaml`, `.coderabbit.yaml`

---

## Context

The repository had a single GitHub Actions workflow (`release.yml`) that only triggered on git tags to build and draft a release. There was no CI on pull requests — no automated tests, no static analysis, no code review tooling. Issues like the `TolerantEpisodeDictionaryConverter` catch-block reader-state bug (PR #37) were only caught by manual review.

## Decision

Add two quality gates that run automatically on every pull request and every push to `main`:

1. **Qodana** (static analysis) — `JetBrains/qodana-action@v2026.1` using the `qodana-cdnet` linter with the `qodana.recommended` profile. Analyses the plugin project (`Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj`) and posts results to qodana.cloud. Uses the `COMMUNITY` plan (free).

2. **CodeRabbit** (AI code review) — installed as a GitHub App. Reviews every PR with full codebase context, configured via `.coderabbit.yaml` with project-specific instructions covering the key architectural invariants (Plugin.Instance safety, Dispatcharr probing rules, tolerant converters, ADR requirement, catch-block reader state after JsonException).

The existing CI workflow also runs the xUnit test suite (`Emby.Xtream.Plugin.Tests`) on every PR and push to `main`, which was previously only run locally.

## Alternatives Considered

- **SonarCloud** — mature, good C# support, but requires a third-party account and has more noise to tune. Passed over in favour of Qodana's tighter JetBrains/.NET integration.
- **CodeQL** — free, built into GitHub, security-focused. Would have been used if Qodana was unavailable (the JetBrains trial had expired on the account; the Community plan was used instead).
- **Greptile** — strong full-codebase context, but $30/seat with no open-source free tier at this time.
- **GitHub Copilot Code Review** — highest precision but requires a Copilot subscription.

## Consequences

- Every PR now gets: test run + Qodana static analysis + CodeRabbit AI review before merge.
- Qodana results are visible on qodana.cloud and as GitHub check annotations.
- CodeRabbit is configured with the architectural rules from `CLAUDE.md` so it enforces them independently of manual review.
- The `workflow_dispatch` trigger allows manual CI runs without opening a PR.
- `InspectSDK/` and `tools/` are excluded from Qodana analysis (dev scripts, not plugin code).
