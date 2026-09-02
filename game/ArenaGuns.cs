using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Arena GUN RAIN (master 2026-09-02): the arena has no normal loot -- guns continuously SPAWN at random valid spots
    // and randomly DELETE over time, so a churning set of ~Target guns sits on the ground and keeps refreshing. Each gun
    // is a real WorldItem pickup (a RANDOM ported gun fitted with a RANDOM optic) plus a bright orange BEACON so the drop
    // reads from a distance. Spots are on land + clear of walls (the same tests the player spawns use).
    public partial class ArenaGuns : Node3D
    {
        public Terrain Terr;
        public Vector3 Centre;
        public float HalfX, HalfZ;
        public System.Func<Vector3, bool> InWall;
        public int Target = 40;
        public float SpawnEvery = 0.2f;    // try to add a gun this often (while under Target)
        public float DeleteEvery = 0.6f;   // remove a random gun this often -> the set churns

        readonly List<WorldItem> _guns = new();
        readonly List<Node3D> _marks = new();       // beacon PARALLEL to _guns: the gun self-hides (player-eye LOS cull), the beacon does not
        readonly List<ushort> _gunIds = new();
        readonly List<ushort> _scopeIds = new();    // real optics (non-iron SIGHTs that have a ripped mesh) -> a random one per gun
        double _sAcc, _dAcc;
        uint _rng = 0x9E3779B9u;
        static StandardMaterial3D _mat;

        public override void _Ready()
        {
            foreach (var a in SDG.Unturned.Assets.all())
            {
                if (a == null) continue;
                if (a.gunName != null) _gunIds.Add(a.id);
                // scope pool: SIGHT-type, not the gun-specific iron sights, and only ones with a real ripped mesh so the
                // fitted optic actually renders when the picked-up gun is aimed (a model-less sight would ADS to nothing).
                else if (a.type == SDG.Unturned.EItemType.SIGHT && a.itemName != null
                         && !a.itemName.Contains("Iron", System.StringComparison.OrdinalIgnoreCase)
                         && AttachmentFit.MeshFor(a.id) != null)
                    _scopeIds.Add(a.id);
            }
            _mat ??= new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.08f), EmissionEnabled = true, Emission = new Color(1f, 0.55f, 0.08f), EmissionEnergyMultiplier = 1.8f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            for (int guard = 0; _guns.Count < Target && guard < Target * 5; guard++) TrySpawn();   // seed the ground full at match start (retry: a dense town rejects many points); _Process then churns
            GD.Print($"[arenaguns] guns={_gunIds.Count} scopes={_scopeIds.Count} seeded={_guns.Count}");
        }

        uint Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return _rng; }

        public override void _Process(double delta)
        {
            for (int i = _guns.Count - 1; i >= 0; i--)   // a gun freed elsewhere (picked up) takes its beacon with it
                if (!GodotObject.IsInstanceValid(_guns[i])) { Kill(_marks[i]); _guns.RemoveAt(i); _marks.RemoveAt(i); }
            _sAcc += delta; _dAcc += delta;
            if (_sAcc >= SpawnEvery) { _sAcc = 0; if (_guns.Count < Target && _gunIds.Count > 0) TrySpawn(); }
            if (_dAcc >= DeleteEvery && _guns.Count > 0) { _dAcc = 0; DeleteRandom(); }
        }

        void TrySpawn()
        {
            if (_gunIds.Count == 0) return;   // catalog not registered -> nothing to spawn (guards the modulo below)
            for (int t = 0; t < 24; t++)   // a few attempts to land on a clear, dry spot
            {
                float fx = (Rand() % 2000u) / 1000f - 1f, fz = (Rand() % 2000u) / 1000f - 1f;
                float x = Centre.X + fx * HalfX, z = Centre.Z + fz * HalfZ;
                if (Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(x, z))) continue;
                float y = Terr != null ? Terr.SampleHeight(x, z) : Centre.Y;
                var p = new Vector3(x, y, z);
                if (InWall != null && InWall(p)) continue;
                var item = new SDG.Unturned.Item(_gunIds[(int)(Rand() % (uint)_gunIds.Count)]);
                if (_scopeIds.Count > 0)   // fit a RANDOM optic (master) -- lives on the item, so it survives the pickup
                {
                    AttachmentFit.SetInstalledId(item, "Sight", _scopeIds[(int)(Rand() % (uint)_scopeIds.Count)]);
                    item.gunAttachSeeded = true;   // else the first equip's SeedDefaults swaps our scope back for the gun's irons
                }
                var g = WorldItem.Spawn(this, item, p + Vector3.Up * 0.5f);
                if (g == null) return;
                g.Freeze = true;   // pin at ground height: the Editor debug world has no terrain collider, so an unfrozen RigidBody item sinks out of sight; a match gun is spawned AT ground level anyway
                _guns.Add(g);
                _marks.Add(Beacon(p));   // SEPARATE node: the WorldItem hides its own subtree when no player has LOS to it, so a child marker vanishes -- this one is always visible + doubles as a find-me beacon in a match
                return;
            }
        }

        // Orange disc+pillar+sphere at a gun's ground position, parented to THIS node (not the gun). Scaled down from the
        // cyan spawn markers since there are Target of them.
        Node3D Beacon(Vector3 groundPos)
        {
            var b = new Node3D { Position = groundPos };
            b.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2f, BottomRadius = 2f, Height = 0.3f }, MaterialOverride = _mat, Position = new Vector3(0f, 0.2f, 0f) });   // ground disc
            b.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.4f, BottomRadius = 0.4f, Height = 5f }, MaterialOverride = _mat, Position = new Vector3(0f, 2.5f, 0f) });   // pillar
            b.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.3f }, MaterialOverride = _mat, Position = new Vector3(0f, 5.5f, 0f) });   // cap ball -> reads as an orange dot from above
            AddChild(b);
            return b;
        }

        void DeleteRandom()
        {
            int i = (int)(Rand() % (uint)_guns.Count);
            var g = _guns[i]; var m = _marks[i];
            _guns.RemoveAt(i); _marks.RemoveAt(i);
            if (GodotObject.IsInstanceValid(g)) g.QueueFree();
            Kill(m);
        }

        static void Kill(Node3D n) { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); }
    }
}
