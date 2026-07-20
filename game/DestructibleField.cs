using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // Destructible props (retail rubble). The registry side of the DestructibleReplication(16) alive-bitmap:
    // a deterministic index space over PEI's placed destructible objects (WorldBuilder assigns the index in
    // placements.txt scan order, holiday-stable), each slot holding the object's live nodes (mesh(es) +
    // collider) plus its retail rubble scalars (Rubble_Health, Rubble_Reset). SetAlive(index,false) breaks a
    // prop -- hides its mesh(es) and drops its collider -- exactly the ResourceField.SetAlive contract, but on
    // individually-placed MeshInstance3D/StaticBody3D nodes instead of a MultiMesh. Server + client both build
    // it (identical index space, content-hash-matched); the server owns health/respawn (ServerDestructibles),
    // the client just mirrors the replicated alive bit.
    //
    // Not a Node: the meshes/colliders it references are already parented to the world root by
    // WorldBuilder.PlaceObject; this is a pure registry the net syncs drive.
    public sealed class DestructibleField
    {
        sealed class Rec
        {
            public MeshInstance3D[] Meshes;   // main mesh + optional foliage mesh (null slot = never built: out-of-season holiday / no colliders)
            public StaticBody3D Body;
            public uint BodyLayer;
            public float MaxHealth;           // 0 = unregistered slot (indestructible)
            public long ResetTicks;
            public bool Alive = true;
        }

        /// <summary>Collider meta key carrying a destructible's index -- the server hit resolution
        /// (GodotWorldRay) reads it to route bullet/melee damage to the right prop.</summary>
        public static readonly StringName MetaKey = "destructible_index";

        Rec[] _recs = System.Array.Empty<Rec>();

        /// <summary>Total destructible-placement slots in the deterministic index space (includes reserved
        /// slots for out-of-season holiday placements that never build a node -- they keep the index aligned
        /// across peers). The bitmap sizes to this.</summary>
        public int InstanceCount => _recs.Length;

        // A reserved-but-unbuilt slot (out-of-season holiday) reads as ALIVE/intact -- there's no prop to break,
        // and the server bitmap keeps it alive too, so the net-sync mirror skips it instead of churning.
        public bool IsAlive(int index) => index < 0 || index >= _recs.Length || _recs[index] == null || _recs[index].Alive;
        public float MaxHealth(int index) => index >= 0 && index < _recs.Length && _recs[index] != null ? _recs[index].MaxHealth : 0f;
        public long ResetTicks(int index) => index >= 0 && index < _recs.Length && _recs[index] != null ? _recs[index].ResetTicks : 0L;

        /// <summary>Reserve the index space (called once with the total destructible-placement count).</summary>
        public void SetCount(int total)
        {
            _recs = new Rec[total];
        }

        /// <summary>Bind a built destructible's live nodes + rubble scalars to its deterministic index.</summary>
        public void Register(int index, StaticBody3D body, MeshInstance3D[] meshes, float maxHealth, long resetTicks)
        {
            if (index < 0 || index >= _recs.Length) return;
            _recs[index] = new Rec { Meshes = meshes, Body = body, BodyLayer = body?.CollisionLayer ?? 0u,
                                     MaxHealth = maxHealth, ResetTicks = resetTicks };
        }

        /// <summary>Break (false) or respawn (true) one prop by index: hide/show its mesh(es) and toggle its
        /// collision. Idempotent. A reserved-but-unbuilt slot (out-of-season holiday) is a no-op.</summary>
        public void SetAlive(int index, bool alive)
        {
            if (index < 0 || index >= _recs.Length) return;
            var r = _recs[index];
            if (r == null || r.Alive == alive) return;
            r.Alive = alive;
            if (r.Meshes != null)
                foreach (var m in r.Meshes)
                    if (m != null && GodotObject.IsInstanceValid(m)) m.Visible = alive;
            if (r.Body != null && GodotObject.IsInstanceValid(r.Body)) r.Body.CollisionLayer = alive ? r.BodyLayer : 0u;
        }

        // ---- catalog: guid -> rubble scalars, parsed from content/objects/rubble.txt ----
        // one line: "<guid> <health> <reset> <mode> <ndrops> <dropId>..." (tools/extract_rubble.py)

        public readonly struct Rubble
        {
            public readonly float Health;
            public readonly long ResetTicks;   // Rubble_Reset seconds x 50 Hz
            public Rubble(float health, long resetTicks) { Health = health; ResetTicks = resetTicks; }
        }

        public static Dictionary<string, Rubble> LoadCatalog()
        {
            var map = new Dictionary<string, Rubble>();
            string path = ProjectSettings.GlobalizePath("res://content/objects/rubble.txt");
            if (!File.Exists(path)) { GD.Print("[rubble] no rubble.txt -- destructibles disabled"); return map; }
            foreach (var line in File.ReadAllLines(path))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 3) continue;
                if (!float.TryParse(sp[1], System.Globalization.CultureInfo.InvariantCulture, out float health)) continue;
                if (!float.TryParse(sp[2], System.Globalization.CultureInfo.InvariantCulture, out float resetSecs)) continue;
                map[sp[0].ToLowerInvariant()] = new Rubble(health, (long)System.Math.Round(resetSecs * 50f));
            }
            GD.Print($"[rubble] catalog {map.Count} destructible object types");
            return map;
        }
    }
}
