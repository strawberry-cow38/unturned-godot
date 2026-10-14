using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace UnturnedBevy;

// The compatibility vector has computed normalized/magnitude properties. Serializing
// those recursively creates an infinite normalized.normalized chain. Wire vectors
// are coordinates only; native event records use {x,y,z}, input also accepts arrays.
public sealed class VectorJson : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        var vector = value.ValueKind == JsonValueKind.Array
            ? new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle())
            : new Vector3(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle(), value.GetProperty("z").GetSingle());
        if (!float.IsFinite(vector.x) || !float.IsFinite(vector.y) || !float.IsFinite(vector.z)) throw new JsonException("Vector must be finite");
        return vector;
    }
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("x", value.x); writer.WriteNumber("y", value.y); writer.WriteNumber("z", value.z); writer.WriteEndObject();
    }
}
