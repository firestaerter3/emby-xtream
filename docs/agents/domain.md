# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Layout

This repo is **single-context** — one plugin, one domain. No `CONTEXT-MAP.md`.

```
/
├── CLAUDE.md                 ← project conventions, deployment, architecture notes (read this first)
├── docs/
│   ├── ARCHITECTURE.md       ← high-level architecture overview
│   ├── architecture/         ← supporting architecture notes
│   ├── decisions/            ← ADRs (NOT docs/adr/ — see note below)
│   │   ├── 001-bypass-dispatcharr-proxy.md
│   │   ├── 002-localstorage-cache-busting.md
│   │   └── ...
│   ├── REQUIREMENTS.md
│   ├── TESTCASES.md
│   └── plans/
└── Emby.Xtream.Plugin/       ← source
```

## Before exploring, read these

- **`CLAUDE.md`** at the repo root — primary source of domain language, architectural constraints, and deployment workflow. Always read first.
- **`docs/decisions/`** — read ADRs that touch the area you're about to work in. **Note: this repo uses `docs/decisions/` instead of the skill default `docs/adr/`.** Numbered sequentially zero-padded to 3 digits (`001`, `002`, ...). When a skill says "check the ADRs" or "consult `docs/adr/`", read from `docs/decisions/` here.
- **`docs/ARCHITECTURE.md`** — supplementary architecture overview when you need a broader picture than CLAUDE.md provides.

There is no `CONTEXT.md` at the repo root yet. If a skill expects one, treat `CLAUDE.md` as the de facto context document and proceed silently — don't suggest creating `CONTEXT.md` upfront. The `/grill-with-docs` skill creates it lazily when domain terms or decisions actually need to be resolved.

## Use the established vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as it appears in `CLAUDE.md` and the ADRs. Examples of established terms in this codebase:

- *Dispatcharr proxy URL*, not "IPTV proxy URL"
- *Xtream tuner* / *M3U tuner*, not "tuner type 1 / type 2"
- *Live TV HLS flow* (specific sequence of `GetChannelStreamMediaSources` → ffmpeg)
- *Tuner host*, *listing provider*, *direct stream*, *probing* — Emby SDK terms used verbatim

Don't drift to synonyms.

## Creating a new ADR

Sequential, zero-padded 3 digits. Format documented in `CLAUDE.md` under "Architecture Decision Records (ADRs)". Template: `docs/decisions/001-bypass-dispatcharr-proxy.md`.

Create one when:
- Choosing between multiple viable approaches (especially after trying alternatives that failed)
- Making a change driven by a non-obvious root cause
- Reversing or replacing a previous approach

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-003 (stream metadata via Streamflow) — but worth reopening because…_
