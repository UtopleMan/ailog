using AiLog.Host;

const int InvalidConfigurationExitCode = 2;

if (TryBuild(args) is not { } app)
{
    return InvalidConfigurationExitCode;
}

app.Run();
return 0;

static WebApplication? TryBuild(string[] args)
{
    try
    {
        return AiLogApp.Build(args);
    }
    catch (ConfigurationException ex)
    {
        Console.Error.WriteLine($"ailog: {ex.Message}");
        return null;
    }
}
