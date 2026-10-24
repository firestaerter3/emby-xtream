# 023 - A provider request always carries a User-Agent

## Context

`PluginConfiguration.HttpUserAgent` is an optional text field. It existed for providers that
reject requests without a specific User-Agent, and the plugin only ever added the header when
that field was non-empty:

```csharp
var ua = _instance?.Configuration?.HttpUserAgent;
if (!string.IsNullOrEmpty(ua))
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
```

`HttpClient` sends no User-Agent unless one is set, so the default configuration produced a
request with no User-Agent at all. Issue [#104](https://github.com/firestaerter3/emby-xtream/issues/104)
reported a 404 on `player_api.php` from a panel that serves the same URL to curl with HTTP 200 and
a valid `user_info` body, from inside the same Emby container.

## Problem

A headerless request is a legitimate HTTP/1.1 request, and curl, browsers, ffmpeg and other
clients send a User-Agent, so a panel author has no reason to notice that one client does not.
Panels and the CDNs in front of them are free to answer such a request with a 404 instead of
routing it, and nothing in the response distinguishes that from a genuinely missing endpoint.
The plugin surfaced it as `Response status code does not indicate success: 404 (Not Found)`,
which points at the URL rather than at the client.

The two call sites are `Plugin.CreateHttpClient` (`Plugin.cs:57-60`) and
`StrmSyncService.ApplyUserAgentToSharedClient` (`StrmSyncService.cs:114-117`).

## Alternatives considered

1. **Tell the user to fill the field in.** No code change, no release. Rejected: the reporter
   did not know the field existed, the failure message does not mention it, and it leaves the
   user with a workaround for a defect rather than a fix.
2. **Retry the request with a User-Agent on 404.** Rejected: it doubles the request count
   against a panel on every 404, including the legitimate ones, and keeps the ambiguous 404 as
   the primary outcome.
3. **Log a warning when the field is blank.** Rejected as the whole fix: it diagnoses without
   changing behaviour, and the user still sees the 404.

## Decision

`XtreamUserAgent.Apply(client, configuredUserAgent)` sets the header on every provider-facing
client, using the configured value when there is one and `Emby-Xtream-Plugin/<version>`
otherwise. It removes any existing `User-Agent` first, so clearing the field restores the
default instead of leaving the previous header on the shared sync client, which lives for the
process lifetime. A whitespace-only value counts as blank, since that is what the config page
leaves behind after a clear. Both existing call sites route through it, so `Plugin.CreateHttpClient`
and the shared sync client behave the same way.

The root cause is not confirmed. The missing header is the most plausible explanation for a
panel that answers curl with 200, and sending no User-Agent is never correct, but a reporter
retest is needed before it is stated as the cause.

## Consequences

- The config page no longer says "leave blank for most providers"; it names the default.
- A provider that requires a specific User-Agent still gets it. The configured value is sent
  unchanged and trimmed.
- The plugin now identifies itself to providers on every request. That was already true for the
  `UpdateChecker` path (`UpdateChecker.cs:78-79`) and the plugin update download
  (`XtreamTunerApi.cs:1207-1209`), which hardcode `Emby-Xtream-Plugin/1.0`; those sites are left
  alone since they talk to GitHub, not to the provider.
- `DispatcharrClient.CreateHttpClient` (`DispatcharrClient.cs:679-682`) still sends no
  User-Agent. It is a separate client with its own handler and is out of scope here, but it is
  the same gap.