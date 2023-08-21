using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OrviboBlazor;
using OrviboControl;

using PlugEnumerator plugEnumerator = new();
plugEnumerator.DiscoverPlugs();

PlugMetadataService metadataService = new();
using PlugSchedulerService schedulerService = new(plugEnumerator, metadataService);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton(plugEnumerator);
builder.Services.AddSingleton(metadataService);
builder.Services.AddSingleton(schedulerService);

WebApplication app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
