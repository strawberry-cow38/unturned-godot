using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // TEMP repro (strawberry 2026-07-21: "props not getting destroyed in SP"): the EXISTING
    // destructible.break_hides_and_untargets test calls field.SetAlive(false) DIRECTLY -- it never fires a
    // real bullet through ServerCombat. This exercises the WHOLE untested server chain the loopback uses:
    //   OnFire -> StepBullets -> WorldRay (finds the prop collider + reads its index meta) -> case 3
    //   -> DamageObject -> ServerDestructibles break -> bitmap flip -> (mirror) field.SetAlive.
    // If props break here but not in-game, the bug is loopback-specific wiring; if they don't break here,
    // the bug is in this server chain.
    public class NetDestructibleFireBreak : GameTest
    {
        public override string Name => "net.shell_destructible_fire_break";

        // the server's world-hit resolver, byte-identical to DedicatedServer/MpLoopback.GodotWorldRay
        bool WorldRay(UnityEngine.Vector3 from, UnityEngine.Vector3 to, out UnityEngine.Vector3 point, out int destructibleIndex)
        {
            point = default; destructibleIndex = -1;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.x, from.y, from.z), new Vector3(to.x, to.y, to.z), (1u << 0) | (1u << 6));
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            var p = (Vector3)hit["position"];
            point = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            if (hit["collider"].As<GodotObject>() is StaticBody3D body && body.HasMeta(DestructibleField.MetaKey))
                destructibleIndex = (int)body.GetMeta(DestructibleField.MetaKey);
            return true;
        }

        public override IEnumerable<Step> Run()
        {
            // a small destructible prop 8 m dead ahead (-Z) of the shooter, on the see-through look layer (1<<6)
            var propPos = new Vector3(0f, 1f, -8f);
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1f, 2f, 1f) }, Position = propPos };
            World.AddChild(mi);
            var body = new StaticBody3D { CollisionLayer = 1u << 6, Position = propPos };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 2f, 1f) } });
            World.AddChild(body);
            body.SetMeta(DestructibleField.MetaKey, 0);

            var field = new DestructibleField();
            field.Register(0, body, new[] { mi }, maxHealth: 25f, resetTicks: 1000);   // 25 hp = one eaglefire ObjectDamage
            field.SetCount(1);

            // the server replication + combat stack, wired the way NetWorldHost wires it
            var players = new PlayerReplication();
            var combatState = new PlayerCombatReplication();
            var zombies = new ZombieReplication();
            var projectiles = new ProjectileReplication();
            var ids = new NetIdMinter();
            var destRepl = new DestructibleReplication();
            var destHost = new ServerDestructibles(destRepl, _ => { });
            long t0 = 1000;
            destHost.ServerInit(1, t0);
            destHost.SetMeta(0, 25f, 1000);

            var combat = new ServerCombat(players, combatState, zombies, projectiles, ids, _ => { }, (_, __) => { });
            combat.WorldRay = WorldRay;
            combat.DamageObject = (idx, amt, tick) => destHost.DamageObject(idx, amt, tick);

            // spawn the shooter (owner id 1) at origin, armed
            var shooterPos = new Vector3(0f, 0f, 0f);
            players.ServerSpawn(ids.Mint(), 1, new UnityEngine.Vector3(shooterPos.X, shooterPos.Y, shooterPos.Z), t0);
            combatState.ServerAdd(1, new UnityEngine.Vector3(shooterPos.X, shooterPos.Y, shooterPos.Z), 30, t0);

            yield return Ticks(1);   // let the collider register in the physics space before the ray

            T.Check("prop intact before firing", destHost.Health(0) == 25f && destRepl.IsAlive(0));

            // sanity: a direct WorldRay at the prop resolves its index (proves the collider + meta are queryable)
            UnityEngine.Vector3 origin = new UnityEngine.Vector3(0f, 1.6f, 0f);
            UnityEngine.Vector3 aim = (new UnityEngine.Vector3(0f, 1f, -8f) - origin).normalized;
            bool rayHit = WorldRay(origin, origin + aim * 10f, out _, out int rayIdx);
            T.Check($"WorldRay resolves the prop's destructible index (hit={rayHit}, idx={rayIdx})", rayHit && rayIdx == 0);

            // FIRE through the real command path (tick well past FirerateTicks so the rate gate passes)
            combat.OnFire(1, new FireCommand { Seq = 1, Origin = origin, Dir = aim }, t0);
            T.Check($"shot accepted by OnFire (accepted={combat.Diag.ShotsAccepted}, rejRange={combat.Diag.ShotsRejectedRange}, rejRate={combat.Diag.ShotsRejectedRate}, rejAmmo={combat.Diag.ShotsRejectedAmmo})",
                    combat.Diag.ShotsAccepted == 1);

            // step the sim so the bullet flies to the prop and resolves its hit
            for (int k = 1; k <= 25; k++) combat.Step(t0 + k);

            T.Check($"bullet hit world/prop (hitsWorld={combat.Diag.BulletHitsWorld})", combat.Diag.BulletHitsWorld >= 1);
            T.Check($"SERVER broke the prop: health 0 (was 25, ObjectDamage 25) -- got {destHost.Health(0)}", destHost.Health(0) == 0f);
            T.Check("SERVER bitmap marks the prop dead", !destRepl.IsAlive(0));

            // the render mirror (DestructibleNetSync does this each tick): bitmap -> field
            if (field.IsAlive(0) != destRepl.IsAlive(0)) field.SetAlive(0, destRepl.IsAlive(0));
            yield return Ticks(1);
            T.Check("prop mesh hidden after the break (client render)", !mi.Visible);
        }
    }

    // Regression for strawberry's SP bug (2026-07-21): the LOCAL player's gunfire (PlayerController.StepBullets, the SP/
    // loopback path where NetFire is unwired -> non-cosmetic bullets) must break a destructible prop. Before the fix the
    // world/prop hit branch only spawned a surface impact -- no DamageObject -- so props were indestructible in SP while
    // the server-authoritative break path only ran in real MP. The fix routes a local prop hit to the authoritative
    // ServerDestructibles via the NetDamageObject seam (wired to the in-process server in the loopback). This drives a
    // REAL PlayerController.Fire() at a prop and asserts the seam broke it.
    public class LocalFireBreaksDestructible : GameTest
    {
        public override string Name => "destructible.local_fire_breaks_prop";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false };
            p.LoadGun("res://content/eaglefire.dat");
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            p.Rotation = new Vector3(0f, 0f, 0f);   // face -Z

            // a destructible prop 8 m ahead at eye height, on the look layer (1<<6), meta-tagged like a real placement
            var propPos = new Vector3(0f, 1.6f, -8f);
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2f, 2f, 2f) }, Position = propPos };
            World.AddChild(mi);
            var body = new StaticBody3D { CollisionLayer = 1u << 6, Position = propPos };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2f, 2f, 2f) } });
            World.AddChild(body);
            body.SetMeta(DestructibleField.MetaKey, 0);

            var field = new DestructibleField();
            field.Register(0, body, new[] { mi }, maxHealth: 25f, resetTicks: 1000);
            field.SetCount(1);

            // the authoritative destructible system the loopback owns; the seam routes the LOCAL hit here
            var destRepl = new DestructibleReplication();
            var destHost = new ServerDestructibles(destRepl, _ => { });
            destHost.ServerInit(1, 0);
            destHost.SetMeta(0, 25f, 1000);
            p.NetDamageObject = (idx, dmg) => destHost.DamageObject(idx, dmg, 0);   // MpLoopback wires this to Server.DestructibleHost

            T.Check("prop intact before firing", destHost.Health(0) == 25f && destRepl.IsAlive(0));

            // fire until the equip completes + a bullet lands (loop Fire() -- the viewmodel equip gate takes ~1.6 s)
            bool broke = false;
            for (int i = 0; i < 200 && !broke; i++)
            {
                p.Fire();
                yield return Ticks(1);
                if (field.IsAlive(0) != destRepl.IsAlive(0)) field.SetAlive(0, destRepl.IsAlive(0));   // the DestructibleNetSync mirror
                if (!destRepl.IsAlive(0)) broke = true;
            }

            T.Check($"LOCAL fire broke the prop via NetDamageObject (health {destHost.Health(0)})", destHost.Health(0) == 0f && !destRepl.IsAlive(0));
            T.Check("prop mesh hidden after the local-fire break (client render)", !mi.Visible);
            T.Check("prop collider dropped (untargetable)", body.CollisionLayer == 0u);
        }
    }
}
