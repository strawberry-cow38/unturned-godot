using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WALLBANGING (strawberry 2026-08-21: "implement wallbanging. for the water plane, and for props marked 'thin'
    // (we'll do a pass on it later) projectile hits surface, loses x velocity and damage, hits behind").
    //
    // The subject is not "a bullet went through something" -- it is that penetration is OPT-IN and COSTS the round.
    // So the load-bearing check here is the CONTROL: an identical wall without the marker must still eat the shot.
    // Without it every assertion below is also satisfied by "bullets now pass through everything", which is a much
    // worse bug than the one being fixed and would read as a pass.
    public sealed class WallbangTests : GameTest
    {
        public override string Name => "gun.wallbang";
        public override double TimeoutSimSeconds => 120;

        const float Range = 30f;   // well inside the eaglefire's 113 m falloff start, so distance costs nothing here

        static StaticBody3D Slab(Node w, float z, uint layer, string meta, Variant val)
        {
            var b = new StaticBody3D { CollisionLayer = layer, Position = new Vector3(0f, 1.2f, z) };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(6f, 4f, 0.3f) } });
            if (meta != null) b.SetMeta(meta, val);
            w.AddChild(b);
            return b;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 0f, 0f));
            yield return Ticks(10);

            var d = new TargetDummy { MaxHealth = 100000f, RespawnSeconds = 999f };
            World.AddChild(d);
            d.GlobalPosition = new Vector3(0f, 0f, -Range);
            yield return Ticks(5);

            float torsoY = (Humanoid.TorsoMinY + Humanoid.HeadMinY) * 0.5f;

            // WARM-UP, and it is not optional. The viewmodel equip gate swallows Fire() for ~1.6 s (~80 ticks)
            // after LoadGun, and a single Fire() before that returns having done nothing -- which reads as "the
            // shot missed" and made the very first baseline check report 0 dmg while every LATER shot landed
            // fine. Burn rounds until one actually connects, so every measured shot below starts from a gun that
            // is genuinely ready. destructible.local_fire_breaks_prop loops Fire() for the same reason.
            {
                float h0 = d.Health;
                p.ForceAim(true);
                for (int i = 0; i < 200 && Mathf.IsEqualApprox(d.Health, h0); i++) { p.Fire(); yield return Ticks(1); }
                T.Check($"warm-up: the gun is past its equip gate and landing rounds ({h0 - d.Health:0.##} dmg)", d.Health < h0);
            }

            // One shot, aimed the same way every time (drop-compensated + ADS re-asserted immediately before
            // firing -- both traps are documented in gun.damage_falloff and both were re-learned there the hard way).
            IEnumerable<Step> Shoot(System.Action<float> record, System.Action<bool> hit)
            {
                float eye = p.EyesWorld.Y;
                float v = p.Gun.MuzzleVelocity, gg = 9.81f * p.Gun.GravityMultiplier;
                float drop = 0.5f * gg * (Range / v) * (Range / v);
                float pitch = Mathf.RadToDeg(Mathf.Atan2((torsoY + drop) - eye, Range));
                p.RotationDegrees = Vector3.Zero;
                p.DebugSetPitch(pitch);
                p.ForceAim(true);
                yield return Ticks(30);
                float before = d.Health;
                p.Fire();
                for (int i = 0; i < 120 && Mathf.IsEqualApprox(d.Health, before); i++) yield return Ticks(1);
                bool landed = d.Health < before;
                hit(landed);
                // Normalise the hit ZONE out, exactly as gun.damage_falloff does: a head hit is 2x and would read
                // as "penetration cost nothing", a leg hit is 0.6x and would read as a bigger loss than there was.
                float zmul = d.LastZone == TargetDummy.HitZone.Head ? Humanoid.HeadMult
                           : d.LastZone == TargetDummy.HitZone.Torso ? Humanoid.TorsoMult : Humanoid.LegMult;
                record(landed ? d.LastDamage / zmul : 0f);
            }

            // ---- 1. CLEAR LINE. The rig's own baseline: if this misses, every comparison below is meaningless.
            float clear = 0f; bool clearHit = false;
            foreach (var s in Shoot(x => clear = x, h => clearHit = h)) yield return s;
            T.Check($"baseline: a clear shot hits the dummy ({clear:0.##} dmg)", clearHit && clear > 1f);

            // ---- 2. THE CONTROL, FIRST, so a broken control cannot be explained away by later results. An
            // UNTAGGED slab is ordinary world geometry and must still stop the round dead.
            var solid = Slab(World, -Range * 0.5f, 1u << 0, null, default);
            yield return Ticks(5);
            float blocked = 0f; bool blockedHit = false;
            foreach (var s in Shoot(x => blocked = x, h => blockedHit = h)) yield return s;
            T.Check($"control: an UNTAGGED wall still eats the shot ({(blockedHit ? "dummy was hit" : "dummy untouched")})", !blockedHit);

            // ---- 3. THIN PROP. Same slab, same place, now carrying the marker -> the round arrives behind it.
            solid.SetMeta(PlayerController.ThinMeta, true);
            yield return Ticks(5);
            float thin = 0f; bool thinHit = false;
            foreach (var s in Shoot(x => thin = x, h => thinHit = h)) yield return s;
            T.Check($"a THIN-marked wall is wallbanged ({thin:0.##} dmg through it)", thinHit && thin > 1f);
            T.Check($"...and it cost the round damage ({clear:0.##} -> {thin:0.##}, want ~{clear * 0.70f:0.##})",
                thin < clear - 0.5f && Mathf.Abs(thin - clear * 0.70f) < clear * 0.08f);
            solid.QueueFree();
            yield return Ticks(5);

            // ---- 4. WATER. The real ocean body is a StaticBody3D on bit 9 carrying SurfMeta = Surf.Water
            // (Terrain.cs, the bullets-only splash collider). This reproduces exactly that shape, because what
            // PierceCost keys on is the META, not on the body being Terrain's.
            var water = Slab(World, -Range * 0.5f, 1u << 9, PlayerController.SurfMeta, (int)PlayerController.Surf.Water);
            yield return Ticks(5);
            float wet = 0f; bool wetHit = false;
            foreach (var s in Shoot(x => wet = x, h => wetHit = h)) yield return s;
            T.Check($"the water plane is wallbanged ({wet:0.##} dmg through it)", wetHit && wet > 1f);
            T.Check($"...and water costs MORE than a thin prop ({wet:0.##} < {thin:0.##})", wet < thin);
            water.QueueFree();
            yield return Ticks(5);

            // ---- 5. THE CAP. Three thin slabs: two are wallbanged, the third is not, so a round cannot tunnel a
            // whole building. Asserted as "stopped", which is the behaviour MaxPierce exists to produce.
            for (int k = 1; k <= 3; k++) Slab(World, -Range * 0.25f * k, 1u << 0, PlayerController.ThinMeta, true);
            yield return Ticks(5);
            bool cappedHit = false;
            foreach (var s in Shoot(_ => { }, h => cappedHit = h)) yield return s;
            T.Check("MaxPierce caps it: three thin walls stop the round, two would not", !cappedHit);
        }
    }
}
