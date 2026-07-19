using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests for the extracted wire-power solver (proposal phase 3) -- the chain/cycle/over-draw cases that are
    // miserable to stage as scenes. The retail-tuned reference numbers: generator output 4000w, spotlight usage 250w.
    [TestFixture]
    public class PowerSolverTests
    {
        // tiny builders over the plain records (mirror the game's Generator/Spotlight defs)
        static (PowerDevice Dev, PowerPort Out) Generator(float watts = 4000f, bool on = true, bool onFire = false)
        {
            var d = new PowerDevice { Producing = on && !onFire, OnFire = onFire };
            return (d, d.AddPort(PowerPortKind.Output, watts));
        }
        static (PowerDevice Dev, PowerPort In, PowerPort Pass) Spotlight(float usage = 250f, bool onFire = false)
        {
            var d = new PowerDevice { OnFire = onFire };
            return (d, d.AddPort(PowerPortKind.Consumer, usage), d.AddPort(PowerPortKind.Passthrough, 0f));
        }
        static PowerWire Wire(PowerPort src, PowerPort cons) => new PowerWire(src, cons);

        [Test]
        public void Gen_Powers_Single_Consumer()
        {
            var gen = Generator();
            var spot = Spotlight();
            PowerSolver.Solve(new[] { gen.Dev, spot.Dev }, new[] { Wire(gen.Out, spot.In) });

            Assert.That(gen.Out.Live, Is.EqualTo(4000f).Within(0.01f));
            Assert.That(spot.In.Live, Is.EqualTo(4000f).Within(0.01f));
            Assert.That(spot.In.Powered, Is.True);
            Assert.That(spot.Pass.Live, Is.EqualTo(3750f).Within(0.01f));
            Assert.That(gen.Out.Draw, Is.EqualTo(250f).Within(0.01f));
        }

        [Test]
        public void Gen_Off_Nothing_Flows()
        {
            var gen = Generator(on: false);
            var spot = Spotlight();
            PowerSolver.Solve(new[] { gen.Dev, spot.Dev }, new[] { Wire(gen.Out, spot.In) });

            Assert.That(gen.Out.Live, Is.EqualTo(0f).Within(0.01f));
            Assert.That(spot.In.Powered, Is.False);
            Assert.That(spot.Pass.Live, Is.EqualTo(0f).Within(0.01f));
            Assert.That(gen.Out.Draw, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void Chain_Passthrough_Feeds_Second_Consumer_And_Loads_Generator()
        {
            var gen = Generator();
            var spotA = Spotlight();
            var spotB = Spotlight();
            var wires = new[] { Wire(gen.Out, spotA.In), Wire(spotA.Pass, spotB.In) };

            PowerSolver.Solve(new[] { gen.Dev, spotA.Dev, spotB.Dev }, wires);

            Assert.That(spotA.In.Powered, Is.True);
            Assert.That(spotB.In.Live, Is.EqualTo(3750f).Within(0.01f));   // leftover re-export
            Assert.That(spotB.In.Powered, Is.True);
            Assert.That(spotB.Pass.Live, Is.EqualTo(3500f).Within(0.01f));
            Assert.That(gen.Out.Draw, Is.EqualTo(500f).Within(0.01f));     // both consumers traced
        }

        [Test]
        public void Ten_Deep_Chain_Settles_In_One_Solve()
        {
            var gen = Generator();
            var spots = Enumerable.Range(0, 10).Select(_ => Spotlight()).ToArray();
            var devices = new List<PowerDevice> { gen.Dev };
            var wires = new List<PowerWire> { Wire(gen.Out, spots[0].In) };
            for (int i = 0; i < 10; i++)
            {
                devices.Add(spots[i].Dev);
                if (i > 0) wires.Add(Wire(spots[i - 1].Pass, spots[i].In));
            }

            PowerSolver.Solve(devices, wires);

            Assert.That(spots.All(s => s.In.Powered), Is.True, "every link in the 10-deep chain is powered");
            Assert.That(spots[9].In.Live, Is.EqualTo(4000f - 9 * 250f).Within(0.01f));   // 1750w reaches the end
            Assert.That(gen.Out.Draw, Is.EqualTo(2500f).Within(0.01f));                  // 10 x 250w traced
        }

        [Test]
        public void Underpowered_Consumer_Stays_Dark()
        {
            var gen = Generator(watts: 200f);           // produces less than the 250w usage
            var spot = Spotlight();
            PowerSolver.Solve(new[] { gen.Dev, spot.Dev }, new[] { Wire(gen.Out, spot.In) });

            Assert.That(spot.In.Live, Is.EqualTo(200f).Within(0.01f));   // receives it, but not enough
            Assert.That(spot.In.Powered, Is.False);
            Assert.That(spot.Pass.Live, Is.EqualTo(0f).Within(0.01f));   // an unpowered consumer exports nothing
            Assert.That(gen.Out.Draw, Is.EqualTo(0f).Within(0.01f));     // unpowered consumers don't load the gen
        }

        [Test]
        public void Chain_Dies_Past_An_Overdrawing_Link()
        {
            var gen = Generator(watts: 500f);
            var spotA = Spotlight(usage: 250f);
            var spotB = Spotlight(usage: 300f);         // needs more than the 250w leftover
            PowerSolver.Solve(new[] { gen.Dev, spotA.Dev, spotB.Dev },
                new[] { Wire(gen.Out, spotA.In), Wire(spotA.Pass, spotB.In) });

            Assert.That(spotA.In.Powered, Is.True);
            Assert.That(spotB.In.Live, Is.EqualTo(250f).Within(0.01f));
            Assert.That(spotB.In.Powered, Is.False);
            Assert.That(gen.Out.Draw, Is.EqualTo(250f).Within(0.01f));   // only the powered link loads the gen
        }

        [Test]
        public void OnFire_Producer_Exports_Nothing()
        {
            var gen = Generator(onFire: true);          // a burning generator can't run (Producing false)
            var spot = Spotlight();
            PowerSolver.Solve(new[] { gen.Dev, spot.Dev }, new[] { Wire(gen.Out, spot.In) });

            Assert.That(gen.Out.Live, Is.EqualTo(0f).Within(0.01f));
            Assert.That(spot.In.Powered, Is.False);
        }

        [Test]
        public void OnFire_MidChain_Consumer_Stops_Conduction()
        {
            var gen = Generator();
            var spotA = Spotlight(onFire: true);        // burning: stops conducting, its passthrough dies with it
            var spotB = Spotlight();
            PowerSolver.Solve(new[] { gen.Dev, spotA.Dev, spotB.Dev },
                new[] { Wire(gen.Out, spotA.In), Wire(spotA.Pass, spotB.In) });

            Assert.That(spotA.In.Live, Is.EqualTo(4000f).Within(0.01f));   // still receives...
            Assert.That(spotA.In.Powered, Is.False);                       // ...but a burning consumer is never powered
            Assert.That(spotA.Pass.Live, Is.EqualTo(0f).Within(0.01f));
            Assert.That(spotB.In.Powered, Is.False);
            Assert.That(gen.Out.Draw, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void Zero_Watts_Consumer_Relays_All_Of_Its_Input()
        {
            // NEW semantics (master's splitter, 2026-07-19): a 0-watt consumer is a RELAY -- it takes nothing for
            // itself, so it's "powered" whenever it has any live input and its passthrough re-exports the FULL input.
            // (Previously a 0-watt consumer was never powered; the splitter needs this relay, so it's a conscious change.)
            var gen = Generator();
            var relay = new PowerDevice();
            var cons = relay.AddPort(PowerPortKind.Consumer, 0f);
            var pass = relay.AddPort(PowerPortKind.Passthrough, 0f);
            PowerSolver.Solve(new[] { gen.Dev, relay }, new[] { Wire(gen.Out, cons) });

            Assert.That(cons.Live, Is.EqualTo(4000f).Within(0.01f));
            Assert.That(cons.Powered, Is.True);                        // has input -> conducting (takes 0 for itself)
            Assert.That(pass.Live, Is.EqualTo(4000f).Within(0.01f));   // re-exports the full input (leftover of 0 usage)
        }

        [Test]
        public void Splitter_Fans_One_Input_Out_To_Many_Outputs_Undivided()
        {
            // A splitter = a 0-watt relay input + N passthroughs; each output carries the FULL input (not input/N), so
            // every downstream device draws only what it needs and the generator's load traces every branch.
            var gen = Generator();
            var split = new PowerDevice();
            var sIn = split.AddPort(PowerPortKind.Consumer, 0f);
            var out1 = split.AddPort(PowerPortKind.Passthrough, 0f);
            var out2 = split.AddPort(PowerPortKind.Passthrough, 0f);
            var out3 = split.AddPort(PowerPortKind.Passthrough, 0f);
            var spotA = Spotlight();
            var spotB = Spotlight();
            var spotC = Spotlight();
            PowerSolver.Solve(
                new[] { gen.Dev, split, spotA.Dev, spotB.Dev, spotC.Dev },
                new[] { Wire(gen.Out, sIn), Wire(out1, spotA.In), Wire(out2, spotB.In), Wire(out3, spotC.In) });

            Assert.That(sIn.Powered, Is.True, "the splitter relays its input");
            Assert.That(out1.Live, Is.EqualTo(4000f).Within(0.01f));   // each output = the FULL input, NOT 4000/3
            Assert.That(out2.Live, Is.EqualTo(4000f).Within(0.01f));
            Assert.That(out3.Live, Is.EqualTo(4000f).Within(0.01f));
            Assert.That(spotA.In.Powered && spotB.In.Powered && spotC.In.Powered, Is.True, "all three branches light");
            Assert.That(gen.Out.Draw, Is.EqualTo(750f).Within(0.01f));  // load traces all three branches = 3 * 250
        }

        [Test]
        public void Two_Generators_One_Consumer_Last_Wire_Wins_And_Both_Trace_The_Load()
        {
            // CURRENT semantics, pinned: each pass overwrites consumer.Live per wire in list order, so the LAST
            // wire's source is what the consumer "receives" -- sources don't sum. Load tracing walks each output's
            // own first wire, so BOTH generators count the powered consumer as their draw.
            var genA = Generator(watts: 4000f);
            var genB = Generator(watts: 1000f);
            var spot = Spotlight();
            PowerSolver.Solve(new[] { genA.Dev, genB.Dev, spot.Dev },
                new[] { Wire(genA.Out, spot.In), Wire(genB.Out, spot.In) });

            Assert.That(spot.In.Live, Is.EqualTo(1000f).Within(0.01f));   // genB's wire is last in the list
            Assert.That(spot.In.Powered, Is.True);
            Assert.That(genA.Out.Draw, Is.EqualTo(250f).Within(0.01f));
            Assert.That(genB.Out.Draw, Is.EqualTo(250f).Within(0.01f));
        }

        [Test]
        public void Wire_Cycle_Terminates_And_Cannot_Self_Power()
        {
            // two spotlights wired in a loop with NO producer: the solver's fixed pass count terminates and
            // nothing conjures power out of the cycle.
            var spotA = Spotlight();
            var spotB = Spotlight();
            PowerSolver.Solve(new[] { spotA.Dev, spotB.Dev },
                new[] { Wire(spotA.Pass, spotB.In), Wire(spotB.Pass, spotA.In) });

            Assert.That(spotA.In.Powered, Is.False);
            Assert.That(spotB.In.Powered, Is.False);
            Assert.That(spotA.Pass.Live, Is.EqualTo(0f).Within(0.01f));

            // and a cycle grafted onto a real feed: the loop-back wire is later in the list, so its (dead) export
            // overwrites the generator's feed each pass -- last-write-wins means the cycle stays dark. Load tracing
            // (a seen-set walk) terminates too. Pinned so a future solver change is a conscious one.
            var gen = Generator();
            var spotC = Spotlight();
            var spotD = Spotlight();
            PowerSolver.Solve(new[] { gen.Dev, spotC.Dev, spotD.Dev },
                new[] { Wire(gen.Out, spotC.In), Wire(spotC.Pass, spotD.In), Wire(spotD.Pass, spotC.In) });

            Assert.That(spotC.In.Powered, Is.False);
            Assert.That(spotD.In.Powered, Is.False);
            Assert.That(gen.Out.Draw, Is.EqualTo(0f).Within(0.01f));
        }
    }
}
