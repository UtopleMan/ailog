using AiLog.Web;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// BaseAddress is http://host:port/_ailog/, so API calls use relative paths like "api/logs".
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<LogFeed>();
builder.Services.AddScoped<ExchangeLogClient>();
builder.Services.AddBlazorBlueprintComponents();

await builder.Build().RunAsync();
