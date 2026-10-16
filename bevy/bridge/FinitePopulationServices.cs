using System.Text.Json;
using UnityEngine;
using static UnturnedBevy.BridgeHost;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    JsonElement? _finiteVehicles;
    FiniteIslandSurface _finiteTerrain;

    void AdoptFiniteTerrain(JsonElement spec)
    {
        string directory = Text(spec,"finiteTerrainDirectory");
        if (directory.Length == 0) return; // older saved spawn-only registrations
        // Field decisions in source use the complete Terrain, even while native
        // collision meshes are streamed. Retain only one active source surface,
        // not all parsed maps/props/roads; discard it on session replacement.
        _finiteTerrain = FiniteIslandFiles.Open(Godot.ProjectSettings.ContentRoot,directory).Surface;
        float Ground(float x,float z) => _finiteTerrain.SampleHeight(x+(float)_originX,z+(float)_originZ);
        byte Layer(float x,float z) => _finiteTerrain.SampleDominantLayer(x+(float)_originX,z+(float)_originZ);
        _zombies.SourceGround = Ground;
        _wildlife.SourceGround = Ground;
        _zombies.SourceLayer = Layer;
        _wildlife.SourceLayer = Layer;
    }

    void AdoptFinitePopulation(JsonElement spec)
    {
        if (spec.TryGetProperty("finiteVehicles",out var rows)) _finiteVehicles = rows.Clone();
    }

    void TranslateFiniteVehicles(Vector3 delta)
    {
        if (_finiteVehicles is JsonElement rows)
            _finiteVehicles = JsonSerializer.SerializeToElement(rows.EnumerateArray()
                .Select(row => CoordinateJson.Entry(row,delta)).ToArray());
    }

    object RegisterFinitePopulation(JsonElement request)
    {
        if (_finiteVehicles != null) return new { installed = false };
        if (!_geometry.HasTerrain) throw new InvalidOperationException("Register terrain before finite populations");
        string mapRoot = Path.GetFullPath(Text(request,"mapRoot"));
        string map = Text(request,"mapName");
        if (map != _map || !map.StartsWith("Island_",StringComparison.Ordinal))
            throw new ArgumentException("Generated population must match the active finite map");
        var population = request.GetProperty("population");
        // The native host is the sole publisher. All positions in this handoff
        // are original world coordinates, so move once into the current frame.
        var delta = new Vector3((float)-_originX,0,(float)-_originZ);
        float[] Local(JsonElement row) => Array(Vec(row) + delta);
        var zombies = population.GetProperty("zombies").EnumerateArray().Select(Local).ToArray();
        var animals = population.GetProperty("animals").EnumerateArray().Select(Local).ToArray();
        var loot = population.GetProperty("loot").EnumerateArray().Select(p => {
            var v = Vec(p,"position") + delta;
            return (new Godot.Vector3(v.x,v.y,v.z),checked((byte)Int(p,"table")));
        }).ToArray();
        var vehicles = population.GetProperty("vehicles").EnumerateArray().Select((v,i) => new {
            key = $"{map}:generated-vehicle:{i}", kind = Text(v,"kind"), variant = Int(v,"variant"),
            position = Local(v.GetProperty("position")), yaw = Float(v,"yaw") }).ToArray();
        // Construct the field before mutating the other services, so missing
        // original table content fails without partially installing the plan.
        var field = new RetailLootStreamer(mapRoot,loot,_server,_geometry);
        var z = _zombies.Register(JsonSerializer.SerializeToElement(new {mapRoot,points=zombies}),0,0);
        var a = _wildlife.Register(JsonSerializer.SerializeToElement(new {mapRoot,points=animals}),0,0);
        var spec = JsonSerializer.SerializeToElement(new {finiteVehicles=vehicles});
        AdoptFinitePopulation(spec); _serviceSpecs.Add(spec); _worldChanged = true;
        _loot = field; _loot.Step(force:true);
        return new { installed=true,zombies=z,animals=a,loot=new {points=_loot.PointCount,live=_loot.LiveCount},vehicles=vehicles.Length };
    }
}
