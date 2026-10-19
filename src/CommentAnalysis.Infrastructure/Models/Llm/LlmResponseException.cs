namespace CommentAnalysis.Infrastructure.Models.Llm;

/// <summary>
/// The LLM answered, but not in the expected form. The message never contains the answer itself, which may echo
/// the comment (personal data).
/// </summary>
public sealed class LlmResponseException : Exception
{
    public LlmResponseException()
    {
    }

    public LlmResponseException(string message)
        : base(message)
    {
    }

    public LlmResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
