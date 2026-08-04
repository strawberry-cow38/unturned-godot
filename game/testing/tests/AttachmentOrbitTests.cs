using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // ATTACHMENT RING (master: "we only need to show the icons of attachments, not the names too, at a reasonable
    // size, and orbitting the attachment slot, making sure they arent overlapping. the slot icons change color
    // depending on if they can accept an attachment in that slot (gray for no, white for yes) and if they have an
    // attachment show the slot icon in the rarity of the attachment in that slot's color").
    //
    // Two halves are pinned here, both because they fail SILENTLY:
    //
    //  - THE GEOMETRY. "Not overlapping" is a property, not a look. The tempting formula sizes the ring by ARC length
    //    (2*pi*r/n), which always overestimates the gap between neighbours, because what actually separates two icons
    //    on a circle is the CHORD. Arc-sized rings look fine at ten icons and overlap at three -- and three is what a
    //    real gun produces. A render would show it; nothing else would.
    //  - THE COLOUR MAPPING. Grey / white / rarity is three states that all render as "a slot icon", so getting one
    //    wrong is invisible unless you already know what it should be. The rarity leg matters most: a slot showing
    //    WHITE when it holds a legendary scope is not a bug anyone reports, it just quietly stops being information.
    //
    // What is NOT covered: the live menu. AttachmentMenu needs a Viewmodel, a projected hook point and a camera, none
    // of which exist headless -- so what runs here is the pure geometry and the pure colour map, and the actual
    // placement of buttons on screen still needs a human with a gun in hand.
    public sealed class AttachmentOrbitTests : GameTest
    {
        public override string Name => "ui.attachment_orbit";

        public override IEnumerable<Step> Run()
        {
            const float size = 44f, gap = 10f, minR = 56f;

            // ---- NO TWO ICONS TOUCH, at every count a gun can plausibly produce. Measured off the real placement
            // maths rather than trusting the radius formula: put n icons on the ring and check every pair.
            int worstN = 0; float worstGap = float.MaxValue;
            for (int n = 1; n <= 24; n++)
            {
                float r = AttachmentMenu.OrbitRadius(n, size, gap, minR);
                var pts = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    float th = -Mathf.Pi / 2f + Mathf.Tau * i / n;
                    pts[i] = new Vector2(Mathf.Cos(th), Mathf.Sin(th)) * r;
                }
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        float d = pts[i].DistanceTo(pts[j]);
                        if (d < worstGap) { worstGap = d; worstN = n; }
                    }
            }
            T.Check($"no two icons in a 1..24 ring come closer than one icon width (worst {worstGap:0.0}px at n={worstN}, need {size})",
                worstGap >= size);
            // The margin is the requested GAP, not merely "not touching" -- edge-to-edge contact still reads as one
            // smeared blob at 44px.
            T.Check($"...and they keep the asked-for breathing room too ({worstGap:0.0} >= {size + gap})", worstGap >= size + gap - 1e-3f);

            // THE TEETH. An arc-length radius is the plausible wrong answer -- 2*pi*r/n = size+gap, i.e.
            // r = n*(size+gap)/(2*pi) -- so show that this check would actually reject it rather than asserting it
            // would. The ratio arc/chord is (pi/n)/sin(pi/n), which grows as n SHRINKS, so an arc-sized ring is at its
            // worst at the smallest counts: it loses the gap by n=3 and genuinely overlaps by n=2. (I first wrote this
            // claiming n=3 overlapped; it does not, it is 44.7px apart against a 44px icon. The test said so.)
            float arcR2 = 2f * (size + gap) / Mathf.Tau;
            float arcChord2 = 2f * arcR2 * Mathf.Sin(Mathf.Pi / 2f);
            T.Check($"an ARC-sized ring really would OVERLAP at n=2 ({arcChord2:0.0}px apart, icons are {size}) -- so this check has teeth",
                arcChord2 < size);
            float chord2 = 2f * AttachmentMenu.OrbitRadius(2, size, gap, minR) * Mathf.Sin(Mathf.Pi / 2f);
            T.Check($"...and the chord-sized one clears it at the same count ({chord2:0.0}px)", chord2 >= size + gap - 1e-3f);
            // ...and at n=3 the arc version survives contact but loses the requested gap, which is the subtler half of
            // why it is wrong: it degrades into "touching but not overlapping" before it degrades into a blob.
            float arcChord3 = 2f * (3f * (size + gap) / Mathf.Tau) * Mathf.Sin(Mathf.Pi / 3f);
            T.Check($"...and loses the breathing room a count earlier ({arcChord3:0.0}px, wanted {size + gap})", arcChord3 < size + gap);

            // A single icon still has to clear the slot sprite it orbits, and n=0/1 must not divide by sin(pi/n)=0.
            T.Check($"one icon sits at the floor radius ({AttachmentMenu.OrbitRadius(1, size, gap, minR)})",
                Mathf.IsEqualApprox(AttachmentMenu.OrbitRadius(1, size, gap, minR), minR));
            T.Check("zero icons does not divide by zero", AttachmentMenu.OrbitRadius(0, size, gap, minR) == minR);
            // The ring grows with the count -- a fixed radius would pass the pair check at small n and fail at large.
            T.Check($"the ring grows as icons are added ({AttachmentMenu.OrbitRadius(3, size, gap, minR):0} -> {AttachmentMenu.OrbitRadius(12, size, gap, minR):0})",
                AttachmentMenu.OrbitRadius(12, size, gap, minR) > AttachmentMenu.OrbitRadius(3, size, gap, minR));

            // ---- SLOT COLOUR: grey / white / rarity, in that order of precedence.
            var dead = AttachmentMenu.SlotColor(canAccept: false, installed: null);
            var empty = AttachmentMenu.SlotColor(canAccept: true, installed: null);
            T.Check($"a slot that cannot take an attachment is GREY ({dead})",
                Mathf.IsEqualApprox(dead.R, dead.G, 0.05f) && Mathf.IsEqualApprox(dead.G, dead.B, 0.06f) && dead.R < 0.6f);
            T.Check($"...and dimmed, so a dead slot does not draw the eye ({dead.A:0.00})", dead.A < 1f);
            T.Check($"an empty slot that CAN take one is white ({empty})", empty == Colors.White);

            // The rarity leg, against the real colour table rather than a copy of it -- a second table would agree
            // with itself forever while disagreeing with the inventory tile next to it.
            foreach (var rar in new[] { EItemRarity.COMMON, EItemRarity.UNCOMMON, EItemRarity.RARE, EItemRarity.EPIC, EItemRarity.LEGENDARY })
            {
                var asset = new ItemAsset { id = 1, rarity = rar };
                var got = AttachmentMenu.SlotColor(canAccept: true, installed: asset);
                T.Check($"a filled slot wears the attachment's own {rar} colour ({got})", got == ItemTool.RarityColorUI(rar));
            }
            // ...and a filled slot must NOT read as merely-available. If rarity ever collapses to white the feature is
            // gone and the slot still looks perfectly fine.
            var legendary = AttachmentMenu.SlotColor(true, new ItemAsset { id = 1, rarity = EItemRarity.LEGENDARY });
            T.Check($"a filled slot is distinguishable from an empty one ({legendary} vs {empty})", legendary != empty);
            // A dead slot stays grey even if something is somehow recorded in it -- "can't accept" wins, or a gun
            // without the hook would advertise an attachment it cannot show.
            T.Check("can't-accept beats has-attachment",
                AttachmentMenu.SlotColor(false, new ItemAsset { id = 1, rarity = EItemRarity.LEGENDARY }) == dead);

            yield break;
        }
    }
}
