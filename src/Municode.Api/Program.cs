using Municode.Api.Auth;
using Municode.Api.Extensions;
using Municode.Api.Services;
using Municode.Common.Data;
using Municode.Common.Extensions;

if (args.Length > 0 && args[0] == "bootstrap")
{
    return await BootstrapCommand.RunAsync(args[1..]);
}

var migrate = args.Length > 0 && args[0] == "migrate";
var builder = WebApplication.CreateBuilder(migrate ? args[1..] : args);
builder.Configuration.AddDevelopmentWorkspaceConfiguration();
builder.Services.AddMunicodeApi(builder.Configuration);
builder.Services.AddMunicodeBootstrap();
builder.Services.AddSetupTransport();

var app = builder.Build();

if (migrate || app.Configuration.GetValue<bool>("Database:RunMigrationsOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await DatabaseMigrationCommand.RunAsync(scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>());
    if (migrate) return 0;
}

if (app.Configuration["Http:PathBase"] is { Length: > 0 } pathBase)
    app.UsePathBase(pathBase);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseSetupTransport();
app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("Dashboard");
app.UseRateLimiter();
app.UseMiddleware<KratosUnavailableMiddleware>();
app.UseAuthentication();
app.UseMiddleware<BrowserRequestSecurityMiddleware>();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
await app.RunAsync();
return 0;

public partial class Program;
