# OpenAI Codex subscription provider

## Target
Add a separate `openai-codex` provider with ChatGPT device-code sign-in and the Codex Responses protocol.

## Dependents
- `AgentProviders`: setup validation, model discovery and provider construction; consumed by `AgentService`, `ListedRates`, and `ModelProfileVm`.
- `AgentProviderDefinition`: provider choice labels and credential controls.
- `ProviderConfiguration`: account identity must clone and round-trip without altering legacy API keys.
- `ModelProfileVm`, `AgentSettingsViewModel`, `SettingsWindow.axaml`: sign-in/out and shared provider setup, autosaved to runtime roles.
- `SecretStore`, `AppConfig`: reuse machine-local secret protection and profile paths; OAuth tokens stay outside agent settings and the shared database.
- `ModelRoles`: existing configuration signature includes provider configuration, so account identity changes rebuild seats without changing fallback behavior.
- `ListedRates`, `PublishedRates`: retain separate Codex usage identity; do not apply OpenAI API token prices to a subscription.

## Affected Stories
No existing release plan or epic capsules.

## Test Coverage
- Baseline: `tools/AgentProviderCheck` passes.
- Add offline checks for device-code login, cancellation, refresh rotation, sign-out, persistence, provider selection, model discovery, Responses streaming, tool continuation, usage and failure handling.
- Run existing provider and streaming checks, localization check, Linux and Windows builds.
- Gap: real ChatGPT login and account entitlement require human approval and cannot be proven by mocks.

## Risk: Medium
Authentication is new, but existing protocols, API keys, speech, and saved model identities remain unchanged. Pin authentication and inference destinations to OpenAI; never send subscription tokens to custom endpoints or include response bodies in authentication errors.

## Recommended action
Proceed with a dedicated account helper and Responses adapter; use existing provider registry and Settings controls rather than a separate setup screen. No dependencies or database schema changes.
