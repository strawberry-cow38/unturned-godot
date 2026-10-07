using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>
    /// A place to fill a bottle BY HAND with clean water: every kitchen sink and every bathtub on the map (strawberry
    /// 2026-10-07: "allow filling containers w clean water from sinks and bathtubs").
    ///
    /// Not a FluidContainer and nothing to do with the fluid net. SinkSource is the hose side of a sink and exists only
    /// where fluid is simulated (never on a dedicated server, see WorldBuilder); a tap has to exist on the SERVER too,
    /// because the server is who fills the bottle -- the inventory is server-owned, and a fill the client makes on its own
    /// copy is put back by the next echo. So a tap is just WHERE the prop is and how big it is, registered in every mode,
    /// and both sides ask the same two questions of it: is the player aiming into it (client), and is the player close
    /// enough to it (server).
    ///
    /// Keyed off the prop's own mesh bounds and placement, not a collider: a sink counter is also a loot container, and
    /// adding a third interactable to the look-ray's arbitration would change what F does at every sink on the map.
    /// </summary>
    public partial class WaterTap : Node3D
    {
        /// <summary>How far from the prop's bounds a player may stand and still fill (server side). Generous on
        /// purpose -- the server only has the player's position, not where they are looking.</summary>
        public const float Reach = 2.5f;
        /// <summary>How far outside the prop's bounds the look-point may land and still count as aiming into it. The
        /// look-ray stops ON the surface, so a point exactly on a face needs a little slack.</summary>
        public const float AimSlack = 0.15f;

        public static bool IsTapProp(string name) => WorldBuilder.IsSinkProp(name) || name == "Tub_0";
        public static string LabelFor(string name) => name == "Tub_0" ? "Bathtub" : "Sink";

        public string DisplayName = "Sink";
        /// <summary>The prop's own mesh bounds, in mesh space; the node's transform IS the prop's placement.</summary>
        public Aabb LocalBounds;

        static readonly HashSet<WaterTap> _all = new();
        public static int Count => _all.Count;

        /// <summary>Running = the town's water is on. A bathtub or sink tap is the mains; after toggleGlobalWater it is
        /// dry. (A sink's 5 L basin still serves a hose after a shutoff -- that is SinkSource, the fluid-net side.)</summary>
        public static bool Running => FluidNet.GlobalWater;

        public static WaterTap Make(string propName, Transform3D placement, Aabb meshBounds)
            => new WaterTap { Name = "WaterTap", DisplayName = LabelFor(propName), Transform = placement, LocalBounds = meshBounds };

        public override void _EnterTree() => _all.Add(this);
        public override void _ExitTree() => _all.Remove(this);

        Vector3 ToLocal(Vector3 world) => GlobalTransform.AffineInverse() * world;

        /// <summary>Is this world point inside the prop (plus the slack)? The look-point test.</summary>
        public bool Contains(Vector3 world)
        {
            var p = ToLocal(world);
            var b = LocalBounds.Grow(AimSlack / Mathf.Max(0.01f, GlobalTransform.Basis.Scale.X));
            return b.HasPoint(p);
        }

        /// <summary>Distance from a world point to the nearest point of the prop's bounds -- 0 inside it.</summary>
        public float DistanceTo(Vector3 world)
        {
            var p = ToLocal(world);
            var lo = LocalBounds.Position; var hi = LocalBounds.End;
            var c = new Vector3(Mathf.Clamp(p.X, lo.X, hi.X), Mathf.Clamp(p.Y, lo.Y, hi.Y), Mathf.Clamp(p.Z, lo.Z, hi.Z));
            return (GlobalTransform * c).DistanceTo(world);
        }

        /// <summary>The tap the player's look-point is in, or null.</summary>
        public static WaterTap AimedAt(Vector3 lookPoint)
        {
            foreach (var t in _all) if (IsInstanceValid(t) && t.IsInsideTree() && t.Contains(lookPoint)) return t;
            return null;
        }

        /// <summary>The SERVER's question: is there a running tap within <see cref="Reach"/> of this player position?</summary>
        public static bool RunningNear(Vector3 pos)
        {
            if (!Running) return false;
            foreach (var t in _all) if (IsInstanceValid(t) && t.IsInsideTree() && t.DistanceTo(pos) <= Reach) return true;
            return false;
        }
    }
}
