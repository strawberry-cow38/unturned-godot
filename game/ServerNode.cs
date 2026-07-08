using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Headless dedicated SERVER: the authoritative NetServer + a scripted bot player (a local NetClient that
    // connects first -> id 1). Each physics tick: move the bot + send its state, drain client packets,
    // broadcast the world snapshot. No rendering -- runs under `godot --headless -- --server`.
    public partial class ServerNode : Node
    {
        public NetServer Server;
        public NetClient Bot;
        float _t;

        public override void _PhysicsProcess(double delta)
        {
            _t += (float)delta;
            var p = new Vector3(Mathf.Cos(_t * 0.7f) * 6.5f, 0f, Mathf.Sin(_t * 0.7f) * 6.5f); // feet on ground
            Bot.SendState(new PlayerState { X = p.X, Y = p.Y, Z = p.Z, Yaw = _t });
            Server.Poll();
            Server.TickZombies((float)delta); // authoritative zombie sim
            Server.Broadcast();
        }
    }
}
