using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SUPPRESSED SHOTS DRAW NO TRACER (strawberry: "hide tracers when using a suppressed weapon. pdw and matamorez
    // are integrally suppressed, other guns can have the suppressed flag set via attachments"), and the matamorez
    // kicks like the subsonic marksman rifle it is ("reduce the matamorez recoil to be almost nothing").
    //
    // The load-bearing check here is the CONTROL. A test that only fires the suppressed guns and finds no tracer
    // cannot tell "the tracer was suppressed" from "no bullet was fired" -- and an equip that silently failed, a
    // fire-rate refusal, or a mis-parsed .dat all produce that same clean-looking zero. So an ordinary rifle fires
    // in the same run and is required to produce one.
    public sealed class SuppressionTests : GameTest
    {
        public override string Name => "gun.suppression";
        public override double TimeoutSimSeconds => 60;

        static GunDef Def(string g) =>
            GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/") + g + ".dat"));

        public override IEnumerable<Step> Run()
        {
            // ---- 1. who is integrally suppressed, from the .dat
            T.Check("honeybadger is integrally suppressed (AAC Honey Badger PDW)", Def("honeybadger").IntegrallySuppressed);
            T.Check("matamorez is integrally suppressed (VSS Vintorez)", Def("matamorez").IntegrallySuppressed);
            // Not everyone. A flag that reads true for every gun would pass every check below while deleting
            // tracers from the whole game.
            T.Check("an ordinary rifle is NOT", !Def("eaglefire").IntegrallySuppressed);
            T.Check("...nor the P90, which takes a can rather than being one", !Def("peacemaker").IntegrallySuppressed);

            // ---- 2. the matamorez barely kicks
            var mata = Def("matamorez");
            var sport = Def("sportshot");   // the .22 -- previously the softest thing in the game
            T.Check($"matamorez vertical recoil is tiny ({mata.RecoilMinY}-{mata.RecoilMaxY})",
                mata.RecoilMaxY > 0f && mata.RecoilMaxY <= 1.5f);
            T.Check($"...softer even than the .22 ({mata.RecoilMaxY} vs {sport.RecoilMaxY})",
                mata.RecoilMaxY < sport.RecoilMaxY);
            T.Check($"...and horizontal is near-symmetric and small ({mata.RecoilMinX} to {mata.RecoilMaxX})",
                Mathf.Abs(mata.RecoilMinX) <= 0.5f && Mathf.Abs(mata.RecoilMaxX) <= 0.5f);
            // Still a real gun, not a zeroed one: a gun with NO recoil at all reads as unset .dat fields rather
            // than as tuning, and this is the check that tells those apart.
            T.Check($"...but not zero ({mata.RecoilMaxY})", mata.RecoilMaxY > 0f);

            // ---- 3. live: the tracer actually stops being drawn
            Rigs.Ground(World);
            var p = new PlayerController { CaptureMouse = false, Inventory = new SDG.Unturned.PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(40);

            // CONTROL FIRST. If this does not draw a tracer, nothing below means anything.
            p.EquipHeldGun("eaglefire");
            // WAIT for the equip, do not count ticks at it. Fire() refuses until IsEquipComplete, and a fixed
            // tick budget that is one frame short fails as "the control rifle did not fire" -- which reads as a
            // broken fire path rather than an impatient test. Cost me exactly that on the first run.
            yield return Until(() => p.HeldItemReady, 6);
            T.Check("the control rifle finished equipping", p.HeldItemReady);
            p.Ammo = 30;
            yield return Ticks(2);
            p.DebugSetPitch(0f);
            yield return Ticks(2);
            T.Check("the control rifle fired", p.Fire());
            yield return Ticks(1);
            T.Check($"an unsuppressed rifle draws a tracer ({p.DebugTracerCount} of {p.DebugBulletCount} bullets)",
                p.DebugTracerCount > 0);

            // Same gun, can fitted -> the attachment path.
            yield return Ticks(30);
            p.SetSuppressor(true);
            yield return Ticks(5);
            T.Check("a fitted suppressor registers", p.Suppressed);
            p.Ammo = 30;
            int before = p.DebugTracerCount;
            T.Check("the suppressed rifle fired", p.Fire());
            yield return Ticks(1);
            T.Check($"...and drew no new tracer ({p.DebugTracerCount} vs {before}, bullets {p.DebugBulletCount})",
                p.DebugTracerCount <= before);
            p.SetSuppressor(false);

            // The integral path -- no attachment involved.
            yield return Ticks(30);
            p.EquipHeldGun("matamorez");
            yield return Until(() => p.HeldItemReady, 6);
            T.Check("the matamorez finished equipping", p.HeldItemReady);
            p.Ammo = 20;
            yield return Ticks(2);
            T.Check("the matamorez reports suppressed with no attachment", p.Suppressed);
            int before2 = p.DebugTracerCount;
            T.Check("the matamorez fired", p.Fire());
            yield return Ticks(1);
            T.Check($"...and drew no tracer ({p.DebugTracerCount} vs {before2}, bullets {p.DebugBulletCount})",
                p.DebugTracerCount <= before2);
            T.Check($"...while still spawning a real bullet ({p.DebugBulletCount})", p.DebugBulletCount > 0);

            p.QueueFree();
            yield break;
        }
    }
}
