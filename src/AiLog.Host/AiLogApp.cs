using System.Collections;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace AiLog.Host;

/// <summary>Composes the ailog web application: configuration, Kestrel, services, UI and the proxy catch-all.</summary>
public static class AiLogApp
{
    private const string EnvironmentPrefix = "AILOG_";
    private const string ConfigurationSection = "AiLog";
    private const int AnyFreePort = 0;

    private static readonly TimeSpan UpstreamConnectionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["--port"] = $"{ConfigurationSection}:Port",
        ["--logs"] = $"{ConfigurationSection}:LogsPath",
    };

    /// <summary>Builds the configured application from command-line arguments; not yet started.</summary>
    public static WebApplication Build(string[] args)
    {
        WebApplicationBuilder builder = CreateBuilder(args);
        AiLogOptions options = ReadOptions(builder.Configuration);
        builder.WebHost.ConfigureKestrel(kestrel => ConfigureKestrel(kestrel, options));
        RegisterServices(builder.Services, options);

        WebApplication app = builder.Build();
        MapEndpoints(app);

        // Start indexing existing logs right away rather than on the first UI request.
        app.Services.GetRequiredService<LogIndex>();

        app.Lifetime.ApplicationStarted.Register(() => PrintBanner(app, options));
        return app;
    }

    /// <summary>Addresses Kestrel actually bound to (resolves port 0).</summary>
    public static IReadOnlyCollection<string> GetListeningAddresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];

    private static WebApplicationBuilder CreateBuilder(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,

            // Fixed so the static web assets manifest (ailog.staticwebassets.*.json) is found when hosted by tests too.
            ApplicationName = typeof(AiLogApp).Assembly.GetName().Name,
        });

        // Precedence (last wins): appsettings.json < AILOG_* environment variables < command line.
        builder.Configuration.AddInMemoryCollection(ReadPrefixedEnvironment());
        builder.Configuration.AddCommandLine(args, SwitchMappings);

        // Serve AiLog.Web straight from its project folders when running from a build (no-op once published).
        builder.WebHost.UseStaticWebAssets();
        return builder;
    }

    /// <summary>Maps AILOG_PORT, AILOG_ROUTES__GEMINI etc. onto the AiLog configuration section.</summary>
    private static Dictionary<string, string?> ReadPrefixedEnvironment()
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string settingPath = key[EnvironmentPrefix.Length..].Replace("__", ":");
                values[$"{ConfigurationSection}:{settingPath}"] = entry.Value as string;
            }
        }

        return values;
    }

    private static AiLogOptions ReadOptions(IConfiguration configuration)
    {
        AiLogOptions options = configuration.GetSection(ConfigurationSection).Get<AiLogOptions>() ?? new AiLogOptions();
        options.ApplyDefaults();
        return options;
    }

    private static void ConfigureKestrel(KestrelServerOptions kestrel, AiLogOptions options)
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = null;
        if (options.Port == AnyFreePort)
        {
            kestrel.Listen(IPAddress.Loopback, AnyFreePort);
        }
        else
        {
            kestrel.ListenLocalhost(options.Port);
        }
    }

    private static void RegisterServices(IServiceCollection services, AiLogOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new HeaderRedactor(options.RedactHeaders));
        services.AddSingleton<ExchangeLogWriter>();
        services.AddSingleton(CreateUpstreamClient());
        services.AddSingleton<ProxyHandler>();
        services.AddSingleton<LogIndex>();
    }

    private static HttpClient CreateUpstreamClient()
    {
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = UpstreamConnectionLifetime,
            ConnectTimeout = UpstreamConnectTimeout,
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.UseUiPaths();
        app.UseRouting();
        app.MapUi();

        // Everything outside /_ailog is proxied. A catch-all endpoint has the lowest route precedence.
        var proxy = app.Services.GetRequiredService<ProxyHandler>();
        app.Map("/{**path}", proxy.HandleAsync);
    }

    private static void PrintBanner(WebApplication app, AiLogOptions options)
    {
        IReadOnlyCollection<string> addresses = GetListeningAddresses(app);
        foreach (string address in addresses)
        {
            Console.WriteLine($"ailog listening on {address}");
        }

        if (!UiEndpoints.HasAssets(app))
        {
            Console.WriteLine("  ui: not available (no static web assets found next to the executable)");
        }
        else if (addresses.Count > 0)
        {
            Console.WriteLine($"  ui: {addresses.First().TrimEnd('/')}/{UiEndpoints.Prefix}/");
        }

        foreach (var (name, upstream) in options.Routes)
        {
            Console.WriteLine($"  /{name} -> {upstream}");
        }

        Console.WriteLine($"  logs: {options.LogsPath}");

        if (addresses.Count > 0)
        {
            PrintHarnessCommands(addresses.First(), options.Routes);
        }
    }

    private static void PrintHarnessCommands(string proxyAddress, IReadOnlyDictionary<string, string> routes)
    {
        foreach (string line in HarnessCommands.Describe(proxyAddress, routes))
        {
            Console.WriteLine($"  {line}");
        }
    }
}
