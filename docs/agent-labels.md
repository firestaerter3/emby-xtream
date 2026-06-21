# Agent Labels

These labels support automated issue triage, implementation routing, and maintainer approval.

| Label | Meaning |
| --- | --- |
| `needs-triage` | New or updated issue still needs classification. |
| `needs-info` | Issue lacks reproduction details, logs, version data, or a clear use case. |
| `bug` | Reported behavior appears to be a defect in Xtream Tuner. |
| `feature` | Request proposes new behavior or a workflow enhancement. |
| `agent-ready` | Automation may prepare a branch and PR. |
| `agent-rejected` | Automation should not implement this without maintainer intervention. |
| `high-value` | Fix or feature likely helps many users, provider variants, or support cases. |
| `low-value` | Request is narrow, costly, or weakly aligned with project scope. |
| `provider-issue` | Root cause is likely upstream provider data or behavior. |
| `configuration` | Root cause is likely setup, local Emby behavior, or documented configuration. |
| `regression` | Behavior previously worked and appears broken by a recent change. |

## Recommended setup

Create labels with GitHub CLI:

```bash
gh label create needs-triage --color FFB000 --description "Needs automated or maintainer triage"
gh label create needs-info --color D876E3 --description "Waiting for reporter details"
gh label create bug --color D73A4A --description "Defect in Xtream Tuner"
gh label create feature --color 1D76DB --description "Feature request or enhancement"
gh label create agent-ready --color 0E8A16 --description "Automation may prepare a PR"
gh label create agent-rejected --color 5319E7 --description "Automation should not implement"
gh label create high-value --color 0E8A16 --description "Likely high value for users"
gh label create low-value --color C5DEF5 --description "Low value, narrow, or high maintenance cost"
gh label create provider-issue --color FBCA04 --description "Likely caused by provider data or behavior"
gh label create configuration --color BFDADC --description "Likely setup or configuration issue"
gh label create regression --color E99695 --description "Previously worked and now broken"
```
