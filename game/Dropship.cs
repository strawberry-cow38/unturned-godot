using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// The airdrop plane, assembled from the parts the retail prefab actually has.
    ///
    /// Retail's Level/Dropship.prefab is not one model. It is a hull (Model_0), a single rotor blade
    /// (Model_1) instanced four times under a Rotors node, and three emissive nav lights -- red on the
    /// port wingtip, green on starboard, white on the tail. Merging those into one static mesh is what
    /// the generic object extractor does, and it costs the two things that read as "alive" at 450 m:
    /// the props turning and the lights burning.
    ///
    /// There is no albedo. The hull material's _MainTex is a 2x2 grey placeholder, so the aircraft is
    /// flat-shaded by design and the numbers below are read straight off the prefab rather than picked
    /// to look right. Parts and transforms come from tools/extract_dropship.py.
    /// </summary>
    public partial class Dropship : Node3D
    {
        /// <summary>Mean of the hull's four placeholder pixels (61,66,61 / 105 / 84 / 94).</summary>
        static readonly Color HullGrey = new(0.33725f, 0.34216f, 0.33725f);
        static readonly Color RotorGrey = new(0.39217f, 0.39217f, 0.39217f);

        /// <summary>Prefab-space engine mounts. All four blades share one basis -- 225 deg about Y at
        /// 1.5 scale -- which stands the disc across the fuselage axis, i.e. facing the way it flies.</summary>
        static readonly Vector3[] RotorMounts =
        {
            new(-8.754f, 4.730f, 1.007f),
            new(-4.770f, 4.954f, 0.852f),
            new( 4.770f, 4.954f, 0.852f),
            new( 8.754f, 4.730f, 1.007f),
        };

        static readonly Basis RotorBasis = new(
            new Vector3(-1.060661f, 0f, 1.060661f),
            new Vector3(0f, 1.5f, 0f),
            new Vector3(-1.060660f, 0f, -1.060661f));

        /// <summary>Nav lights, by prefab node. The meshes are authored in prefab space already, so
        /// they need no transform -- only their colour, which is also their emission.</summary>
        static readonly (string Part, Color Tint)[] Lights =
        {
            ("Dropship_light_0", new Color(0.74265f, 0.14198f, 0.17237f)),   // port, red
            ("Dropship_light_1", new Color(0.15300f, 0.62200f, 0.19100f)),   // starboard, green
            ("Dropship_light_2", new Color(0.78431f, 0.78431f, 0.78431f)),   // tail, white
        };

        /// <summary>True when the real hull loaded. False means the fallback block is flying, which a
        /// shot scene wants to know before it claims the model renders.</summary>
        public bool HasModel { get; private set; }

        public static Dropship Build()
        {
            var d = new Dropship();
            d.Assemble();
            return d;
        }

        // ObjMesh.Load reads the real filesystem, not res:// -- passing the res:// path straight in
        // silently loads nothing, which shows up as an empty sky rather than an error.
        static ArrayMesh Part(string name) =>
            ObjMesh.Load(ProjectSettings.GlobalizePath($"res://content/objects/{name}.obj"));

        void Assemble()
        {
            var hull = Part("Dropship");
            if (hull == null)
            {
                // A stripped checkout still flies something, so the airdrop reads as an event rather
                // than throwing on a missing asset.
                AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(26f, 7f, 19f) },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = HullGrey },
                });
                return;
            }
            HasModel = true;
            AddChild(new MeshInstance3D { Mesh = hull, MaterialOverride = Flat(HullGrey) });

            // The "blade" is not a blade. It is Model_1, a flat 16-gon at a constant radius of 1.282 --
            // retail's motion-BLUR disc, the thing InteractableVehicle.PropellerModel swaps in for a
            // spinning prop. The dropship prefab has no PropellerModel driving it, so the disc is simply
            // always on and never turns. Nothing here spins it either: a rotationally symmetric 16-gon
            // in a flat colour looks identical at every angle, so a spin would be a per-frame cost that
            // renders no differently from standing still.
            var disc = Part("Dropship_rotor");
            if (disc != null)
            {
                // Alpha comes from the prefab (_Color a=1.0) rather than a number picked to look nice.
                // The material is authored Fade/ZWrite-off but fully opaque, so the disc reads as a solid
                // grey plate -- which is what retail draws.
                var mat = Flat(RotorGrey);
                foreach (var mount in RotorMounts)
                {
                    var hub = new Node3D { Transform = new Transform3D(RotorBasis, mount) };
                    hub.AddChild(new MeshInstance3D { Mesh = disc, MaterialOverride = mat });
                    AddChild(hub);
                }
            }

            foreach (var (part, tint) in Lights)
            {
                var m = Part(part);
                if (m == null) continue;
                var mat = Flat(tint);
                mat.EmissionEnabled = true;
                mat.Emission = tint;
                mat.EmissionEnergyMultiplier = 4f;
                AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat });
            }
        }

        // CullMode disabled because ObjMesh keeps Unity's vertex order; roughness 1 because the prefab
        // sets Glossiness 0 and a specular sheen on a flat-grey hull reads as wet plastic.
        static StandardMaterial3D Flat(Color c) => new()
        {
            AlbedoColor = c,
            Roughness = 1f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        /// <summary>Points the aircraft down its velocity.
        ///
        /// The prefab is authored nose-along-+Y, up-along-+Z (the tail fin is the only geometry above
        /// Z=3.5, and it sits at Y=-10 on the centreline), so it needs a correction to fly level.
        ///
        /// Retail's is LookRotation(velocity) * Euler(-90, 180, 0), and copying those angles across is
        /// the trap: Unity's LookRotation aims +Z at the target, Godot's LookAt aims -Z, so the 180 term
        /// is already paid by the difference in convention. Applying it again rolls the aircraft about
        /// its own flight axis -- it flew inverted, fin down, with the red and green nav lights on the
        /// wrong wingtips. The -90 about local X is the whole correction here.</summary>
        public void FaceVelocity(Vector3 velocity)
        {
            if (velocity.X * velocity.X + velocity.Z * velocity.Z <= 0.01f) return;
            LookAt(GlobalPosition + new Vector3(velocity.X, 0f, velocity.Z), Vector3.Up);
            RotateObjectLocal(Vector3.Right, Mathf.DegToRad(-90f));
        }
    }
}
