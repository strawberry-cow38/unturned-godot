using Godot;

namespace UnturnedGodot
{
    // A blocky low-poly humanoid (head/torso/arms/legs as boxes) built feet-at-origin. Matches Unturned's
    // angular character style, and its box layout mirrors the per-limb HITBOX model the server uses for
    // location-based damage (see NetServer.Hitscan zones). Honest stand-in for the full modular-skinned
    // ripped character (Phase 2 to assemble); the hitbox/zone structure is the real gameplay part.
    public static class Humanoid
    {
        // vertical zone bands (feet=0), shared with the server hit-reg:
        public const float HeadMinY = 1.45f;   // above -> HEAD (headshot)
        public const float TorsoMinY = 0.78f;   // 0.78..1.45 -> TORSO ; below -> LEGS
        public const float TopY = 1.8f;
        public const float Radius = 0.42f;      // hit cylinder radius

        public static Node3D Build(Color skin, Color shirt, Color pants)
        {
            var root = new Node3D();
            void Box(Vector3 center, Vector3 size, Color c)
            {
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = size },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = c },
                    Position = center,
                });
            }
            Box(new Vector3(0f, 1.62f, 0f), new Vector3(0.32f, 0.34f, 0.32f), skin);     // head
            Box(new Vector3(0f, 1.12f, 0f), new Vector3(0.50f, 0.66f, 0.28f), shirt);    // torso
            Box(new Vector3(-0.36f, 1.12f, 0f), new Vector3(0.16f, 0.62f, 0.22f), shirt); // left arm
            Box(new Vector3(0.36f, 1.12f, 0f), new Vector3(0.16f, 0.62f, 0.22f), shirt);  // right arm
            Box(new Vector3(-0.13f, 0.42f, 0f), new Vector3(0.20f, 0.84f, 0.24f), pants); // left leg
            Box(new Vector3(0.13f, 0.42f, 0f), new Vector3(0.20f, 0.84f, 0.24f), pants);  // right leg
            return root;
        }
    }
}
