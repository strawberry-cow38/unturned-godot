using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot
{
    // Contaminated ground: the volumes, and the per-player accounting of standing in one.
    //
    // This finishes a seam that was already half-built. ClothingDef parses Proof_Radiation off item data,
    // PlayerInventory can now report which slots carry it -- and until now nothing in the world ever
    // produced radiation, so the flag protected against a hazard that did not exist. A protective stat
    // with nothing to protect from is a stat nobody can tell is broken.
    //
    // The arithmetic (grace on entry, suit resolution, filter burn-down, dose per second) is engine-free
    // in SDG.Unturned.DeadzoneSim and L0-tested there. This node owns only the world: which volumes exist,
    // who is standing in one, and applying the result through the player's normal infection path.
    //
    // ⚠ INFECTION IS THE ONLY OUTPUT (strawberry 2026-09-11: "wire deadzones to deal infection damage
    // instead of hp"). It also publishes PlayerController.DeadzoneSeconds, which the HUD's radiation icon
    // and DeadzoneOverlay's grain both read -- from the sim's own clock, so what you see on screen cannot
    // drift from the dose you are taking.
    public partial class DeadzoneField : Node3D
    {
        readonly List<DeadzoneVolumeDef> _volumes = new();
        readonly Dictionary<PlayerController, DeadzoneSim> _inside = new();
        readonly List<PlayerController> _stale = new();   // reused scratch, see _PhysicsProcess
        double _acc;

        /// <summary>How often the field re-evaluates. Deadzones are slow hazards; polling every physics
        /// tick would be per-player work for no felt difference.</summary>
        const double PollSeconds = 0.25;

        public int VolumeCount => _volumes.Count;

        /// <summary>The built volumes, for the server-side copy. They are authored world data rather than
        /// live state, so the MP host copies them across at boot instead of replicating them.</summary>
        public IReadOnlyList<DeadzoneVolumeDef> Volumes => _volumes;

        public void AddVolume(Vector3 center, Vector3 halfExtent, DeadzoneKind kind = DeadzoneKind.Radiation)
            => AddVolume(center, halfExtent, DeadzoneDef.Default(kind));

        public void AddVolume(Vector3 center, Vector3 halfExtent, DeadzoneDef zone)
        {
            _volumes.Add(new DeadzoneVolumeDef
            {
                Center = new UVector3(center.X, center.Y, center.Z),
                HalfExtent = new UVector3(halfExtent.X, halfExtent.Y, halfExtent.Z),
                Zone = zone,
            });
        }

        /// <summary>The volume containing a point, if any.</summary>
        public bool TryGetVolume(Vector3 p, out DeadzoneVolumeDef found)
        {
            var q = new UVector3(p.X, p.Y, p.Z);
            foreach (var v in _volumes)
                if (v.Contains(q)) { found = v; return true; }
            found = default;
            return false;
        }

        public bool IsInside(Vector3 p) => TryGetVolume(p, out _);

        public override void _PhysicsProcess(double delta)
        {
            _acc += delta;
            if (_acc < PollSeconds) return;
            float dt = (float)_acc;
            _acc = 0;

            foreach (var player in PlayerRegistry.All)
            {
                if (!IsInstanceValid(player)) continue;
                Apply(player, dt);
            }

            // Forget players who left the world entirely, so their accrued state does not linger.
            // Scratch list reused rather than allocated per poll (this codebase already paid for that
            // pattern once with the per-frame query objects).
            _stale.Clear();
            foreach (var kv in _inside) if (!IsInstanceValid(kv.Key)) _stale.Add(kv.Key);
            foreach (var k in _stale) _inside.Remove(k);
        }

        /// <summary>One player, one step. Public so the L1 tests can drive it deterministically rather
        /// than waiting on wall time.</summary>
        public void Apply(PlayerController player, float dt)
        {
            if (player == null || !IsInstanceValid(player)) return;

            if (!TryGetVolume(player.GlobalPosition, out var volume))
            {
                if (_inside.TryGetValue(player, out var left)) { left.Exit(); _inside.Remove(player); }
                player.DeadzoneSeconds = 0f;   // the overlay and the HUD icon both read this; leaving must clear it
                return;
            }

            if (!_inside.TryGetValue(player, out var sim))
            {
                sim = new DeadzoneSim();
                _inside[player] = sim;
            }

            var gear = player.Inventory?.RadiationProtection() ?? default;
            // EDGE IS TAMER (strawberry 2026-09-11: "a warning to turn around"). Intensity comes off where in
            // the volume you are standing, so the boundary still builds -- just slowly enough to be a warning.
            var here = player.GlobalPosition;
            float intensity = volume.Intensity(new UVector3(here.X, here.Y, here.Z));
            var r = sim.Step(volume.Zone, gear, dt, intensity);

            // Exposure time, published for the screen effect and the HUD icon. Set from the sim rather than
            // accumulated here, so there is one clock and the visuals cannot drift from the dose.
            player.DeadzoneSeconds = sim.SecondsInside;

            // RADIATION, and infection as its scar (strawberry 2026-09-11: "radiation is a separate hidden
            // thing different from infection"). One call so the two cannot drift: Irradiate raises the dose and
            // scars you in the same step. Health is never touched here -- PlayerVitalsSim owns what a high
            // infection costs, so a deadzone death runs the same path a zombie bite does.
            if (r.Radiation > 0f) player.AbsorbDose(r.Radiation, dt);
            if (r.MaskQualityLost > 0 && player.Inventory?.wornMask != null)
            {
                var mask = player.Inventory.wornMask;
                // The filter is the consumable, not the mask: burning it down is what eventually drops
                // your protection while you are still wearing the thing.
                int q = mask.quality - r.MaskQualityLost;
                mask.quality = (byte)Mathf.Clamp(q, 0, 100);
            }
        }

        /// <summary>Seconds this player has stood in their current zone; 0 if they are not in one.</summary>
        public float DebugSecondsInside(PlayerController player)
            => _inside.TryGetValue(player, out var s) ? s.SecondsInside : 0f;

        public bool DebugTracking(PlayerController player) => _inside.ContainsKey(player);
    }
}
