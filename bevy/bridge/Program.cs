using System.Text.Json;
using System.Text.Json.Serialization;
using UnturnedBevy;

// stdout is a protocol channel. Diagnostics go to stderr, including content loaders.
var host = new BridgeHost();
// A dedicated host's native parent owns shutdown: systemd/Ctrl-C signal the
// whole process group, and the parent then asks for the final save. The child
// must outlive that request instead of dying on the same signal.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => context.Cancel = host.DedicatedActive);
using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGINT, context => context.Cancel = host.DedicatedActive);
using var sighup = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGHUP, context => context.Cancel = host.DedicatedActive);
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
            : op is "init" or "tick" or "snapshot" or "action" or "save" or "load" or "rebase" or "disconnect" or "reconnect" or "dedicatedServe";
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
// A parent that dies without dedicatedStop closes this pipe: still save.
host.EndOfInput();
host.Dispose();
