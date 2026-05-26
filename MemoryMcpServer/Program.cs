using MemoryMcpServer.Contracts;
using MemoryMcpServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddScoped<IContextRetrievalService, ContextRetrievalService>();
builder.Services.AddScoped<IContextService, ContextService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/memory/get-context", async (GetContextRequest request, IContextService contextService, CancellationToken cancellationToken) =>
{
    var response = await contextService.GetContextAsync(request, cancellationToken);
    return Results.Ok(response);
});

app.Run();
