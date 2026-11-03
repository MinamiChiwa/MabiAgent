namespace MabiAgent.Core;

// Names come from packet identity; the selection UI uses the account-local slot.
public sealed record CharacterSlotIdentity(string AccountId,string CharacterId,string Name);
