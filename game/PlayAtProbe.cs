using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>UG_PLAYAT=prop[,nth[,dist[,pitch[,rise]]]]: a PLAY-MODE HARNESS (no effect unless set) for a generated town. A
    /// player can walk into a building, stand on a table and press F on a fridge; an offline render cannot, so this does it
    /// for one: it finds the nth piece of furniture named `prop` in the town's generated buildings (a node "Furniture_prop"
    /// under a "Building_*"), stands the player `dist` metres in front of it looking back at it (`pitch` degrees, negative
    /// down) -- or, with dist 0, drops them from `rise` metres over it -- and says what happened, in the log:
    ///   - where the player came to rest against the piece's own floor (on it, or through it to the floor),
    ///   - with UG_PLAYAT_PRESS=1, what F did (the look-ray's focus, then whether a container opened),
    ///   - what the piece's building is made of: its furniture, how much of it has a collider, its lights and containers,
    ///     and the same over every generated building in the town (the light count is the one that matters).
    /// UG_PLAYAT_CMD runs console lines first (';'-separated: `timeset 22`), and UG_PLAYAT_SHOT=png saves the frame and
    /// quits once it is over. Frames are counted in PHYSICS ticks, because a frame of a whole town under a software
    /// rasteriser can be seconds long and the player is a physics body.
    /// UG_PLAYAT_RETAIL=prop (House_00): after that shot, place that RETAIL building through the editor's placer on open ground
    /// beside the town, stand the player in its largest room looking down its longest clear line, and save a second shot
    /// (png name + "_retail") -- a retail interior and a generated one in the SAME scene, under the same sun and settings.</summary>
    public partial class PlayAtProbe : Node
    {
        PlayerController _player;
        Node _world;
        string _prop; int _nth; float _dist = 2.2f, _pitch = -10f, _rise = 2.5f;
        bool _press;
        string _shot, _retail;
        int _tick, _retailTick;
        Node3D _target, _retailNode;
        Vector3 _retailAt; float _retailYaw;

        public static void AttachIfAsked(Node world, PlayerController player)
        {
            var spec = System.Environment.GetEnvironmentVariable("UG_PLAYAT");
            if (string.IsNullOrEmpty(spec) || world == null || player == null) return;
            var a = spec.Split(',');
            var p = new PlayAtProbe { Name = "PlayAtProbe", _player = player, _world = world, _prop = a[0].Trim() };
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var nf = System.Globalization.NumberStyles.Float;
            if (a.Length > 1) int.TryParse(a[1], out p._nth);
            if (a.Length > 2) float.TryParse(a[2], nf, ic, out p._dist);
            if (a.Length > 3) float.TryParse(a[3], nf, ic, out p._pitch);
            if (a.Length > 4) float.TryParse(a[4], nf, ic, out p._rise);
            p._press = System.Environment.GetEnvironmentVariable("UG_PLAYAT_PRESS") == "1";
            p._shot = System.Environment.GetEnvironmentVariable("UG_PLAYAT_SHOT");
            p._retail = System.Environment.GetEnvironmentVariable("UG_PLAYAT_RETAIL");
            world.AddChild(p);
        }

        static void Walk(Node n, System.Action<Node> f)
        {
            f(n);
            foreach (var c in n.GetChildren()) Walk(c, f);
        }

        /// <summary>A node's name as it was asked for: a sibling with the same name is renamed "@Name@123" by AddChild.</summary>
        static string BaseName(Node n)
        {
            var s = n.Name.ToString().TrimStart('@');
            int at = s.IndexOf('@');
            return at >= 0 ? s.Substring(0, at) : s;
        }

        /// <summary>A generated building's furniture: everything directly under its root -- or under its basement's gated node
        /// (BuildingCull) -- but its surfaces (WallSurface), its stairs' invisible ramps (a bare StaticBody3D) and an outlet's power
        /// source (beside its plate) -- a piece's mesh, its holder, or a switch.</summary>
        static IEnumerable<Node3D> Furniture(Node3D building)
        {
            foreach (var c in building.GetChildren())
            {
                if (c is Node3D bn && c.Name == "Basement" && c.GetType() == typeof(Node3D))
                {
                    foreach (var f in Furniture(bn)) yield return f;
                    continue;
                }
                if (c is Node3D n && n is not WallSurface && n is not GridPowerSource && !(n is StaticBody3D && n is not RoomLightSwitch)) yield return n;
            }
        }

        static Node3D BuildingOf(Node n)
        {
            for (; n != null; n = n.GetParent()) if (n is Node3D b && BaseName(b).StartsWith("Building_")) return b;
            return null;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!GodotObject.IsInstanceValid(_player)) return;
            _tick++;
            if (_tick == 2) Begin();
            if (_target == null) return;
            if (_tick < 12) Place();        // held a few ticks: the spawn logic and the interp snapshots settle under it
            if (_tick == 110) Report();
            if (_press && _tick == 120)
            {
                Log.Print($"[playat] pressing F: looking at {LookedAt()}");
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.F, PhysicalKeycode = Key.F, Pressed = true });
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.F, PhysicalKeycode = Key.F, Pressed = false });
            }
            if (_press && _tick == 150)
                Log.Print($"[playat] after F: inventory {(_player.InventoryOpen ? "OPEN" : "closed")}, storage '{_player.OpenStorageLabel ?? "-"}'");
            if (!string.IsNullOrEmpty(_shot) && _tick == (_press ? 175 : 140))
            {
                Save(_shot);
                if (string.IsNullOrEmpty(_retail)) { GetTree().Quit(); return; }
                PlaceRetail();
                _retailTick = _tick;
            }
            if (_retailTick > 0)
            {
                if (_tick == _retailTick + 3) FindRetailSpot();
                if (_tick > _retailTick + 3 && _tick < _retailTick + 14 && _retailAt != Vector3.Zero)
                {
                    _player.TeleportTo(_retailAt);
                    _player.RotationDegrees = new Vector3(0f, _retailYaw, 0f);
                    _player.DebugSetLookPitch(_pitch);
                }
                if (_tick == _retailTick + 110)
                {
                    Save(_shot.EndsWith(".png") ? _shot.Substring(0, _shot.Length - 4) + "_retail.png" : _shot + "_retail");
                    GetTree().Quit();
                }
            }
        }

        void Save(string path)
        {
            var cam = GetViewport().GetCamera3D();
            if (cam != null) Log.Print($"[playat] shot from {cam.GlobalPosition}, looking at {LookedAt()}");
            var img = GetViewport().GetTexture()?.GetImage();
            if (img == null) { Log.Err("[playat] no image -- run with a rendering driver, not --headless"); return; }
            var err = img.SavePng(path);
            Log.Print($"[playat] saved {path}: {err}");
        }

        /// <summary>The retail building, placed by the editor's own placer on the open ground 40 m past the town's furthest
        /// building in +X.</summary>
        void PlaceRetail()
        {
            float maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var c in _world.GetChildren())
                if (c is Node3D b && BaseName(b).StartsWith("Building_"))
                { var o = b.GlobalPosition; maxX = Mathf.Max(maxX, o.X); minZ = Mathf.Min(minZ, o.Z); maxZ = Mathf.Max(maxZ, o.Z); }
            if (maxX == float.MinValue) return;
            var at = new Vector3(maxX + 60f, 0f, (minZ + maxZ) * 0.5f);
            at.Y = Terrain.Active != null ? Terrain.Active.SampleHeight(at.X, at.Z) : _target.GlobalPosition.Y;
            _retailNode = (_world as Editor)?.Objects?.Place(_retail, at, EditorObjects.Upright(0f));
            Log.Print($"[playat] placed retail {_retail} at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0}): {(_retailNode != null ? "ok" : "FAILED")}");
        }

        /// <summary>A spot inside the retail building to look from: over a floor with a ceiling above it, clear of every wall by
        /// 0.6 m, with the longest free line of sight (up to 12 m) of any such spot on a 1 m grid -- looking down it, at the
        /// far wall and its corners.</summary>
        void FindRetailSpot()
        {
            if (_retailNode == null) return;
            var ab = new Aabb();
            bool first = true;
            Walk(_retailNode, n => { if (n is MeshInstance3D mi && mi.Mesh != null) { var w = mi.GlobalTransform * mi.GetAabb(); ab = first ? w : ab.Merge(w); first = false; } });
            var space = _retailNode.GetWorld3D().DirectSpaceState;
            const uint mask = (1u << 0) | (1u << 6);
            // (back faces too: a retail building is a shell drawn double-sided, so from inside a room its walls can face away)
            Godot.Collections.Dictionary Ray(Vector3 a, Vector3 b)
            {
                var q = PhysicsRayQueryParameters3D.Create(a, b, mask);
                q.HitBackFaces = true;
                return space.IntersectRay(q);
            }
            int floors = 0, roofed = 0, clear = 0;
            float best = 0f; Vector3 bestAt = Vector3.Zero; float bestYaw = 0f;
            float baseY = _retailNode.GlobalPosition.Y;
            for (float x = ab.Position.X + 1f; x < ab.End.X - 1f; x += 1f)
                for (float z = ab.Position.Z + 1f; z < ab.End.Z - 1f; z += 1f)
                {
                    var floor = Ray(new Vector3(x, baseY + 2.6f, z), new Vector3(x, baseY - 0.5f, z));
                    if (floor.Count == 0) continue;
                    float fy = ((Vector3)floor["position"]).Y;
                    if (fy < baseY + 0.3f) continue;                                          // the ground outside, not a floor
                    floors++;
                    var ceil = Ray(new Vector3(x, fy + 1.0f, z), new Vector3(x, fy + 4.5f, z));
                    if (ceil.Count == 0) continue;                                            // open sky: not inside
                    roofed++;
                    var eye = new Vector3(x, fy + 1.6f, z);
                    float minFree = float.MaxValue, maxFree = 0f, maxYaw = 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        float yaw = k * 45f;
                        var dir = new Vector3(-Mathf.Sin(Mathf.DegToRad(yaw)), 0f, -Mathf.Cos(Mathf.DegToRad(yaw)));   // the player's -Z at that yaw
                        var h = Ray(eye, eye + dir * 12f);
                        if (h.Count == 0) continue;                                           // out a door or a window: not a view of the room
                        float d = ((Vector3)h["position"]).DistanceTo(eye);
                        minFree = Mathf.Min(minFree, d);
                        if (d > maxFree) { maxFree = d; maxYaw = yaw; }
                    }
                    if (minFree < 0.6f || maxFree < 2.5f) continue;                           // in a wall, or a cupboard of a space
                    clear++;
                    if (maxFree > best) { best = maxFree; bestAt = new Vector3(x, fy + 0.05f, z); bestYaw = maxYaw; }
                }
            _retailAt = bestAt; _retailYaw = bestYaw;
            Log.Print($"[playat] retail spot search over {ab.Size.X:0} x {ab.Size.Z:0} m: {floors} over a floor, {roofed} under a ceiling, {clear} clear");
            Log.Print(bestAt == Vector3.Zero ? "[playat] no spot inside the retail building"
                      : $"[playat] retail spot ({bestAt.X:0.0}, {bestAt.Y:0.0}, {bestAt.Z:0.0}) looking {best:0.0} m at yaw {bestYaw:0}");
        }

        void Begin()
        {
            // a piece is a direct child of its building (Furniture() below) that is this prop: by the editor's obj_name meta, by its
            // name, or -- a duplicate name is renamed "@MeshInstance3D@n" by AddChild -- by wearing the catalogue's own cached mesh
            var mesh = (_world as Editor)?.Objects?.PreviewMesh(_prop);
            var found = new List<Node3D>();
            foreach (var c in _world.GetChildren())
                if (c is Node3D b && BaseName(b).StartsWith("Building_"))
                    foreach (var f in Furniture(b))
                        if ((f.HasMeta("obj_name") && (string)f.GetMeta("obj_name") == _prop) || BaseName(f) == "Furniture_" + _prop
                            || (mesh != null && f is MeshInstance3D mi && mi.Mesh == mesh))
                            found.Add(f);
            Log.Print($"[playat] {found.Count} '{_prop}' in the town's buildings");
            if (_nth < 0 || _nth >= found.Count) { Log.Err($"[playat] no {_prop} #{_nth}"); if (!string.IsNullOrEmpty(_shot)) GetTree().Quit(1); return; }
            _target = found[_nth];
            var console = FindConsole(_world);
            var cmd = System.Environment.GetEnvironmentVariable("UG_PLAYAT_CMD");
            if (!string.IsNullOrEmpty(cmd))
                foreach (var line in cmd.Split(';'))
                    if (line.Trim().Length > 0) { Log.Print($"[playat] console: {line.Trim()}"); console?.DebugRun(line.Trim()); }
            Census();
            foreach (var n in GetTree().Root.FindChildren("*", nameof(WorldEnvironment), true, false))
                if (n is WorldEnvironment we && we.Environment != null)
                    Log.Print($"[playat] {we.GetPath()}: SSAO {(we.Environment.SsaoEnabled ? "ON" : "off")}, in world_env {(we.IsInGroup("world_env") ? "yes" : "NO")}, "
                            + $"ambient {we.Environment.AmbientLightEnergy:0.00}; GraphicsOptions.AmbientOcclusion {GraphicsOptions.AmbientOcclusion}, UG_AO {System.Environment.GetEnvironmentVariable("UG_AO") ?? "-"}");
        }

        /// <summary>What the eye's ray meets within reach, on the player's look-ray layers (world, vehicles, props, interactables),
        /// and the nearest container above it -- the ancestry PlayerController's ShelfOf walks.</summary>
        string LookedAt()
        {
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return "no camera";
            var from = cam.GlobalPosition;
            var q = PhysicsRayQueryParameters3D.Create(from, from - cam.GlobalBasis.Z * 4f, (1u << 0) | (1u << 5) | (1u << 6) | (1u << 7));
            q.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
            var hit = cam.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return "nothing within 4 m";
            var n = (Node)hit["collider"];
            string shelf = "no container";
            Node s = n;
            for (int i = 0; i < 4 && s != null; i++, s = s.GetParent()) if (s is StoreShelf ss) { shelf = $"container '{ss.LabelText}'"; break; }
            return $"{n.GetPath()} at {((Vector3)hit["position"]).DistanceTo(from):0.00} m ({shelf} within 4 parents)";
        }

        static DevConsole FindConsole(Node n)
        {
            if (n is DevConsole dc) return dc;
            foreach (var c in n.GetChildren()) { var f = FindConsole(c); if (f != null) return f; }
            return null;
        }

        void Place()
        {
            var t = _target.GlobalTransform;
            var front = t.Basis * Vector3.Up;          // a piece's front is its OBJ +Y (EditorObjects.Upright takes it there)
            front.Y = 0f;
            front = front.LengthSquared() > 1e-6f ? front.Normalized() : Vector3.Forward;
            if (_dist > 0.01f && _clearDist < 0f) _clearDist = ClearDistance(t, front);
            var at = _dist > 0.01f ? t.Origin + front * _clearDist + Vector3.Up * 0.05f : t.Origin + Vector3.Up * _rise;
            _player.TeleportTo(at);
            // the player looks down its own -Z: turned so that is -front, back at the piece
            _player.RotationDegrees = new Vector3(0f, Mathf.RadToDeg(Mathf.Atan2(front.X, front.Z)), 0f);
            _player.DebugSetLookPitch(_pitch);
        }

        float _clearDist = -1f;

        /// <summary>The nearest distance from `dist` out (0.1 m steps, 3 m at most) along the piece's front where a player can
        /// STAND: no piece of the building's furniture standing on the floor within a capsule's reach of the spot (by its MESH
        /// bounds -- the same whether or not the furniture has colliders, so a before and an after pick the same spot), and no
        /// wall between the piece and it. Without it a spot inside a coffee table is a fine place to stand only while the table
        /// is a ghost.</summary>
        float ClearDistance(Transform3D t, Vector3 front)
        {
            float floorY = t.Origin.Y;
            var boxes = new List<Aabb>();
            var b = BuildingOf(_target);
            if (b != null)
                foreach (var f in Furniture(b))
                    Walk(f, n =>
                    {
                        if (n is not MeshInstance3D mi || mi.Mesh == null || !mi.IsVisibleInTree()) return;   // (not a hidden outline or light cone)
                        var w = mi.GlobalTransform * mi.GetAabb();
                        if (w.Size.Y > 0.1f && w.Position.Y < floorY + 1.8f && w.End.Y > floorY + 0.1f) boxes.Add(w);   // standing in the way: not a rug, not a lamp
                    });
            var space = _target.GetWorld3D().DirectSpaceState;
            for (float d = _dist; d <= _dist + 3f; d += 0.1f)
            {
                var p = t.Origin + front * d;
                bool blocked = false;
                foreach (var w in boxes)
                    if (p.X > w.Position.X - 0.45f && p.X < w.End.X + 0.45f && p.Z > w.Position.Z - 0.45f && p.Z < w.End.Z + 0.45f) { blocked = true; break; }
                if (blocked) continue;
                var from = t.Origin + Vector3.Up * 1.2f;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, p + front * 0.45f + Vector3.Up * 1.2f, 1u << 0));
                if (hit.Count > 0) break;                                                       // the next spot is through a wall
                Log.Print($"[playat] standing {d:0.0} m in front (asked {_dist:0.0}): clear of {boxes.Count} pieces' bounds");
                return d;
            }
            Log.Print($"[playat] no clear spot within 3 m past {_dist:0.0} m -- standing at {_dist:0.0} as asked");
            return _dist;
        }

        void Report()
        {
            var t = _target.GlobalTransform;
            var p = _player.GlobalPosition;
            Log.Print($"[playat] {_prop} #{_nth} at ({t.Origin.X:0.00}, {t.Origin.Y:0.00}, {t.Origin.Z:0.00}); the player came to rest at "
                    + $"({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}) -- {p.Y - t.Origin.Y:0.00} m over the piece's floor, "
                    + $"{new Vector2(p.X - t.Origin.X, p.Z - t.Origin.Z).Length():0.00} m from its origin in plan");
        }

        void Census()
        {
            var b = BuildingOf(_target);
            Log.Print($"[playat] {b?.Name}: {Count(b)}");
            // ...and that building piece by piece, beside the shot: what each is, where, and whether it has a collider, is a
            // container or carries a light
            if (b != null && !string.IsNullOrEmpty(_shot))
            {
                var lines = new List<string>();
                foreach (var f in Furniture(b))
                {
                    string what = f.HasMeta("obj_name") ? (string)f.GetMeta("obj_name") : BaseName(f);
                    bool body = false, shelf = false, lamp = false;
                    Walk(f, d => { if (d is CollisionObject3D) body = true; if (d is StoreShelf) shelf = true; if (d is LampLight) lamp = true; });
                    var o = f.Position;
                    lines.Add(System.FormattableString.Invariant($"{what,-28} {f.GetType().Name,-16} ({o.X,6:0.00}, {o.Y,5:0.00}, {o.Z,6:0.00})  collider {(body ? "yes" : "NO ")}{(shelf ? "  container" : "")}{(lamp ? "  light" : "")}"));
                }
                System.IO.File.WriteAllLines(_shot + ".building.txt", lines);
                Log.Print($"[playat] {b.Name}'s furniture listed in {_shot}.building.txt");
            }
            int buildings = 0;
            var all = new Counts();
            foreach (var c in _world.GetChildren())
                if (c is Node3D n && BaseName(n).StartsWith("Building_")) { buildings++; Add(all, n); }
            Log.Print($"[playat] every generated building ({buildings}): {all}");
        }

        sealed class Counts
        {
            public int Furniture, WithCollider, Lamps, Omni, Spot, Containers, Switches, Outlets;
            public override string ToString() => $"{Furniture} pieces of furniture, {WithCollider} with a collider, {Lamps} LampLight(s), "
                                                 + $"{Omni} OmniLight3D + {Spot} SpotLight3D, {Containers} container(s), {Switches} light switch(es), {Outlets} outlet(s)";
        }

        static Counts Count(Node3D b) { var c = new Counts(); if (b != null) Add(c, b); return c; }

        static void Add(Counts c, Node3D building)
        {
            foreach (var f in Furniture(building))
            {
                c.Furniture++;
                bool body = false;
                Walk(f, d => { if (d is CollisionObject3D) body = true; });
                if (body) c.WithCollider++;
            }
            Walk(building, n =>
            {
                if (n is LampLight) c.Lamps++;
                if (n is OmniLight3D) c.Omni++;
                if (n is SpotLight3D) c.Spot++;
                if (n is StoreShelf) c.Containers++;
                if (n is RoomLightSwitch) c.Switches++;
                if (n is GridPowerSource) c.Outlets++;
            });
        }
    }
}
