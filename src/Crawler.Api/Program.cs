using Crawler.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CrawlDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("CrawlerDb")));

var app = builder.Build();

app.MapGet("/", () => "Crawler API — Phase 1 scaffold");

app.Run();
