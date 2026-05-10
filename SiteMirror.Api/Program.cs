using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using SiteMirror.Api.Logging;
using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InMemoryLogBuffer>();
builder.Host.UseSerilog((context, services, configuration) =>
{
    var buffer = services.GetRequiredService<InMemoryLogBuffer>();
    var logDir = Path.Combine(context.HostingEnvironment.ContentRootPath, "logs");
    Directory.CreateDirectory(logDir);
    var filePath = Path.Combine(logDir, "sitemirror-.log");

    configuration
        .ReadFrom.Configuration(context.Configuration)
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            filePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
        .WriteTo.Sink(new RingBufferSink(buffer));
});

builder.Services.Configure<MirrorSettings>(builder.Configuration.GetSection(MirrorSettings.SectionName));
builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection(DatabaseSettings.SectionName));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection(AuthSettings.SectionName));
var dbConnectionString = builder.Configuration.GetSection(DatabaseSettings.SectionName).Get<DatabaseSettings>()?.ConnectionString
                         ?? string.Empty;
builder.Services.AddDbContext<CrawlReadDbContext>(options => options.UseSqlServer(dbConnectionString));

var authSettings = builder.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>() ?? new AuthSettings();
var keyBytes = Encoding.UTF8.GetBytes(
    authSettings.JwtSecret.Length >= 32 ? authSettings.JwtSecret : authSettings.JwtSecret.PadRight(32, 'x'));

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = authSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<SqlServerCrawlRepository>();
builder.Services.AddSingleton<ICrawlRepository>(sp => sp.GetRequiredService<SqlServerCrawlRepository>());
builder.Services.AddSingleton<IMirrorContentAddressRegistry>(sp => sp.GetRequiredService<SqlServerCrawlRepository>());
builder.Services.AddSingleton<MirrorGlobalExecutionGate>();
builder.Services.AddHostedService<MirrorQueueBackgroundService>();
builder.Services.AddSingleton<IUserRepository, SqlUserRepository>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<ISiteMirrorService, MirrorService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p
        .WithOrigins(
            "http://localhost:3000",
            "https://localhost:3000",
            "http://127.0.0.1:3000")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var crawlRepository = scope.ServiceProvider.GetRequiredService<ICrawlRepository>();
    await crawlRepository.EnsureSchemaAsync();
    var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    await userRepository.EnsureUserSchemaAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try
{
    Log.Information("SiteMirror API starting ({Environment})", app.Environment.EnvironmentName);
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
