using Godot;

namespace UnturnedGodot
{
    // The barricade REPAIR + SALVAGE tool logic (the blowtorch's build-side behaviour), packaged as a reusable,
    // testable unit. Deployable already carries the HP verbs — Hurt / Repair / IsWreck / WreckOnFire / Salvage /
    // Pickup (Deployable.cs:286,287,479,482,492,525) — and PlayerController.UpdateSalvage (PlayerController.cs:
    // 1435-1482) already drives blowtorch REPAIR of a hurt live piece + SALVAGE of a burnt WRECK into scrap. This
    // packages that and adds the piece the deployable path lacked for barricades: salvaging a LIVE (intact)
    // barricade to RECLAIM it — Unturned's hold-to-salvage (BarricadeManager.askSalvageBarricade returns the
    // barricade item to the owner), as opposed to a cold wreck that breaks into Metal Scrap.
    //
    // Structures reuse the same verbs at merge via IRepairable, so tinyclaw's walls repair/salvage through this same
    // tool without feat/barricades referencing StructureManager.
    public static class RepairTool
    {
        public const float RepairRate = 30f;   // src blowtorch continuous heal: (blowtorch VehicleDamage 10) * 3 HP/s (PlayerController.cs:1442)
        public const float SalvageTime = 3f;    // src: hold the salvage interaction this long to reclaim (PlayerController.SalvageTime)
        public const ushort MetalScrapId = 67;  // wreck teardown yield (DeployableNetSchema.cs:32, Deployable.Salvage)

        // Heal a hurt, non-wreck barricade toward max at the blowtorch rate. Returns the HP ACTUALLY restored (0 if it
        // couldn't heal, and capped at the real deficit near max) — a caller bills materials off the true amount, never
        // an over-repair. Same "don't lie to callers" convention the structure repair tool uses.
        public static float Repair(Deployable target, float dt)
        {
            if (target == null || !GodotObject.IsInstanceValid(target) || target.IsWreck || !target.Hurt) return 0f;
            float before = target.Health;
            target.Repair(RepairRate * dt);   // Deployable.Repair clamps at HealthMax
            return target.Health - before;
        }

        public enum SalvageState { NoTarget, TooHot, InProgress, Done }

        // What the caller should grant to the salvager's INVENTORY on a completed salvage:
        //  - a LIVE barricade -> its own item, count 1 (reclaim; the caller does the actual grant).
        //  - a cold WRECK     -> nothing to grant (ItemId 0): Deployable.Salvage already drops the scrap in the world.
        public struct Refund { public ushort ItemId; public int Count; }

        // Accumulate salvage-hold progress against a target; when `held` crosses SalvageTime, tear it down and report
        // the inventory Refund. A still-burning wreck is TooHot and resets the hold (src "Too hot to salvage").
        public static SalvageState Tick(Deployable target, ref float held, float dt, out Refund refund)
        {
            refund = default;
            if (target == null || !GodotObject.IsInstanceValid(target)) { held = 0f; return SalvageState.NoTarget; }
            if (target.IsWreck && target.WreckOnFire) { held = 0f; return SalvageState.TooHot; }
            held += dt;
            if (held < SalvageTime) return SalvageState.InProgress;
            held = 0f;
            if (target.IsWreck)
            {
                target.Salvage();   // cold husk -> Metal Scrap dropped in-world; nothing for the caller to grant
            }
            else
            {
                refund = new Refund { ItemId = target.Def != null ? target.Def.Id : (ushort)0, Count = 1 };   // reclaim the barricade item (caller grants it)
                target.Pickup();
            }
            return SalvageState.Done;
        }
    }
}
