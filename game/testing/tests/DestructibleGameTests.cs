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

    // The BATCHED destructible path (PropBatcher). The tests above hand-build a MeshInstance3D per prop, which
    // is the path a batched prop does NOT take -- they cannot observe this code at all, so passing them says
    // nothing here. A batched prop has no node: its visual is one instance inside a shared MultiMesh, and
    // breaking it means rewriting that instance's transform.
    //
    // Every assertion below reads MultiMesh.GetInstanceTransform -- the value the RENDERER consumes -- rather
    // than the Slot bookkeeping that put it there, so a slot that agrees with itself while drawing in the wrong
    // place still fails. Two instances share the batch on purpose: with one, a bug that always writes slot 0
    // would be invisible, and the neighbour check is what catches it.
    public class DestructibleBatchedBreakContract : GameTest
    {
        public override string Name => "destructible.batched_break_swaps_slots";

        // NOT GetInstanceTransform: a headless boot has no RenderingServer, so a MultiMesh reads back empty
        // (measured -- see PropBatcher.Slot.Visible). Asserting on the buffer here would pass and fail
        // identically, which is no test at all. These read the recorded decision instead, which proves the
        // routing and the swap but says nothing about pixels.
        static bool Hidden(PropBatcher.Slot s) => !s.Visible;
        static bool At(PropBatcher.Slot s, Transform3D want) => s.Visible && s.Xf.Origin.IsEqualApprox(want.Origin);

        public override IEnumerable<Step> Run()
        {
            var batcher = new PropBatcher();
            var mat = new StandardMaterial3D();
            Mesh intact = new BoxMesh { Size = Vector3.One };
            Mesh rubbleMesh = new BoxMesh { Size = new Vector3(1f, 0.2f, 1f) };
            var xf = new Transform3D(Basis.Identity, new Vector3(3f, 1f, -7f));
            var xf2 = new Transform3D(Basis.Identity, new Vector3(9f, 1f, -7f));   // same 64m cell -> same batch

            var alive = batcher.Add("guid", "m", 0, false, intact, mat, 0f, 100f, GeometryInstance3D.ShadowCastingSetting.On, xf);
            var neighbour = batcher.Add("guid", "m", 0, false, intact, mat, 0f, 100f, GeometryInstance3D.ShadowCastingSetting.On, xf2);
            var debris = batcher.Add("guid", "m", 0, true, rubbleMesh, mat, 0f, 100f, GeometryInstance3D.ShadowCastingSetting.On, xf);
            batcher.Flush(World);
            yield return Ticks(1);

            T.Check("the two props really do share one MultiMesh (else the neighbour check proves nothing)",
                    alive.Mm != null && ReferenceEquals(alive.Mm, neighbour.Mm) && alive.Index != neighbour.Index);
            T.Check("the debris is in a DIFFERENT batch from the intact prop", !ReferenceEquals(alive.Mm, debris.Mm));
            T.Check("intact: the prop draws at its placed transform", At(alive, xf));
            T.Check("intact: the debris is parked out of sight", Hidden(debris));

            var body = new StaticBody3D { CollisionLayer = 1u << 6, Position = xf.Origin };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            World.AddChild(body);
            var field = new DestructibleField();
            field.SetCount(1);
            body.SetMeta(DestructibleField.MetaKey, 0);
            field.RegisterBatched(0, body, new[] { alive }, new[] { debris },
                                  new Aabb(-Vector3.One * 0.5f, Vector3.One), xf, mat, maxHealth: 50f, resetTicks: 100);
            yield return Ticks(1);

            field.SetAlive(0, false);   // the break
            yield return Ticks(1);
            T.Check("broken: the prop's instance left the batch", Hidden(alive));
            T.Check("broken: the debris took EXACTLY the place the prop stood", At(debris, xf));
            T.Check("broken: the neighbour sharing the batch is untouched", At(neighbour, xf2));
            T.Check("broken: the collider dropped", body.CollisionLayer == 0u);
            T.Check("broken: the field reports the slot dead", !field.IsAlive(0));

            field.SetAlive(0, true);    // the rubble reset
            yield return Ticks(1);
            T.Check("respawned: the prop is back at its exact transform", At(alive, xf));
            T.Check("respawned: the debris is hidden again", Hidden(debris));
            T.Check("respawned: the neighbour STILL untouched", At(neighbour, xf2));
            T.Check("respawned: the collider is back", body.CollisionLayer == 1u << 6);
        }
    }
}
