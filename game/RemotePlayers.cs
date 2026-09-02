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

        sealed class Av
        {
            public RiggedCharacter Body; public PlayerInventory Inv; public PlayerClothingController Clothing; public ulong AppSig;
            public float Speed;          // smoothed horizontal glide speed -> idle/walk/run locomotion (the puppet used to be a frozen Idle pose)
            public Nameplate Plate;      // name + profile picture over the head
            public string PlateName;     // what the plate currently reads -- rebuild only on a change
            public ulong PlateAvatar = ulong.MaxValue;   // and which avatar hash it is showing (MaxValue = never set)
        }
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
                    GrassDisplacers.Register(av.Body, GrassDisplacers.PlayerRadius);   // master: remote players flatten grass just like the local one (retail's point covers only self)
                    av.Body.Position = target;
                    _avatars[e.OwnerPlayerId] = av;
                }
                Vector3 prev = av.Body.Position;
                bool snap = prev.DistanceTo(target) > SnapDistance;
                av.Body.Position = snap ? target : prev.Lerp(target, a);
                av.Body.Rotation = new Vector3(0f, Mathf.DegToRad(e.YawDegrees), 0f);

                // LOCOMOTION (master: real 3p rigs, not frozen dummies): the puppet's own horizontal glide velocity
                // tracks the remote player's speed in steady chase, so feed its magnitude to the SAME SetLocomotion the
                // local 3p body uses (PlayerController) -> idle/walk/run by speed. Skip the teleport frame so a snap
                // can't flash a sprint. (Stance + the held-gun layer need the Buttons/gun surfaced on the replica -- next.)
                if (!snap && delta > 0.0)
                {
                    Vector3 d = av.Body.Position - prev;
                    float inst = new Vector2(d.X, d.Z).Length() / (float)delta;
                    av.Speed = Mathf.Lerp(av.Speed, inst, 1f - Mathf.Exp(-8f * (float)delta));
                }
                av.Body.SetLocomotion(av.Speed);
                av.Body.Tick(delta);   // advance the rig, exactly like the local 3p body (PlayerController) -- required once the gun layer puts _ap in Manual; harmless no-op while gun-less

                // dress from the replicated appearance (cross-keyed by OwnerPlayerId); re-dress only on a change
                if (Client.CombatState.TryGet(e.OwnerPlayerId, out var ce))
                {
                    ulong sig = AppSig(ce);
                    if (sig != av.AppSig) { Dress(av, ce); av.AppSig = sig; }
                }

                // WHO THIS IS. Name + profile picture from the replicated profile block; the picture's bytes
                // arrive separately (the snapshot carries only a hash), so the plate is rebuilt when EITHER
                // changes -- including the moment the bytes finally land for a hash it already knew about.
                if (Client.Profiles.TryGet(e.OwnerPlayerId, out var prof))
                {
                    Client.Profiles.TryGetAvatar(e.OwnerPlayerId, out var png);
                    // Key on 0 while the bytes are still in flight, so their arrival counts as a change and
                    // the placeholder gets replaced rather than sticking until the player re-dresses.
                    ulong shown = png != null ? prof.AvatarHash : 0UL;
                    if (av.PlateName != prof.Name || av.PlateAvatar != shown)
                    {
                        av.Plate ??= Nameplate.Attach(av.Body);
                        av.Plate?.Set(prof.Name, png);
                        av.PlateName = prof.Name;
                        av.PlateAvatar = shown;
                    }
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
