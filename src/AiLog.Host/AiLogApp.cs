using System.Collections;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AiLog.Host;

public static class AiLogApp
{
    private const string EnvironmentPrefix = "AILOG_";

    private static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["--port"] = "AiLog:Port",
        ["--logs"] = "AiLog:LogsPath",
    };

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
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

        var options = builder.Configuration.GetSection("AiLog").Get<AiLogOptions>() ?? new AiLogOptions();
        options.ApplyDefaults();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;
            if (options.Port == 0)
            {
                kestrel.Listen(IPAddress.Loopback, 0);
            }
            else
            {
                kestrel.ListenLocalhost(options.Port);
            }
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new HeaderRedactor(options.RedactHeaders));
        builder.Services.AddSingleton<ExchangeLogWriter>();
        builder.Services.AddSingleton(CreateUpstreamClient());
        builder.Services.AddSingleton<ProxyHandler>();
        builder.Services.AddSingleton<LogIndex>();

        var app = builder.Build();

        app.UseUiPaths();
        app.UseRouting();
        app.MapUi();

        // Everything outside /_ailog is proxied. A catch-all endpoint has the lowest route precedence.
        var proxy = app.Services.GetRequiredService<ProxyHandler>();
        app.Map("/{**path}", proxy.HandleAsync);

        // Start indexing existing logs right away rather than on the first UI request.
        app.Services.GetRequiredService<LogIndex>();

        app.Lifetime.ApplicationStarted.Register(() => PrintBanner(app, options));
        return app;
    }

    /// <summary>Addresses Kestrel actually bound to (resolves port 0).</summary>
    public static IReadOnlyCollection<string> GetListeningAddresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];

    private static HttpClient CreateUpstreamClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Maps AILOG_PORT, AILOG_ROUTES__GEMINI etc. onto the AiLog configuration section.</summary>
    private static Dictionary<string, string?> ReadPrefixedEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                values["AiLog:" + key[EnvironmentPrefix.Length..].Replace("__", ":")] = entry.Value as string;
            }
        }

        return values;
    }

    private static void PrintBanner(WebApplication app, AiLogOptions options)
    {
        var addresses = GetListeningAddresses(app);
        foreach (var address in addresses)
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
    }
}
