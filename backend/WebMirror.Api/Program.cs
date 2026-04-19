using WebMirror.Api.Data;
using WebMirror.Api.Options;
using WebMirror.Api.Services;
using WebMirror.Api.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<MirrorOptions>(
    builder.Configuration.GetSection(MirrorOptions.SectionName));

builder.Services.AddHttpClient();

builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();

builder.Services.AddScoped<IPageRepository, PageRepository>();
builder.Services.AddScoped<IAssetRepository, AssetRepository>();
builder.Services.AddScoped<ICrawlQueueRepository, CrawlQueueRepository>();

builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddSingleton<IUrlMapper, UrlMapper>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<ILinkRewriterService, LinkRewriterService>();
builder.Services.AddScoped<IAssetService, AssetService>();
builder.Services.AddScoped<ICrawlerService, CrawlerService>();
builder.Services.AddScoped<ICrawlOrchestrator, CrawlOrchestrator>();

builder.Services.AddHostedService<CrawlQueueWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
