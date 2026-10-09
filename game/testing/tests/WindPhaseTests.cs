using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>The sway phase (wind_vec.w) advances at the WIND's rate, however many things ask it to.
    ///
    /// strawberry 2026-10-09: "the wind speed on the inf map is really fast". Every PowerLineField keeps the wind
    /// alive in player-less worlds by calling WindField.PushGlobalsIfIdle, and the infinite world builds one field per
    /// region with poles -- 13 round the spawn. Nothing stopped all of them integrating in the same frame, and the
    /// "player pushed recently" guard counted FRAMES while the player pushes at 60 Hz, so above ~120 fps the idle
    /// callers got in between its pushes. Both legs below fail on that code: 13 callers with no player ran the phase
    /// ~13x, and a player pushing every third frame (60 Hz at 180 fps) let them in every third frame.</summary>
    public partial class WindIdleCallers : GameTest
    {
        public override string Name => "wind.idle_callers_once_per_frame";

        public partial class Clock : Node
        {
            public bool AsPlayer; public int Every = 3;
            public double Elapsed; int _n; double _acc;
            public override void _Process(double delta)
            {
                Elapsed += delta;
                if (!AsPlayer) return;
                _acc += delta;
                if (++_n % Every == 0) { WindField.PushGlobals(Vector3.Zero, _acc); _acc = 0; }   // the player's 60 Hz throttle
            }
        }

        const float MaxRate = 0.55f + 0.9f * 1f;   // WindField.Integrate at a full gale: the fastest the phase may run

        public override IEnumerable<Step> Run()
        {
            for (int i = 0; i < 13; i++) World.AddChild(new PowerLineField());
            var clock = new Clock();
            World.AddChild(clock);
            yield return Ticks(5);

            for (int leg = 0; leg < 2; leg++)
            {
                clock.AsPlayer = leg == 1;
                yield return Ticks(30);   // let the guard settle into this leg's regime
                double t0 = clock.Elapsed; float last = WindField.PhaseForTest, advance = 0f;
                for (int f = 0; f < 90; f++)
                {
                    yield return Ticks(1);
                    float ph = WindField.PhaseForTest;
                    advance += Mathf.PosMod(ph - last, Mathf.Tau * 10f);   // the phase wraps at 20*PI
                    last = ph;
                }
                double elapsed = clock.Elapsed - t0;
                string who = leg == 0 ? "13 power-line fields, no player" : "13 fields + a player pushing every 3rd frame";
                T.Check($"{who}: phase ran {advance:0.00} rad in {elapsed:0.00} s = {advance / elapsed:0.00}/s (wind allows <= {MaxRate:0.00}/s)",
                        elapsed > 0.1 && advance > 0f && advance / elapsed <= MaxRate * 1.05);
            }
        }
    }
}
