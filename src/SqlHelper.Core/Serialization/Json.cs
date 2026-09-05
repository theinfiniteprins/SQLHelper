using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHelper.Core.Serialization;

/// <summary>Shared JSON settings: string enums, indented, case-insensitive on read.</summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
