using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // "zombies infinitely spawn" (strawberry 2026-09-17). They did, and not through a spawner running away: a chunk
    // DROPS its Live list when it freezes and rebuilds a full Cap when you come back, and nothing anywhere recorded
    // that anything had died. So clearing a town, walking far enough for its chunks to freeze, and returning handed
    // the whole population back -- indefinitely, as often as you cared to walk the loop.
    //
    // ⚠ TWO LEGS, because they fail differently. The first drives the REAL death path (a body, killed, noticed by
    // Move) to prove a kill is recorded at all. The second drives a freeze/thaw to prove the record then SURVIVES
    // the list being thrown away. A test that only set Killed by hand would pass with the death path unwired, and a
    // test that only killed things would pass with the thaw still refilling -- each covers the other's blind spot.
    public sealed class ZombieRespawnTests : GameTest
    {
        public override string Name => "zombie.kills_persist";
        public override int Tier => 1;

        static ZombieChunkField.Chunk Only(ZombieChunkField f)
        {
            foreach (var kv in f.Chunks) return kv.Value;
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            var at = new Vector3(96f, 0f, 96f);
            var f = new ZombieChunkField();
            World.AddChild(f);
            f.DebugSeed(at, 8, 6f);
            f.DebugAnchor = at;
            f.ForceReclassify();

            var c = Only(f);
            T.Check("a chunk was seeded", c != null);
            if (c == null) yield break;
            T.Check($"it holds its full cap to start (live {c.Live?.Count}, cap {c.Cap})", c.Live != null && c.Live.Count == 8 && c.Cap == 8);
            T.Check($"nothing has died yet (killed {c.Killed})", c.Killed == 0);

            // ---- LEG 1: a real kill, through the path the game uses.
            yield return Ticks(4);                       // let Move promote the near ones to bodies
            ZombieChunkField.Zombie victim = null;
            foreach (var z in c.Live) if (z.Body != null) { victim = z; break; }
            T.Check("at least one zombie got a body to kill", victim != null);
            if (victim == null) yield break;

            int before = c.Live.Count;
            victim.Body.Damage(9999f, at + Vector3.Up);   // the gun/melee entry point
            T.Check("the body reports itself dead", victim.Body != null && victim.Body.Dead);
            yield return Ticks(3);                        // Move notices and retires it

            T.Check($"the dead one left the live list ({before} -> {c.Live.Count})", c.Live.Count == before - 1);
            T.Check($"and the chunk REMEMBERS it (killed {c.Killed})", c.Killed >= 1);
            int killed = c.Killed;

            // ---- LEG 2: freeze, thaw, and the dead must stay dead.
            f.DebugAnchor = new Vector3(100000f, 0f, 100000f);
            f.ForceReclassify();
            T.Check("far away -> the chunk froze and dropped its live list", c.Live == null);
            // Population is what the map totals report; a frozen chunk must not claim back what died in it.
            T.Check($"a frozen chunk reports cap minus its dead ({c.Population} == {8 - killed})", c.Population == 8 - killed);

            f.DebugAnchor = at;
            f.ForceReclassify();
            T.Check("coming back re-materialized it", c.Live != null);
            // THE ONE THE BUG WAS. Before this it came back as 8 every single time.
            T.Check($"the thaw does NOT hand the dead back (live {c.Live?.Count}, expected {8 - killed})",
                    c.Live != null && c.Live.Count == 8 - killed);

            // The ceiling itself: never more bodies than the cap allows, however many are in range.
            int bodies = 0;
            foreach (var z in c.Live) if (z.Body != null) bodies++;
            T.Check($"live bodies stay within MaxHotBodies ({bodies} <= {ZombieChunkField.MaxHotBodies})",
                    bodies <= ZombieChunkField.MaxHotBodies);

            f.QueueFree();
            yield break;
        }
    }

    // "zombies seem to agro on me no matter what" (strawberry 2026-09-17). The loudness on every noise has always
    // been a RADIUS IN METRES -- Walk 10, Gunshot 48 -- and nothing treated it as one: it only picked which noise
    // won, after which every non-frozen zombie in the flow field's ±160 m walked at it. A moving player emits every
    // 0.4 s, so the 8 s alert never lapsed and the map aggroed permanently the moment you took a step.
    //
    // ⚠ The quiet leg alone would pass on a harness where the field never builds and NOTHING ever moves, so the
    // loud leg is the control that proves the setup can actually produce movement. Neither is worth much alone.
    public sealed class ZombieEarshotTests : GameTest
    {
        public override string Name => "zombie.hears_by_radius";
        public override int Tier => 1;

        static Vector3 FirstPos(ZombieChunkField f)
        {
            foreach (var kv in f.Chunks) if (kv.Value.Live != null && kv.Value.Live.Count > 0) return kv.Value.Live[0].Pos;
            return Vector3.Zero;
        }

        public override IEnumerable<Step> Run()
        {
            // 100 m out: the chunk is WARM (<=128) so it simulates, but past HotBodyDist (90) so nobody gets a body
            // and the drift is pure arithmetic -- no physics, no ground needed.
            var at = new Vector3(100f, 0f, 0f);
            var f = new ZombieChunkField();
            World.AddChild(f);
            f.DebugSeed(at, 6, 2f);
            f.DebugAnchor = Vector3.Zero;
            f.ForceReclassify();
            yield return Ticks(20);                      // let the 4 Hz tier/field pass run

            var before = FirstPos(f);
            T.Check($"zombies seeded ~100 m out ({before.X:0.0}, {before.Z:0.0})", before.X > 90f);

            // QUIET, and far: a footstep at the origin is 100 m away and carries 10 m. They must not care.
            SoundBus.Emit(World.GetTree(), Vector3.Zero, SoundBus.Walk);
            yield return Ticks(40);
            var afterQuiet = FirstPos(f);
            float movedQuiet = (afterQuiet - before).Length();
            T.Check($"a 10 m footstep 100 m away does NOT pull them (moved {movedQuiet:0.00} m)", movedQuiet < 0.5f);

            // LOUD, and close: a gunshot 20 m away carries 48 m. This is the control -- if this does not move them
            // the harness cannot drive movement at all and the check above proved nothing.
            SoundBus.Emit(World.GetTree(), new Vector3(80f, 0f, 0f), SoundBus.Gunshot);
            yield return Ticks(40);
            var afterLoud = FirstPos(f);
            float movedLoud = (afterLoud - afterQuiet).Length();
            T.Check($"CONTROL: a 48 m gunshot 20 m away DOES pull them (moved {movedLoud:0.00} m)", movedLoud > 0.5f);

            f.QueueFree();
            yield break;
        }
    }
}
