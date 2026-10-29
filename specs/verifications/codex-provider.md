# Codex provider verification

## Result

Automated checks pass. Live discovery returns eight selectable models with the existing saved login. Desktop layout and paid inference remain unverified.

| Command | Result |
| --- | --- |
| `dotnet run --project tools/CodexProviderCheck` | 12 scenarios pass |
| `dotnet run --project tools/AgentProviderCheck` | 19 regression scenarios pass |
| `dotnet run --project tools/AgentStreamCheck` | Existing provider wire-format and UI-thread checks pass |
| `dotnet run --project tools/LocCheck` | No translation errors |
| `dotnet build -c Release` | Zero warnings and errors |
| `dotnet build -c Release -r linux-x64` | Zero warnings and errors |
| `dotnet build -c Release -r win-x64` | Zero warnings and errors |
| `git diff --check` | Pass |

The installed default `dotnet` has no SDK. These commands used `/tmp/eveconsole-dotnet/dotnet`, SDK 10.0.403.

## Codex coverage

The offline HTTP fixtures check device-code approval, pending polling, cancellation, private token-file permissions, and profile isolation.
They check concurrent refresh, refresh-token rotation, one retry after HTTP 401, and credential-safe errors.
They also check model discovery, subscription identity, streamed text, tool output, images, encrypted reasoning, usage, and tool budgets.
Settings checks cover sign-in, sign-out, both runtime roles, unchanged speech keys, and recovery from a copied profile without usable tokens.
A delayed HTTP fixture checks that Codex streaming does not resume on the caller's UI context.

## Initial terminal evidence

Before the model-discovery correction, one final shell run used `set -e` and ran all commands above. It exited with code 0:

```text
Codex scenarios: 11
Provider regressions: 19
Agent stream check: every provider ran its whole turn off the UI thread and put the right things on the wire.
Translations: no problems.
FINAL VALIDATION PASSED
```

## Model-discovery regression

The live catalog request reproduced the reported empty dropdown.
With `client_version=0.99.0`, OpenAI returned one hidden model and no selectable models.
Changing only the version to `0.165.0` returned eight selectable models.

The current version comes from [OpenAI Codex release 0.165.0](https://github.com/openai/codex/releases/tag/rust-v0.165.0).
The [upstream catalog client](https://github.com/openai/codex/blob/main/codex-rs/codex-api/src/endpoint/models.rs) sends this version as a query parameter.

A version-aware HTTP fixture now reproduces the server filter. The regression check failed before the correction and passed afterward.
The original live probe then passed through the corrected provider:

```text
DISCOVERY: 8 selectable models
PASS: signed-in discovery offers models
```

The temporary probe printed no credentials or account identifiers. It made no inference requests and was removed after verification.
Provider regressions, streaming checks, and all three Release builds passed afterward.
All four existing build folders contain the corrected catalog version.

## Limits

- No new ChatGPT login or paid inference request was made. Live discovery used the existing saved login.
- The Codex service protocol and account entitlements can change.
- English resource entries fall back to English in other interface languages.
- The LSP probe returned no server answer for nine changed C# files. Compiler builds passed instead.
- Offline test credentials use the private-file fallback. Live discovery read the existing protected login, but did not test platform storage writes.
