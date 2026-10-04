using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Only a generator burns (strawberry 2026-10-04: "stop lamps and every deployables except generators from
    /// smoking then catching fire and exploding when damaged").
    ///
    /// Every deployable used to share the generator's lifecycle: smoke under 45% HP, fire at 0, a 4 s fuse, a blast
    /// that chain-damaged its neighbours, then debris or a burning husk. Now a non-generator carries no smoke/fire rig
    /// at all and simply breaks apart at 0 HP -- never OnFire, gone the same frame -- while the generator still lights
    /// its fuse. Checked on lamps of three kinds plus the spotlight, against the generator as the control that MUST
    /// still burn (a check where both sides pass would prove nothing). Also: no deployable draws a faux beam any more
    /// (strawberry: "remove the other faux beams too") -- the spotlight's additive shafts are gone, the real spots stay.</summary>
    public sealed class DeployOnlyGeneratorsBurnTests : GameTest
    {
        public override string Name => "deploy.only_generators_burn";

        public override IEnumerable<Step> Run()
        {
            var cases = new (string label, DeployableDef def)[]
            {
                ("spotlight", DeployableDef.Spotlight), ("desk lamp", DeployableDef.DeskLamp),
                ("standing lamp", DeployableDef.StandingLamp), ("ceiling lamp", DeployableDef.CeilingBulbLamp),
            };
            float x = 0f;
            foreach (var (label, def) in cases)
            {
                var d = Deployable.Spawn(World, def, new Vector3(x += 4f, 0f, 0f), 0f);
                yield return Ticks(1);
                T.Check($"{label}: not a generator, so it does not burn", !def.IsGenerator && !d.Burns);
                int fx = 0;
                foreach (var ch in d.GetChildren()) if (ch is CpuParticles3D) fx++;
                T.Check($"{label}: carries no smoke/fire emitters ({fx})", fx == 0);
                // ...and no faux beam ("remove the other faux beams too"): the spotlight drew an additive "SpotBeam"
                // shaft under each lamp from the moment it was placed, lit or not.
                int shafts = 0;
                var stack = new Stack<Node>(); stack.Push(d);
                while (stack.Count > 0) { var n = stack.Pop(); if (n is MeshInstance3D m && m.Name.ToString().Contains("Beam")) shafts++; foreach (var c in n.GetChildren()) stack.Push(c); }
                T.Check($"{label}: draws no faux beam ({shafts})", shafts == 0);
                d.TakeDamage(d.HealthMax * 0.7f);   // well under the old 45% smoke line
                T.Check($"{label}: badly hurt, still not on fire", !d.OnFire && GodotObject.IsInstanceValid(d));
                d.TakeDamage(d.HealthMax);
                T.Check($"{label}: at 0 HP it is never OnFire (no fuse, no blast)", !d.OnFire || !GodotObject.IsInstanceValid(d));
                T.Check($"{label}: ...it broke apart", d.DebugExploded && d.IsQueuedForDeletion());
            }

            // CONTROL: the generator keeps the whole lifecycle.
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-6f, 0f, 0f), 0f);
            yield return Ticks(1);
            T.Check("generator: is one, and burns", DeployableDef.Generator.IsGenerator && gen.Burns);
            int gfx = 0;
            foreach (var ch in gen.GetChildren()) if (ch is CpuParticles3D) gfx++;
            T.Check($"generator: carries its smoke + fire emitters ({gfx})", gfx >= 3);
            gen.TakeDamage(gen.HealthMax);
            T.Check("generator: at 0 HP it catches fire and is still standing (the 4 s fuse)", gen.OnFire && !gen.IsQueuedForDeletion());
        }
    }
}
