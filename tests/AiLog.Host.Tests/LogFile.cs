using AiLog.Contracts;

namespace AiLog.Host.Tests;

/// <summary>A log file found in the logs folder, with its parsed content.</summary>
public sealed record LogFile(string FileName, ExchangeLog Log);
