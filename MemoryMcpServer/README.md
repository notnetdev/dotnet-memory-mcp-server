# MemoryMcpServer (MVP)

Минимальный API-сервер для `memory.get_context`.

## Требования

- .NET 10 SDK
- Локальный PostgreSQL со схемой, заполненной `Mcp.Scanner`
- Переменная окружения `MCP_SCANNER_CONNECTION`

Пример:

```powershell
$env:MCP_SCANNER_CONNECTION="Host=localhost;Port=5432;Database=Sky;Username=postgres;"
```

## Запуск

```powershell
dotnet run --project .\MemoryMcpServer\MemoryMcpServer.csproj
```

По умолчанию endpoint доступен на:

- `POST /memory/get-context`

## Пример запроса

```json
{
  "task": "fix context ranking for interface implementation",
  "scope": "MemoryMcpServer",
  "constraints": ["do-not-touch:bin,obj"],
  "filesHint": ["ContextService.cs"]
}
```

## Пример вызова (PowerShell)

```powershell
$body = @{
  task = "fix context ranking for interface implementation"
  scope = "MemoryMcpServer"
  constraints = @("do-not-touch:bin,obj")
  filesHint = @("ContextService.cs")
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "http://localhost:5000/memory/get-context" -ContentType "application/json" -Body $body
```

## Что возвращает `memory.get_context`

- `task_intent`
- `primary_targets`
- `related_symbols`
- `constraints_applied`
- `proposed_edits`
- `verification`
- `freshness`
- `confidence`
- `inclusion_reasons`

## Русскоязычные задачи (MVP)

- Retrieval поддерживает русские формулировки задач: базовые intent-маркеры + нормализация токенов.
- Для повышения точности применяются RU->EN синонимы (например: `интерфейс -> interface`, `сервис -> service`).
- Если известны конкретные файлы/классы, рекомендуется явно передавать их в `filesHint`.

Пример запроса на русском:

```json
{
  "task": "исправить контекст для интерфейса и его реализации в сервисе",
  "scope": "Mcp.Scanner",
  "constraints": ["do-not-touch:bin,obj"],
  "filesHint": ["ContextService.cs"]
}
```
