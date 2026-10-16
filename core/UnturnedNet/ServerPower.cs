using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// v78: the server half of Deployable._PhysicsProcess's power simulation, over the replicated graph instead of
    /// scene nodes. Per fixed tick, in the source's order: side triggers (a TurnOn/TurnOff port fed at least 1 W sets
    /// a switch or commands a generator), the generator's load-scaled fuel burn and its dry cut-out, the battery's
    /// OUT discharge and IN charge, and the turbine's wind factor (local wind x height above sea, capped at 2).
    ///
    /// The unified source game ran these on each CLIENT's replica nodes: battery charge and wind were per-client and
    /// never reached the server's solve (DUPLICATE_AUDIT 2.1/2.4), and the replica rewrote every node's fuel from an
    /// entity nothing ever burned, so generators never ran dry. Here the server owns the result and replicates it;
    /// clients only render it. The generator's 1.3 s/1.1 s spin-up remains client presentation, as in the unified
    /// server's solve -- see DeployableEntity.Producing.
    ///
    /// Stepped by the HOST, like ServerItemMovers, so a bare harness that never asked for it is unchanged.
    /// Step-driven toggles (triggers, running dry) replicate through the entity snapshot rather than a reliable
    /// toggle event each: a player-built trigger oscillator would otherwise send one event per tick.
    /// </summary>
    public sealed class ServerPower
    {
        /// <summary>DeployableDef.GenFuelBurnPerSec: mL per second at full load (UG_GENBURN tunes it, as the source).</summary>
        public static readonly float FuelBurnPerSecond =
            float.TryParse(Environment.GetEnvironmentVariable("UG_GENBURN"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : 100f;
        /// <summary>Deployable._PhysicsProcess: idle burns 20%, full load 100%.</summary>
        public const float IdleBurn = 0.2f, LoadBurn = 0.8f;
        /// <summary>Deployable._PhysicsProcess: +1x wind per 40 m above sea, at most 2x, and a factor cap of 2.</summary>
        public const float WindHeightSpan = 40f, WindMax = 2f;
        /// <summary>A side trigger is "fed" at this many watts (DUPLICATE_AUDIT 1.13's bare literal).</summary>
        public const float TriggerWatts = 1f;
        /// <summary>Deployable._lastWindDirty: the net re-solves only when the wind moved more than this.</summary>
        public const float WindResolve = 0.04f;

        readonly DeployableReplication _deployables;
        readonly Func<long> _tick;
        long _solvedVersion = -1;

        /// <summary>WindField.SampleWind at a world point: 0..1 local strength. Null = a calm world.</summary>
        public Func<Vector3, float> WindAt;
        /// <summary>The map's sea level, the turbine's height reference (Terrain.SeaLevelY).</summary>
        public Func<float> SeaLevel = () => 0f;

        public ServerPower(DeployableReplication deployables, Func<long> tick)
        {
            _deployables = deployables;
            _tick = tick;
        }

        public void Step(float dt)
        {
            if (dt <= 0f) return;
            bool any = false;
            foreach (var e in _deployables.All)
                if (_deployables.Schema.TryGet(e.DefId, out var d) && (d.IsGenerator || d.IsSwitch || d.IsBattery || d.IsWindTurbine))
                { any = true; break; }
            if (!any) return;
            // Every device reads the port values of the last recompute, as each source node read its ports.
            // Every Solve input moves GraphVersion, so an unchanged circuit keeps its solve.
            if (_deployables.GraphVersion != _solvedVersion) { _deployables.Solve(); _solvedVersion = _deployables.GraphVersion; }
            long tick = _tick();
            var entities = new List<DeployableReplication.DeployableEntity>(_deployables.All);
            foreach (var e in entities)
            {
                if (!_deployables.Schema.TryGet(e.DefId, out var def)) continue;
                var ports = e.Solved;
                if (def.IsSwitch || def.IsGenerator) Triggers(e, def, ports);
                float fuel = e.SimFuel, energy = e.SimEnergy, wind = e.Wind;
                int output = Last(def, PowerPortKind.Output), consumer = Last(def, PowerPortKind.Consumer);
                float draw = output >= 0 && output < ports.Length ? ports[output].Draw : 0f;
                if (def.IsGenerator && e.ToggledOn && !e.OnFire && fuel > 0f)
                {
                    float watts = output >= 0 ? def.Ports[output].Watts : 0f;
                    float load = watts > 0f ? Mathf.Clamp(draw / watts, 0f, 1f) : 0f;
                    fuel = Mathf.Max(0f, fuel - FuelBurnPerSecond * (IdleBurn + LoadBurn * load) * dt);
                    // Ran dry: the toggle drops OFF and needs a refuel plus a manual restart, not an auto-resume.
                    if (fuel <= 0f) SetToggle(e, false, tick);
                }
                if (def.IsBattery)
                {
                    if (draw > 0f) energy = Mathf.Max(0f, energy - draw * dt);
                    if (consumer >= 0 && consumer < ports.Length && ports[consumer].Powered && energy < def.EnergyMax)
                        energy = Mathf.Min(def.EnergyMax, energy + def.ChargeWatts * dt);
                }
                if (def.IsWindTurbine)
                {
                    float height = 1f + Mathf.Clamp((e.Pos.y - SeaLevel()) / WindHeightSpan, 0f, 1f);
                    float live = Mathf.Min(WindMax, (WindAt?.Invoke(e.Pos) ?? 0f) * height);
                    // The accepted factor (output cap, bar, blade spin) moves when the source would re-solve.
                    if (Mathf.Abs(live - e.Wind) > WindResolve) wind = live;
                }
                if (def.IsGenerator || def.IsBattery || def.IsWindTurbine)
                    _deployables.ServerSetPowerState(e, fuel, energy, wind, tick);
            }
        }

        // Deployable._PhysicsProcess remote control, port order: a switch takes the fed trigger's state; a
        // generator starts only with fuel and stops unconditionally. Triggers draw 0 W, so Live is the feed.
        // The ports apply in order to one state, as the source's local bool, and only the result is accepted.
        void Triggers(DeployableReplication.DeployableEntity e, DeployableNetDef def, DeployableReplication.PowerPortResult[] ports)
        {
            bool on = e.ToggledOn;
            for (int i = 0; i < def.Ports.Length && i < ports.Length; i++)
            {
                byte role = def.Ports[i].Role;
                if (role == 0 || ports[i].Live < TriggerWatts) continue;
                bool wantOn = role == 1;
                if (def.IsSwitch) on = wantOn;
                else if (wantOn && !on && e.Fuel > 0f) on = true;
                else if (!wantOn && on) on = false;
            }
            SetToggle(e, on, _tick());
        }

        void SetToggle(DeployableReplication.DeployableEntity e, bool on, long tick) =>
            _deployables.ServerToggle(e.NetIdValue, on, tick);

        // Deployable.Spawn keeps the LAST consumer/output port it builds as _consumerPort/_outputPort.
        static int Last(DeployableNetDef def, PowerPortKind kind)
        {
            for (int i = def.Ports.Length - 1; i >= 0; i--) if (def.Ports[i].Kind == (byte)kind) return i;
            return -1;
        }
    }
}
