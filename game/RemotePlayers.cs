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
        public override void _Ready() { TickHub.AddProcess(this, HubProcess); SetProcess(false); }   // PERF: hub-ticked (see TickHub.AddProcess)
        public NetWorldClient Client;

        const float GlideRate = 14f;      // 1/s exponential approach to the replicated target
        const float SnapDistance = 5f;    // beyond this the glide would look like skating -> snap
        readonly RandomNumberGenerator _rng = new RandomNumberGenerator();   // footstep pitch jitter, same +/-6% the shell uses

        sealed class Av
        {
            public RiggedCharacter Body; public PlayerInventory Inv; public PlayerClothingController Clothing; public ulong AppSig;
            public float Speed;          // smoothed horizontal glide speed -> idle/walk/run locomotion (the puppet used to be a frozen Idle pose)
            public Nameplate Plate;      // name + profile picture over the head
            public string PlateName;     // what the plate currently reads -- rebuild only on a change
            public ulong PlateAvatar = ulong.MaxValue;   // and which avatar hash it is showing (MaxValue = never set)
            public AnimatableBody3D Hull;                // the thing you bump into (see RemotePlayerLayer)
            public CollisionShape3D HullShape;
            public CapsuleShape3D HullCapsule;
            public float HullHeight = -1f;               // last height the capsule was built at
            public bool HullSeated;                      // last seated state pushed to Disabled
            public float StrideAcc;                      // metres of ground covered since this puppet's last footstep
            public bool Grounded = true;                 // last probe result -- the false->true edge is a landing
            public string MeleeName;                     // melee model in the hand (null = none/fists) -- the hold pose + swing clips key off it
            public bool HeldGun;                         // a gun is in the hand (the overlay layer belongs to it, not to a melee swing)
            public float SwingLeft;                      // seconds of a remote melee swing still playing; back to the hold when it hits 0
        }
        readonly Dictionary<ushort, Av> _avatars = new();
        static readonly Color Skin = new Color(0.82f, 0.66f, 0.52f);   // the 3P body skin (matches PlayerController._body)

        /// <summary>Remote players are SOLID (strawberry 2026-09-03: "player vs player collision", and the
        /// in-game report "Yeah, no player collision." with the camera inside another player's torso).
        ///
        /// RiggedCharacter is a bare Node3D: until now a live puppet had no collider at all, and the only
        /// CollisionShape3D in it belongs to the RAGDOLL, on the ragdoll bit, built when the player dies. So
        /// you could walk through everybody.
        ///
        /// Its own bit rather than the world bit, because bit 0 is what a VEHICLE masks: put players there and
        /// a car stops dead on a pedestrian instead of running them over, which is the opposite of what the
        /// bumper Area3D is for ("the body's own mask ignores the enemy layer, so it plows through").
        /// PlayerController adds this bit to its own mask; nothing else does.
        ///
        /// AnimatableBody3D, not StaticBody3D: the puppet is moved by script every frame, and an animatable
        /// body carries that motion into the characters it touches instead of letting them sink in.</summary>
        public const uint RemotePlayerLayer = 1u << 14;

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

        // Who is aboard something, rebuilt ONCE per frame. The obvious shape -- ask "is this player seated"
        // per puppet -- is a scan of every vehicle per player per frame, which on a map with ~88 cars is
        // thousands of comparisons a frame to answer a question that changes when somebody presses F.
        readonly HashSet<ushort> _seated = new();
        void RefreshSeated()
        {
            _seated.Clear();
            if (Client == null) return;
            foreach (var v in Client.Vehicles.All)
            {
                if (v.DriverPlayerId != 0) _seated.Add(v.DriverPlayerId);
                var pax = v.Passengers;   // v20: a PASSENGER is seated too, and his hull must drop as well
                if (pax != null) foreach (ushort occ in pax) if (occ != 0) _seated.Add(occ);
            }
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (Client == null) return;
            float a = 1f - Mathf.Exp(-GlideRate * (float)delta);
            RefreshSeated();   // one pass over the vehicles, not one per puppet

            foreach (var e in Client.Players.All)
            {
                if (e.OwnerPlayerId == Client.PlayerId) continue;   // self is the shell, never a puppet
                var target = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);
                if (!_avatars.TryGetValue(e.OwnerPlayerId, out var av) || !IsInstanceValid(av.Body))
                {
                    av = Build();
                    if (av == null) continue;
                    AddChild(av.Body);
                    av.HullCapsule = new CapsuleShape3D { Height = SDG.Unturned.PlayerMovementDef.HEIGHT_STAND, Radius = 0.35f };
                    av.HullShape = new CollisionShape3D { Shape = av.HullCapsule };
                    av.Hull = new AnimatableBody3D { Name = "Hull", CollisionLayer = RemotePlayerLayer, CollisionMask = 0, SyncToPhysics = false };
                    av.Hull.AddChild(av.HullShape);
                    av.Body.AddChild(av.Hull);   // rides the puppet's transform
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
                bool aliveNow = !Client.CombatState.TryGet(e.OwnerPlayerId, out var ceAlive) || ceAlive.Alive;   // a corpse does not walk, and must not step
                // v18: the replicated stance code (0 STAND / 1 SPRINT / 2 CROUCH / 3 PRONE) -> crouch/prone poses, not just standing.
                var stance = e.Stance switch
                {
                    1 => SDG.Unturned.EPlayerStance.SPRINT,
                    2 => SDG.Unturned.EPlayerStance.CROUCH,
                    3 => SDG.Unturned.EPlayerStance.PRONE,
                    _ => SDG.Unturned.EPlayerStance.STAND,
                };
                // Match the local shell's capsule for this stance, so crawling under something you could crawl
                // under alone still works when someone is standing there.
                bool seated = _seated.Contains(e.OwnerPlayerId);
                if (IsInstanceValid(av.HullShape))
                {
                    float hh = SDG.Unturned.PlayerMovementDef.HeightForStance(stance);
                    if (Mathf.Abs(hh - av.HullHeight) > 0.001f)
                    {
                        av.HullCapsule.Height = hh;
                        av.HullShape.Position = new Vector3(0f, hh * 0.5f, 0f);
                        av.HullHeight = hh;
                    }
                    // A SEATED player's hull sits inside the car, and two solid bodies sharing a space is
                    // exactly the "getting into a vehicle on the server makes the physics freak out because
                    // player hit box overlaps" report. The local shell already disables its own shapes on
                    // seat confirm (PlayerController); this is the same move for everyone else.
                    if (seated != av.HullSeated) { av.HullShape.Disabled = seated; av.HullSeated = seated; }
                }
                // FOOTSTEPS + LANDINGS FOR EVERYONE ELSE. PlayerController runs this for the local shell only
                // ("Local player only here; puppets step in RemotePlayers"). A puppet has no IsOnFloor, no Velocity
                // and no grounded bit on the wire, so the SAME downward probe that names the surface also answers
                // whether there is any ground under it -- a miss is airborne, and an airborne puppet must not step
                // on nothing. Deliberately NOT a new snapshot field: this is cosmetic, and a wire bit would cost a
                // version bump and a re-golden to tell every client something it can already see for itself.
                float vy = delta > 0.0 ? (av.Body.Position.Y - prev.Y) / (float)delta : 0f;
                bool stepping = !seated && aliveNow && !snap && delta > 0.0;
                if (!stepping) { av.StrideAcc = 0f; av.Grounded = true; }   // seated/dead/teleported: reset, so rejoining the world can't fire a phantom landing
                else if (av.Speed > 0.3f || Mathf.Abs(vy) > 0.5f || !av.Grounded)   // a puppet standing still needs no ray at all
                {
                    var exclude = IsInstanceValid(av.Hull) ? av.Hull.GetRid() : default;
                    bool grounded = PlayerController.TryFootSurfaceAt(this, av.Body.GlobalPosition, exclude, out var psurf);
                    if (grounded && av.Speed > 0.3f)
                    {
                        float stride = stance switch { SDG.Unturned.EPlayerStance.SPRINT => 2.0f, SDG.Unturned.EPlayerStance.CROUCH => 1.0f, SDG.Unturned.EPlayerStance.PRONE => 0.9f, _ => 1.5f };
                        av.StrideAcc += av.Speed * (float)delta;
                        if (av.StrideAcc >= stride)
                        {
                            av.StrideAcc = 0f;
                            bool run = stance == SDG.Unturned.EPlayerStance.SPRINT || av.Speed > 4.5f;
                            var clip = GameAudio.PickFootstep(psurf, run);
                            float vol = stance switch { SDG.Unturned.EPlayerStance.PRONE => -14f, SDG.Unturned.EPlayerStance.CROUCH => -8f, SDG.Unturned.EPlayerStance.SPRINT => 0f, _ => -3f };
                            GameAudio.PlayAt(this, clip, av.Body.GlobalPosition, vol, 4f, 30f, _rng.RandfRange(0.94f, 1.06f));
                        }
                    }
                    else if (!grounded) av.StrideAcc = 0f;                              // airborne: land on a fresh stride, not half of one
                    else av.StrideAcc = Mathf.Min(av.StrideAcc, 0.9f);                  // stopped: keep most of it so the next step isn't instant (matches the shell)
                    // The frame the probe first finds ground again. The glide DAMPS the fall, so a puppet's landing
                    // reads softer than the shell's for the same drop -- right way round for a noise happening
                    // somewhere other than under your own feet.
                    if (grounded && !av.Grounded && vy < -2.5f)
                        GameAudio.PlayAt(this, GameAudio.Pick("landing", GameAudio.LandSurface(psurf)), av.Body.GlobalPosition, Mathf.Clamp(-9f + (-vy - 2.5f) * 1.2f, -9f, 2f), 5f, 40f);
                    av.Grounded = grounded;
                }
                av.Body.SetLocomotion(av.Speed, stance);
                av.Body.Tick(delta);
                if (av.SwingLeft > 0f)   // a remote melee swing is playing -> park back on the hold when it ends
                {
                    av.SwingLeft -= (float)delta;
                    if (av.SwingLeft <= 0f) { av.SwingLeft = 0f; if (av.MeleeName != null) av.Body.ShowMeleeHold(av.MeleeName); else if (!av.HeldGun) av.Body.DisableGunLayer(); }
                }   // advance the rig, exactly like the local 3p body (PlayerController) -- required once the gun layer puts _ap in Manual; harmless no-op while gun-less

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
            ApplyHeld(av, ce.HeldId);
        }

        /// <summary>The held weapon on a puppet (master 2026-09-03: "your melee weapons/guns shown to other players"): the same
        /// Right_Hook attach + gun overlay layer the local 3P body uses (PlayerController.UpdateBodyGun). Asset gunName -> gun,
        /// meleeName -> melee, anything else / 0 -> empty hands.</summary>
        /// <summary>EventPlayerMelee (v25): play that player's weak/strong swing on their puppet's upper body.</summary>
        public void OnRemoteMelee(ushort playerId, bool strong)
        {
            if (!_avatars.TryGetValue(playerId, out var av) || av.Body == null) return;
            float len = av.Body.PlayMeleeSwing(av.MeleeName ?? "fists", strong);
            if (len > 0f) av.SwingLeft = len;
        }
        static void ApplyHeld(Av av, ushort heldId)
        {
            var a = heldId != 0 ? SDG.Unturned.Assets.find(heldId) : null;
            string gun = a?.gunName, melee = a?.meleeName;
            av.MeleeName = null; av.HeldGun = false;
            if (!string.IsNullOrEmpty(gun))
            {
                av.HeldGun = true;
                av.Body.DetachMelee();
                av.Body.AttachGun(gun);
                string cap = char.ToUpper(gun[0]) + gun.Substring(1);
                string aim = av.Body.ClipLength(cap + "_Aim") > 0f ? cap + "_Aim" : "Gun_Aim";
                string equip = av.Body.ClipLength(cap + "_Equip") > 0f ? cap + "_Equip" : "Gun_Equip";
                if (!av.Body.GunLayerOn) av.Body.EnableGunLayer(aim); else av.Body.RebakeAim(aim);
                av.Body.SnapGunOverlay(equip);   // straight to the ready hold (the pull-out already happened on their screen)
            }
            else
            {
                av.Body.DetachGun(); av.Body.DisableGunLayer();
                if (!string.IsNullOrEmpty(melee)) { av.Body.AttachMelee(melee); av.Body.ShowMeleeHold(melee); av.MeleeName = melee; }   // ready-to-swing, like the local 3P body
                else av.Body.DetachMelee();
            }
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
            M(ce.WornMask); M(ce.WornGlasses); M(ce.WornBackpack); M(ce.HeldId);   // v22: a weapon swap re-dresses the hand
            return h;
        }
    }
}
