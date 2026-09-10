using Godot;

namespace UnturnedGodot
{
    /// <summary>A thing that warms or cools whoever stands near it (strawberry 2026-09-10: "wire heat sources,
    /// that warm the player when in radius + LOS raycast (only when in range). also a cooling source, same
    /// thing, but cools the player").
    ///
    /// ONE node for both directions, with the sign on DeltaC. A separate CoolingSource class would be the same
    /// twenty lines with a minus, and the two would drift -- one would get a fix the other missed.
    ///
    /// Attach it as a child of whatever emits: a lit campfire, an oven that is actually cooking, a running
    /// freezer. Whoever owns it drives Active, because "is this fire lit" is the owner's business and not
    /// something the thermal system can work out by looking.</summary>
    public partial class ThermalSource : Node3D
    {
        /// <summary>Metres. Beyond this the source contributes exactly nothing -- see ThermalFalloff.</summary>
        [Export] public float Radius = 6f;

        /// <summary>Degrees C at the centre. POSITIVE warms, NEGATIVE cools.</summary>
        [Export] public float DeltaC = 25f;

        /// <summary>Off = contributes nothing at all. An unlit campfire is scenery.</summary>
        [Export] public bool Active = true;

        /// <summary>Metres. How far the source's OWN body extends, so the line-of-sight ray can tell "I hit the
        /// stove I am looking at" from "I hit a wall in front of it".
        ///
        /// This has to be declared rather than measured. ThermalField sees a Node3D in a group; it has no idea
        /// which collider belongs to the emitter, because the emitter is a child of whatever placed it. The
        /// owner knows its own size and nothing else does -- same reason Active is the owner's business.
        /// Guessed a flat 0.35 m first and the sunk-fire-pit case failed instantly: a 1.2 m body is entered
        /// 0.6 m from its centre, so the ray reported a wall and the source silently switched off.</summary>
        [Export] public float SelfRadius = 0.75f;

        public const string Group = "thermal_sources";

        public override void _Ready() => AddToGroup(Group);

        /// <summary>Convenience for the common case: hang a source on an existing node.</summary>
        public static ThermalSource AttachTo(Node3D host, float deltaC, float radius, bool active = true)
        {
            var s = new ThermalSource { DeltaC = deltaC, Radius = radius, Active = active };
            host.AddChild(s);
            return s;
        }
    }
}
