using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MirrorSettings>(builder.Configuration.GetSection(MirrorSettings.SectionName));
builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection(DatabaseSettings.SectionName));
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<ICrawlRepository, SqlServerCrawlRepository>();
builder.Services.AddScoped<ISiteMirrorService, MirrorService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var crawlRepository = scope.ServiceProvider.GetRequiredService<ICrawlRepository>();
    await crawlRepository.EnsureSchemaAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.MapControllers();

app.Run();
