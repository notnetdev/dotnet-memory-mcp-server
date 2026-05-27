namespace MemoryMcpServer.Options;

public sealed class RetrievalOptions
{
    public ScoringOptions Scoring { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
    public FilterOptions Filters { get; set; } = new();
    public ConfidenceOptions Confidence { get; set; } = new();
    public LanguageOptions Language { get; set; } = new();
}

public sealed class ScoringOptions
{
    public double FileHintBoost { get; set; } = 100.0;
    public double TargetHintBoost { get; set; } = 10.0;
    public double ScopeBoost { get; set; } = 3.0;
    public double MinScoreToInclude { get; set; } = 0.01;
}

public sealed class LimitsOptions
{
    public int MaxPrimaryTargets { get; set; } = 10;
    public int MaxRelatedSymbols { get; set; } = 20;
    public int MaxProposedEdits { get; set; } = 5;
}

public sealed class FilterOptions
{
    public bool RequireScopeMatchWhenProvided { get; set; } = false;
}

public sealed class ConfidenceOptions
{
    public double BaseScore { get; set; } = 0.4;
    public double TargetBoostPerItem { get; set; } = 0.03;
    public double MaxTargetsBoost { get; set; } = 0.3;
    public double FileHintBoost { get; set; } = 0.2;
    public double ConstraintsPenalty { get; set; } = 0.05;
    public double EmptyScore { get; set; } = 0.1;
    public double MaxScore { get; set; } = 0.99;
}

public sealed class LanguageOptions
{
    public bool RuSynonymsEnabled { get; set; } = true;

    public Dictionary<string, string[]> RuEnHintMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["контекст"] = ["context"],
        ["контекстн"] = ["context"],
        ["сервис"] = ["service"],
        ["сервисн"] = ["service"],
        ["интерфейс"] = ["interface"],
        ["интерфейсн"] = ["interface"],
        ["реализация"] = ["implementation"],
        ["реализ"] = ["implementation"],
        ["сканер"] = ["scanner"],
        ["метод"] = ["method"],
        ["класс"] = ["class"],
        ["проект"] = ["project"],
        ["файл"] = ["file"],
        ["проверка"] = ["verify", "validation"],
        ["тест"] = ["test"]
    };
}
