using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // WHICH PROPS ARE "SMART", IN ONE PLACE.
    //
    // A handful of PEI props are not scenery: a clock's hands turn, a television toggles, a wardrobe opens, a
    // desk lamp lights a room. Each already has its own class and its own prop->behaviour predicate
    // (LampLight.KindFor, TVDevice.IsDeviceProp, HeartMonitor.IsMonitorProp...). What did NOT exist was a single
    // answer to "is this prop smart, and how" -- WorldBuilder asked the question as a chain of nine inline name
    // tests, and the map editor never asked it at all, so a prop placed in the editor was a dead mesh.
    //
    // strawberry: "make all 'smart' props (tvs, clocks, lights, doors, etc etc) functional in the editor and
    // editor play mode". This file is the shared table that makes that possible without a second copy of the
    // selection logic drifting away from the first. WorldBuilder keeps its own PLACEMENT (world-space, cull-aware,
    // batch-aware); the editor gets AttachEditor below (local-space, gizmo-following). Only the DECISION is
    // shared -- but the decision is the part that silently rots, because a prop that gets the WRONG device reads
    // as a deliberate choice rather than a bug.
    //
    // The batching deny-list is derived from the same table (see WorldBuilder.Batchable). It used to be a
    // hand-maintained list of nine names next to a runtime PushError apologising for the day someone forgot to
    // extend it. A device carves sub-meshes off its own prop's node, so a batched smart prop cannot have one;
    // deriving the list means adding a device to this enum excludes it from batching in the same edit.
    [System.Flags]
    public enum SmartKind
    {
        None        = 0,
        Streetlight = 1 << 0,   // StreetLight: lens split off the housing + downward sodium spot + LightTap
        IndoorLamp  = 1 << 1,   // LampLight: ceiling strip / floor shade / desk bulb
        Lighthouse  = 1 << 2,   // LighthouseBeam: the sweeping cone at the gallery ring
        Toaster     = 1 << 3,   // Toaster: shoot it, bread pops
        Clock       = 1 << 4,   // ClockDevice: hands carved off the dial and spun to game time
        Monitor     = 1 << 5,   // HeartMonitor: the ward's patient monitors
        Screen      = 1 << 6,   // TVDevice: televisions, CRT + flat monitors, the laptop
        Signal      = 1 << 7,   // TrafficLight: two independently-timed heads per mast
        Doors       = 1 << 8,   // ObjectDoor: one openable leaf per doors.txt row
        Water       = 1 << 9,   // FluidContainer: tower / hydrant / well / kitchen sink
    }

    public static class SmartProps
    {
        /// <summary>Every behaviour this prop carries. `hasDoors` is the door catalogue's ContainsKey -- passed in
        /// rather than loaded here because both callers already hold the parsed catalogue and re-reading doors.txt
        /// per prop would be the sort of quiet cost that only shows up on a 4000-prop map.</summary>
        public static SmartKind KindsFor(string name, System.Func<string, bool> hasDoors = null)
        {
            if (string.IsNullOrEmpty(name)) return SmartKind.None;
            var k = SmartKind.None;
            if (name == "Street_Light_0") k |= SmartKind.Streetlight;
            if (name == "Traffic_Light_0") k |= SmartKind.Signal;
            if (name == "Lighthouse_0") k |= SmartKind.Lighthouse;
            if (name == "Toaster_0") k |= SmartKind.Toaster;
            if (name == "Clock_0") k |= SmartKind.Clock;
            if (LampLight.KindFor(name) != LampLight.Kind.Generic) k |= SmartKind.IndoorLamp;
            if (TVDevice.IsDeviceProp(name)) k |= SmartKind.Screen;
            if (HeartMonitor.IsMonitorProp(name)) k |= SmartKind.Monitor;
            if (WaterSourceFor(name) != WaterSource.None) k |= SmartKind.Water;
            if (hasDoors != null && hasDoors(name)) k |= SmartKind.Doors;
            return k;
        }

        /// <summary>Does a placement of this prop need its OWN mesh node? Every smart prop does: the device carves
        /// its lens/screen/hands/leaf off that instance's mesh, which a shared MultiMesh has nowhere to put.</summary>
        public static bool NeedsOwnNode(string name, System.Func<string, bool> hasDoors = null)
            => KindsFor(name, hasDoors) != SmartKind.None;

        enum WaterSource { None, Tower, Hydrant, Well, Sink }
        static WaterSource WaterSourceFor(string name) => name switch
        {
            "Tower_Water_0" => WaterSource.Tower,
            "Fire_Hydrant_0" => WaterSource.Hydrant,
            "Well_0" => WaterSource.Well,
            _ => WorldBuilder.IsSinkProp(name) ? WaterSource.Sink : WaterSource.None,
        };

        /// <summary>A one-line description of what a placed prop will DO, for the editor's palette + selection
        /// readout. Empty for ordinary scenery -- the absence is the signal.</summary>
        public static string Describe(string name, System.Func<string, bool> hasDoors = null)
        {
            var k = KindsFor(name, hasDoors);
            if (k == SmartKind.None) return "";
            var parts = new List<string>();
            if (k.HasFlag(SmartKind.Streetlight)) parts.Add("street light");
            if (k.HasFlag(SmartKind.IndoorLamp)) parts.Add(LampLight.IsToggle(LampLight.KindFor(name)) ? "lamp (F toggles)" : "ceiling light");
            if (k.HasFlag(SmartKind.Lighthouse)) parts.Add("sweeping beam");
            if (k.HasFlag(SmartKind.Toaster)) parts.Add("toaster");
            if (k.HasFlag(SmartKind.Clock)) parts.Add("clock");
            if (k.HasFlag(SmartKind.Monitor)) parts.Add("patient monitor");
            if (k.HasFlag(SmartKind.Screen)) parts.Add($"{TVDevice.KindFor(name)} (F toggles)");
            if (k.HasFlag(SmartKind.Signal)) parts.Add("traffic signal");
            if (k.HasFlag(SmartKind.Doors)) parts.Add("openable (F)");
            if (k.HasFlag(SmartKind.Water)) parts.Add("water source");
            return string.Join(" · ", parts);
        }

        /// <summary>What AttachEditor actually built, so the caller can tag the prop's collider and a test can
        /// assert the set against KindsFor without reaching into the scene tree.</summary>
        public sealed class Attached
        {
            public StreetLight Street;
            public LampLight Lamp;
            public LighthouseBeam Beacon;
            public Toaster Toaster;
            public ClockDevice Clock;
            public HeartMonitor Monitor;
            public TVDevice Screen;
            public FluidContainer Water;
            public LightTap Tap;      // rides Street/Signal; not its own SmartKind, so it is not in Kinds below
            public MeshInstance3D Lens;
            public readonly List<TrafficLight> Signals = new();
            public readonly List<ObjectDoor> Doors = new();

            /// <summary>The kinds actually attached -- compared against KindsFor to prove the editor honours the
            /// same table the world loader does, rather than merely intending to.</summary>
            public SmartKind Kinds
            {
                get
                {
                    var k = SmartKind.None;
                    if (Street != null) k |= SmartKind.Streetlight;
                    if (Lamp != null) k |= SmartKind.IndoorLamp;
                    if (Beacon != null) k |= SmartKind.Lighthouse;
                    if (Toaster != null) k |= SmartKind.Toaster;
                    if (Clock != null) k |= SmartKind.Clock;
                    if (Monitor != null) k |= SmartKind.Monitor;
                    if (Screen != null) k |= SmartKind.Screen;
                    if (Signals.Count > 0) k |= SmartKind.Signal;
                    if (Doors.Count > 0) k |= SmartKind.Doors;
                    if (Water != null) k |= SmartKind.Water;
                    return k;
                }
            }
        }

        public const string YawOnlyMeta = "smart_yawonly";   // marks a child whose frame is yaw-only, not the prop's full basis

        /// <summary>Build every device this prop carries, as children of an editor-placed prop root.
        ///
        /// The editor's convention is the INVERSE of WorldBuilder's and that is the whole reason this is a separate
        /// function rather than a call into the loader. WorldBuilder puts each prop's nodes in WORLD space under a
        /// root at the origin, because nothing ever moves again. The editor's root CARRIES the placement and the
        /// gizmo drags it, so every device has to be local to that root or it stays behind when the prop is moved
        /// -- which is why the factories' TopLevel is cleared here. (TopLevel is what makes their Position a world
        /// position; with a root that already applies the placement, the same number is the local offset and the
        /// result is identical -- for free, and it follows the gizmo.)
        ///
        /// `worldPos`/`yawDeg` are passed rather than read off the root: Place() builds the whole prop before
        /// adding it to the tree, so GlobalPosition is not meaningful yet.</summary>
        public static Attached AttachEditor(Node3D root, string name, MeshInstance3D mainMi, Material mat,
                                            Vector3 worldPos, float yawDeg, string dir,
                                            IReadOnlyDictionary<string, List<WorldBuilder.DoorCatalogEntry>> doorCatalog,
                                            Dictionary<string, ArrayMesh> meshCache = null)
        {
            var a = new Attached();
            if (root == null || mainMi == null) return a;
            var mesh = mainMi.Mesh as ArrayMesh;
            if (mesh == null) return a;
            var basis = root.Basis;

            // STREETLIGHT. The bulb is real geometry inside the head, drawn with the pole's grey material until it
            // is split onto its own instance; StreetLight swaps that instance to an emissive material when lit.
            if (name == "Street_Light_0")
            {
                var (body, lens) = ObjMesh.SplitLens(mesh);
                if (body != null && lens != null)
                {
                    mainMi.Mesh = body;
                    a.Lens = new MeshInstance3D { Mesh = lens, MaterialOverride = mat };
                    root.AddChild(a.Lens);
                }
                // Emit from where the lens actually IS, not a hand-measured point -- the same lesson the world
                // path learned when a typed (0, 2.35, 6.48) put the cone half a metre off the end of the arm.
                var lampLocal = a.Lens?.Mesh != null ? a.Lens.Mesh.GetAabb().GetCenter() : new Vector3(0f, 2.35f, 6.48f);
                a.Street = StreetLight.Make(lampLocal, Mathf.Max(4f, (basis * lampLocal).Y), a.Lens);
                a.Street.TopLevel = false;   // local to the prop root: follows the gizmo
                root.AddChild(a.Street);
            }

            // INDOOR LIGHTS: ceiling strip (Light_0/1), floor shade (Lamp_1), desk bulb (Lamp_0). LampLight splits
            // the warm diffuser faces off the housing so only the diffuser glows.
            var lampKind = LampLight.KindFor(name);
            if (lampKind != LampLight.Kind.Generic)
            {
                a.Lamp = LampLight.Make(mesh.GetAabb().GetCenter(), mainMi, lampKind);
                a.Lamp.TopLevel = false;
                root.AddChild(a.Lamp);
            }

            if (name == "Lighthouse_0")
            {
                // The gallery ring, ~4.5 m under the roof. Derived off the real placement like the world path does
                // -- the tower is placed pitched, so the mesh's own "top" is not the world top.
                var ab = mesh.GetAabb();
                float topY = float.MinValue;
                for (int i = 0; i < 8; i++) topY = Mathf.Max(topY, (basis * ab.GetEndpoint(i)).Y);
                var centre = basis * ab.GetCenter();
                a.Beacon = LighthouseBeam.Make(new Vector3(centre.X, topY - 4.5f, centre.Z));
                a.Beacon.TopLevel = false;
                root.AddChild(a.Beacon);
            }

            // These three ride mainMi's own transform, which under an editor root is identity -- so they need no
            // TopLevel handling at all and inherit the gizmo for free.
            if (name == "Toaster_0") { a.Toaster = Toaster.Make(mainMi); root.AddChild(a.Toaster); }
            if (name == "Clock_0") { a.Clock = ClockDevice.Make(mainMi, mat, 0f); if (a.Clock != null) root.AddChild(a.Clock); }
            if (HeartMonitor.IsMonitorProp(name)) { a.Monitor = HeartMonitor.Make(mainMi, GD.Randf() < HeartMonitor.AliveChance); root.AddChild(a.Monitor); }
            if (TVDevice.IsDeviceProp(name)) { a.Screen = TVDevice.Make(mainMi, name); root.AddChild(a.Screen); }
            if (name == "Well_0") WellShaft.Make(mainMi, WellShaft.WallColor((mat as StandardMaterial3D)?.AlbedoTexture));   // the bottomless shaft disc, a child of the ring's node

            // TRAFFIC SIGNALS: one mast, two heads, each on its own dumb timer hashed off world position + head
            // index. The hash reads the placement position, so two signals dropped in different places differ --
            // and moving one afterwards re-rolls it, which is the editor being an editor.
            if (name == "Traffic_Light_0")
            {
                var (heads, tlBody) = ObjMesh.SplitTrafficLensesPerHead(mesh);
                if (heads != null && tlBody != null)
                {
                    mainMi.Mesh = tlBody;
                    for (int hd = 0; hd < heads.Length; hd++)
                    {
                        var lensMis = new MeshInstance3D[3];
                        for (int li = 0; li < 3; li++)
                        {
                            if (heads[hd][li] == null) continue;
                            lensMis[li] = new MeshInstance3D { Mesh = heads[hd][li], MaterialOverride = mat };
                            root.AddChild(lensMis[li]);
                        }
                        var tl = TrafficLight.Make(worldPos, yawDeg, lensMis[0], lensMis[1], lensMis[2], hd);
                        tl.TopLevel = false;
                        tl.Position = Vector3.Zero;   // Make put the world position here; the root carries it now
                        root.AddChild(tl);
                        a.Signals.Add(tl);
                    }
                }
            }

            // OPENABLE LEAVES. One ObjectDoor per doors.txt row, grouped so a wardrobe's two leaves swing together.
            if (doorCatalog != null && doorCatalog.TryGetValue(name, out var leaves))
            {
                foreach (var cfg in leaves)
                {
                    ArrayMesh leaf = null;
                    if (meshCache == null || !meshCache.TryGetValue(cfg.MeshFile, out leaf))
                    {
                        leaf = ObjMesh.Load(dir + cfg.MeshFile);
                        if (meshCache != null) meshCache[cfg.MeshFile] = leaf;
                    }
                    if (leaf == null) continue;
                    string curveBase = cfg.MeshFile.EndsWith("_door.obj") ? cfg.MeshFile.Substring(0, cfg.MeshFile.Length - "_door.obj".Length) : name;
                    // Transform3D.Identity, not the placement: the leaf is a CHILD of the root that carries it.
                    a.Doors.Add(ObjectDoor.Spawn(root, Transform3D.Identity, cfg.Pivot, cfg.Axis, cfg.AngleDeg, cfg.DurationSec,
                                                 leaf, mat, startOpen: cfg.DefaultOpen,
                                                 openCurve: WorldBuilder.LoadDoorCurve(dir, curveBase, "open"),
                                                 closeCurve: WorldBuilder.LoadDoorCurve(dir, curveBase, "close"),
                                                 soundName: cfg.Sound));
                }
                if (a.Doors.Count > 1) foreach (var d in a.Doors) d.SetGroup(a.Doors);
            }

            // THE POWER TAP. A streetlight or signal carries a wire-able port on its base -- an INPUT while it is
            // intact, swapping to a live OUTPUT stub once the fixture is smashed. Attached here for the same
            // reason the light itself is: a lamp you can place but cannot wire is only half the prop, and the
            // difference would show up as "the editor's streetlights don't work with generators".
            if (a.Street != null)
                a.Tap = LightTap.Attach(root, Vector3.Zero, Basis.Identity, LightTap.LightKind.Streetlight,
                                        WorldBuilder.TapInLocal, WorldBuilder.TapOutLocal);
            else if (a.Signals.Count > 0)
                a.Tap = LightTap.Attach(root, Vector3.Zero, Basis.Identity, LightTap.LightKind.Traffic,
                                        WorldBuilder.TapInLocal, WorldBuilder.TapOutLocal);

            // WATER. The one family that cannot simply ride the root: a fluid node's port anchors are expressed in
            // a YAW-ONLY frame (WorldBuilder draws the prop with the full pitched basis but gives the fluid node
            // yaw alone), so parenting it to a root that carries the 270 pitch would tip every port on its side.
            // Kept TopLevel and re-synced from the root instead -- see Resync, which the editor calls whenever a
            // placed prop moves.
            var ws = WaterSourceFor(name);
            if (ws != WaterSource.None)
            {
                a.Water = ws switch
                {
                    WaterSource.Tower => WaterTowerSource.Make(),
                    WaterSource.Hydrant => FireHydrantSource.Make(),
                    WaterSource.Well => WellSource.Make(),
                    _ => SinkSource.Make(basis, yawDeg),
                };
                if (a.Water != null)
                {
                    a.Water.TopLevel = true;
                    a.Water.SetMeta(YawOnlyMeta, true);
                    root.AddChild(a.Water);
                    Resync(root, worldPos, yawDeg);
                }
            }
            return a;
        }

        /// <summary>Re-seat the yaw-only children after the prop moves. Everything else is an ordinary child of the
        /// root and needs nothing; this exists solely for the fluid nodes described above.</summary>
        public static void Resync(Node3D root, Vector3 worldPos, float yawDeg)
        {
            if (root == null) return;
            foreach (var c in root.GetChildren())
                if (c is Node3D n && n.HasMeta(YawOnlyMeta))
                {
                    n.Position = worldPos;
                    n.RotationDegrees = new Vector3(0f, yawDeg, 0f);
                }
        }

        /// <summary>Same as the overload, reading the placement off a root that is already in the tree.</summary>
        public static void Resync(Node3D root)
        {
            if (root == null || !root.IsInsideTree()) return;
            var b = root.GlobalBasis;
            Resync(root, root.GlobalPosition, Mathf.RadToDeg(Mathf.Atan2(-b.X.Z, b.X.X)));
        }

        /// <summary>Tag the prop's collider so the player's look-ray and bullets resolve back to the device -- the
        /// same metas WorldBuilder writes, so PlayerController needs no editor-specific branch to interact with an
        /// editor-placed prop in playtest.</summary>
        public static void TagBody(StaticBody3D body, Attached a)
        {
            if (body == null || a == null) return;
            if (a.Street != null) body.SetMeta(StreetLight.HitMeta, a.Street);
            if (a.Toaster != null) body.SetMeta(Toaster.HitMeta, a.Toaster);
            if (a.Screen != null) body.SetMeta(TVDevice.HitMeta, a.Screen);
            if (a.Monitor != null) body.SetMeta(HeartMonitor.HitMeta, a.Monitor);
            if (a.Lamp != null && LampLight.IsToggle(a.Lamp.LampKind)) body.SetMeta(LampLight.LookMeta, a.Lamp);
            if (a.Doors.Count > 0) body.SetMeta("objectdoor", a.Doors[0]);
            if (a.Signals.Count > 0)
            {
                var arr = new Godot.Collections.Array();
                foreach (var s in a.Signals) arr.Add(s);
                body.SetMeta(TrafficLight.HitMeta, arr);
            }
        }
    }
}
