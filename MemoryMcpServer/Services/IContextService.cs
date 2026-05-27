using MemoryMcpServer.Contracts;

namespace MemoryMcpServer.Services;

public interface IContextService
{
    Task<GetContextResponse> GetContextAsync(GetContextRequest request, string traceId, CancellationToken cancellationToken);
}
