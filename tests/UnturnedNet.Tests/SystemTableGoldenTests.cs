using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// The SYSTEM id space had no golden of any kind. CommandTableGoldenTests and EventTableGoldenTests
    /// filter `typeof(ReplicationIds)` by literal name prefix -- `StartsWith("Command")` and
    /// `StartsWith("Event")` -- so "System" is simply a prefix nobody wrote a file for. Renumbering
    /// SystemVitals 13 -> 19, or reusing a retired id, went red nowhere.
    ///
    /// ⭐ TWO DISTINCT GUARANTEES, and the second is the one that was really missing. A table golden pins
    /// WHAT THE CONSTANTS ARE. It says nothing about WHO CLAIMS WHICH -- and each IReplicatedSystem
    /// implementor names its own id through a `SystemId =>` property. Point one at the wrong constant and
    /// SnapshotComposer only throws if it happens to COLLIDE with a registered sibling
    /// (SnapshotUnitTests.cs:115); otherwise it ships silently and writes its block under another system's
    /// id, which the client then hands to the wrong reader. So both halves live here.
    ///
    /// Found by a peer session's read-only wire sweep (2026-09-16), which also made the priority call:
    /// the binding half matters more than the table half, and one reflection pass closes both.
    ///
    /// ⚠ NO INSTANCE IS CONSTRUCTED. These types have real constructors with dependencies, and a sweep
    /// that skipped whatever it could not build would be a coverage hole painted green -- the exact shape
    /// this file exists to remove. GetUninitializedObject allocates without running a constructor, which
    /// is sound here precisely because a correct `SystemId` getter returns a CONSTANT and touches no
    /// instance state. A getter that needed initialised state would throw and fail the test loudly, which
    /// is also the right outcome: SystemId is read during registration, before anything is set up.
    /// </summary>
    [TestFixture]
    public class SystemTableGoldenTests
    {
        // Goldened 2026-09-16 at NetProtocol.Version 52, hand-written from PlayerReplication.cs:16-37.
        // SystemSyncCheck=255 is IN the table on purpose: it is a reserved sentinel rather than a real
        // system, which makes it the id most likely to be "tidied" by someone who notices that.
        static readonly Dictionary<string, byte> ExpectedIds = new()
        {
            ["SystemPlayers"] = 1,       ["SystemPlayerCombat"] = 2,  ["SystemZombies"] = 3,
            ["SystemProjectiles"] = 4,   ["SystemSkills"] = 5,        ["SystemDeployables"] = 6,
            ["SystemInventory"] = 7,     ["SystemWorldItems"] = 8,    ["SystemVehicles"] = 9,
            ["SystemWorldClock"] = 10,   ["SystemCrops"] = 11,        ["SystemResources"] = 12,
            ["SystemVitals"] = 13,       ["SystemContainers"] = 14,   ["SystemAnimals"] = 15,
            ["SystemDestructibles"] = 16, ["SystemInteractables"] = 17, ["SystemProfiles"] = 18,
            ["SystemSyncCheck"] = 255,
        };

        static Dictionary<string, byte> ActualIds() =>
            typeof(ReplicationIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name.StartsWith("System"))
                .ToDictionary(f => f.Name, f => (byte)f.GetRawConstantValue());

        [Test]
        public void the_system_table_matches_the_version_it_was_goldened_against()
        {
            var actual = ActualIds();
            Assert.Multiple(() =>
            {
                foreach (var kv in ExpectedIds)
                    Assert.That(actual.TryGetValue(kv.Key, out byte got) ? got : (byte?)null, Is.EqualTo(kv.Value),
                        $"{kv.Key} moved or was removed. System ids are ROUTING: the composer writes a block "
                        + "under this byte and the client dispatches on it, so a changed id sends a whole "
                        + "system's state to the wrong reader on any peer that disagrees.");
                foreach (var name in actual.Keys.Except(ExpectedIds.Keys))
                    Assert.Fail($"{name} is a NEW system id with no golden entry. Add it here in the same "
                        + "commit that adds the constant, and bump NetProtocol.Version -- an unbumped new id "
                        + "is silently dropped by the peer that does not know it.");
            });
        }

        [Test]
        public void every_system_id_is_unique()
        {
            var dupes = ActualIds().GroupBy(kv => kv.Value).Where(g => g.Count() > 1)
                                   .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Key))}").ToList();
            Assert.That(dupes, Is.Empty, "two system constants share an id; the second registration throws at "
                + "construction only if both are actually registered, so a reserved-but-unused duplicate hides.");
        }

        // ---- the binding half: who claims which id.

        static List<Type> Implementors() =>
            typeof(IReplicatedSystem).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IReplicatedSystem).IsAssignableFrom(t))
                .OrderBy(t => t.Name).ToList();

        [Test]
        public void every_replicated_system_claims_the_id_it_is_supposed_to()
        {
            var report = new List<string>();
            foreach (var t in Implementors())
            {
                byte id;
                try
                {
                    var inst = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
                    id = ((IReplicatedSystem)inst).SystemId;
                }
                catch (Exception e)
                {
                    // Not skipped -- recorded as a failure. A SystemId getter that needs constructed state
                    // is itself the bug: the composer reads this during registration.
                    report.Add($"{t.Name} = <threw {e.GetType().Name}>");
                    continue;
                }
                var names = ActualIds().Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
                report.Add($"{t.Name} = {id} ({(names.Count == 1 ? names[0] : "NO MATCHING CONSTANT")})");
            }
            Assert.That(report, Is.EqualTo(ExpectedBindings),
                "a replicated system's SystemId no longer matches its goldened constant.\nACTUAL:\n  "
                + string.Join("\n  ", report) + "\nA system pointing at the wrong constant throws at "
                + "construction ONLY if it collides with a registered sibling; otherwise it ships and writes "
                + "its block under another system's id.");
        }

        // Captured 2026-09-16; each line is "<type> = <id> (<constant>)".
        static readonly string[] ExpectedBindings =
        {
            "AnimalReplication = 15 (SystemAnimals)",
            "ContainerReplication = 14 (SystemContainers)",
            "CropReplication = 11 (SystemCrops)",
            "DeployableReplication = 6 (SystemDeployables)",
            "DestructibleReplication = 16 (SystemDestructibles)",
            "InteractableStateReplication = 17 (SystemInteractables)",
            "InventoryReplication = 7 (SystemInventory)",
            "PlayerCombatReplication = 2 (SystemPlayerCombat)",
            "PlayerProfileReplication = 18 (SystemProfiles)",
            "PlayerReplication = 1 (SystemPlayers)",
            "PlayerVitalsReplication = 13 (SystemVitals)",
            "ProjectileReplication = 4 (SystemProjectiles)",
            "ResourceReplication = 12 (SystemResources)",
            "SkillsReplication = 5 (SystemSkills)",
            "VehicleReplication = 9 (SystemVehicles)",
            "WorldClockReplication = 10 (SystemWorldClock)",
            "WorldItemReplication = 8 (SystemWorldItems)",
            "ZombieReplication = 3 (SystemZombies)",
        };
    }
}
