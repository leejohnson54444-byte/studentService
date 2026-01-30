using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddMudServices();

// Read API base URL from configuration (wwwroot/appsettings.json or appsettings.Production.json)
var apiBaseUrl = builder.Configuration["ApiSettings:BaseUrl"];

if (string.IsNullOrEmpty(apiBaseUrl))
{
    // Fallback: use the browser's host (development scenario)
    apiBaseUrl = builder.HostEnvironment.BaseAddress;
}

Console.WriteLine($"API Base URL: {apiBaseUrl}");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

await builder.Build().RunAsync();


