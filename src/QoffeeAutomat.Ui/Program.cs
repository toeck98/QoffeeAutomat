using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Ui;
using QoffeeAutomat.Ui.Components;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// The UI talks to the agent that served it: same origin, so the session cookie travels with every request.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IAgentApi>(provider => new AgentApiClient(provider.GetRequiredService<HttpClient>()));
builder.Services.AddQoffeeAutomatUi();

await builder.Build().RunAsync();
