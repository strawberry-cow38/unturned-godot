using System.Text.Json;
using System.Text.Json.Serialization;
using UnturnedBevy;

// stdout is a protocol channel. Diagnostics go to stderr, including content loaders.
var host = new BridgeHost();
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
options.Converters.Add(new JsonStringEnumConverter());
options.Converters.Add(new VectorJson());
options.Converters.Add(new ByteArrayJson());
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
        bool includeState = request.TryGetProperty("includeState", out var stateFlag) ? stateFlag.GetBoolean()
            : op is "init" or "tick" or "snapshot" or "action" or "save" or "load" or "rebase";
        bool includeRecipes = request.TryGetProperty("includeRecipes", out var recipeFlag) ? recipeFlag.GetBoolean() : op != "tick";
        Console.WriteLine(JsonSerializer.Serialize(new { id, ok = true, result, state = includeState ? host.State(includeRecipes) : null }, options));
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Console.WriteLine(JsonSerializer.Serialize(new { id, ok = false, error = error.Message }, options));
    }
    if (shutdown) break;
}
host.Dispose();
