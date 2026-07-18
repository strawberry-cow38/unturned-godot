using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Debug hitbox wireframes (F1 console: `hitbox client|server|off`) -- the collision-parity lens for the
    // MP prediction-pullback workstream: pullbacks come from client-vs-server COLLIDER MISMATCHES (a
    // deployable solid on the client but not the server, a parked car solid on the server but not the
    // client), and this overlay makes both sides visible at once so each parity fix is verified on screen.
    //
    //   CLIENT (cyan / dim steel-blue): the colliders THIS process actually owns -- every enabled
    //   CollisionShape3D under a PhysicsBody3D within UG_HITBOX_RADIUS (default 60 m) of the player.
    //   Cyan = the body touches the movement-solid bit 0 (layer or mask); dim = detection-only
    //   (a puppet's look-ray hull, the water plane) -- exactly the "solid here but not there" tell.
    //
    //   SERVER (magenta): reconstructed from the replica stores (DevConsole.RemoteClient, set only by
    //   ClientWorldSession) -- WHERE from each entity's replicated transform, WHAT shape from the same
    //   derivation the client-side builder uses. The magenta player capsule sits at the authoritative
    //   e.Pos, NOT the predicted shell: its offset from the cyan shell capsule IS the reconciler's
    //   pending correction, drawn.
    //
    // Inert until toggled: no node exists (and nothing scans or builds meshes) unless a console toggle is
    // on, so SP/MpLoopback/goldens are byte-identical. Both toggles are independent; either frees its own
    // wires when switched off. Materials are unshaded + no-depth-test so hitboxes read through walls.
    public partial class HitboxDebugOverlay : Node3D
    {
        public static bool ClientEnabled { get; private set; }
        public static bool ServerEnabled { get; private set; }
        public static bool InstanceAlive => _instance != null && GodotObject.IsInstanceValid(_instance);
        public static int DebugClientWires => InstanceAlive ? _instance._clientWires.Count : 0;   // L1 probes
        public static int DebugServerWires => InstanceAlive ? _instance._serverWires.Count : 0;
        static HitboxDebugOverlay _instance;

        const int ScanIntervalTicks = 25;      // client discovery sweep cadence (0.5 s at 50 Hz); wires track via parenting between sweeps
        const int MaxConcaveTris = 8000;       // skip terrain-tile-sized trimeshes (a building hull passes, a whole terrain chunk doesn't)
        const byte KindPlayer = 1, KindVehicle = 2, KindDeployable = 3, KindZombie = 4;

        /// <summary>The console verb (DevConsole `hitbox &lt;arg&gt;`): parse + flip + attach/detach the
        /// overlay node. Bare `hitbox` reports state. Returns the log line.</summary>
        public static string Console(string arg, SceneTree tree)
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "client": ClientEnabled = !ClientEnabled; break;
                case "server": ServerEnabled = !ServerEnabled; break;
                case "off": case "none": ClientEnabled = ServerEnabled = false; break;
                case "": case "status": break;   // report only
                default: return "usage: hitbox client|server|off  (each toggles; off kills both)";
            }
            Sync(tree);
            string state = $"hitbox: client {(ClientEnabled ? "ON (cyan; dim = detection-only)" : "off")} | server {(ServerEnabled ? "ON (magenta)" : "off")}";
            if (ServerEnabled && DevConsole.RemoteClient == null) state += " -- no server session: magenta draws only on a joined client";
            return state;
        }

        public static void ResetForTests()
        {
            ClientEnabled = ServerEnabled = false;
            if (InstanceAlive) _instance.QueueFree();   // _ExitTree frees the wires it parented into the world
            _instance = null;
        }

        static void Sync(SceneTree tree)
        {
            if (!ClientEnabled && !ServerEnabled)
            {
                if (InstanceAlive) _instance.QueueFree();
                _instance = null;
                return;
            }
            if (InstanceAlive || tree == null) return;
            _instance = new HitboxDebugOverlay { Name = "HitboxDebugOverlay" };
            tree.Root.AddChild(_instance);
        }

        // ---- instance ----

        readonly Dictionary<CollisionShape3D, MeshInstance3D> _clientWires = new();
        readonly Dictionary<(byte Kind, uint Id), MeshInstance3D> _serverWires = new();
        readonly Dictionary<string, ArrayMesh> _serverMeshCache = new();   // per join/leave churn; client shapes build once at discovery
        readonly Dictionary<ushort, (Vector3 Size, Vector3 Center, float Lift)> _depGeo = new();
        int _scanCooldown;   // 0 = sweep due (first _PhysicsProcess scans immediately)

        readonly StandardMaterial3D _matClientSolid = MakeMat(new Color(0.15f, 1f, 1f, 0.9f));      // cyan: movement-solid (bit 0)
        readonly StandardMaterial3D _matClientGhost = MakeMat(new Color(0.25f, 0.45f, 0.9f, 0.7f)); // dim blue: detection-only collider
        readonly StandardMaterial3D _matServer = MakeMat(new Color(1f, 0.15f, 1f, 0.9f));           // magenta: the server's authoritative box

        static readonly float Radius = ParseRadius();
        static float ParseRadius() =>
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_HITBOX_RADIUS"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r) && r > 0f ? r : 60f;

        static StandardMaterial3D MakeMat(Color c) => new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = c,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,   // transparent pass -> draws after the world
            NoDepthTest = true,                                     // hitboxes show THROUGH walls (a debug aid)
            RenderPriority = 10,
        };

        public override void _PhysicsProcess(double delta)
        {
            if (ClientEnabled) { if (_scanCooldown-- <= 0) { _scanCooldown = ScanIntervalTicks; ScanClient(); } }
            else if (_clientWires.Count > 0) { ClearClient(); _scanCooldown = 0; }
            if (ServerEnabled) UpdateServer();
            else if (_serverWires.Count > 0) ClearServer();
        }

        public override void _ExitTree()
        {
            ClearClient(); ClearServer();
            if (_instance == this) _instance = null;
        }

        // ---- Toggle 1: CLIENT colliders -- wireframe what this process's physics actually contains ----
        // Discovery walks the whole tree (deployables, vehicles + puppet hulls, the shell, zombies, world
        // items, static world bodies within radius); each wire is parented UNDER its CollisionShape3D so it
        // tracks the collider's global transform for free and dies with it. Sweeps only add/prune.

        void ScanClient()
        {
            Vector3 eye = Viewpoint();
            List<CollisionShape3D> drop = null;
            foreach (var kv in _clientWires)
                if (!IsInstanceValid(kv.Key) || !Eligible(kv.Key, eye))
                {
                    if (IsInstanceValid(kv.Value)) kv.Value.QueueFree();
                    (drop ??= new List<CollisionShape3D>()).Add(kv.Key);
                }
            if (drop != null) foreach (var k in drop) _clientWires.Remove(k);
            Walk(GetTree().Root, eye);
        }

        void Walk(Node n, Vector3 eye)
        {
            if (n == this) return;   // never wireframe our own wires
            if (n is CollisionShape3D cs && !_clientWires.ContainsKey(cs) && Eligible(cs, eye)) AddClientWire(cs);
            foreach (var c in n.GetChildren()) Walk(c, eye);
        }

        bool Eligible(CollisionShape3D cs, Vector3 eye)
        {
            if (!cs.IsInsideTree() || cs.Disabled || cs.Shape == null) return false;   // a Disabled shape isn't solid (hidden shell in a vehicle, retired ports)
            if (cs.GetParent() is not PhysicsBody3D) return false;                     // Area3D sensors (vehicle bumper) never block anything
            if (cs.Shape is HeightMapShape3D or WorldBoundaryShape3D) return false;    // unbounded -- a wireframe would be a megamesh
            if (cs.Shape is ConcavePolygonShape3D cave && (cave.Data == null || cave.Data.Length > MaxConcaveTris * 3)) return false;   // terrain-tile trimeshes
            return cs.GlobalPosition.DistanceTo(eye) <= Radius;
        }

        void AddClientWire(CollisionShape3D cs)
        {
            var mesh = cs.Shape.GetDebugMesh();   // Godot-native line mesh for ANY shape (box/capsule/convex/trimesh)
            if (mesh == null) return;
            var body = (PhysicsBody3D)cs.GetParent();
            bool solid = ((body.CollisionLayer | body.CollisionMask) & 1) != 0;   // touches the movement-solid bit 0 either way (a puppet's look hull / water plane doesn't)
            var wire = new MeshInstance3D { Mesh = mesh, MaterialOverride = solid ? _matClientSolid : _matClientGhost };
            cs.AddChild(wire);   // inherits the collider's transform -> tracks movement with zero per-frame work
            _clientWires[cs] = wire;
        }

        Vector3 Viewpoint()
        {
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is Node3D p && p.IsInsideTree()) return p.GlobalPosition;
            var cam = GetViewport()?.GetCamera3D();
            return cam != null ? cam.GlobalPosition : Vector3.Zero;
        }

        void ClearClient()
        {
            foreach (var kv in _clientWires) if (IsInstanceValid(kv.Value)) kv.Value.QueueFree();
            _clientWires.Clear();
        }

        // ---- Toggle 2: SERVER colliders -- reconstructed on the client from the replica stores ----
        // WHERE = the replicated entity transform (players: the reconciled authoritative e.Pos, NOT the
        // local predicted shell). WHAT = the shape the server-side builder uses, derived the same way:
        //   players     PlayerController.cs ~1723: capsule HEIGHT_STAND x r 0.35, node origin at the feet
        //   vehicles    Vehicle build ~1080: the spec's main body BoxCollider (BoxSize @ BoxCenter)
        //   deployables Deployable.Spawn: mesh-AABB box (or def.Size) @ ab.GetCenter(), GroundLift + StandBasis
        //               -- e.Pos is the SURFACE point (DeployableReplicaView passes it straight to Spawn)
        //   zombies     ZombieController.cs ~110: capsule HeightFor(speciality) x r 0.4, centered at h/2

        void UpdateServer()
        {
            var client = DevConsole.RemoteClient;   // set only by ClientWorldSession -- null in SP/MpLoopback
            if (client == null) { if (_serverWires.Count > 0) ClearServer(); return; }
            var seen = new HashSet<(byte, uint)>();

            foreach (var e in client.Players.All)
            {
                float h = SDG.Unturned.PlayerMovementDef.HEIGHT_STAND;
                var mi = WireFor((KindPlayer, e.NetIdValue), seen, $"cap:{h}:0.35", () => new CapsuleShape3D { Height = h, Radius = 0.35f });
                mi.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(e.YawDegrees)),
                    new Vector3(e.Pos.x, e.Pos.y + h * 0.5f, e.Pos.z));
            }

            foreach (var e in client.Vehicles.All)
            {
                string name = e.TypeId < Vehicle.SpecNames.Length ? Vehicle.SpecNames[e.TypeId] : "jeep";
                Vehicle.GetBodyBox(name, out var size, out var center);
                var mi = WireFor((KindVehicle, e.NetIdValue), seen, "veh:" + name, () => new BoxShape3D { Size = size });
                var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(e.PitchDegrees), Mathf.DegToRad(e.YawDegrees), Mathf.DegToRad(e.RollDegrees)));
                mi.GlobalTransform = new Transform3D(basis, new Vector3(e.Pos.x, e.Pos.y, e.Pos.z)) * new Transform3D(Basis.Identity, center);
            }

            foreach (var e in client.Deployables.All)
            {
                if (!TryDeployableGeo(e.DefId, out var geo)) continue;
                var mi = WireFor((KindDeployable, e.NetIdValue), seen, "dep:" + e.DefId, () => new BoxShape3D { Size = geo.Size });
                mi.GlobalTransform = new Transform3D(DeployableDef.StandBasis(e.YawDegrees),
                        new Vector3(e.Pos.x, e.Pos.y, e.Pos.z) + Vector3.Up * geo.Lift)
                    * new Transform3D(Basis.Identity, geo.Center);
            }

            foreach (var e in client.Zombies.All)
            {
                if (e.IsDead) continue;   // a dead entity is a corpse, not a collider
                float h = ZombieReplication.HeightFor(e.Speciality);
                var mi = WireFor((KindZombie, e.NetIdValue), seen, $"cap:{h}:0.4", () => new CapsuleShape3D { Height = h, Radius = 0.4f });
                mi.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(e.YawDegrees)),
                    new Vector3(e.Pos.x, e.Pos.y + h * 0.5f, e.Pos.z));
            }

            List<(byte, uint)> gone = null;
            foreach (var kv in _serverWires)
                if (!seen.Contains(kv.Key)) (gone ??= new List<(byte, uint)>()).Add(kv.Key);
            if (gone != null)
                foreach (var key in gone)
                {
                    if (IsInstanceValid(_serverWires[key])) _serverWires[key].QueueFree();
                    _serverWires.Remove(key);
                }
        }

        MeshInstance3D WireFor((byte, uint) key, HashSet<(byte, uint)> seen, string meshKey, System.Func<Shape3D> shape)
        {
            seen.Add(key);
            if (_serverWires.TryGetValue(key, out var mi) && IsInstanceValid(mi)) return mi;
            if (!_serverMeshCache.TryGetValue(meshKey, out var mesh))
                _serverMeshCache[meshKey] = mesh = shape().GetDebugMesh();
            mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _matServer, TopLevel = true };
            AddChild(mi);
            _serverWires[key] = mi;
            return mi;
        }

        // Same derivation as Deployable.Spawn (mesh AABB in the flat authored frame): box = ab.Size (or
        // def.Size when meshless), local center = ab.GetCenter(), node lifted by GroundLift above e.Pos.
        bool TryDeployableGeo(ushort defId, out (Vector3 Size, Vector3 Center, float Lift) geo)
        {
            if (_depGeo.TryGetValue(defId, out geo)) return true;
            var def = DeployableDef.ById(defId);
            if (def == null) { geo = default; return false; }
            var mesh = def.LoadMesh();
            Aabb ab = mesh != null ? mesh.GetAabb() : new Aabb();
            geo = (ab.Size == Vector3.Zero ? def.Size : ab.Size, ab.GetCenter(), DeployableDef.GroundLift(ab));
            _depGeo[defId] = geo;
            return true;
        }

        void ClearServer()
        {
            foreach (var kv in _serverWires) if (IsInstanceValid(kv.Value)) kv.Value.QueueFree();
            _serverWires.Clear();
        }
    }
}
