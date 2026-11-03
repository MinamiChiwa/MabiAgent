namespace MabiAgent.Core;

/// <summary>Only a proven preflight rejection before native input may use this exception.</summary>
public class InputNotDispatchedException(string reason) : Exception(reason);
