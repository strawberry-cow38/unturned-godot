using NUnit.Framework;
using SDG.Unturned;

/// <summary>The gun .dat key behind the heavy-weapon movement penalty actually parses.
///
/// master 2026-10-05: "implement slower movement speed with heavy snipers, minigun and LMGs". Retail already
/// ships that for three of them -- nykorev and dragonfang declare `Equipable_Movement_Speed_Multiplier 0.95`
/// and fury declares 0.90 -- so ItemCatalog reads the number out of the data instead of hardcoding a guess.
///
/// ⚠ WHICH MAKES THE PARSE LOAD-BEARING IN A WAY A HARDCODED TABLE WOULD NOT BE. If this key failed to read,
/// ParseFloat would hand back its default and the three retail guns would silently carry NO penalty -- the
/// feature would look implemented, the code would look right, and the only symptom would be a minigun that
/// walks at full speed. That is the same shape as the ten retail skills that levelled up and changed nothing,
/// so the key gets a test of its own rather than being trusted because it is spelled correctly in a comment.
///
/// The document below is the real surrounding lines from Bundles/Items/Guns/Nykorev -- blank lines either
/// side and a quoted string above it -- because "the key parses on its own" and "the key parses where it
/// actually lives" are different claims and only the second one ships.</summary>
[TestFixture]
public class EquipableMovementSpeedTests
{
    const string NykorevExcerpt = @"ID 126
Type Gun
Caliber 10
Ammo_Max 200
Real_Weapon ""PKM""

Equipable_Movement_Speed_Multiplier 0.95

Range 150
";

    [Test]
    public void TheRetailKeyParsesWhereItActuallyLives()
    {
        IDatDictionary d = new DatParser().Parse(NykorevExcerpt);

        Assert.That(d.ParseFloat("Equipable_Movement_Speed_Multiplier", 0f), Is.EqualTo(0.95f).Within(1e-6f),
            "the retail LMG's own movement multiplier has to come back off the .dat, not fall through to a default");

        // Controls. The first proves the reader is not simply echoing the default it was handed, which is the
        // exact failure this test exists to catch -- a key that never matches looks identical to a key that
        // matches and happens to hold the default. The second proves neighbouring keys still read, so a pass
        // here cannot mean "the whole document parsed as one blob".
        Assert.That(d.ParseFloat("Equipable_Movement_Speed_Multiplier", 123f), Is.EqualTo(0.95f).Within(1e-6f),
            "control: changing the default must not change the answer");
        Assert.That(d.ParseFloat("Not_A_Real_Key", 123f), Is.EqualTo(123f).Within(1e-6f),
            "control: a key that is genuinely absent must fall through, or the lookup is matching anything");
        Assert.That(d.ParseInt32("Ammo_Max", 0), Is.EqualTo(200), "control: the belt-fed capacity beside it still reads");
    }

    [Test]
    public void AGunThatDeclaresNothingFallsThroughToNoPenalty()
    {
        // Most of the 59 ported guns say nothing about movement, and ItemCatalog treats a non-positive read as
        // "not declared" before consulting its own table. If this ever came back as 0 rather than the default,
        // every ordinary rifle in the game would pin the player's speed to zero.
        IDatDictionary d = new DatParser().Parse("ID 4\nType Gun\nAmmo_Max 30\n");
        Assert.That(d.ParseFloat("Equipable_Movement_Speed_Multiplier", 0f), Is.EqualTo(0f).Within(1e-6f));
    }
}
