using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Hub.Ui;
using QoffeeAutomat.Hub.Ui.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// The UI talks to the hub that served it: same origin, so the session cookie travels with every request. The base address is the one of the page
// (the hub writes a path prefix such as Home Assistant ingress into <base href>), and every address of the app is relative to it.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddQoffeeAutomatHubUi();

await builder.Build().RunAsync();
