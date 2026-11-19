using Microsoft.Extensions.Options;
using TPVBar.PrintAgent;
using TPVBar.PrintAgent.Workers;

var builder = Host.CreateApplicationBuilder(args);

// Se ejecuta como servicio de Windows cuando se instala como tal; en consola funciona igual.
builder.Services.AddWindowsService(options => options.ServiceName = "TPVBarPrintAgent");

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddHttpClient<ServerClient>((sp, client) =>
{
    var agent = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    if (Uri.TryCreate(agent.ServerUrl, UriKind.Absolute, out var uri))
    {
        client.BaseAddress = uri;
    }
});
builder.Services.AddSingleton<PrintJobProcessor>();
builder.Services.AddHostedService<PrintLoopWorker>();

var host = builder.Build();

// Falla pronto y con un mensaje claro si la configuración no es válida (p. ej. falta la clave de API).
var problems = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Validate();
if (problems.Count > 0)
{
    var logger = host.Services.GetRequiredService<ILogger<PrintLoopWorker>>();
    foreach (var problem in problems)
    {
#pragma warning disable CA1848 // Mensaje único de arranque: no merece un delegado LoggerMessage.
        logger.LogCritical("Configuración no válida: {Problem}", problem);
#pragma warning restore CA1848
    }

    Environment.ExitCode = 1;
    return;
}

await host.RunAsync();
