using CommentAnalysis.Infrastructure.Models.Llm;

namespace CommentAnalysis.Infrastructure.Models;

/// <summary>
/// Selects and configures the <see cref="Core.Models.IDecisionModel"/> implementation.
/// </summary>
public sealed class ModelOptions
{
    public const string SectionName = "Model";

    /// <summary>Required; there is deliberately no default so a missing setting fails at startup.</summary>
    public ModelProvider? Provider { get; set; }

    public StaticModelOptions Static { get; set; } = new();

    public LlmModelOptions Llm { get; set; } = new();
}

public enum ModelProvider
{
    /// <summary>Returns a fixed prediction. For local development and tests only.</summary>
    Static,

    /// <summary>A chat model served through an OpenAI-compatible API, e.g. a local model in LM Studio.</summary>
    Llm,
}
