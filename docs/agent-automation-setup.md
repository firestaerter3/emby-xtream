# Agent Automation Setup

This repository contains the policy and GitHub Actions wiring for automated issue triage, agent dispatch, CI validation, and PR-ready notifications.

## Existing build instructions

The automation intentionally follows the repository build path documented in `CONTRIBUTING.md` and `Emby.Xtream.Plugin/build.sh`:

```bash
dotnet test Emby.Xtream.Plugin.Tests/ -v minimal
cd Emby.Xtream.Plugin
bash build.sh
```

`build.sh` derives the plugin version from git tags, runs the test project, publishes the plugin, and writes the DLL to `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll`.

## Required repository secrets

| Secret | Purpose |
| --- | --- |
| `AGENT_TRIAGE_WEBHOOK_URL` | Receives issue triage events. The external agent should comment on the issue and apply labels. |
| `AGENT_TRIAGE_WEBHOOK_SECRET` | Optional HMAC secret for triage webhook signatures. |
| `AGENT_WEBHOOK_URL` | Receives `agent-ready` implementation events. The external agent should create a branch and PR. |
| `AGENT_WEBHOOK_SECRET` | Optional HMAC secret for implementation webhook signatures. |
| `PR_NOTIFY_WEBHOOK_URL` | Receives PR opened/updated and CI-passed notifications. |
| `PR_NOTIFY_WEBHOOK_SECRET` | Optional HMAC secret for PR notification webhook signatures. |

## Workflow overview

1. `issue-triage.yml` runs when an issue is opened, edited, reopened, manually dispatched, or when someone comments `/agent triage`.
2. The triage webhook receives issue text, recent comments, and policy-file names. It should use `docs/agent-triage-policy.md` to classify the issue and apply labels.
3. When `agent-ready` is applied, `agent-dispatch.yml` sends a bounded implementation request to the coding agent.
4. The coding agent should create `agent/issue-<number>-<slug>`, write tests, implement the change, run the build instructions, and open a PR.
5. `ci.yml` validates pushes and pull requests.
6. `pr-ready-notify.yml` sends a notification when a PR becomes ready and when CI passes.

## Maintainer control

Automation prepares work, but does not merge or release. Releases still require explicit maintainer approval, matching `AGENTS.md`.
