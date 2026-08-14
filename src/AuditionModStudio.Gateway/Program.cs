using AuditionModStudio.Gateway;
using AuditionModStudio.Gateway.Security;
using AuditionModStudio.Core.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new GatewayPrivacyLoggerProvider(new SensitiveDataRedactor()));
GatewayApplication.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();
GatewayApplication.ConfigurePipeline(app);
app.Run();

public partial class Program;
