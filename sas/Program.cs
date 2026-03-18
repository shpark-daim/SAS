using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using sas;
using Serilog;
using System.Reflection;


var builder = Host.CreateApplicationBuilder(args);
var env = builder.Environment;

// Configuration
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddTomlFileWithOverride("sas.toml", optional: true, reloadOnChange: true);

if (env.IsDevelopment() && !string.IsNullOrEmpty(env.ApplicationName)) {
    var appAssembly = Assembly.Load(new AssemblyName(env.ApplicationName));
    if (appAssembly is not null)
        builder.Configuration.AddUserSecrets(appAssembly, optional: true);
}

builder.Configuration.AddEnvironmentVariables();


// Options
builder.Services
    .Configure<GeneralOptions>(builder.Configuration.GetSection(GeneralOptions.Section))
    .AddXmsClient()
    .ConfigureHttpClient((sp, client) => {
        var options = sp.GetRequiredService<IOptions<GeneralOptions>>().Value;
        client.BaseAddress = new Uri(options.XmsUrl);
    });

// Services
builder.Services
        .AddSingleton<IVehicleService, VehicleService>()
        .AddSingleton<IMapService, MapService>()
        .AddSingleton<CoreSyncService>()
        .AddHostedService(s => s.GetRequiredService<CoreSyncService>())
        .AddSingleton<IDetectionMapService, DetectionMapService>()
        .AddSingleton<ConnectionService>()
        .AddSingleton<IConnectionService>(s => s.GetRequiredService<ConnectionService>())
        .AddHostedService(s => s.GetRequiredService<ConnectionService>())
        .AddSingleton<ISafetyMonitoringManager, SafetyMonitoringManager>()
        .AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(SafetyMonitoringService).Assembly))
        .AddSingleton<SafetyMonitoringService>()
        .AddSingleton<ISafetyMonitoringService>(s => s.GetRequiredService<SafetyMonitoringService>())
        .AddHostedService(s => s.GetRequiredService<SafetyMonitoringService>())
;

// Serilog
builder.Services.AddSerilog((services, configuration) => {
    configuration
        .MinimumLevel.Information()
        .Enrich.WithProperty("Application", $"{env.ApplicationName}{builder.Configuration.GetValue<string>("ApplicationSubName")?.Insert(0, ".")}")
        .Filter.ByExcluding(logEvent =>
                                        logEvent.Properties.TryGetValue("SourceContext", out var source) &&
                                        source.ToString().Contains("System.Net.Http.HttpClient"))
        .WriteTo.Console()
        .WriteTo.Seq("http://localhost:5341");
});

//builder.Services.AddWindowsService();

var app = builder.Build();
await app.StartAsync();
app.Services.GetRequiredService<ILogger<Program>>().LogInformation("SAS started successfully");
await app.WaitForShutdownAsync();