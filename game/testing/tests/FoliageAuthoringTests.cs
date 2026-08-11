using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Testing
{
    // FOLIAGE AUTHORING: the format and the two-population rule.
    //
    // The editor tool needs to add and remove foliage, and retail draws a distinction this port did not have:
    // hand-placed foliage carries `clearWhenBaked = false` -- "Manually placed, should not be cleared" -- and
    // removal is filtered by ManuallyPlaced / Baked / All. So manual and generated foliage are two populations
    // that must survive each other's operations.
    //
    // That is a FORMAT property, not UI behaviour, which is why it is tested before any tool exists: every
    // baked .bin on disk is v1 with no flag, so v2 has to be added without breaking them. Retrofitting later
    // means re-baking every map.
    //
    // The checks that matter are the ones a careless implementation passes anyway unless they are written
    // adversarially: a bake sweep must leave hand-placed foliage ALONE (trivially true if removal is broken and
    // deletes nothing, hence the paired check that it does remove baked), and the flag must survive a
    // write/read cycle (trivially true if everything is flagged manual, hence checking a baked one stays 0).
    public sealed class FoliageAuthoringFormatTests : GameTest
    {
        public override string Name => "foliage.authoring_format";

        public override IEnumerable<Step> Run()
        {
            var field = new FoliageField();
            World.AddChild(field);
            field.LoadGrass();
            yield return Step.Ticks(2);

            string type = null;
            foreach (var t in field.AuthoringTypes) { type = t; break; }
            T.Check($"baked foliage loaded and registered for authoring ({type})", type != null);
            if (type == null) yield break;

            int baked0 = field.InstanceCount(type);
            T.Check($"{type} has baked instances ({baked0})", baked0 > 0);
            T.Check($"and v1 content is all BAKED, none manual ({field.ManualCount(type)})", field.ManualCount(type) == 0);

            // Hand-place a clutch somewhere far from the baked field so the sphere tests cannot catch strays.
            var spot = new Vector3(12345f, 0f, -12345f);
            int placed = 0;
            for (int i = 0; i < 5; i++)
                if (field.AddInstance(type, new Transform3D(Basis.Identity, spot + new Vector3(i * 0.5f, 0f, 0f)), manual: true)) placed++;
            T.Check($"placed 5 manual instances ({placed})", placed == 5);
            T.Check($"total grew by exactly those 5 ({field.InstanceCount(type)} vs {baked0})", field.InstanceCount(type) == baked0 + 5);
            T.Check($"and they are counted as manual ({field.ManualCount(type)})", field.ManualCount(type) == 5);

            // THE RULE. A bake sweep clears BAKED foliage only; hand-placed must be untouched.
            int clearedByBake = field.RemoveInSphere(type, spot, 50f, manual: false, baked: true);
            T.Check($"a bake sweep over the hand-placed clutch removes NOTHING ({clearedByBake})", clearedByBake == 0);
            T.Check($"...and all 5 are still there ({field.ManualCount(type)})", field.ManualCount(type) == 5);

            // Paired with the above so "removes nothing" cannot pass by removal being broken outright.
            int manualCleared = field.RemoveInSphere(type, spot, 50f, manual: true, baked: false);
            T.Check($"a manual sweep DOES remove them ({manualCleared})", manualCleared == 5);
            T.Check($"leaving the baked field intact ({field.InstanceCount(type)} vs {baked0})", field.InstanceCount(type) == baked0);

            // FORMAT ROUND TRIP. Re-place one manual instance, save, and read the bytes back by hand -- asserting
            // on the file rather than on the object, since the object is what wrote it.
            field.AddInstance(type, new Transform3D(Basis.Identity, spot), manual: true);
            string dir = Path.Combine(ProjectSettings.GlobalizePath("user://"), "foliage_authoring_test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            field.SaveAll(dir);
            string binPath = Path.Combine(dir, type + ".bin");
            T.Check($"SaveAll wrote {type}.bin", File.Exists(binPath));
            if (File.Exists(binPath))
            {
                using var br = new BinaryReader(File.OpenRead(binPath));
                int first = br.ReadInt32();
                int ver = first < 0 ? br.ReadInt32() : 1;
                int count = first < 0 ? br.ReadInt32() : first;
                // Sign, not a magic string: a v1 count is never negative, so this is what makes old files
                // distinguishable without touching them.
                T.Check($"header is the negative sentinel, so a v1 reader cannot mistake it ({first})", first < 0);
                T.Check($"version is {FoliageField.FormatVersion} ({ver})", ver == FoliageField.FormatVersion);
                T.Check($"count matches the store ({count} vs {field.InstanceCount(type)})", count == field.InstanceCount(type));

                int manualSeen = 0, flagged = 0;
                for (int i = 0; i < count; i++)
                {
                    for (int f = 0; f < 12; f++) br.ReadSingle();
                    if (br.ReadByte() != 0) { manualSeen++; }
                    flagged++;
                }
                T.Check($"every instance carries a flag byte, none truncated ({flagged}/{count})", flagged == count);
                T.Check($"exactly the 1 hand-placed instance is flagged manual ({manualSeen})", manualSeen == 1);
                T.Check($"...so baked instances round-trip as NOT manual ({count - manualSeen})", count - manualSeen == baked0);
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            field.QueueFree();
        }
    }
}
