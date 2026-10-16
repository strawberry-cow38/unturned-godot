using System.Text.Json;
using UnityEngine;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    const string CropTry = "(try: carrot, corn, wheat, potato, tomato, pumpkin...)";

    /// <summary>DevConsole `plant &lt;crop&gt; [grown]` at PlayerController.LookPoint.
    /// The native client sends that point as `at` (its camera ray to 2.6 m
    /// against its own world/vehicles/props, else the ray end); without one the
    /// accepted eye is cast against the bridge geometry. The listen host plants
    /// directly, as its CropManager + CropNetSync do, behind the same cheats gate
    /// as every other host console verb here. A joined client resolves the seed
    /// and sends PlantCrop, which the server pays for with a seed item.</summary>
    bool CropConsole(JsonElement args, bool remote)
    {
        var words = Text(args, "text").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words[0].Equals("plant", StringComparison.OrdinalIgnoreCase)) return false;
        string arg = words.Length > 1 ? words[1].Trim() : "";
        if (arg.Length == 0) { Emit("console", new { text = "plant: needs an argument (or add it to NoArgVerbs if it should run bare)" }); return true; }
        var pp = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool grown = pp.Length > 1 && (pp[1].Equals("grown", StringComparison.OrdinalIgnoreCase) || pp[1] == "1");
        bool known = CropRegistry.TryByName(pp[0].ToLowerInvariant(), out var crop) && crop.SeedId != 0;
        Vector3 at = args.TryGetProperty("at", out var look) ? Vec(look) : LookPoint();
        string text;
        if (!remote && !_server.Transactions.AllowCheats) text = "console commands are disabled on this server";
        else if (remote)
            text = !known ? $"no crop '{pp[0]}' {CropTry}"
                : _client.SendPlantCrop(crop.SeedId, at) ? $"-> plant {pp[0]} sent to server" : "not connected";
        else if (!known || _server.Transactions.PlantCrop(crop.SeedId, at, grown) == null)
            text = $"no crop '{pp[0]}' {CropTry}";
        else
            text = $"planted {pp[0]}{(grown ? " (grown)" : "")} -- UG_FARMSPEED speeds growth; E near a grown crop to harvest";
        Emit("console", new { text });
        return true;
    }

    /// <summary>PlayerController.LookPoint from the accepted local eye.</summary>
    Vector3 LookPoint()
    {
        float yaw = _yaw * Mathf.Deg2Rad, pitch = _pitch * Mathf.Deg2Rad;
        var direction = new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
        var from = _position + new Vector3(0, PlayerMovementDef.EyeHeightForStance(_movement.Stance), 0);
        var to = from + direction * HandRules.LookReach;
        return _geometry != null && _geometry.RayDetailed(from, to, out var hit) ? hit.Point : to;
    }

    static readonly float FarmSpeed = float.TryParse(Environment.GetEnvironmentVariable("UG_FARMSPEED"), out var s) && s > 0 ? s : 1f;

    /// <summary>CropNetSync's force-mature: the listen host's CropManager clock
    /// runs at UG_FARMSPEED (a development aid, default 1), and a node that
    /// matures ahead of the tick derivation force-grows its entity.</summary>
    void StepFarmSpeed()
    {
        if (FarmSpeed == 1f || _server == null) return;
        long tick = _server.Session.CurrentTick;
        if (tick % 25 != 0) return;   // CropNetSync.DivisorTicks: 2 Hz
        foreach (var e in _server.Crops.All.ToArray())
            if (!_server.Crops.IsGrown(e, tick) && _server.Crops.Schema.TryGet(e.SeedId, out var def)
                && (tick - e.PlantedAtTick) * SDG.Unturned.SimClock.FixedDelta * FarmSpeed >= def.GrowthSeconds)
                _server.Crops.ServerForceGrown(e.NetIdValue, tick);
    }
}
