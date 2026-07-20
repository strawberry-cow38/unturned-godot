using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    // Destructible props (rubble), in-engine contract (what L0's engine-free replication test can't cover):
    // 1) a destructible's collider carries the index meta that the server hit resolution (GodotWorldRay) reads
    //    to route bullet/melee damage, and a raycast at it resolves that index;
    // 2) DestructibleField.SetAlive(false) HIDES the mesh AND drops the collider so the same ray now misses
    //    (the client render + server collision result of a break), and SetAlive(true) restores both.
    // Uses a hand-built prop (StaticBody3D + MeshInstance3D) so it needs no PEI map data.
    public class DestructibleBreakContract : GameTest
    {
        public override string Name => "destructible.break_hides_and_untargets";

        int RayHitIndex(Vector3 from, Vector3 to)
        {
            var q = new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = (1u << 0) | (1u << 6) };
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return -2;   // ray missed everything
            if (hit["collider"].As<GodotObject>() is StaticBody3D body && body.HasMeta(DestructibleField.MetaKey))
                return (int)body.GetMeta(DestructibleField.MetaKey);
            return -1;   // hit something, not a destructible
        }

        public override IEnumerable<Step> Run()
        {
            var propPos = new Vector3(0f, 0f, 0f);
            // a small destructible prop on the see-through look layer (1<<6), like a sign/billboard placement
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1f, 2f, 1f) }, Position = propPos + Vector3.Up };
            World.AddChild(mi);
            var body = new StaticBody3D { CollisionLayer = 1u << 6, Position = propPos + Vector3.Up };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 2f, 1f) } });
            World.AddChild(body);

            var field = new DestructibleField();
            field.SetCount(1);
            body.SetMeta(DestructibleField.MetaKey, 0);
            field.Register(0, body, new[] { mi }, maxHealth: 50f, resetTicks: 100);
            yield return Ticks(1);

            var from = propPos + new Vector3(0f, 1f, 5f);
            var to = propPos + new Vector3(0f, 1f, -5f);
            T.Check("intact: a forward ray at the prop resolves its destructible index (combat's meta read)", RayHitIndex(from, to) == 0);
            T.Check("intact: mesh visible", mi.Visible);

            field.SetAlive(0, false);   // the break
            yield return Ticks(1);
            T.Check("broken: mesh hidden", !mi.Visible);
            T.Check("broken: collider dropped -> the same ray now misses (no more damage/collision)", RayHitIndex(from, to) == -2);
            T.Check("broken: field reports the slot dead", !field.IsAlive(0));

            field.SetAlive(0, true);    // the respawn
            yield return Ticks(1);
            T.Check("respawned: mesh visible again", mi.Visible);
            T.Check("respawned: collider back -> ray resolves the index again", RayHitIndex(from, to) == 0);
            T.Check("respawned: field reports the slot alive", field.IsAlive(0));
        }
    }

    // Regression: WorldBuilder calls Register DURING the placement scan and SetCount AFTER it (the final
    // destructible count isn't known until the scan finishes). An earlier version sized the array only in
    // SetCount, so every inline Register no-oped against an empty array -> NOTHING on the dedicated server
    // was destructible (health metadata never bound). This locks the register-before-setcount order.
    public class DestructibleRegisterBeforeSetCount : GameTest
    {
        public override string Name => "destructible.register_before_setcount";

        public override IEnumerable<Step> Run()
        {
            var field = new DestructibleField();
            var m0 = new MeshInstance3D { Mesh = new BoxMesh() }; World.AddChild(m0);
            var b0 = new StaticBody3D { CollisionLayer = 1u << 6 }; World.AddChild(b0);
            var m2 = new MeshInstance3D { Mesh = new BoxMesh() }; World.AddChild(m2);
            var b2 = new StaticBody3D { CollisionLayer = 1u << 6 }; World.AddChild(b2);

            // the WorldBuilder order: Register the built props FIRST (during the scan)...
            field.Register(0, b0, new[] { m0 }, maxHealth: 275f, resetTicks: 15000);
            field.Register(2, b2, new[] { m2 }, maxHealth: 50f, resetTicks: 3000);
            // ...THEN SetCount with the final total (4 -> index 3 is a reserved-but-unbuilt holiday tail slot)
            field.SetCount(4);
            yield return Ticks(1);

            T.Check("index space covers the reserved total", field.InstanceCount == 4);
            T.Check("registered-before-setcount slot 0 kept its health (275)", field.MaxHealth(0) == 275f);
            T.Check("registered-before-setcount slot 2 kept its health (50)", field.MaxHealth(2) == 50f);
            T.Check("unregistered slot 1 is indestructible (health 0)", field.MaxHealth(1) == 0f);
            T.Check("reserved tail slot 3 is indestructible (health 0)", field.MaxHealth(3) == 0f);

            field.SetAlive(0, false);
            yield return Ticks(1);
            T.Check("a registered slot actually breaks (mesh hidden)", !m0.Visible);
            T.Check("a registered slot's collider drops", b0.CollisionLayer == 0u);
        }
    }
}
