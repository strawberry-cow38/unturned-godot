using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>Oxygen actually drains when your head is under (master 2026-09-07: "its also not depleting
    /// underwater").
    ///
    /// The bar shipped with sim-level tests that call Step directly with submerged:true -- which proves the
    /// RULE and says nothing about whether the shell ever passes true. This drives the real shell against a
    /// real water level instead, which is the seam the report is about.
    ///
    /// The gate is HeadUnderwater = pos.Y + EyeHeight &lt; SeaLevelY, deliberately the head rather than the
    /// +1.25 chest probe that starts the swim stance -- treading water with your face in the air must not cost
    /// a breath. So the two are half a metre apart, and this pins BOTH ends of that: submerged drains, and a
    /// player floating with their head clear does not.</summary>
    public sealed class OxygenDrainTests : GameTest
    {
        public override string Name => "player.oxygen_drains";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            bool hadWater = Terrain.HasWater; float hadSea = Terrain.SeaLevelY;
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 20f;

            // WELL under: head (pos.Y + 1.75) is 8 m below the surface, so there is no argument about the gate.
            var p = Rigs.Player(World, new Vector3(0f, 10f, 0f));
            yield return Ticks(2);

            T.Check($"the shell agrees the head is under (y={p.GlobalPosition.Y:0.0}, sea={Terrain.SeaLevelY:0.0})",
                    p.HeadUnderwater);
            T.Check($"oxygen starts full ({p.Oxygen:0.000})", p.Oxygen > 0.999f);

            float start = p.Oxygen;
            for (int i = 0; i < 150; i++) yield return Ticks(1);   // ~3 s at 50 Hz
            float after = p.Oxygen;
            T.Check($"submerged, oxygen DRAINS ({start:0.000} -> {after:0.000})", after < start - 0.01f);

            // ...and comes back at the surface. Lift the player clear rather than moving the sea, so the thing
            // under test is the same predicate the game evaluates.
            p.GlobalPosition = new Vector3(0f, 40f, 0f);
            yield return Ticks(2);
            T.Check("head clear of the water", !p.HeadUnderwater);
            float dry = p.Oxygen;
            for (int i = 0; i < 150; i++) yield return Ticks(1);
            T.Check($"surfaced, oxygen REFILLS ({dry:0.000} -> {p.Oxygen:0.000})", p.Oxygen > dry + 0.01f);

            // THE OTHER END: floating with the head clear must not cost a breath. Feet below the surface,
            // eyes above it -- the case the chest probe would call "swimming" and the head probe must not
            // call "drowning".
            p.GlobalPosition = new Vector3(0f, Terrain.SeaLevelY - 1.0f, 0f);   // head at sea+0.75
            yield return Ticks(2);
            T.Check($"treading water: feet under, head clear (y={p.GlobalPosition.Y:0.0})", !p.HeadUnderwater);
            float bob = p.Oxygen;
            for (int i = 0; i < 100; i++) yield return Ticks(1);
            T.Check($"...costs no air ({bob:0.000} -> {p.Oxygen:0.000})", p.Oxygen >= bob - 0.001f);

            Terrain.HasWater = hadWater; Terrain.SeaLevelY = hadSea;
            p.QueueFree();
        }
    }
}
