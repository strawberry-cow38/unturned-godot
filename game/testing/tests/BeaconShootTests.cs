using System.Collections.Generic;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // Review fix: the beacon's Health field was dead code -- you couldn't destroy it to cancel the horde. It now has a
    // bullet-hittable collider (props layer bit 6, tagged "beacon") that StepBullets routes to Beacon.TakeDamage, and
    // dropping Health to 0 cancels the horde with NO loot (source: a destroyed beacon barricade -> ManualOnDestroy).
    public class BeaconShotDownCancelsHorde : GameTest
    {
        public override string Name => "beacon.shot_down_cancels_horde";
        public override IEnumerable<Step> Run()
        {
            var defender = new Node3D();
            World.AddChild(defender);
            defender.GlobalPosition = new Vector3(30f, 0f, 0f);   // valid + far, so the beacon doesn't abandon

            var beacon = new Beacon { Wave = 8, MaxAlive = 4, Health = 80f };
            World.AddChild(beacon);
            beacon.GlobalPosition = Vector3.Zero;
            beacon.Activate(defender);
            yield return Ticks(2);
            T.Check($"beacon active with a live horde (alive={beacon.Alive})", beacon.Active && beacon.Alive > 0);

            // the bullet-wiring precondition: a "beacon"-tagged collider on the props/bullet layer (bit 6) so a fired
            // bullet's ray (StepBullets mask includes bit 6) resolves to this Beacon and calls TakeDamage.
            StaticBody3D hitBody = null;
            foreach (var c in beacon.GetChildren())
                if (c is StaticBody3D sb && sb.HasMeta("beacon")) hitBody = sb;
            T.Check("beacon carries a 'beacon'-tagged collider", hitBody != null);
            T.Check("collider sits on the props/bullet layer (bit 6)", hitBody != null && (hitBody.CollisionLayer & (1u << 6)) != 0);
            T.Check("collider resolves back to this beacon", hitBody != null && hitBody.GetMeta("beacon").As<Beacon>() == beacon);

            // partial damage doesn't cancel it
            beacon.TakeDamage(50f);
            T.Check("a glancing hit (50 < 80) doesn't cancel the horde", !beacon.Done && beacon.Active);

            // finishing it off cancels the horde (no loot)
            beacon.TakeDamage(50f);
            T.Check("shooting the beacon to 0 HP cancels the horde (Done)", beacon.Done);
        }
    }
}
