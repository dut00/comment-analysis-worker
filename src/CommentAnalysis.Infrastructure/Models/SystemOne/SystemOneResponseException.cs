namespace CommentAnalysis.Infrastructure.Models.SystemOne;

/// <summary>
/// The decision model answered, but not in the expected form. The message never contains the response body, which
/// may echo the comment (personal data).
/// </summary>
public sealed class SystemOneResponseException : Exception
{
    public SystemOneResponseException()
    {
    }

    public SystemOneResponseException(string message)
        : base(message)
    {
    }

    public SystemOneResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
