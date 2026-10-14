using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    /// <summary>A room's light switch (astraclaw's Light_Switch_0, strawberry round 12: "light switches should function to
    /// switch on/off the ceiling lights within a room"): F while looking at it flips its room's ceiling lights, and only
    /// those -- the lamps its room's electrical record names (RoomElectrical.Lights, the generator's: never a table lamp,
    /// never the next room's) -- and the rocker with them.
    ///
    /// Built from the handoff's two components rather than the whole OBJ, so the rocker can move: the base at the fixture's
    /// origin, the rocker on its own pivot (native (0, 0.055, 0)) turned about its X axis, +8 degrees on (the complete OBJ's
    /// pose: the bottom of the rocker out) and -8 off. The node's frame is the OBJ's native one (Z up, front +Y) under the
    /// same stand-up basis every prop is placed with (EditorObjects.Upright), so the pivot and the turn are the art's own.
    ///
    /// The state is the RECORD's (RoomElectrical.LightsOn), not this node's: the record is what the layout saves, so a
    /// switch flipped and the map saved comes back flipped. Several switches on one record all show it.</summary>
    public partial class RoomLightSwitch : StaticBody3D
    {
        public const float RockerDegrees = 8f;
        /// <summary>The rocker's pivot in the fixture's native frame (notes/household_electrical_manifest.json).</summary>
        public static readonly Vector3 RockerPivot = new Vector3(0f, 0.055f, 0f);

        MeshInstance3D _base, _rocker;
        Node3D _pivot;
        RoomElectrical _record;
        readonly List<LampLight> _lamps = new();
        /// <summary>The switches sharing a record, so one press turns every rocker on it.</summary>
        readonly List<RoomLightSwitch> _siblings = new();

        public static RoomLightSwitch Make(Mesh baseMesh, Mesh rockerMesh, Material material)
        {
            // the small-prop look layer the player's look-ray reads (1 << 6), as the grid-power box's interaction body
            var s = new RoomLightSwitch { Name = "LightSwitch", CollisionLayer = 1u << 6, CollisionMask = 0 };
            s._base = new MeshInstance3D { Name = "Base", Mesh = baseMesh, MaterialOverride = material };
            s._pivot = new Node3D { Name = "RockerPivot", Position = RockerPivot };
            s._rocker = new MeshInstance3D { Name = "Rocker", Mesh = rockerMesh, MaterialOverride = material };
            s.AddChild(s._base);
            s.AddChild(s._pivot);
            s._pivot.AddChild(s._rocker);
            // the plate, a little proud of it so the look-ray takes it before the wall behind (native: 0.32 x 0.12 x 0.54)
            s.AddChild(new CollisionShape3D { Position = new Vector3(0f, 0.06f, 0f), Shape = new BoxShape3D { Size = new Vector3(0.34f, 0.12f, 0.56f) } });
            s.ApplyRocker();
            return s;
        }

        /// <summary>Tie this switch to its room's record and the lamps it names, and show the record's state on them.</summary>
        public void Bind(RoomElectrical record, IEnumerable<LampLight> lamps, IEnumerable<RoomLightSwitch> siblings)
        {
            _record = record;
            _lamps.Clear(); _lamps.AddRange(lamps);
            _siblings.Clear(); _siblings.AddRange(siblings);
            Apply();
        }

        public bool On => _record?.LightsOn ?? true;
        public IReadOnlyList<LampLight> Lamps => _lamps;
        /// <summary>The rocker's turn about its pivot, in degrees: +RockerDegrees on, -RockerDegrees off.</summary>
        public float RockerAngle => _pivot != null ? Mathf.RadToDeg(_pivot.Rotation.X) : 0f;

        /// <summary>F pressed while looking at it: flip the room's lights.</summary>
        public void Press()
        {
            if (_record == null) return;
            _record.LightsOn = !_record.LightsOn;
            Apply();
            foreach (var s in _siblings) if (s != this && IsInstanceValid(s)) s.ApplyRocker();
            Log.Print($"[light-switch] {(On ? "on" : "off")}: {_lamps.Count} ceiling light(s)");
        }

        void Apply()
        {
            foreach (var l in _lamps) if (IsInstanceValid(l)) l.SetSwitched(On);
            ApplyRocker();
        }

        void ApplyRocker()
        {
            if (_pivot != null) _pivot.Rotation = new Vector3(Mathf.DegToRad(On ? RockerDegrees : -RockerDegrees), 0f, 0f);
        }

        /// <summary>The white look-at outline, as an elevator's floor button shows it.</summary>
        public void SetLookFocused(bool on)
        {
            foreach (var mi in new[] { _base, _rocker })
                if (mi != null && IsInstanceValid(mi)) mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
        }
    }
}
