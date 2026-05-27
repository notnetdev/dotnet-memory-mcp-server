using MemoryMcpServer.Contracts;
using MemoryMcpServer.Options;
using MemoryMcpServer.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection("Retrieval"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RetrievalOptions>>().Value);
builder.Services.AddScoped<IContextRetrievalService, ContextRetrievalService>();
builder.Services.AddScoped<IContextService, ContextService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/memory/get-context", async (HttpContext httpContext, GetContextRequest request, IContextService contextService, CancellationToken cancellationToken) =>
{
    var traceId = httpContext.TraceIdentifier;
    var response = await contextService.GetContextAsync(request, traceId, cancellationToken);

    httpContext.Response.Headers.Append("x-trace-id", traceId);
    return Results.Ok(response);
});

app.Run();
