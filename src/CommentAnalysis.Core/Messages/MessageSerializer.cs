using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommentAnalysis.Core.Messages;

/// <summary>
/// The JSON format shared by all messages: camelCase properties, enums as camelCase strings,
/// and strict handling of required and non-nullable properties so malformed messages fail fast.
/// </summary>
public static class MessageSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <exception cref="JsonException">The payload is not valid JSON for <typeparamref name="T"/>.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, Options)
        ?? throw new JsonException($"The payload deserialized to null instead of {typeof(T).Name}.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}
