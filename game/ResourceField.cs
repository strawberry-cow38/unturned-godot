using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // PEI harvestable RESOURCES (Terrain/Trees.dat): trees, bushes, ore rocks, mushrooms, snow piles...
    // 1694 spawns across 26 types (version-8 flat format: GUID + point + EulerXYZ + scale + isGenerated).
    // tools/resource_extract.py bakes each ResourceAsset's `Resource` prefab Model_0 subtree (trunk +
    // Foliage_0 leaves as SEPARATE parts, since bark vs leaf need different textures) from core.masterbundle
    // into content/resources/<name>_<i>.obj + _tex.png, lists them in resources.txt ("<name> <partCount>"),
    // and exports per-spawn (pos, EulerXYZ, scale) = 9 floats -> <name>.bin. Placement uses the SAME prop
    // convention as Main.BuildObjectsTest (raw Unity mesh, double-sided; Basis(Y,180-ey)*Basis(X,ex)*Basis(Z,-ez),
    // pos.z negated). Tree roots sit ~1.2 below origin, so origin-at-spawn-point sinks them (punch-list #8).
    //
    // MP Phase 8 (§3.7): every instance gets a deterministic LOAD-ORDER INDEX (manifest order x .bin order --
    // identical on every peer, content-hash-matched), which is the implicit wire id ResourceReplication's
    // alive-bitmap keys on. SetAlive(index,false) despawns an instance (zero-scaled out of its MultiMesh +
    // collider off); dedicated servers build with VisualInstances=false (colliders + indices, no rendering).
    public partial class ResourceField : Node3D
    {
        /// <summary>Dedicated fx hygiene (§2.1/§5): false = skip all MultiMesh/material/texture work; the
        /// instance registry (indices for the wire) and tree trunk colliders (the sim needs them) remain.</summary>
        public bool VisualInstances = true;

        sealed class InstanceRec
        {
            public readonly List<(MultiMesh Mm, int Slot)> Slots = new();   // one entry per part-mesh
            public Transform3D Xf;
            public StaticBody3D Trunk;      // trees only
            public uint TrunkLayer;
            public bool Alive = true;
            public string TypeName;         // "Birch_0" etc -- the key into the harvest table
        }
        readonly List<InstanceRec> _instances = new();
        readonly Dictionary<string, List<(Mesh Mesh, Material Mat)>> _partsOfType = new();   // for the falling-tree debris

        /// <summary>Total placed resource instances, in the deterministic load order (the wire index space).</summary>
        public int InstanceCount => _instances.Count;

        public bool IsAlive(int index) => index >= 0 && index < _instances.Count && _instances[index].Alive;

        /// <summary>Meta key stamped on every trunk collider carrying its load-order index -- the same id
        /// space the replication and the harvest sim use.</summary>
        public const string ResourceIndexMeta = "ug_res_index";

        /// <summary>The resource TYPE of an instance ("Birch_0"), or null. The harvest table is keyed by
        /// this, so it is the join between a world instance and what it drops.</summary>
        public string TypeNameOf(int index) =>
            index >= 0 && index < _instances.Count ? _instances[index].TypeName : null;

        /// <summary>The instance index behind a collider a ray hit, or -1 if it is not a resource.</summary>
        public static int IndexOfCollider(GodotObject collider) =>
            collider is Node n && n.HasMeta(ResourceIndexMeta) ? (int)n.GetMeta(ResourceIndexMeta) : -1;

        /// <summary>Where an instance stands, in the field's own space (the placement origin, so the base
        /// of the trunk). The server registers these so a felling can lay its drops out from the tree.</summary>
        public Vector3 PositionOf(int index) =>
            index >= 0 && index < _instances.Count ? _instances[index].Xf.Origin : Vector3.Zero;

        /// <summary>Test seam: the tree-trunk StaticBody3D for an instance (null for non-trees) -- L1s
        /// assert the §7-risk-7 collider toggle without reaching into the registry.</summary>
        public StaticBody3D DebugTrunk(int index) => index >= 0 && index < _instances.Count ? _instances[index].Trunk : null;

        /// <summary>Fell (false) or respawn (true) one resource instance by its load-order index: the visual
        /// leaves/enters its MultiMesh (zero-scale -- MultiMesh has no per-instance visibility) and a tree's
        /// trunk collider toggles with it. Idempotent; never called on the SP direct path.</summary>
        public void SetAlive(int index, bool alive)
        {
            if (index < 0 || index >= _instances.Count) return;
            var r = _instances[index];
            if (r.Alive == alive) return;
            r.Alive = alive;
            var hidden = new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), new Vector3(0f, -10000f, 0f));
            foreach (var (mm, slot) in r.Slots) mm.SetInstanceTransform(slot, alive ? r.Xf : hidden);
            if (r.Trunk != null) r.Trunk.CollisionLayer = alive ? r.TrunkLayer : 0;
            // A respawned tree is a NEW tree as far as the player is concerned: forget what we knew about
            // the stump, or it stands back up wearing the old bar at 3%. The debris latch clears with it,
            // so the next felling gets its own falling tree.
            if (alive) { _knownHealth.Remove(index); _debrisSpawned.Remove(index); }
        }

        // ---- felling: the tree becomes physics debris and topples ---------------------------------------
        //
        // Retail does not animate a tree falling. It hides the standing model and instantiates the SAME
        // model as a Rigidbody at `position + up * Debris_Vertical_Offset`, then AddForce(ragdoll) with
        // drag 1 / angularDrag 1 and destroys it after 8 s. The falling-over look is just physics acting on
        // a tall thin body that got shoved sideways -- which is why the ragdoll direction has to be the
        // server's (ResourceHarvestedEvent.Ragdoll), or two players watch the same tree fall two ways.

        /// <summary>Retail ResourceAsset.Debris_Vertical_Offset default: where the gib is spawned along the
        /// tree's own up axis.</summary>
        public const float DebrisVerticalOffset = 1.0f;

        /// <summary>Retail destroys the gib on an 8 s timer, whether or not it has settled.</summary>
        public const float DebrisLifetime = 8f;

        /// <summary>Smallest horizontal shove a felled tree is given, in retail's post-jitter force units.
        /// Measured, not guessed: below ~40 the stand-in trunk collider settles upright instead of tipping.</summary>
        public const float MinTopplePush = 70f;

        readonly HashSet<int> _debrisSpawned = new();

        /// <summary>
        /// Spawn the falling-tree gib. OFF by default, deliberately, and not because the code is untested.
        ///
        /// The body spawns, carries the tree's meshes, takes the server's shove, lands and expires -- all
        /// covered by tree.falls_as_debris. What is not reliable is the TOPPLE. It works in the render rig
        /// (tools/shot.py treefell shows the birch on its way over) but across runs at the same magnitude it
        /// sometimes settles bolt upright instead, and I could not pin down what separates the two. A tree
        /// that stands there for eight seconds after you chop it and then fades out looks more broken than
        /// the instant vanish this replaces, and it would do that at random, so it stays off until the fall
        /// is dependable. The alive-bit flip runs eitherway -- felling still works with this false.
        /// </summary>
        public static bool DebrisEnabled;

        /// <summary>Fell an instance WITH its physics debris. `ragdoll` is the server's
        /// `direction * totalDamage`; zero (an explosion, an admin command) just drops it in place.
        ///
        /// Deliberately NOT gated on the instance still being alive. Two things fell a tree locally: this
        /// event, and the alive-bitmap poll that mirrors replicated state every physics frame. Whichever
        /// arrives first, the other must still do its half -- an early poll would otherwise eat the tree
        /// before the event could drop the gib, and the tree would blink out with no fall at all. The
        /// once-per-felling guard is what makes calling both safe.</summary>
        public void Fell(int index, Vector3 ragdoll)
        {
            if (index < 0 || index >= _instances.Count) return;
            var rec = _instances[index];
            SetAlive(index, false);
            if (!DebrisEnabled) return;                                        // see the flag: the fall is not dependable yet
            if (!_debrisSpawned.Add(index)) return;                            // already toppling
            if (!VisualInstances || rec.TypeName == null) return;              // dedicated: no gib, no meshes loaded
            if (!_partsOfType.TryGetValue(rec.TypeName, out var parts) || parts.Count == 0) return;

            // Retail's client-side embellishment of the server's vector, verbatim: lift it, scatter it
            // horizontally, then double it. The +8 up is what stops a felled tree from being fired along
            // the ground like a log from a cannon.
            ragdoll.Y += 8f;
            ragdoll.X += (float)GD.RandRange(-16.0, 16.0);
            ragdoll.Z += (float)GD.RandRange(-16.0, 16.0);
            ragdoll *= 2f;

            // A floor on the HORIZONTAL shove, which retail does not have and we need.
            //
            // An axe's Resource_Damage is ~20, the same order as retail's own +-16 jitter, so a real swing
            // can arrive with its horizontal component very nearly cancelled. Retail survives that: even a
            // gentle slide tips its tree, because the torque comes from friction against the prefab's
            // authored collider. Ours needs a real push, so a cancelled swing left the trunk standing
            // upright and fading out where it stood -- which reads worse than the instant-vanish this
            // replaces, and it happened at RANDOM, so a third of fellings looked broken.
            // Direction is still entirely the server's and the jitter's; only the length is guaranteed.
            var flat = new Vector3(ragdoll.X, 0f, ragdoll.Z);
            if (flat.Length() < MinTopplePush)
            {
                var dir = flat.LengthSquared() > 0.0001f ? flat.Normalized() : new Vector3(1f, 0f, 0f);
                ragdoll.X = dir.X * MinTopplePush;
                ragdoll.Z = dir.Z * MinTopplePush;
            }

            float sh = Mathf.Abs(rec.Xf.Basis.Scale.Y);
            var body = new ResourceDebris
            {
                Mass = 1f,                       // retail Rigidbody default -- the ragdoll magnitudes assume it
                LinearDamp = 1f, AngularDamp = 1f,
                Life = DebrisLifetime,
                CollisionLayer = 1u << 2,        // the debris bit (WheelDebris uses it): lands and rolls...
                CollisionMask = 1u << 0,         // ...against the world only, never shoving the player who felled it
                Transform = new Transform3D(rec.Xf.Basis, rec.Xf.Origin + rec.Xf.Basis.Y.Normalized() * (DebrisVerticalOffset * sh)),
            };
            foreach (var (mesh, mat) in parts)
                body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
            // As thick and as tall as the standing trunk -- but NOT at the standing trunk's local offset.
            // A placed tree's collider is centred 2.5*s up with a half-height of 4*s, so its bottom is
            // 1.5*s BELOW the placement origin: correct for a static trunk with buried roots, fatal for a
            // dynamic body. The gib spawned already interpenetrating the ground, and depenetration pinned
            // it -- it barely translated, never rotated at any shove magnitude or application point, and
            // fell asleep within a second. Sitting the cylinder's BASE on the origin is what lets it move.
            float halfHeight = 4f * sh;
            body.AddChild(new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = 0.5f * Mathf.Max(Mathf.Abs(rec.Xf.Basis.Scale.X), Mathf.Abs(rec.Xf.Basis.Scale.Z)), Height = halfHeight * 2f },
                Position = new Vector3(0f, halfHeight - DebrisVerticalOffset * sh, 0f),   // base at the tree's base, which the +offset spawn then lifts clear
            });
            AddChild(body);
            // Retail's AddForce is ForceMode.Force -- one fixed step's worth of acceleration, not an
            // instantaneous kick. Godot has no equivalent one-shot, so convert: impulse = force * dt at
            // Unity's 50 Hz default, which is the rate retail's numbers were tuned against.
            // Handed to the body rather than applied here (see ResourceDebris.PendingImpulse), and applied
            // 6 m UP THE TRUNK rather than centrally. That offset is the one deliberate deviation from
            // retail, and it is measured, not assumed: retail's AddForce is central and its tree still
            // topples, because the shove slides the trunk while friction at the base converts that into
            // torque -- behaviour that comes out of the prefab's own authored collider. With our stand-in
            // cylinder (invented for blocking bullets) a central impulse settles the trunk perfectly
            // upright and puts it to sleep, every time. Off-centre produces the torque directly.
            body.PendingImpulse = ragdoll * 0.02f;
            body.PendingOffset = new Vector3(0f, 6f * sh, 0f);
        }

        // ---- what the local player has learned about tree health (ResourceHealthEvent) ------------------
        //
        // NOT replicated state and deliberately not derived from anything: the server unicasts a health
        // number to whoever swung, and this is where it lands. So the map's trees are unknown until you hit
        // one, which is the truth -- a bar drawn from a guess would be worse than no bar.
        readonly Dictionary<int, (int Health, int Max)> _knownHealth = new();

        /// <summary>Record what the server said this instance has left. Called from the wire event.</summary>
        public void SetKnownHealth(int index, int health, int max)
        {
            if (index < 0 || max <= 0) return;
            _knownHealth[index] = (Mathf.Clamp(health, 0, max), max);
        }

        /// <summary>What we know of an instance's health, if we have ever hit it.</summary>
        public bool TryGetKnownHealth(int index, out int health, out int max)
        {
            if (_knownHealth.TryGetValue(index, out var v)) { health = v.Health; max = v.Max; return true; }
            health = 0; max = 0; return false;
        }

        // ---- the look-at tree bar ----------------------------------------------------------------------
        //
        // ONE billboard for the whole field, moved to whichever trunk is under the crosshair. A map has
        // ~1700 resources and exactly one can be focused, so a billboard per instance would be 1700 idle
        // SubViewports to draw one bar.
        InfoBillboard _info;
        int _infoIndex = -1;
        int _infoHp = -1, _infoMax = -1;   // what the panel is currently DRAWING, so an unchanged frame is free

        /// <summary>Test seam: the shared look-at billboard, or null before anything has been focused.</summary>
        public InfoBillboard DebugInfo => _info;
        /// <summary>Test seam: the instance the bar is currently on, or -1.</summary>
        public int InfoIndex => _infoIndex;

        /// <summary>Put the look-at panel on one resource instance (its name, and its health bar if this
        /// player has ever hit it). -1 hides it.</summary>
        public void ShowInfoFor(int index)
        {
            if (index < 0 || index >= _instances.Count || !_instances[index].Alive) { HideInfo(); return; }
            var rec = _instances[index];
            if (rec.Trunk == null) { HideInfo(); return; }   // only trees carry a trunk to hang it off

            if (_info == null)
            {
                _info = new InfoBillboard { TopLevel = true };   // TopLevel: the field's own transform must not drag it
                AddChild(_info);
            }
            if (_infoIndex != index)
            {
                _infoIndex = index;
                _info.SetName(DisplayLabel(rec.TypeName), Colors.White);   // the BAR is red; a red name too just reads as a warning
                _info.SetBar(1, 0f, InfoBillboard.FuelColor, false);
                _info.SetBar(2, 0f, InfoBillboard.FuelColor, false);
            }
            // Above the trunk's base, below the canopy, scaled with the instance so a sapling's bar is not
            // parked in the sky.
            float sh = Mathf.Abs(rec.Xf.Basis.Scale.Y);
            _info.GlobalPosition = rec.Trunk.GlobalPosition + new Vector3(0f, 3.2f * sh, 0f);

            if (TryGetKnownHealth(index, out int hp, out int max))
            {
                // Redrawn only when the NUMBER moves, not every frame: this runs from the look ray, so a
                // player standing still facing a tree would otherwise churn a string per frame forever.
                if (hp != _infoHp || max != _infoMax)
                {
                    _infoHp = hp; _infoMax = max;
                    _info.SetBar(0, max > 0 ? hp / (float)max : 0f, InfoBillboard.HealthColor);
                    _info.SetPrompt($"{hp} / {max}", Colors.White);
                }
            }
            else
            {
                _infoHp = _infoMax = -1;
                // Never seen it hit -- so we do not know, and saying "800/800" would be a guess that reads
                // as fact. The name alone is the honest panel until the first swing lands.
                _info.SetBar(0, 0f, InfoBillboard.HealthColor, false);
                _info.SetPrompt("", Colors.White);
            }
            _info.SetActive(true);
        }

        public void HideInfo()
        {
            if (_info == null) return;
            _info.SetActive(false);
            _infoIndex = -1;
        }

        /// <summary>"Birch_0" -> "Birch". The retail asset name is "Birch #1" (English.dat, baked verbatim
        /// into the harvest table); the trailing variant marker distinguishes gameplay-identical models, so
        /// it is noise on a health bar. Falls back to the type key when the table has no row.</summary>
        static string DisplayLabel(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "Resource";
            string label = ResourceHarvestTable.LabelFor(typeName);
            if (string.IsNullOrEmpty(label)) label = typeName.Replace('_', ' ');
            int hash = label.LastIndexOf('#');
            if (hash > 0) label = label.Substring(0, hash).TrimEnd();
            return label;
        }

        public void LoadResources(string activeHoliday)
        {
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            string manifest = dir + "resources.txt";
            if (!File.Exists(manifest)) { GD.Print("[resources] no resources.txt -- skipping"); return; }
            int total = 0, types = 0, treeCols = 0;
            foreach (var line in File.ReadAllLines(manifest))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 2 || !int.TryParse(sp[1], out int parts)) continue;
                string name = sp[0];
                string holiday = sp.Length >= 3 ? sp[2] : "NONE";   // Cane_00(candy cane)/Snow_Pile_00/Ornament_XMAS are CHRISTMAS-only
                if (holiday != "NONE" && holiday != activeHoliday) continue;   // out-of-season resource (same gate as the objects)
                bool isTree = name.StartsWith("Birch") || name.StartsWith("Maple") || name.StartsWith("Pine");   // only trees cast shadows
                string binPath = dir + name + ".bin";
                if (!File.Exists(binPath)) continue;
                var xf = ReadInstances(binPath);
                if (xf.Count == 0) continue;
                // the deterministic index space: instances register in manifest x .bin order on every peer
                var recs = new List<InstanceRec>(xf.Count);
                foreach (var t in xf)
                {
                    var rec = new InstanceRec { Xf = t, TypeName = name };
                    recs.Add(rec);
                    _instances.Add(rec);
                }
                if (isTree)   // MultiMesh has no colliders -> add a trunk cylinder per tree so trees BLOCK bullets/movement (master), tagged Wood
                {
                    // Where THIS type's instances start in the global load order. recs[] is per-type and
                    // restarts at 0 for every manifest line, so the loop counter below is a per-type index,
                    // NOT the wire id -- stamping it raw put every Maple and Pine trunk under a Birch's
                    // index, and a chop would then have felled some unrelated tree elsewhere on the map and
                    // paid out its drops. Only the first type in the manifest was ever right.
                    int baseIndex = _instances.Count - xf.Count;
                    for (int k = 0; k < xf.Count; k++)
                    {
                        var t = xf[k];
                        // part-0's mesh AABB is the WHOLE tree (incl. canopy) -> that gave a giant ~5m-radius cylinder
                        // floating at canopy height that missed the ground. Use a FIXED trunk (~0.5m radius, ~8m tall) at
                        // the base, scaled by the instance scale, on an ORTHONORMAL body (Jolt drops non-uniform-scaled shapes).
                        Vector3 sc = t.Basis.Scale;
                        float sr = Mathf.Max(Mathf.Abs(sc.X), Mathf.Abs(sc.Z)), sh = Mathf.Abs(sc.Y);
                        var body = new StaticBody3D { CollisionLayer = 1u << 0, Transform = new Transform3D(t.Basis.Orthonormalized(), t.Origin) };
                        body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Wood);
                        body.AddToGroup("tree");   // for the UG_TREECHECK raycast self-test
                        // The trunk has to know WHICH resource it is. The registry maps index -> body, but
                        // a raycast hands you the body, and without this the melee path can find a tree it
                        // cannot name -- so it could never tell the server which one was chopped.
                        body.SetMeta(ResourceIndexMeta, baseIndex + k);
                        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.5f * sr, Height = 8f * sh }, Position = new Vector3(0f, 2.5f * sh, 0f) });
                        AddChild(body);
                        recs[k].Trunk = body;
                        recs[k].TrunkLayer = body.CollisionLayer;
                        treeCols++;
                    }
                }
                if (VisualInstances)
                {
                    // Bucket instances into spatial CELLS so each chunk frustum-culls independently (behind the player) + distance-culls,
                    // instead of one map-wide MultiMesh that's never culled. Trees keep their shadows within range (master); props cull closer.
                    const float Cell = 64f;
                    float cullRange = isTree ? 320f : 180f;
                    var byCell = new Dictionary<(int, int), List<int>>();
                    for (int k = 0; k < xf.Count; k++)
                    {
                        var key = ((int)Mathf.Floor(xf[k].Origin.X / Cell), (int)Mathf.Floor(xf[k].Origin.Z / Cell));
                        if (!byCell.TryGetValue(key, out var cl)) { cl = new List<int>(); byCell[key] = cl; }
                        cl.Add(k);
                    }
                    for (int i = 0; i < parts; i++)
                    {
                        string objP = dir + name + "_" + i + ".obj";
                        if (!File.Exists(objP)) continue;
                        var mesh = ObjMesh.Load(objP);
                        if (mesh == null) continue;
                        var mat = MakeMat(dir + name + "_" + i + "_tex.png", !isTree);
                        // Keep the parts by TYPE so a felled tree can be rebuilt as a physics body. Retail
                        // does the same thing: with no dedicated Debris prefab it instantiates the tree's
                        // own model as the gib, so the thing that topples over IS the tree you were looking
                        // at, not a stand-in log.
                        if (!_partsOfType.TryGetValue(name, out var pl)) { pl = new List<(Mesh, Material)>(); _partsOfType[name] = pl; }
                        pl.Add((mesh, mat));
                        foreach (var kv in byCell)
                        {
                            var lst = kv.Value;
                            var mm = new MultiMesh { Mesh = mesh, TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = lst.Count };
                            for (int k = 0; k < lst.Count; k++)
                            {
                                mm.SetInstanceTransform(k, xf[lst[k]]);
                                recs[lst[k]].Slots.Add((mm, k));
                            }
                            AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat,
                                CastShadow = isTree ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                                VisibilityRangeEnd = cullRange, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled });
                        }
                    }
                }
                total += xf.Count; types++;
                GD.Print($"[resources] {name}: {xf.Count} x {parts} part(s)");
            }
            GD.Print($"[resources] {total} instances across {types} types (MultiMesh), {treeCols} tree trunk colliders");
        }

        static List<Transform3D> ReadInstances(string binPath)
        {
            var list = new List<Transform3D>();
            using var br = new BinaryReader(File.OpenRead(binPath));
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float ex = br.ReadSingle(), ey = br.ReadSingle(), ez = br.ReadSingle();
                float sx = br.ReadSingle(), sy = br.ReadSingle(), sz = br.ReadSingle();
                // identical to Main.BuildObjectsTest prop rotation (raw-mesh frame): Y(180-ey)*X(ex)*Z(-ez)
                var basis = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey))
                          * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(ex))
                          * new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(-ez));
                basis = basis.Scaled(new Vector3(sx, sy, sz));
                list.Add(new Transform3D(basis, new Vector3(px, py, -pz)));   // negate-Z position like every other placement
            }
            return list;
        }

        static StandardMaterial3D MakeMat(string texPath, bool unshaded)
        {
            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // leaves are double-sided billboards
                Roughness = 1f,
            };
            _ = unshaded;   // (kept for signature compat) resources are LIT + receive shadows per master; grass/flowers get up-normals instead
            if (File.Exists(texPath))
            {
                var img = new Image();
                if (img.Load(texPath) == Error.Ok)
                {
                    img.GenerateMipmaps();
                    mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                }
            }
            else mat.AlbedoColor = new Color(0.35f, 0.45f, 0.28f);   // leafy-green fallback
            return mat;
        }
    }
}
