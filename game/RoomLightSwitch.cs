using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot
{
    /// <summary>A room's light switch (astraclaw's Light_Switch_0, strawberry round 12: "light switches should function to
    /// switch on/off the ceiling lights within a room"): F while looking at it presses it, flipping its rocker -- and its
    /// room's ceiling lights with it, and only those: the lamps its room's electrical record names (RoomElectrical.Lights,
    /// the generator's: never a table lamp, never the next room's).
    ///
    /// TWO-WAY (strawberry: "add support for two switches controlling one rooms light"): a room by two doorways has a switch
    /// at each, every press flips that switch's OWN rocker, and the lights are on while an odd number of the room's rockers
    /// are down (RoomElectrical.LightsOn) -- so either switch works the light from wherever the other was left.
    ///
    /// The rocker's pose is the art's convention, kept in three constants so a revised convention is re-pointed here and
    /// nowhere else: the rocker turns about its own X axis at RockerPivot (native, the OBJ's own frame: Z up, front +Y), to
    /// RockerDownDegrees with its BOTTOM pressed in -- down, which with one switch is on (strawberry: "down pressed should be
    /// on, up pressed should be off") -- and to RockerUpDegrees with its top pressed in. Built from the handoff's two
    /// components (Base, Rocker) rather than the whole OBJ, so the rocker can move; the node's frame is the native one under
    /// the same stand-up basis every prop is placed with (EditorObjects.Upright), so the pivot and the turn are the art's.
    ///
    /// The state is the RECORD's (RoomElectrical.Down), not this node's: the record is what the layout saves, so a switch
    /// pressed and the map saved comes back pressed.</summary>
    public partial class RoomLightSwitch : StaticBody3D
    {
        /// <summary>astraclaw's rocker convention (notes/household_electrical_handoff.md, revision 979c422c6): pivot native
        /// (0, 0.046, 0) -- Godot (0, 0, -0.046) -- and local X -8 degrees with the bottom pressed in, +8 with the top.</summary>
        public static readonly Vector3 RockerPivot = new Vector3(0f, 0.046f, 0f);
        public const float RockerDownDegrees = -8f, RockerUpDegrees = 8f;

        MeshInstance3D _base, _rocker;
        Node3D _pivot;
        RoomElectrical _record;
        int _index = -1;   // this switch's place in its record's Switches (and Down)
        readonly List<LampLight> _lamps = new();
        /// <summary>The switches sharing a record (a two-way pair), so a press shows on every lamp and each rocker stays its own.</summary>
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

        /// <summary>Tie this switch to its room's record (as its switch `index` there) and the lamps it names, and show the
        /// record's state on them.</summary>
        public void Bind(RoomElectrical record, int index, IEnumerable<LampLight> lamps, IEnumerable<RoomLightSwitch> siblings)
        {
            _record = record;
            _index = index;
            _lamps.Clear(); _lamps.AddRange(lamps);
            _siblings.Clear(); _siblings.AddRange(siblings);
            Apply();
        }

        /// <summary>Its room's lights are on (the record's two-way rule).</summary>
        public bool On => _record?.LightsOn ?? true;
        /// <summary>Its own rocker is down (bottom pressed in).</summary>
        public bool Down => _record != null && _index >= 0 && _index < _record.Down.Count ? _record.Down[_index] : true;
        public IReadOnlyList<LampLight> Lamps => _lamps;
        /// <summary>The rocker's turn about its pivot, in degrees: RockerDownDegrees down, RockerUpDegrees up.</summary>
        public float RockerAngle => _pivot != null ? Mathf.RadToDeg(_pivot.Rotation.X) : 0f;

        /// <summary>F pressed while looking at it: flip its rocker, and so the room's lights.</summary>
        public void Press()
        {
            if (_record == null || _index < 0) return;
            _record.Flip(_index);
            Apply();
            Log.Print($"[light-switch] {(Down ? "down" : "up")}: {_lamps.Count} ceiling light(s) {(On ? "on" : "off")}");
        }

        void Apply()
        {
            foreach (var l in _lamps) if (IsInstanceValid(l)) l.SetSwitched(On);
            ApplyRocker();
            foreach (var s in _siblings) if (s != this && IsInstanceValid(s)) s.ApplyRocker();
        }

        void ApplyRocker()
        {
            if (_pivot != null) _pivot.Rotation = new Vector3(Mathf.DegToRad(Down ? RockerDownDegrees : RockerUpDegrees), 0f, 0f);
        }

        /// <summary>The white look-at outline, as an elevator's floor button shows it.</summary>
        public void SetLookFocused(bool on)
        {
            foreach (var mi in new[] { _base, _rocker })
                if (mi != null && IsInstanceValid(mi)) mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
        }
    }
}
