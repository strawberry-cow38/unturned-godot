using System.Text.Json;
using System.Text.Json.Serialization;
using UnturnedBevy;

// stdout is a protocol channel. Diagnostics go to stderr, including content loaders.
var host = new BridgeHost();
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
options.Converters.Add(new JsonStringEnumConverter());
string line;
while ((line = Console.ReadLine()) != null)
{
    long id = 0;
    bool shutdown = false;
    try
    {
        using var doc = JsonDocument.Parse(line);
        var request = doc.RootElement;
        if (request.TryGetProperty("id", out var idValue)) id = idValue.GetInt64();
        var op = request.GetProperty("op").GetString();
        var result = host.Handle(op, request);
        shutdown = op == "shutdown";
        Console.WriteLine(JsonSerializer.Serialize(new { id, ok = true, result, state = host.State() }, options));
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine(JsonSerializer.Serialize(new { id, ok = false, error = error.Message }, options));
    }
    if (shutdown) break;
}
host.Dispose();
