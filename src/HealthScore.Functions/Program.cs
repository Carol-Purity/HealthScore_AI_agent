using Microsoft.Extensions.Hosting;

// Azure Functions isolated worker with ASP.NET Core integration (HttpRequest / IActionResult).
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .Build();

host.Run();
