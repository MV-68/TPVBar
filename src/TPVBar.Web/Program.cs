using Serilog;
using TPVBar.Application;
using TPVBar.Infrastructure;
using TPVBar.Infrastructure.Persistence;
using TPVBar.Web.Cli;
using TPVBar.Web.Components;
using TPVBar.Web.Endpoints;
using TPVBar.Web.Observability;
using TPVBar.Web.Realtime;
using TPVBar.Web.Security;
using TPVBar.Web.Services;

// Formatos españoles (moneda EUR, fechas) para todo el proceso, incluidos los circuitos de Blazor.
var spanish = new System.Globalization.CultureInfo("es-ES");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = spanish;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = spanish;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Comprobación de vida del proceso web. Las comprobaciones de PostgreSQL, SignalR
// e impresión se añadirán cuando existan esos módulos (ver sección 17 del prompt maestro).
builder.Host.UseWindowsService(options => options.ServiceName = "TPVBar.Web");

// Configuración protegida de producción: un archivo FUERA del repositorio y de la carpeta de la aplicación
// (con permisos restringidos), indicado por la variable de entorno TPVBAR_CONFIG_FILE. Contiene la cadena de
// conexión, la clave del agente y el certificado. Desarrollo usa User Secrets.
var protectedConfig = Environment.GetEnvironmentVariable("TPVBAR_CONFIG_FILE");
if (!string.IsNullOrWhiteSpace(protectedConfig))
{
    builder.Configuration.AddJsonFile(protectedConfig, optional: false, reloadOnChange: false);
}

// Registro estructurado (JSON compacto, un evento por línea) en archivo rotativo diario, solo si se configura.
var logDirectory = builder.Configuration["Logging:File:Directory"];
if (!string.IsNullOrWhiteSpace(logDirectory))
{
    var retained = builder.Configuration.GetValue("Logging:File:RetainedDays", 30);
    var fileLogger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.File(
            new Serilog.Formatting.Compact.CompactJsonFormatter(),
            Path.Combine(logDirectory, "tpvbar-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: retained,
            shared: false)
        .CreateLogger();
    builder.Logging.AddSerilog(fileLogger, dispose: true);
}

builder.Services.AddHealthChecks()
    .AddCheck<TPVBar.Web.Diagnostics.DatabaseHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CircuitCountHandler>();
builder.Services.AddScoped<TPVBar.Web.Diagnostics.DiagnosticsService>();

// La API del agente usa los mismos ajustes JSON que el contrato (enums como texto).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    foreach (var converter in TPVBar.PrintContracts.PrintJson.Options.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

builder.Services.AddScoped<UseCaseRunner>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<OperationEventBus>();
builder.Services.AddSingleton<OperationEventPublisher>();

builder.Services.AddHostedService<TPVBar.Web.Services.BillingSubmissionWorker>();

builder.Services
    .AddTpvBarApplication()
    .AddTpvBarInfrastructure()
    .AddTpvBarSecurity(builder.Configuration);

var app = builder.Build();

// Comandos de administración (--migrate, --create-admin, --create-user): se ejecutan y terminan sin arrancar el servidor.
if (AdminCommands.IsAdminCommand(args))
{
    Environment.ExitCode = await AdminCommands.RunAsync(app, args);
    return;
}

// --seed-demo: historial ficticio de ventas para demostraciones (solo fuera de producción).
if (DemoCommands.IsDemoCommand(args))
{
    Environment.ExitCode = await DemoCommands.RunAsync(app);
    return;
}

if (app.Environment.IsDevelopment())
{
    // Solo en desarrollo: migraciones y usuarios de prueba automáticos. En producción las migraciones
    // se aplican de forma explícita (--migrate) y con copia de seguridad previa.
    await DatabaseInitializer.MigrateAsync(app.Services);
    await DatabaseInitializer.SeedReferenceDataAsync(app.Services);
    await DatabaseInitializer.SeedDevelopmentDataAsync(app.Services);
}
else
{
    await DatabaseInitializer.SeedReferenceDataAsync(app.Services);
}

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(spanish),
    SupportedCultures = [spanish],
    SupportedUICultures = [spanish],
});

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Las páginas de error son para personas: la API del agente y el hub deben devolver el código HTTP tal cual.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
               !context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseRateLimiter();
app.UsePrintAgentKey();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
// /health: el proceso vive. /health/ready: además PostgreSQL responde y el esquema está al día (503 si no).
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
}).AllowAnonymous();
app.MapAuthEndpoints();
app.MapPrintAgentEndpoints();
app.MapReportExportEndpoints();
app.MapHub<OperationsHub>(OperationsHub.Path).RequireAuthorization();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
