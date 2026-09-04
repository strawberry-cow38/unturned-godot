using Godot;

namespace UnturnedGodot
{
    /// <summary>ROOF MAP for the rain (strawberry 2026-09-04: "when going into a building, it turns off rain altogether. it
    /// shouldnt. each building's roof should kill rain that reaches it"). A grid of physics rays cast straight DOWN over a
    /// 64 m square around the player, refreshed a few times a second (spread over frames), records the TOPMOST solid
    /// height at every cell -- roofs, canopies, car bodies, the ground itself -- into a float texture. The rain streak
    /// shader hides any drop BELOW that surface at its XZ (it has passed through a roof); the wet-surface / terrain
    /// shaders keep roofed ground dry. The rain never switches off: it just cannot get under things.
    ///
    /// CPU rays rather than a depth camera on purpose: a SubViewport's colour output passes through the world
    /// environment's tonemap and an sRGB 8-bit buffer, which mangles an encoded height; a ray grid is exact,
    /// orientation-free, and ~100 rays a frame.
    ///
    /// Globals (RainSystem3D.EnsureGlobals): `rain_roof` (Rf texture, world Y of the topmost hit, -1e6 = nothing) and
    /// `rain_roof_rect` (centre X, centre Z, half-size, unused; z = 0 -> no map).</summary>
    public sealed partial class RainRoofMap : Node3D
    {
        public const float Half = 32f;         // half-size of the square the map covers (m)
        public const int Res = 64;             // cells per side -> 1 m cells
        public const float Above = 80f;        // rays start this far above the followed camera
        public const float RayLen = 200f;
        public const int RaysPerFrame = 128;   // 4096 rays / 128 = a full refresh every 32 frames (~0.5 s)
        public const uint SolidMask = (1u << 0) | (1u << 5) | (1u << 6);   // world / props / small props -- what the look ray calls solid
        public const float NoHit = -1e6f;

        public Camera3D Follow;
        public Rid[] Exclude = System.Array.Empty<Rid>();
        public static bool Enabled = true;

        Image _img; ImageTexture _tex; float[] _next; int _cursor; Vector3 _centre, _nextCentre; bool _wired;
        PhysicsRayQueryParameters3D _q;

        public override void _Ready()
        {
            _img = Image.CreateEmpty(Res, Res, false, Image.Format.Rf);
            _img.Fill(new Color(NoHit, 0f, 0f, 1f));
            _tex = ImageTexture.CreateFromImage(_img);
            _next = new float[Res * Res];
            for (int i = 0; i < _next.Length; i++) _next[i] = NoHit;
            _q = new PhysicsRayQueryParameters3D { CollisionMask = SolidMask, HitBackFaces = true, Exclude = new Godot.Collections.Array<Rid>(Exclude) };
            GD.Print($"[rainroof] {Res}x{Res} ray grid, {Half * 2:0} m square, {RaysPerFrame} rays/frame");
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!Enabled || Follow == null || !IsInstanceValid(Follow)) return;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return;
            if (_cursor == 0) _nextCentre = Follow.GlobalPosition;   // a refresh pass is centred where it started; the rect follows the FINISHED pass
            float cell = 2f * Half / Res;
            for (int n = 0; n < RaysPerFrame && _cursor < _next.Length; n++, _cursor++)
            {
                int ix = _cursor % Res, iz = _cursor / Res;
                float x = _nextCentre.X - Half + (ix + 0.5f) * cell, z = _nextCentre.Z - Half + (iz + 0.5f) * cell;
                _q.From = new Vector3(x, _nextCentre.Y + Above, z);
                _q.To = _q.From + Vector3.Down * RayLen;
                var hit = space.IntersectRay(_q);
                _next[_cursor] = hit.Count > 0 ? ((Vector3)hit["position"]).Y : NoHit;
            }
            if (_cursor >= _next.Length)
            {
                _cursor = 0;
                _centre = _nextCentre;
                for (int iz = 0; iz < Res; iz++) for (int ix = 0; ix < Res; ix++) _img.SetPixel(ix, iz, new Color(_next[iz * Res + ix], 0f, 0f, 1f));
                _tex.Update(_img);
                if (!_wired) { _wired = true; RenderingServer.GlobalShaderParameterSet("rain_roof", _tex); }
                RenderingServer.GlobalShaderParameterSet("rain_roof_rect", new Vector4(_centre.X, _centre.Z, Half, 0f));
            }
        }

        /// <summary>The map's topmost height at a world XZ, decoded the way the shaders do (float.MinValue outside / no hit).</summary>
        public float RoofYAt(Vector3 world)
        {
            float u = (world.X - _centre.X) / (2f * Half) + 0.5f, v = (world.Z - _centre.Z) / (2f * Half) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return float.MinValue;
            float h = _img.GetPixel(Mathf.Clamp((int)(u * Res), 0, Res - 1), Mathf.Clamp((int)(v * Res), 0, Res - 1)).R;
            return h <= NoHit * 0.5f ? float.MinValue : h;
        }

        /// <summary>SELF-CHECK (UG_ROOFCHECK=1): the map against fresh rays at +-X / +-Z offsets -- a flipped axis shows here, not in play.</summary>
        public void DebugCheck(PhysicsDirectSpaceState3D space)
        {
            if (Follow == null || !_wired) return;
            var fp = Follow.GlobalPosition;
            foreach (var off in new[] { new Vector3(8f, 0f, 0f), new Vector3(-8f, 0f, 0f), new Vector3(0f, 0f, 8f), new Vector3(0f, 0f, -8f), Vector3.Zero })
            {
                var p = fp + off;
                var from = new Vector3(p.X, _centre.Y + Above, p.Z);
                var hit = space.IntersectRay(new PhysicsRayQueryParameters3D { From = from, To = from + Vector3.Down * RayLen, CollisionMask = SolidMask, HitBackFaces = true });
                float rayY = hit.Count > 0 ? ((Vector3)hit["position"]).Y : float.MinValue;
                float mapY = RoofYAt(p);
                GD.Print($"[rainroof] check off=({off.X:0},{off.Z:0}) ray {rayY:0.00} map {mapY:0.00} {(Mathf.Abs(rayY - mapY) < 1.2f ? "OK" : "MISMATCH")}");
            }
        }
    }
}
