using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Remote player avatars in a REAL world (MP_PLAN §4 Phase 4 + B10 appearance): one DRESSED RiggedCharacter
    // puppet per remote player -- position-smoothed toward the 25 Hz transform (Client.Players), worn clothing
    // driven off the combat-block appearance (Client.CombatState, published by PlayerAppearanceNetSync). Spawned
    // on first sight, re-dressed only when the replicated worn set changes, freed when the player leaves. The
    // LOCAL player never gets a puppet -- that's the PlayerController shell (loopback) or the prediction path.
    public partial class RemotePlayers : Node3D
    {
        public NetWorldClient Client;

        const float GlideRate = 14f;      // 1/s exponential approach to the replicated target
        const float SnapDistance = 5f;    // beyond this the glide would look like skating -> snap

        sealed class Av { public RiggedCharacter Body; public PlayerInventory Inv; public PlayerClothingController Clothing; public ulong AppSig; }
        readonly Dictionary<ushort, Av> _avatars = new();
        static readonly Color Skin = new Color(0.82f, 0.66f, 0.52f);   // the 3P body skin (matches PlayerController._body)

        public int PuppetCount => _avatars.Count;
        public bool TryGetPuppet(ushort playerId, out Node3D avatar)
        {
            if (_avatars.TryGetValue(playerId, out var av) && IsInstanceValid(av.Body)) { avatar = av.Body; return true; }
            avatar = null; return false;
        }
        // L1/test hook + the source of truth for what a puppet wears (the visual textures may not load headless,
        // but the worn STATE always reflects the replicated appearance).
        public bool TryGetWorn(ushort playerId, out PlayerInventory inv)
        {
            if (_avatars.TryGetValue(playerId, out var av)) { inv = av.Inv; return true; }
            inv = null; return false;
        }

        public override void _Process(double delta)
        {
            if (Client == null) return;
            float a = 1f - Mathf.Exp(-GlideRate * (float)delta);

            foreach (var e in Client.Players.All)
            {
                if (e.OwnerPlayerId == Client.PlayerId) continue;   // self is the shell, never a puppet
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_avatars.TryGetValue(e.OwnerPlayerId, out var av) || !IsInstanceValid(av.Body))
                {
                    av = Build();
                    if (av == null) continue;
                    AddChild(av.Body);
                    av.Body.Position = target;
                    _avatars[e.OwnerPlayerId] = av;
                }
                av.Body.Position = av.Body.Position.DistanceTo(target) > SnapDistance ? target : av.Body.Position.Lerp(target, a);
                av.Body.Rotation = new Vector3(0f, Mathf.DegToRad(e.YawDegrees), 0f);

                // dress from the replicated appearance (cross-keyed by OwnerPlayerId); re-dress only on a change
                if (Client.CombatState.TryGet(e.OwnerPlayerId, out var ce))
                {
                    ulong sig = AppSig(ce);
                    if (sig != av.AppSig) { Dress(av, ce); av.AppSig = sig; }
                }
            }

            if (_avatars.Count > 0)   // a player left -> free the stale puppet
            {
                List<ushort> stale = null;
                foreach (var kv in _avatars)
                    if (!Client.Players.TryGetByOwner(kv.Key, out _)) (stale ??= new List<ushort>()).Add(kv.Key);
                if (stale != null)
                    foreach (var id in stale) { if (IsInstanceValid(_avatars[id].Body)) _avatars[id].Body.QueueFree(); _avatars.Remove(id); }
            }
        }

        static Av Build()
        {
            var body = RiggedCharacter.Build("res://content/rig.json", Skin);
            if (body == null) return null;
            body.PlayLoop("Idle");   // a standing idle pose (the puppet body isn't ticked -> the clip's rest frame)
            var inv = new PlayerInventory();
            return new Av { Body = body, Inv = inv, Clothing = new PlayerClothingController(body, inv), AppSig = ulong.MaxValue };
        }

        // Reconstruct the worn slots from the replicated ids, then Refresh() paints/attaches every slot -- the
        // exact PlayerClothingController the local 3P body uses, so a joiner sees the same outfit the wearer does.
        static void Dress(Av av, PlayerCombatReplication.CombatEntity ce)
        {
            ApplyWorn(av.Inv, ce);
            av.Clothing.Refresh();
        }

        /// <summary>Reconstruct the worn slots from the replicated appearance ids -- the render's core, exposed
        /// because a puppet only spawns for a networked REMOTE player (so the L1 exercises it directly).</summary>
        public static void ApplyWorn(PlayerInventory inv, PlayerCombatReplication.CombatEntity ce)
        {
            inv.wearShirt(ce.WornShirt != 0 ? new Item(ce.WornShirt) : null);
            inv.wearPants(ce.WornPants != 0 ? new Item(ce.WornPants) : null);
            inv.wearHat(ce.WornHat != 0 ? new Item(ce.WornHat) : null);
            inv.wearVest(ce.WornVest != 0 ? new Item(ce.WornVest) : null);
            inv.wearMask(ce.WornMask != 0 ? new Item(ce.WornMask) : null);
            inv.wearGlasses(ce.WornGlasses != 0 ? new Item(ce.WornGlasses) : null);
            inv.wearBackpack(ce.WornBackpack != 0 ? new Item(ce.WornBackpack) : null);
        }

        static ulong AppSig(PlayerCombatReplication.CombatEntity ce)
        {
            ulong h = 1469598103934665603UL;
            void M(ushort v) { h = (h ^ v) * 1099511628211UL; }
            M(ce.WornShirt); M(ce.WornPants); M(ce.WornHat); M(ce.WornVest);
            M(ce.WornMask); M(ce.WornGlasses); M(ce.WornBackpack);
            return h;
        }
    }
}
