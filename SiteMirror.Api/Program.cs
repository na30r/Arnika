using SiteMirror.Api.Models;
using SiteMirror.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MirrorSettings>(builder.Configuration.GetSection(MirrorSettings.SectionName));
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<ISiteMirrorService, MirrorService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.MapControllers();

app.Run();
