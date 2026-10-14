using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnturnedBevy;

// Geometry splat/layer bytes are numeric arrays, consistent with every other
// terrain array. Accept base64 too for callers of original byte[] action APIs.
public sealed class ByteArrayJson : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetBytesFromBase64();
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected byte array");
        var bytes = new List<byte>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) bytes.Add(reader.GetByte());
        return bytes.ToArray();
    }
    public override void Write(Utf8JsonWriter writer, byte[] bytes, JsonSerializerOptions options)
    {
        writer.WriteStartArray(); foreach (byte value in bytes) writer.WriteNumberValue(value); writer.WriteEndArray();
    }
}
