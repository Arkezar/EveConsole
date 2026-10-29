# Codex provider security review

## Scope

Self-review of the new account helper, Responses adapter, provider registry, and Settings controls.
No independent reviewer or external security scanner was used.

## Findings

No confirmed security defect remains in this change.

## Checked boundaries

- Authentication uses fixed HTTPS URLs at `auth.openai.com`.
- Subscription inference uses a fixed HTTPS destination at `chatgpt.com`. Configured endpoint overrides are ignored.
- Both HTTP clients reject redirects.
- Authentication and HTTP error messages omit response bodies.
- Tokens stay outside agent settings and the shared database.
- Secret storage reuses `SecretStore`. The fallback file has owner-only permissions on Unix.
- Refresh operations share a semaphore. Tests cover concurrent rotation and HTTP 401 recovery.
- JWT claims select routing and expiry. OpenAI validates the bearer token.
- Cancellation and incomplete streams cannot execute returned tools.
- Sign-out removes the local token file and clears the in-memory cache.

## Disclosed limits

If system secret protection is unavailable, the token file contains plaintext credentials.
The Settings screen states this behavior. Sign-out removes local access but does not revoke the session at OpenAI.
Live model discovery used the existing protected ChatGPT login without printing credentials or account identifiers.
Platform storage writes and live inference remain untested.
