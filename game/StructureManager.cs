using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace UnturnedGodot
{
    /// <summary>What a structure query found: the piece, the face you hit, and what it is made of.</summary>
    public readonly struct StructureHit
    {
        public readonly Node3D Node;
        public readonly Vector3 Normal;
        public readonly int Tier;
        public readonly EConstruct Construct;
        public StructureHit(Node3D n, Vector3 normal, int tier, EConstruct c)
        { Node = n; Normal = normal; Tier = tier; Construct = c; }
    }

    // The real structure system, replacing BuildTool's box-and-grid stand-in.
    //
    // Owns: the occupied-slot lattice, placement validity (support + overlap), per-piece health and damage,
    // upgrade/salvage, and save/load. Everything is keyed on StructureCatalog.SlotKey, so "is this slot taken"
    // is a dictionary hit rather than a physics sweep -- placement is checked many times a second while the
    // ghost follows the aim, and a sweep per frame per candidate is the kind of cost that only shows up once
    // someone builds a large base.
    //
    // SUPPORT is the rule that makes a base a base rather than floating tiles: retail gates it on the asset's
    // Requires_Pillars (SDK ItemStructureAsset.cs:62,240). Here that lives on the tier -- wood and brick need
    // something underneath, metal does not. A floor on the ground is always supported; anything else needs a
    // neighbour in the lattice.
    public partial class StructureManager : Node3D
    {
        public static StructureManager Instance { get; private set; }

        public const string Group = "structures";   // the collision/scene group barricades and damage query

        public sealed class Piece
        {
            public Node3D Node;
            public EConstruct Construct;
            public int Tier;
            public int Health;
            public int MaxHealth;
            public Vector3 Pos;
            public float YawDeg;
            public string Key;
        }

        readonly Dictionary<string, Piece> _bySlot = new();
        readonly List<Piece> _all = new();

        public int Count => _all.Count;
        public IReadOnlyList<Piece> All => _all;

        /// <summary>Load on entry / save on exit. OFF by default and switched on only by the game's own
        /// provisioning (BuildTool.EnsureManager), so a test that news up a manager never touches the player's
        /// real save file -- an L1 run that silently overwrote user://structures.json with its fixtures would
        /// destroy a base and pass while doing it.</summary>
        public bool AutoPersist;

        public override void _EnterTree()
        {
            Instance = this;
            if (AutoPersist)
            {
                int n = LoadFromDisk();
                if (n > 0) GD.Print($"[structures] restored {n} piece(s) from {SavePath}");
            }
        }

        public override void _ExitTree()
        {
            if (AutoPersist) SaveToDisk();
            if (Instance == this) Instance = null;
        }

        /// <summary>Godot delivers this on a clean quit. _ExitTree alone is not enough: a window close tears the
        /// tree down in an order where the save can be missed, and losing a base on quit is the one failure
        /// nobody forgives.</summary>
        public override void _Notification(int what)
        {
            if (what == NotificationWMCloseRequest || what == NotificationPredelete)
                if (AutoPersist) SaveToDisk();
        }

        // ---- the seam other systems build against (published to cow tools' barricade branch) ---------------

        /// <summary>Nearest structure piece within `radius` of a world point, or null. Barricades use this to
        /// decide what they are attaching to.</summary>
        public StructureHit? QueryAt(Vector3 world, float radius = 1.0f)
        {
            Piece best = null; float bestD = radius * radius;
            foreach (var p in _all)
            {
                float d = p.Pos.DistanceSquaredTo(world);
                if (d <= bestD) { bestD = d; best = p; }
            }
            if (best == null) return null;
            Vector3 n = StructureCatalog.IsFace(best.Construct)
                ? Vector3.Up
                : new Vector3(Mathf.Sin(Mathf.DegToRad(best.YawDeg)), 0f, Mathf.Cos(Mathf.DegToRad(best.YawDeg))).Normalized();
            return new StructureHit(best.Node, n, best.Tier, best.Construct);
        }

        /// <summary>Can a barricade attach here? True when a structure piece is close enough AND the surface
        /// faces roughly the same way the caller is asking about. Deliberately permissive on the normal: a
        /// wall-mounted barricade approaches from either side.</summary>
        public bool CanAttach(Vector3 world, Vector3 normal)
        {
            var hit = QueryAt(world, 1.5f);
            if (hit == null) return false;
            if (normal == Vector3.Zero) return true;
            return Mathf.Abs(hit.Value.Normal.Dot(normal.Normalized())) > 0.5f;
        }

        // ---- placement -------------------------------------------------------------------------------------

        /// <summary>Is this slot free AND supported? Returns the reason when not, so the ghost can say why
        /// instead of just turning red -- "no support" and "already occupied" are different mistakes.</summary>
        public bool CanPlace(Vector3 world, EConstruct c, int tier, out string reason)
        {
            var (pos, _) = StructureCatalog.Snap(world, c);
            string key = StructureCatalog.SlotKey(pos, c);
            if (_bySlot.ContainsKey(key)) { reason = "occupied"; return false; }
            if (!HasSupport(pos, c, tier)) { reason = "no support"; return false; }
            reason = null;
            return true;
        }

        /// <summary>Support rule. A floor at ground level stands on its own; everything else needs a piece in
        /// an adjacent lattice slot. Metal (RequiresPillars=false) skips the check entirely, which is the port
        /// of retail letting some assets place free-standing.</summary>
        public bool HasSupport(Vector3 snapped, EConstruct c, int tier)
        {
            if (!StructureCatalog.TierAt(tier).RequiresPillars) return true;
            if (c == EConstruct.Floor && Mathf.Abs(snapped.Y) < StructureCatalog.WallHeight * 0.5f) return true;
            if (_all.Count == 0) return false;

            // anything in the ring of neighbouring slots (same level or one below) counts as support
            foreach (var p in _all)
            {
                float dy = snapped.Y - p.Pos.Y;
                if (dy < -0.01f || dy > StructureCatalog.WallHeight + 0.01f) continue;
                float dxz = new Vector2(snapped.X - p.Pos.X, snapped.Z - p.Pos.Z).Length();
                if (dxz <= StructureCatalog.EdgeLength + StructureCatalog.OverlapPadding) return true;
            }
            return false;
        }

        /// <summary>Place a piece. Returns null when the slot is refused -- callers must not assume success.</summary>
        public Piece Place(Vector3 world, EConstruct c, int tier)
        {
            if (!CanPlace(world, c, tier, out _)) return null;
            var (pos, yaw) = StructureCatalog.Snap(world, c);
            var t = StructureCatalog.TierAt(tier);
            var node = BuildNode(c, t, pos, yaw);
            AddChild(node);
            node.AddToGroup(Group);
            var piece = new Piece
            {
                Node = node, Construct = c, Tier = tier, Health = t.Health, MaxHealth = t.Health,
                Pos = pos, YawDeg = yaw, Key = StructureCatalog.SlotKey(pos, c),
            };
            _bySlot[piece.Key] = piece;
            _all.Add(piece);
            return piece;
        }

        Node3D BuildNode(EConstruct c, StructureCatalog.Tier t, Vector3 pos, float yawDeg)
        {
            var ext = StructureCatalog.Extents(c);
            var root = new Node3D { Name = $"{c}_{t.Name}", Position = pos + Vector3.Up * StructureCatalog.PivotOffset(c) };
            root.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = ext } };
            mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = t.Tint };
            root.AddChild(mi);
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = ext } });
            root.AddChild(body);
            return root;
        }

        // ---- damage / upgrade / salvage --------------------------------------------------------------------

        /// <summary>Apply damage. Returns true if the piece was destroyed. A tier that is not Vulnerable ignores
        /// non-explosive damage entirely (retail's isVulnerable), which is what stops a hatchet felling a metal
        /// wall.</summary>
        public bool Damage(Piece p, int amount, bool explosive = false)
        {
            if (p == null || !_all.Contains(p)) return false;
            if (!StructureCatalog.TierAt(p.Tier).Vulnerable && !explosive) return false;
            p.Health -= Mathf.Max(0, amount);
            if (p.Health > 0) return false;
            Remove(p);
            return true;
        }

        /// <summary>Upgrade to the next tier, refilling health. Returns false at the top of the ladder.</summary>
        public bool Upgrade(Piece p)
        {
            if (p == null || p.Tier >= StructureCatalog.TierCount - 1) return false;
            p.Tier++;
            var t = StructureCatalog.TierAt(p.Tier);
            p.MaxHealth = t.Health;
            p.Health = t.Health;
            if (p.Node != null && GodotObject.IsInstanceValid(p.Node))
            {
                foreach (var ch in p.Node.GetChildren())
                    if (ch is MeshInstance3D mi) mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = t.Tint };
                p.Node.Name = $"{p.Construct}_{t.Name}";
            }
            return true;
        }

        /// <summary>Repair toward full health. Returns the amount actually restored, which is 0 at full -- so a
        /// caller can tell "already fine" from "repaired" and not consume a resource for nothing.</summary>
        public int Repair(Piece p, int amount)
        {
            if (p == null || amount <= 0 || !_all.Contains(p)) return 0;
            int before = p.Health;
            p.Health = Mathf.Min(p.MaxHealth, p.Health + amount);
            return p.Health - before;
        }

        /// <summary>How long taking this piece back down should take, in seconds. Retail scales a base duration
        /// by the asset's Salvage_Duration_Multiplier (ItemStructureAsset.cs:79) -- sturdier tiers are slower,
        /// which is what stops a raider dismantling a metal wall as quickly as a wooden one.</summary>
        public float SalvageSeconds(Piece p, float baseSeconds = 2f)
            => p == null ? 0f : baseSeconds * StructureCatalog.TierAt(p.Tier).SalvageDurationMultiplier;

        /// <summary>Take a piece back down, freeing its slot. Returns the tier salvaged, or -1 if there was
        /// nothing there -- callers refund materials off that, so "nothing happened" must be distinguishable
        /// from "salvaged tier 0".</summary>
        public int Salvage(Piece p)
        {
            if (p == null || !_all.Contains(p)) return -1;
            int tier = p.Tier;
            Remove(p);
            return tier;
        }

        // ---- disk persistence ------------------------------------------------------------------------------
        // There is no general world-save in the port yet (only user://map_settings.cfg), so structures own their
        // file. user:// rather than res://: it is the writable per-user dir and it survives an exported build,
        // which res:// does not.

        public const string SavePath = "user://structures.json";

        public bool SaveToDisk(string path = SavePath)
        {
            try
            {
                using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
                if (f == null) { GD.PrintErr($"[structures] save open failed: {Godot.FileAccess.GetOpenError()}"); return false; }
                f.StoreString(Serialize());
                return true;
            }
            catch (System.Exception e) { GD.PrintErr($"[structures] save failed: {e.Message}"); return false; }
        }

        /// <summary>Load, returning how many pieces came back. A MISSING file is not an error -- it is a world
        /// nobody has built in yet, and treating it as a failure would spam the log on every fresh start.</summary>
        public int LoadFromDisk(string path = SavePath)
        {
            if (!Godot.FileAccess.FileExists(path)) return 0;
            try
            {
                using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
                if (f == null) { GD.PrintErr($"[structures] load open failed: {Godot.FileAccess.GetOpenError()}"); return 0; }
                return Deserialize(f.GetAsText());
            }
            catch (System.Exception e) { GD.PrintErr($"[structures] load failed: {e.Message}"); return 0; }
        }

        public void Remove(Piece p)
        {
            if (p == null) return;
            _bySlot.Remove(p.Key);
            _all.Remove(p);
            if (p.Node != null && GodotObject.IsInstanceValid(p.Node)) p.Node.QueueFree();
        }

        public void Clear() { foreach (var p in new List<Piece>(_all)) Remove(p); }

        // ---- save / load -----------------------------------------------------------------------------------
        // Retail gates persistence on the asset's isSaveable (SDK ItemStructureAsset.cs:86). Every tier here is
        // saveable, so the flag has no false case yet -- the shape is kept so adding one is a data change.

        public string Serialize()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var p in _all)
                rows.Add(new Dictionary<string, object>
                {
                    ["c"] = (int)p.Construct, ["t"] = p.Tier, ["h"] = p.Health,
                    ["x"] = p.Pos.X, ["y"] = p.Pos.Y, ["z"] = p.Pos.Z,
                });
            return JsonSerializer.Serialize(rows);
        }

        /// <summary>Rebuild from a save. Placement rules are deliberately BYPASSED: a saved base is already
        /// known-good, and re-running support checks would delete pieces whose supporting neighbour simply had
        /// not been restored yet -- load order would silently eat parts of someone's base.</summary>
        public int Deserialize(string json)
        {
            Clear();
            if (string.IsNullOrWhiteSpace(json)) return 0;
            List<Dictionary<string, JsonElement>> rows;
            try { rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json); }
            catch (System.Exception e) { GD.PrintErr($"[structures] load failed: {e.Message}"); return 0; }
            if (rows == null) return 0;
            int n = 0;
            foreach (var r in rows)
            {
                try
                {
                    var c = (EConstruct)r["c"].GetInt32();
                    int tier = r["t"].GetInt32();
                    var pos = new Vector3(r["x"].GetSingle(), r["y"].GetSingle(), r["z"].GetSingle());
                    var (snapped, yaw) = StructureCatalog.Snap(pos, c);
                    var t = StructureCatalog.TierAt(tier);
                    var node = BuildNode(c, t, snapped, yaw);
                    AddChild(node);
                    node.AddToGroup(Group);
                    var piece = new Piece
                    {
                        Node = node, Construct = c, Tier = tier,
                        Health = r.TryGetValue("h", out var h) ? h.GetInt32() : t.Health,
                        MaxHealth = t.Health, Pos = snapped, YawDeg = yaw,
                        Key = StructureCatalog.SlotKey(snapped, c),
                    };
                    if (_bySlot.ContainsKey(piece.Key)) { node.QueueFree(); continue; } // duplicate slot in the save
                    _bySlot[piece.Key] = piece;
                    _all.Add(piece);
                    n++;
                }
                catch (System.Exception) { /* one bad row must not lose the rest of the base */ }
            }
            return n;
        }
    }
}
