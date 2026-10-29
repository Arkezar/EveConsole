# EVE Console

Domain language for EVE Console. These terms describe the agent's access to language models.

## Language

**Agent provider**:
A source of language models available to the agent, such as Claude, OpenAI, or a local model server. A provider is distinct from an individual model.

**Provider configuration**:
The credentials and provider-wide choices shared by model profiles for an agent provider. There is one configuration per provider; Local model profiles can use different servers.

**Model profile**:
A saved choice of a provider and one of its models, with a stable identity and an optional name. Several profiles can use the same provider.

**Model role**:
A purpose assigned to a model profile, such as conversation or analysis, with an optional fallback profile.
