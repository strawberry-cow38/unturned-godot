using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>Every replicated vital survives a real round trip -- the ItemWireCompletenessTests idea,
    /// applied to the owner vitals block for the same reason it was applied to Item.
    ///
    /// Adding a field to PlayerVitalsSim and forgetting to serialise it is a bug with NO natural symptom on
    /// the writing side: the server's value is right, every existing test still passes, and the client simply
    /// shows the constructor default forever. That has now happened repeatedly in this project on the ITEM
    /// wire (per-slot attachments, the gas-can fuel level, the magazine cartridge lock) which is why that
    /// reflection test exists; oxygen was the first field to make the vitals block big enough for the same
    /// trap, so the guard goes in beside it rather than after the next one.
    ///
    /// A field that genuinely should not ride this block gets an EXEMPTION with a stated reason. The list is
    /// the point: it forces the question to be answered rather than left implicit.</summary>
    [TestFixture]
    public class VitalsWireCompletenessTests
    {
        static readonly Dictionary<string, string> Exempt = new()
        {
            // HP is NEVER owned by the vitals block -- ServerStep re-seeds it from CombatState (the single
            // authority) and routes the delta back out through the damage/regen sinks.
            ["Health"] = "server HP is the coarse CombatState value, re-seeded every step",
            ["MaxHealth"] = "constant per player; not per-tick state",
            // Purely local pacing for the stamina regen hold; re-derived on whichever side is stepping.
            ["StaminaRegenDelay"] = "local regen pacing, re-derived by whoever steps the sim",
            // Same shape as StaminaRegenDelay, and exempt for the same reason rather than by analogy: it is
            // pacing for a value the client does not own. The pattern in this block is that the VALUE rides and
            // the PACING does not -- Stamina is here and StaminaRegenDelay is not; Health is server-owned and
            // exempt, and this is Health's regen pacing.
            //
            // Checked rather than assumed: RegenLockDelay is read in exactly one place, the regen gate inside
            // Step, and a client with server-owned fine vitals never reaches that -- UpdateVitals early-returns
            // on NetFineVitalsAdopted before the local sim mutates anything. Every party that needs the lock
            // derives it from the damage itself: the server from hp that went missing between its own last
            // output and this tick's read, an SP-authoritative shell from PlayerController.TakeDamage. Nothing
            // renders it, so putting a float per player per tick on the wire would buy a value no one reads.
            //
            // ⚠ If a client ever needs to SHOW the lock (a "too hurt to heal" tell), this stops being true and
            // the field should be serialised instead of re-exempted.
            ["RegenLockDelay"] = "post-damage regen pacing; the only reader is the sim's own regen gate, which a "
                               + "client with adopted fine vitals never steps. Both authorities arm it from the damage.",
        };

        static IEnumerable<FieldInfo> VitalFields() =>
            typeof(PlayerVitalsSim).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => !f.IsInitOnly && f.FieldType == typeof(float));

        [Test]
        public void every_replicated_vital_survives_a_real_round_trip()
        {
            var missing = new List<string>();
            foreach (var f in VitalFields())
            {
                if (Exempt.ContainsKey(f.Name)) continue;

                // A value distinguishable from BOTH the default (1) and zero, so "arrived" and
                // "default-constructed" cannot be confused. Quantised, so it survives the wire exactly.
                const float mark = 0.375f;

                var server = new PlayerVitalsReplication();
                server.ServerAdd(1, 0L);
                Assert.That(server.TryGet(1, out var se), Is.True);
                f.SetValue(se.Sim, mark);

                var w = new NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };
                w.Reset();
                server.WriteFull(w, new ReplicationContext(0L, 1, default));
                w.Flush();

                var sent = new byte[w.writeByteIndex];
                System.Array.Copy(w.buffer, sent, sent.Length);
                var r = new NetPakReader();
                r.SetBuffer(sent);
                var client = new PlayerVitalsReplication();
                client.ReadSnapshot(r, full: true);

                float got = client.TryGet(1, out var ce) ? (float)f.GetValue(ce.Sim) : float.NaN;
                if (System.Math.Abs(got - mark) > 0.01f) missing.Add($"{f.Name} (sent {mark}, got {got})");
            }

            Assert.That(missing, Is.Empty,
                "vital(s) that do NOT survive the owner block: " + string.Join(", ", missing)
                + ". Either serialise it in WriteOwnerBlock/ReadSnapshot (and add it to StampIfChanged and "
                + "StateHash, and adopt it in the shell), or add it to Exempt with a reason.");
        }
    }
}
