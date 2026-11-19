using System.Text.Json;

namespace CaptureTool;

internal static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Only nulls are omitted: false and 0 are meaningful (roundTrips: false, ms: 0).
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Line(object value) => JsonSerializer.Serialize(value, Options);
}
