using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppSentry.Core.Util;

/// <summary>Shared JSON settings for the database, the service pipe and legacy imports.</summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
