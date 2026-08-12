using AuditionModStudio.Gateway;

var builder = WebApplication.CreateBuilder(args);
GatewayApplication.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();
GatewayApplication.ConfigurePipeline(app);
app.Run();

public partial class Program;
