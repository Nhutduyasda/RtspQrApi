using Microsoft.EntityFrameworkCore;
using RtspQrApi.Auth;
using RtspQrApi.Data;
using RtspQrApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.Configure<BasicAuthOptions>(builder.Configuration.GetSection("BasicAuth"));
builder.Services.AddDbContextFactory<RtspQrDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("RtspQrDb")
        ?? ProgramDefaults.ConnectionString;
    options.UseSqlServer(connectionString);
});
builder.Services.AddSingleton<CameraManager>();
builder.Services.AddSingleton<QrProcessor>();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

var databaseLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");
try
{
    await using var dbContext = await app.Services
        .GetRequiredService<IDbContextFactory<RtspQrDbContext>>()
        .CreateDbContextAsync()
        .ConfigureAwait(false);
    await dbContext.Database.MigrateAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    databaseLogger.LogWarning(ex, "Could not migrate QR database. QR detection will continue, but persistence may be unavailable.");
}

await app.Services.GetRequiredService<CameraManager>()
    .LoadPersistedCamerasAsync()
    .ConfigureAwait(false);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<BasicAuthMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();

app.Run();
