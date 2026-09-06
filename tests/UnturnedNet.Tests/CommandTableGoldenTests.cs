using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // THE GUARD THAT WAS MISSING WHEN FOUR COMMANDS WENT ONTO THE WIRE UNVERSIONED.
    //
    // Version was set to 14 on 2026-07-27. CommandFitAttachment(35), CommandReloadSwap(36),
    // CommandWearClothing(37) and CommandUnwearClothing(38) were added on 2026-08-16, and NetProtocol.cs
    // was never touched again -- so the client->server command table grew by four entries under a version
    // number asserting the wire had not changed.
    //
    // Nothing caught it, and the reason is specific: PacketHeaderGoldenTests pins the twelve bytes of a
    // keepalive datagram, and a keepalive carries no command id. Not one byte in it moves when the command
    // table grows. That golden guards the FRAMING and is good at it; the command table simply had no
    // equivalent. The drift was eventually found by reading git dates against the Version constant, which
    // is not a mechanism.
    //
    // Why it matters rather than being bookkeeping: CommandRegistry.TryDispatch rejects an unregistered id
    // and COUNTS it (UnknownIdRejected) instead of erroring. So a newer client against an older server that
    // reports the SAME version has its clothing change, reload swap and attachment fit silently dropped --
    // it looks fine locally and never happened on the server. That is precisely the shape of the magazine
    // load/unload reverting on the next inventory move.
    //
    // This test fails whenever the table changes. That is the point: the fix is to update the list here AND
    // decide, deliberately, whether the wire change earns a Version bump. Editing this list without asking
    // that question is the same mistake with an extra step.
    [TestFixture]
    public class CommandTableGoldenTests
    {
        // id -> name, as of NetProtocol.Version 17.
        static readonly Dictionary<byte, string> Expected = new()
        {
            [35] = "CommandFitAttachment",
            [36] = "CommandReloadSwap",
            [37] = "CommandWearClothing",
            [38] = "CommandUnwearClothing",
            [39] = "CommandMagLoad",
            // v16 (gun-state-authority). Both carry state only the CLIENT can know onto the server's copy of an
            // item, because nine Item fields had no server-side writer at all and the owner echo was therefore
            // overwriting real values with constructor defaults -- a moved gun came back with a full magazine.
            [40] = "CommandGunState",
            [41] = "CommandSetAutoDrink",
            // v17 (player-profiles): who you are, as an intent. Every field is attacker-controlled and lands
            // on someone else's screen, so the server re-runs ProfileRules and publishes its OWN answer.
            [42] = "CommandSetProfile",
            // v28 (cooking): the appliance on/off button. Addressed by crate NetId, reach-checked and
            // validated against the registered-cooker set, so a forged id is a no-op rather than an oven.
            [43] = "CommandSetCookerOn",
            // v32 (craft-cancel): the other half of v31's timed crafting. Ingredients are spent at enqueue and
            // nothing but a disconnect gave them back, so this is the only way to abandon a job and be made
            // whole. Addressed by queue SLOT, and an out-of-range slot is rejected rather than clamped.
            [44] = "CommandCraftCancel",
        };

        static Dictionary<byte, string> Actual() =>
            typeof(ReplicationIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name.StartsWith("Command"))
                .Select(f => (Id: (byte)f.GetRawConstantValue(), f.Name))
                .Where(x => x.Id >= 35)          // the v15 window; older ids are pinned by their own goldens
                .ToDictionary(x => x.Id, x => x.Name);

        [Test]
        public void TheCommandTableMatchesTheVersionItWasGoldenedAgainst()
        {
            var actual = Actual();
            var added = actual.Keys.Except(Expected.Keys).OrderBy(k => k).ToList();
            var removed = Expected.Keys.Except(actual.Keys).OrderBy(k => k).ToList();

            Assert.That(added, Is.Empty,
                $"NEW command id(s) on the wire: {string.Join(", ", added.Select(k => $"{k}={actual[k]}"))}. "
                + "Update Expected here, and decide whether this needs a NetProtocol.Version bump -- an "
                + "unregistered id is silently dropped by the receiver, not rejected loudly.");
            Assert.That(removed, Is.Empty,
                $"command id(s) REMOVED: {string.Join(", ", removed.Select(k => $"{k}={Expected[k]}"))}. "
                + "Ids are append-only; reusing one makes two builds disagree about what a byte means.");

            foreach (var (id, name) in Expected)
                Assert.That(actual[id], Is.EqualTo(name), $"command id {id} was renamed or reassigned");
        }

        [Test]
        public void EveryCommandIdIsUnique()
        {
            // An id collision is invisible at compile time -- two constants can hold the same byte quite
            // happily -- and shows up as one command silently invoking the other's handler.
            var all = typeof(ReplicationIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name.StartsWith("Command"))
                .Select(f => (Id: (byte)f.GetRawConstantValue(), f.Name))
                .ToList();
            var dupes = all.GroupBy(x => x.Id).Where(g => g.Count() > 1)
                           .Select(g => $"{g.Key} = {string.Join(" AND ", g.Select(x => x.Name))}").ToList();
            Assert.That(dupes, Is.Empty, "duplicate command ids: " + string.Join("; ", dupes));
        }
    }
}
