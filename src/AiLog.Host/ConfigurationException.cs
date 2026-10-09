namespace AiLog.Host;

/// <summary>Settings ailog cannot start with; the message is shown to the user as is.</summary>
public sealed class ConfigurationException(string message) : Exception(message);
