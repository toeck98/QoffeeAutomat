using System;
using QoffeAutomatService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// load .conf (INI style) from config/homeassistant.conf
builder.Configuration.AddIniFile("config/homeassistant.conf", optional: true, reloadOnChange: true);

// bind options
builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection("homeassistant"));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<HomeAssistantOptions>, HomeAssistantOptionsValidator>();

// http client and services
builder.Services.AddHttpClient<HomeAssistantClient>(client =>
{
    // default timeout; can be overridden
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<HomeAssistantClient>();
builder.Services.AddSingleton<IServiceModule, DisplayModule>();
builder.Services.AddHostedService<HomeAssistantWorker>();

var app = builder.Build();
await app.RunAsync();
