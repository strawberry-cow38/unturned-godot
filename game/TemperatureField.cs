using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// The world's heat, as far as the game layer is concerned.
    ///
    /// A process-wide field rather than a node someone has to find and pass around, because the things
    /// that register heat (a placed campfire) and the thing that reads it (a player) never meet: retail
    /// has the same shape -- TemperatureTrigger registers on enable, PlayerLife asks the manager.
    ///
    /// It exists on BOTH ends. Burning does damage, which is the server's call, but a client that did
    /// not have the field would show a HUD that disagreed with the health it was being sent. Since
    /// deployables replicate, each end registering its own bubble gets both there for free -- no
    /// separate "here is where the fires are" message.
    /// </summary>
    public static class TemperatureField
    {
        public static readonly TemperatureSim Sim = new();

        /// <summary>Attach a deployable's authored heat volumes. Returns the handles so the caller can
        /// hand them back when it dies -- a bubble that outlives its fire is a permanently hot patch of
        /// ground that nothing is standing on.</summary>
        public static (int Warm, int Burn) Attach(DeployableDef def, Vector3 at)
        {
            int warm = def is { HeatWarmRadius: > 0f }
                ? Sim.Register(new UnityEngine.Vector3(at.X, at.Y, at.Z), def.HeatWarmRadius, PlayerTemperature.Warm) : 0;
            // Registered AFTER the warm one on purpose. Resolve is last-wins among non-burning bubbles
            // and burning is sticky, so this order is the one retail's own registration produces --
            // and the burning core has to be able to beat the warm sphere it sits inside.
            int burn = def is { HeatBurnRadius: > 0f }
                ? Sim.Register(new UnityEngine.Vector3(at.X, at.Y, at.Z), def.HeatBurnRadius, PlayerTemperature.Burning) : 0;
            return (warm, burn);
        }

        public static void Detach((int Warm, int Burn) handles)
        {
            if (handles.Warm != 0) Sim.Deregister(handles.Warm);
            if (handles.Burn != 0) Sim.Deregister(handles.Burn);
        }

        public static void Move((int Warm, int Burn) handles, Vector3 to)
        {
            var v = new UnityEngine.Vector3(to.X, to.Y, to.Z);
            if (handles.Warm != 0) Sim.Move(handles.Warm, v);
            if (handles.Burn != 0) Sim.Move(handles.Burn, v);
        }

        public static PlayerTemperature At(Vector3 point, bool fireproof = false) =>
            Sim.Resolve(new UnityEngine.Vector3(point.X, point.Y, point.Z), fireproof);

        /// <summary>Wipe the field between worlds. Without this a bubble from the last map survives into
        /// the next one and heats a patch of a level it was never placed in.</summary>
        public static void Clear() => Sim.Clear();
    }
}
