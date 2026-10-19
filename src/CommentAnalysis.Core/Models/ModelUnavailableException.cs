namespace CommentAnalysis.Core.Models;

/// <summary>
/// The decision model cannot answer right now, e.g. its server is unreachable, overloaded or serves another model.
/// It is not the comment's fault: the same comment is expected to succeed once the model is back, so it must not
/// be counted as a failed attempt or dead-lettered.
/// </summary>
public sealed class ModelUnavailableException : Exception
{
    public ModelUnavailableException()
    {
    }

    public ModelUnavailableException(string message)
        : base(message)
    {
    }

    public ModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
