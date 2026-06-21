## Summary

<!-- What changed and why? -->

## Linked issue

Fixes #

## Agent report

Classification:
Confidence:
Value:
Risk:

## Tests

- [ ] `dotnet test Emby.Xtream.Plugin.Tests/ -v minimal`
- [ ] `cd Emby.Xtream.Plugin && bash build.sh`

## Risk checklist

- [ ] No credentials, provider URLs, usernames, or passwords are included in logs or tests.
- [ ] Live TV Dispatcharr proxy probing behavior remains safe (`SupportsProbing = false`, `AnalyzeDurationMs = 0` for proxy URLs).
- [ ] No service constructor reads `Plugin.Instance.Configuration`.
- [ ] User-facing release note impact is clear if this will ship in a release.
