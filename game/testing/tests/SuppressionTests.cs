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
            T.Check($"an unsuppressed rifle draws a tracer ({p.DebugTracerCount} of {p.DebugBulletCount} bullets)",
                p.DebugTracerCount > 0);
            yield return Ticks(1);

            // Same gun, can fitted -> the attachment path.
            yield return Ticks(30);
            p.SetSuppressor(true);
            yield return Ticks(5);
            T.Check("a fitted suppressor registers", p.Suppressed);
            p.Ammo = 30;
            int before = p.DebugTracerCount;
            T.Check("the suppressed rifle fired", p.Fire());
            int tSupp = p.DebugTracerCount, bSupp = p.DebugBulletCount;
            T.Check($"...and drew no new tracer ({tSupp} vs {before}, bullets {bSupp})", tSupp <= before);
            T.Check($"...while still spawning a real bullet ({bSupp})", bSupp > 0);
            yield return Ticks(1);
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
            // Read the counters IMMEDIATELY, with no tick in between. SpawnBullet runs synchronously inside
            // Fire(), so both are set the moment it returns -- and yielding first lets a bullet that hits
            // something be removed before it is counted, which reads as "no bullet was ever spawned". That is
            // what the first run of this after the recoil pass reported: bullets 0, because the shot connected
            // inside one tick rather than because suppression broke.
            p.DebugSetPitch(0f);   // don't inherit whatever the earlier shots' recoil left on the aim
            yield return Ticks(2);
            T.Check("the matamorez fired", p.Fire());
            int tracersNow = p.DebugTracerCount, bulletsNow = p.DebugBulletCount;
            T.Check($"...and drew no tracer ({tracersNow} vs {before2}, bullets {bulletsNow})",
                tracersNow <= before2);
            T.Check($"...while still spawning a real bullet ({bulletsNow})", bulletsNow > 0);
            yield return Ticks(1);

            p.QueueFree();
            yield break;
        }
    }
}
