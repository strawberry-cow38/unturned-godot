using Godot;

namespace UnturnedGodot
{
    /// <summary>The jagged glass left clinging in a frame after a pane goes. strawberry_cow: "broken glass
    /// preset, places the glass shard props in the corners of an opening."
    ///
    /// NOT A NEW STATE. The opening already knows it is broken -- WallOpening.GlassBroken is written when a
    /// pane shatters and survives save, load and rebuild, and HasGlass is already `Glazed && !GlassBroken`.
    /// Until now that state rendered as an empty hole, so a shot-out window and a window that was never
    /// glazed looked identical. This is the missing half of a feature, not a parallel one -- which is why
    /// authoring it in the editor and shooting one out in play produce the same thing, and why it persists
    /// and duplicates without a line of new save code.
    ///
    /// THE MESHES ARE RETAIL AND MEASURED, NOT GUESSED. Glass_0.obj and Glass_1.obj are 2 x 2 plates 0.1
    /// thick whose outlines both run from (-1,-1) to (1,-1) along one edge and then break upward in a ragged
    /// line -- Glass_0 keeps a tall piece on one side, Glass_1 a low band across. They are already remnants
    /// clinging to an edge, so they need placing, not modelling.</summary>
    public static class GlassShards
    {
        /// <summary>Half the opening, so opposite corners' shards meet in the middle at most and a whole
        /// pane is never reconstructed out of four quarters.</summary>
        const float CornerFrac = 0.5f;

        /// <summary>Sunk into the frame by this much so the ragged edge starts inside the reveal rather than
        /// floating a hair proud of it.</summary>
        const float Bite = 0.02f;

        static ArrayMesh _a, _b;

        static ArrayMesh Mesh(int which)
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            if (which == 0) return _a ??= ObjMesh.Load(dir + "Glass_0.obj");
            return _b ??= ObjMesh.Load(dir + "Glass_1.obj");
        }

        /// <summary>Shards for one opening, parented at the opening's CENTRE in wall space -- the same
        /// anchor GlassPane uses, so a broken window sits exactly where the pane it replaces did.
        ///
        /// Returns null when the meshes are missing rather than throwing: a content file that failed to
        /// extract should cost the shards, not the building.</summary>
        public static Node3D Build(Vector2 size, Color tint, int seed)
        {
            if (size.X <= 0.01f || size.Y <= 0.01f) return null;

            var root = new Node3D { Name = "GlassShards" };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = ShardTint(tint),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                // Unshaded and double-sided for the same reasons the shatter particles are: a flat glass
                // colour reads as glass where a lit one reads as dirty plastic, and a remnant is a plate you
                // can walk round, so a back face culled to nothing would make it vanish from one side.
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };

            // One shard per corner. Each is rotated about the wall normal so its SOLID edge lies along the
            // frame and its ragged edge breaks inward -- glass tears away from the middle of a pane and
            // stays gripped at the edges, so a shard with its straight edge facing inward reads as a
            // shape someone cut, not glass that broke.
            var rng = new RandomNumberGenerator { Seed = (ulong)seed };
            float hw = size.X * 0.5f, hh = size.Y * 0.5f;

            for (int c = 0; c < 4; c++)
            {
                var mesh = Mesh(rng.RandiRange(0, 1));
                if (mesh == null) return null;

                float spin = c * 90f;
                // Along its own solid edge each shard spans a corner-sized bite of the frame; the mesh is
                // 2 units wide, so the scale is the bite over 2.
                float w = (c % 2 == 0 ? size.X : size.Y) * CornerFrac;
                float h = (c % 2 == 0 ? size.Y : size.X) * CornerFrac;

                var mi = new MeshInstance3D
                {
                    Mesh = mesh,
                    MaterialOverride = mat,
                    // A remnant is scenery: it must not catch a bullet, a footstep or a shadow. The pane it
                    // replaces owned the collider, and the pane is gone -- a broken window is a hole you can
                    // shoot and walk through, which is the whole point of having broken it.
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };

                // Stand the plate up (it is modelled lying in XZ), then spin it to its corner.
                var basis = new Basis(Vector3.Back, Mathf.DegToRad(spin))
                          * new Basis(Vector3.Right, Mathf.DegToRad(-90f))
                          * Basis.FromScale(new Vector3(w * 0.5f, 1f, h * 0.5f));

                // Its solid edge sits on the frame edge for this corner, offset outward by the half-size it
                // was NOT scaled along, then bitten in slightly.
                var offset = c switch
                {
                    0 => new Vector3(-hw + w * 0.5f, -hh + Bite, 0f),      // bottom-left, solid edge down
                    1 => new Vector3(hw - Bite, -hh + w * 0.5f, 0f),       // bottom-right, solid edge right
                    2 => new Vector3(hw - w * 0.5f, hh - Bite, 0f),        // top-right, solid edge up
                    _ => new Vector3(-hw + Bite, hh - w * 0.5f, 0f),       // top-left, solid edge left
                };

                mi.Transform = new Transform3D(basis, offset);
                root.AddChild(mi);
            }
            return root;
        }

        /// <summary>The pane hue lightened toward white and taken to 50% alpha -- the same treatment
        /// GlassPane gives its shatter particles, so the remnants left behind and the fragments that flew
        /// off are visibly the same glass.</summary>
        static Color ShardTint(Color hue)
            => new Color(Mathf.Lerp(hue.R, 1f, 0.45f), Mathf.Lerp(hue.G, 1f, 0.45f),
                         Mathf.Lerp(hue.B, 1f, 0.45f), 0.5f);
    }
}
