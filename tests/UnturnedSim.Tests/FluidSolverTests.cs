using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests for the fluid-flow solver (fluid IO F1) — a mirror of the power solver's chain / splitter / combiner /
    // over-draw cases in flow terms. Reference: a source supply cap of 1000 units/s, a consumer demand of 250.
    [TestFixture]
    public class FluidSolverTests
    {
        static (FluidDevice Dev, FluidPort Out) Source(float rate = 1000f, bool supplying = true)
        {
            var d = new FluidDevice { Supplying = supplying };
            return (d, d.AddPort(FluidPortKind.Source, rate));
        }
        static (FluidDevice Dev, FluidPort In, FluidPort Pass) Consumer(float demand = 250f)
        {
            var d = new FluidDevice();
            return (d, d.AddPort(FluidPortKind.Consumer, demand), d.AddPort(FluidPortKind.Passthrough, 0f));
        }
        static (FluidDevice Dev, FluidPort In, FluidPort[] Out) Splitter(int outs = 2)
        {
            var d = new FluidDevice();
            var inp = d.AddPort(FluidPortKind.Consumer, 0f);   // 0-rate relay: takes nothing, re-exports the full input
            var o = new FluidPort[outs];
            for (int i = 0; i < outs; i++) o[i] = d.AddPort(FluidPortKind.Passthrough, 0f);
            return (d, inp, o);
        }
        static (FluidDevice Dev, FluidPort[] In, FluidPort Out) Combiner(int ins = 2)
        {
            var d = new FluidDevice();
            var i = new FluidPort[ins];
            for (int k = 0; k < ins; k++) i[k] = d.AddPort(FluidPortKind.Consumer, 0f);   // 0-rate relays, summed at the output
            var o = d.AddPort(FluidPortKind.Passthrough, 0f);
            return (d, i, o);
        }
        static FluidHose Hose(FluidPort s, FluidPort c) => new FluidHose(s, c);

        [Test]
        public void Source_Flows_Single_Consumer()
        {
            var src = Source();
            var con = Consumer();
            FluidSolver.Solve(new[] { src.Dev, con.Dev }, new[] { Hose(src.Out, con.In) });
            Assert.That(src.Out.Flow, Is.EqualTo(1000f).Within(0.01f));
            Assert.That(con.In.Flowing, Is.True);
            Assert.That(con.Pass.Flow, Is.EqualTo(750f).Within(0.01f));   // leftover re-exported
            Assert.That(src.Out.Load, Is.EqualTo(250f).Within(0.01f));    // source usage = the demand it feeds
        }

        [Test]
        public void Dry_Source_Nothing_Flows()
        {
            var src = Source(supplying: false);
            var con = Consumer();
            FluidSolver.Solve(new[] { src.Dev, con.Dev }, new[] { Hose(src.Out, con.In) });
            Assert.That(src.Out.Flow, Is.EqualTo(0f).Within(0.01f));
            Assert.That(con.In.Flowing, Is.False);
            Assert.That(src.Out.Load, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void Under_Supplied_Consumer_Does_Not_Flow()
        {
            var src = Source(rate: 100f);   // supplies only 100; the consumer needs 250
            var con = Consumer(250f);
            FluidSolver.Solve(new[] { src.Dev, con.Dev }, new[] { Hose(src.Out, con.In) });
            Assert.That(con.In.Flowing, Is.False);   // demand > supply -> no flow (strawberry: flows when demand <= supply)
        }

        [Test]
        public void Chain_Passthrough_Feeds_Second_And_Loads_Source()
        {
            var src = Source();
            var a = Consumer();
            var b = Consumer();
            var hoses = new[] { Hose(src.Out, a.In), Hose(a.Pass, b.In) };
            FluidSolver.Solve(new[] { src.Dev, a.Dev, b.Dev }, hoses);
            Assert.That(a.In.Flowing, Is.True);
            Assert.That(b.In.Flowing, Is.True);
            Assert.That(src.Out.Load, Is.EqualTo(500f).Within(0.01f));   // both consumers, 250 + 250
        }

        [Test]
        public void Splitter_FanOut_Feeds_Two()
        {
            var src = Source();
            var sp = Splitter(2);
            var a = Consumer();
            var b = Consumer();
            var hoses = new[] { Hose(src.Out, sp.In), Hose(sp.Out[0], a.In), Hose(sp.Out[1], b.In) };
            FluidSolver.Solve(new[] { src.Dev, sp.Dev, a.Dev, b.Dev }, hoses);
            Assert.That(a.In.Flowing, Is.True);
            Assert.That(b.In.Flowing, Is.True);
            Assert.That(src.Out.Load, Is.EqualTo(500f).Within(0.01f));   // one source fanned to both
        }

        [Test]
        public void Combiner_Merges_Two_Sources()
        {
            var s1 = Source(rate: 300f);
            var s2 = Source(rate: 300f);
            var cm = Combiner(2);
            var con = Consumer(500f);   // needs 500; each source only 300, but combined 600 covers it
            var hoses = new[] { Hose(s1.Out, cm.In[0]), Hose(s2.Out, cm.In[1]), Hose(cm.Out, con.In) };
            FluidSolver.Solve(new[] { s1.Dev, s2.Dev, cm.Dev, con.Dev }, hoses);
            Assert.That(con.In.Flowing, Is.True);   // 300 + 300 = 600 >= 500
        }

        [Test]
        public void Source_Cap_Starves_One_Overdraw_On_Splitter()
        {
            var src = Source(rate: 300f);   // 300 cap can only cover ONE 250 consumer
            var sp = Splitter(2);
            var a = Consumer(250f);
            var b = Consumer(250f);
            var hoses = new[] { Hose(src.Out, sp.In), Hose(sp.Out[0], a.In), Hose(sp.Out[1], b.In) };
            FluidSolver.Solve(new[] { src.Dev, sp.Dev, a.Dev, b.Dev }, hoses);
            int flowing = (a.In.Flowing ? 1 : 0) + (b.In.Flowing ? 1 : 0);   // which one starves = deterministic traversal, don't care
            Assert.That(flowing, Is.EqualTo(1));                              // the source cap starved the over-draw branch
            Assert.That(src.Out.Load, Is.EqualTo(250f).Within(0.01f));
        }
    }
}
