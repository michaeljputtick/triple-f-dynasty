using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TripleF;
using TripleF.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Sleeper's read API is public and CORS-enabled, so the browser calls it directly.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("https://api.sleeper.app/v1/") });
builder.Services.AddScoped<SleeperClient>();
builder.Services.AddScoped(_ => new PlayerDirectory(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
builder.Services.AddScoped<LeagueHistoryService>();
builder.Services.AddScoped<SiteSettings>();
// FantasyCalc's dynasty values API is public and CORS-enabled, and keys players by Sleeper id.
builder.Services.AddScoped(_ => new TradeValueService(new HttpClient { BaseAddress = new Uri("https://api.fantasycalc.com/") }));

await builder.Build().RunAsync();
