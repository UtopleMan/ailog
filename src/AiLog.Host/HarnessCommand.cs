namespace AiLog.Host;

/// <summary>A harness program and the environment that routes it through ailog, rendered for each shell.</summary>
internal sealed record HarnessCommand(string Program, IReadOnlyList<EnvironmentVariable> Environment)
{
    public string ForPosixShell() =>
        string.Join(' ', [..Environment.Select(variable => variable.ForPosixShell()), Program]);

    public string ForPowerShell() =>
        string.Join("; ", [..Environment.Select(variable => variable.ForPowerShell()), Program]);
}

/// <summary>An environment variable set in front of a harness command.</summary>
internal abstract record EnvironmentVariable(string Name)
{
    public abstract string ForPosixShell();

    public abstract string ForPowerShell();
}

/// <summary>A variable with a fixed value.</summary>
internal sealed record LiteralVariable(string Name, string Value) : EnvironmentVariable(Name)
{
    public override string ForPosixShell() =>
        Value.Any(char.IsWhiteSpace) ? $"{Name}='{Value}'" : $"{Name}={Value}";

    public override string ForPowerShell() => $"$env:{Name}='{Value}'";
}

/// <summary>A variable set to a command's output when the harness starts, so secrets never appear in the banner.</summary>
internal sealed record CommandOutputVariable(string Name, string Command) : EnvironmentVariable(Name)
{
    public override string ForPosixShell() => $"{Name}=$({Command})";

    public override string ForPowerShell() => $"$env:{Name}=$({Command})";
}
