using System;
using SDG.NetPak;

namespace UnturnedGodot.Net
{
    // Wire format v1 for the session layer (MP_PLAN §2.2 / §5 item 1). Every datagram starts with the
    // 83-bit packet header below; the version byte is the escape hatch for all future format changes.
    // Golden byte tests in tests/UnturnedNet.Tests lock this layout -- changing anything here must bump
    // Version and re-golden in the same commit.

    public enum NetChannel : byte
    {
        Control = 0,             // connect/accept/reject/disconnect/keepalive (keepalive doubles as ack carrier)
        ReliableOrdered = 1,     // msgId window + retransmit + fragmentation, delivered in order exactly once
        UnreliableSequenced = 2, // newest-seq-wins, stale datagrams dropped on the floor
    }

    public enum NetControlType : byte
    {
        Connect = 1,
        Accept = 2,
        Reject = 3,
        Disconnect = 4,
        KeepAlive = 5,
    }

    public enum NetRejectReason : byte
    {
        None = 0,
        VersionMismatch = 1,
        ServerFull = 2,
        ContentMismatch = 3,   // Connect carried a content hash that isn't ours (Phase 4 join gate)
    }

    public enum NetDisconnectReason : byte
    {
        None = 0,
        Timeout = 1,   // ~5 s of silence
        Rejected = 2,  // server refused the handshake (see RejectReason)
        Kicked = 3,    // remote sent Disconnect
        Requested = 4, // local app asked
    }

    public enum NetSessionState : byte
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
    }

    public static class NetProtocol
    {
        public const byte Magic = 0x75; // 'u'
        public const byte Version = 32; // v32 (craft-cancel): registers CommandCraftCancel(44) -- give up on a queued craft and take the ingredients back. v31 made crafting timed and server-authoritative but shipped only half the contract: ingredients are spent at ENQUEUE, and the ONLY thing that ever refunded them was disconnecting (RefundAll, on peer removal). There was no cancel on the wire at all. That is not merely a missing feature, because the client's queue tiles stayed clickable in MP and that click ran the SINGLE-PLAYER cancel path -- against a job whose PerUnit escrow list is null, since AdoptServerQueue cannot know what the server spent. It threw inside the gui handler before reaching the refund, so the tile sat there, the materials were gone, and the server kept crafting. Addressed by SLOT (position in the owner's queue, oldest first) rather than by blueprint index: three of the same recipe queued means a specific tile, and an index would cancel the wrong unit the moment the first finished between click and arrival. An out-of-range slot is REJECTED rather than clamped -- refunding a neighbour because the queue shifted under the packet is how a cancel prints materials. RMB-promote is a no-op in MP for the same reason it now sends nothing: there is no reorder command, and moving a display mirror would show a promotion the server never made. Previously v31 (timed crafting): EventCraftQueue(41) carries the owner's pending craft jobs. Crafting became TIMED on the server (master 2026-09-06 "add crafting timed jobs to the server") -- OnCraft used to call DoCraft in the same tick, so the recipe times added hours earlier were enforced by the SP client and ignored by the authoritative side, which is the wrong way round. Ingredients are spent at ENQUEUE, not at completion: checked-then-taken-later lets the same ten scrap fund ten jobs that all validate and all pay out, a duplication bug that looks like patience. The event exists because a server-side timer is invisible otherwise -- the MP client skips its local queue entirely, so without it an 8 s craft reads as a dropped command. Previously v30 (freezing): the item wire gains Item.frozen behind a gate bit, StorageOpenedEvent carries a FREEZER compartment's dimensions, and PlayerInventory grows a tenth page (FREEZER=9) that the generic per-page delta loops pick up for free. strawberry 2026-09-06: "add a 'frozen' state ... frozen acts as a %" + "in fridges, add a second container to the inventory ui above the fridge container". SERVER-OWNED for a sharper reason than cooked was: food at 100 % frozen NEVER spoils, so a client that could assert `frozen` could preserve its whole stockpile for free. The page count is the part that would bite silently -- forty call sites spelled "the player's own pages" as `PAGES - 2`, which silently meant STORAGE the moment a tenth page existed; they now read an explicit OWNPAGES. Previously v29 (fuel bar): StorageOpenedEvent gains the cooker's FUEL FRACTION and a new EventCookerState(40) unicasts it to the opener as it burns down -- strawberry 2026-09-06 "as each fuel item burns, show a progress bar before its consumed". A bar needs a value the client does not have: Fuel/FuelTotal live on the server's Cooker, and the inventory delta cannot carry them because a burning fuel item's REMAINING TIME is not a property of any Item in the grid (the item is consumed the moment it lights). Rate-limited at the source to ~1% buckets and to the one player who has the crate open, so an oven left running with nobody watching costs nothing. Previously v28 (cooking): the inventory ITEM wire gains Item.cooked + Item.cookStyle (one gate bit, then two bytes when set), and registers CommandSetCookerOn(43) -- the oven/toaster/microwave/bbq on-off button (strawberry 2026-09-05). ONE bump for both, per the never-bump-per-gap rule: the schema change and the command that drives it are one wave. The item fields are SERVER-owned (ServerCooking.Step writes them) because an oven left on has to keep cooking with no client near it, and `cooked` multiplies what a meal is worth -- a client allowed to assert it is a client allowed to print food. Gated behind a bit rather than unconditional like magLoadedRound's cartridge byte: that is one byte against magazines, this is two against cooked FOOD, so it is one bit for every bandage and 17 for a steak. Previously v27 (throwables): GrenadeCommand and GrenadeExplodedEvent each gain the throwable's ItemId (u16), and ProjectileKind spends two of the values its byte was already carrying (Smoke=1, Flare=2). NO new command/event/system id and NO snapshot framing change -- the projectile entity's kind byte was already written and read, it had simply only ever held 0. The bump is earned by the two payloads: before it the wire said "a grenade was thrown" and nothing more, so the server had exactly one profile and a smoke canister thrown in multiplayer would have gone off as a 175-damage frag while every client rendered a fireball instead of a cloud. The id is validated, not trusted -- ServerCombat.OnGrenade resolves it against SDG.Unturned.Throwables and REJECTS an id that is not in the table rather than defaulting it to the frag, which is what would otherwise let a client name a bandage and get a blast. Known gap, deliberately not fixed here: the in-flight visual on a remote client is the FAMILY (grenade/smoke/flare), not the exact item, so somebody else's thrown blue smoke flies as a generic canister and only becomes blue when it pops -- carrying the exact id in flight means widening the projectile SNAPSHOT, which re-goldens framing for a cosmetic. Previously v26 (faces): SetProfileCommand + the profile block carry the player's FACE (0..31, retail Items/Faces) -- strawberry 2026-09-04 "port player faces"
        // v25 (melee-swing): registers EventPlayerMelee(39) -- attacker + weak/strong, broadcast per accepted MeleeCommand so other clients animate the swing on that player's puppet (strawberry 2026-09-03 "third person cam doesnt show melee animation"). Previously v24 (vehicle-hp): the vehicle block's Health field widens 10+1 -> 16+1 bits (max 1023 -> 65535). The tank/ship 4000 was already being clipped to 1023 on the wire; "10x vehicle hp" (strawberry 2026-09-03) makes every car exceed it. Re-goldens the vehicle block. Previously v23 (hurt-feedback): registers EventPlayerHurt(38) -- to the VICTIM only, on any real hit: damage taken plus an optional source position. Before this an MP client's own body had NO feedback for a non-lethal hit at all: TakeDamage's flash/flinch (PainAlpha, PlayerLook.flinchLocalRotation) are source-exact but are gated off on a server-owned NetAvatar (C2: an unreplicated local death would desync everyone else), so a hit MP player learned they were hurt only by watching their replicated Health tick down on the next snapshot -- no flash, no flinch, no direction (strawberry 2026-09-03 "add directional visual hit feedback when you get hurt by something"; the flash/flinch gap was found on the way, not asked for). ApplyPlayerDamage now sends this to the victim on EVERY hit, including a killing blow, so the death screen still shows where the last shot came from. Source position is per call site: the bullet's Pos at hit time (not the impact point -- the indicator has to say which way to turn and face, not mark where the round ended its flight), the melee attacker's tracked position, or the grenade's blast centre; the queued external-damage path (fall/OOB/starvation/deadzones) has no attacker to point at and correctly sends none. HasSource is its own bit rather than trusting a zero vector, because a directional indicator quietly pointing at (0,0,0) would read as "the floor did this to you". Previously v22 (held-item): MoveInput carries HeldItemId (u16) after Buttons; the appearance block's HeldId (already on the wire, always 0 before) is now published from it. Previously v21 (mp-vehicle-cosmetics): FlagAlarmed(1<<6) and FlagAlarming(1<<7) claim the last two bits of the vehicle Flags byte. NO new field and no framing change -- the byte was always there and the golden vehicle block is byte-identical -- but the bits gain MEANING, which is exactly the kind of semantic-only wire change v18 bumped for. An unbumped client would silently show no alarms while its neighbour hears them. The real content is on the client: NOTHING read that flags byte. Both publishers (VehicleNetSync and ClientWorldSession) wrote headlights/taillights/siren/braking/exploded and no consumer existed anywhere, so on every screen but the driver's a car drove around DARK -- no headlights, no brake lights -- and silent. VehiclePuppet was a 155-line Node3D with no lamps at all, because BuildPuppetByName loaded the body WHOLE where Vehicle.Build splits the lens geometry out by zone; it now does the same split, carries the same lens materials and the same horn clip, and VehicleReplicaView mirrors the flags onto it. The alarm also stops being a per-client coin flip: Vehicle._alarmed was rolled at 5% independently on every machine, so no two players agreed which cars even HAVE alarms -- puppets are told now, and only a real Vehicle rolls. And its proximity trigger asked GetViewport().GetCamera3D(), which is null on a dedicated server, so the machine that owns every car could never fire one; Vehicle.AlarmProximityTest lets the side that knows where the players are answer instead (VehicleNetSync, off replicated positions), falling back to the camera so singleplayer is untouched. v20 (mp-multi-seat): PASSENGERS. The vehicle entity gains a Passengers block appended after the A6 tow fields -- a self-describing byte count then that many ushorts, seats 1..N, bounds-checked against MaxSeats(16) BEFORE the allocation. Seat 0 stays DriverPlayerId exactly where it was, because the driver is not merely an occupant: he owns the client-authority window and every existing reader of that field means precisely that. CommandEnterVehicle(24) gains a seat byte (255 = AnySeat, the first free one driver-first, which is what every pre-v20 caller meant and what pressing F at a car with no door zone still means) and EventVehicleEntered(23) gains the seat that was actually GRANTED -- not the one asked for, which may have been taken between the ask and the answer. Passengers ride the StateHash too, or occupancy could diverge between server and client invisibly. Before this the wire had exactly one seat and ServerEnter had no occupancy check at all, so a second player pressing F on an occupied car overwrote the driver, took his authority window, and left two clients holding contradictory truths about the same vehicle -- the in-game report "multiple people can't get in a car, and so when they tried it actually just like disappeared, somehow" (strawberry 2026-09-03 "add multiple riders in a car but one per seat"). Seat counts are NOT on the wire: both ends already resolve TypeId through the same spec table, so the game layer declares it server-side (ServerSetSeatCount from Vehicle.SeatCount, which SP has always had) and the server validates every request against it. ServerExit is keyed off the seat table rather than the driver table, or anyone in seat 1+ could never get out; OnVehicleRemoved evicts everybody aboard rather than the driver alone. v19 (mp-gunfire-fx): registers EventPlayerFired(37) -- shooter, muzzle origin, direction and the firing gun's asset name, broadcast once per ACCEPTED trigger pull. Nothing whatsoever was sent when a gun went off before this, so another player's shooting was silent and drew no tracer on your screen: the server resolved the hit analytically and unicast a confirm to the shooter, and every other client saw a body fall for no visible reason (strawberry 2026-09-03 "network gun sounds, gun tracers" + "tracers not networked"; the in-game report "I hear no gunshot sounds even when someone's shooting across the street from me" and "Other players can't see uh the bullet tracers"). No damage rides this event -- it is purely what a shot looks and sounds like to everybody else, and the client spawns a COSMETIC bullet (zero damage, npc-flagged so it takes the straight-from-origin streak rather than the local viewmodel's muzzle anchor) plus the same positional report the NPC-turret path already plays. The gun is an ASSET NAME because that is what resolves both the report (res://content/{name}_shoot.ogg) and the tracer width ({name}.dat); it is length-bounded before allocation and character-validated to [a-z0-9_] on read, since it is network input that becomes a file path. Today it always reads "eaglefire" -- every player fires ServerCombat.DefaultGun because SetGunProfile still has no callers (the deferred equip seam) -- and starts being correct on its own when that lands, with no second wire change. v18 (mp-puppet-pose): the player snapshot (PlayerReplication.WriteEntity) gains a stance byte -- the packed 0-3 code (STAND/SPRINT/CROUCH/PRONE) read off the owner's MoveInput each drive tick -- so a remote client's 3p puppet renders crouch/prone instead of everyone standing. One byte, no new command id; re-goldens the player-snapshot wire (StateHash). tinyclaw's zombie wave spends spare ZombieNetAnim enum values and needed no bump, so this is the single coordinated bump. v17 (player-profiles): a display name and a profile picture, both of them UNTRUSTED INPUT THAT EVERY OTHER CLIENT RENDERS (strawberry: "build a set png profile pic (auto squished to 128px) and set username in the launcher... secured against any kinda sql injection or w/e... protected on the server side too"). There is no SQL in this project, so the honest reading is: make the fields safe against what they are actually pasted into. Godot's RichTextLabel renders BBCode, so a display name is a place someone can put [img]https://attacker/x[/img] and turn every other player's client into an IP grabber for them; the server log is a place a CR forges lines; and a nameplate is a place stacked combining marks draw across the screen. ProfileRules (core/UnturnedSim) is the one implementation of the answer, run on BOTH sides -- the client so the player sees the name they will get, the server on the raw arrival because it never trusts the client's pass, and once more on the receiving client because a hostile server is also a thing. It normalises FIRST and validates the NORMALISED string, since stripping after matching is how a soft hyphen inside "[i&shy;mg]" defeats a check and then renders as nothing. Registers CommandSetProfile(42) (name + PNG bytes, length bounds-checked BEFORE the allocation), SystemProfiles(18) (name + a 64-bit avatar content hash per player -- everyone-visible, unlike skills) and EventAvatarData(36) (the bytes, once per (peer, hash) on the reliable channel; a PNG per player per tick is not a snapshot). The picture is validated HEADER-ONLY and never decoded server-side: signature, IHDR, exactly 128x128, no APNG, no interlace. The dimension check is the load-bearing one -- it is what stops a decompression bomb, since a 16384x16384 PNG is a few KB compressed and a gigabyte decoded, and a byte-size cap alone would wave it through. v16 (gun-state-authority): THE NINE FIELDS THE SERVER NEVER KNEW. gunAmmo, gunChambered, gunFiremode, gunMagId, gunAttach and the four per-slot attachment ids are written ONLY by the client (SaveGunState, AttachmentFit) and had no server-side writer anywhere, so the server's copy of each sat at its constructor default for the whole session while WriteJar faithfully sent that default back on every owner echo. The wire was never the problem -- every one of those fields round-trips perfectly and ItemWireCompletenessTests proves it, which is exactly why this survived: the test answers "does the field survive the wire" and the bug is "the source was never populated". It only shows once the grid moves, because a move is a REQUEST -- the client does not touch its own grid, it repaints from the echo -- so "fire it, holster it, take it out again" was always fine and "fire it, then drag it anywhere" handed back a full magazine (strawberry: "sometimes ammo magically refills into guns after some combo of equip/dequip/moving around between primary slot/inv"), along with resetting the fitted sight, the attach mask, the loaded mag and the fire mode on the same echo. Registers CommandGunState(40): the client asserts the block for one grid address, the server clamps the ammo count to the loaded magazine's real capacity (+1 chambered) exactly as OnReload already clamps ReloadSwapCommand.SpentAmount, and the existing owner echo carries it back. ItemAsset gains gunAmmoMax (copied off the gun's .dat by ItemCatalog) because GunDef lives in the game layer and core cannot see it, and a server with no gun simulation still needs a number to clamp against. Sends are COALESCED behind a 0.25s floor -- SaveGunState runs on every shot and a reliable-ordered datagram per shot is the head-of-line stutter v10 removed -- with a forced flush before every grid mutation request so a move cannot race it. v15 (wire-catchup): THE BUMP FOUR COMMANDS ALREADY NEEDED. Version was set to 14 on 2026-07-27; CommandFitAttachment(35), CommandReloadSwap(36), CommandWearClothing(37) and CommandUnwearClothing(38) were added on 2026-08-16 and NetProtocol.cs was never touched again, so the wire gained four client->server commands under a version number asserting it had not changed. That is not cosmetic: CommandRegistry.TryDispatch rejects an unregistered id and COUNTS it (UnknownIdRejected) rather than erroring, so a newer client against an older same-numbered server has its clothing change, reload swap and attachment fit silently dropped -- the action looks fine locally and never happened on the server. That is the identical failure mode as the magazine load/unload reverting on the next inventory move. Also changes the inventory ITEM WIRE: WriteJar/ReadJar gain the magazine cartridge lock (Item.magLoadedRound) as a 1-byte index into the sorted distinct magRound set -- it was added to Item for the magazine feature and never joined the schema, so every owner echo rebuilt a part-loaded magazine with no lock and it would accept a mixed cartridge on the next drag. Third time a field has been added to Item without joining this schema (per-slot attachments, gas-can fuelLevel, now this), so ItemWireCompletenessTests now walks Item by reflection and fails on any field that does not survive a real round trip. Also registers CommandMagLoad(39) for the magazine load/unload intent (client asks, server applies to its authoritative inventory, the existing owner-inventory echo carries it back -- the shape NetCraft already uses), so the mag work lands under THIS bump rather than earning its own. Batched deliberately: the plan doc's "never bump per-gap" rule exists because interim versions version-reject the live launcher and fragment the population, and the right reading of that rule here is that the four unbumped commands and the fifth incoming one are ONE wave. v14 (sp-mp-interactables): doors + beds + deadzones stop being singleplayer-only. Registers SystemInteractables(17) -- the join answer for door open/locked + bed owner, resent whole on any change -- plus CommandToggleDoor(32)/CommandSetDoorLocked(33)/CommandClaimBed(34) as client INTENT (validated server-side against the same DoorLogic/BedClaims singleplayer runs, plus a reach check the client cannot vouch for) and EventDoorState(34)/EventBedClaimed(35) as the authoritative answers. EventDoorState carries open AND locked: a lock used to be visible only to the server. Deadzones need no id of their own -- the server steps the same DeadzoneSim off replicated positions and its damage/infection/filter-burn ride the existing combat + vitals + owner-inventory paths. Respawn now prefers a claimed bed over the map spawn. v13 (destructible-props): registers SystemDestructibles(16) -- the rubble alive-bitmap, one bit per placed destructible object keyed by deterministic placement index (the ResourceReplication(12) shape) -- plus EventObjectDestroyed(32)/EventObjectRestored(33) for break/respawn immediacy. Server owns health + Rubble_Reset respawn (ServerDestructibles); combat routes an object hit into it. Composed after Animals(15), included in EnableSyncCheck. v12 (fluid-fix): the inventory item wire (WriteJar/ReadJar) gains the gas-can FUEL LEVEL (WriteClampedFloat 12,2) -- the server fills a can at a pump but the owner-inventory echo dropped the level, so a filled can showed empty on the client ("can won't fill"). Also mixed into the inventory StateHash. v11 (mp-sp-unify wave 2): registers SystemVitals(13 -- resolves the long-reserved owner-vitals slot) + SystemContainers(14) + SystemAnimals(15) as EMPTY stubs (composer/applier slots so the ids exist; bodies land under this SAME v11 as they fill), and reserves CommandPickupDeployable(28)/CommandExtractFuel(29)/CommandAttachTow(30)/CommandDetachTow(31). ONE coordinated bump for the whole wave (never per-gap -- avoids the v8/v9-style launcher fragmentation the note below warns about); the new systems' wire bodies + vehicle-tow fields + combat-appearance block + PlayerStateCommand.HeldItemId re-golden under this v11 as each gap lands. v10 (mp-event-coalesce): PlayerStateCommand 27 carries a redundant list of recent combat events (Fire/Melee/Grenade/Reload) folded into the 50Hz unreliable transform stream + deduped server-side by a strictly-increasing combat seq; owner entity snapshot gains LastProcessedCombatSeq ack; the standalone ReliableOrdered CommandFire/Melee/Grenade/Reload 2-5 datagrams are no longer sent by the client (registrations kept dormant). Kills reliable-ordered HOL-block combat stutter. v9 (mp-clientauth-foot): on-foot client authority -- owner movement changes from an input stream the server simulates to a transform stream the server envelope-validates and adopts: new CommandPlayerState 27 (@50 Hz UnreliableSeq) + new EventPlayerRecov 31 (rollback of an out-of-envelope claim); MoveInput drops the C2 ClaimedPos/HasClaim claim fields (the ack band is gone); EventMisprediction 30 retired. NOTE: v8 is RESERVED by the pending owner-vitals branch (SystemId 13) -- do not reuse; coordinate the v8/v9 ordering at merge. v7 (mp-geomfix P3): Accept carries the server's activeHoliday string -- the client builds the SERVER's holiday world (the ~285 holiday-gated props carry colliders; each machine's local clock silently forked the collision set across a holiday boundary, invisible to the content hash); v6 (mp-predict-a A2): vehicle client authority -- new CommandVehicleState 26 (the predicted driver's reported transform @25 Hz UnreliableSeq, envelope-validated then adopted) + new EventVehicleRecov 29 (the retail rollback of an out-of-envelope driver); v5 (mp-predict-c C1+C2, one coordinated bump): MoveInput datagram = MoveInputPacket carrying the last 3 inputs redundantly, each entry carrying the shell's claimed post-move position (hasClaim:1 + position grid) for the server ack band; v4 (mp-exitfix): VehicleExitedEvent carries the authoritative exit spot (float32 x3); v3 (PEI client C2): MoveInput gained the buttons byte (bit 0 = jump); v2 (Phase 4) = Connect carries contentHash:u64; v1 = Phases 1-3

        /// <summary>Conservative internet-safe datagram budget (MP_PLAN §2.2): no session datagram exceeds this.</summary>
        public const int MaxDatagramBytes = 1200;

        // header layout: magic:8 + version:8 + channel:3 + seq:16 + ack:16 + ackBits:32
        public const int HeaderBits = 83;

        // ReliableOrdered fragment framing after the header: msgId:16 + fragIdx:8 + fragCount:8 + byteLen:16,
        // then AlignToByte + payload. 83+48 = 131 bits -> 17 bytes aligned -> 1183 payload bytes fit the budget.
        public const int MaxFragmentPayload = 1183;
        public const int MaxFragments = 255; // fragCount is 8 bits
        public const int MaxReliableMessageBytes = MaxFragmentPayload * MaxFragments; // ~301 kB

        // UnreliableSequenced framing after the header: byteLen:16, then AlignToByte + payload.
        // 83+16 = 99 bits -> 13 bytes aligned -> 1187 payload bytes. Bigger payloads are refused, not
        // fragmented: losing one fragment of an unreliable snapshot would waste the whole thing.
        public const int MaxUnreliablePayload = 1187;

        // Tick-based timing (the session never reads a wall clock; the driver ticks it at 50 Hz).
        public const int TicksPerSecond = 50;              // matches SimClock.FixedDelta = 0.02
        public const int KeepAliveIntervalTicks = 50;      // 1 Hz keepalive when idle
        public const int TimeoutTicks = 250;               // 5 s of silence = disconnect
        public const int ConnectRetryTicks = 25;           // re-send Connect every 0.5 s while connecting
        public const int ConnectTimeoutTicks = 250;        // give up connecting after 5 s
        public const int MinRtoTicks = 5;                  // RTO floor = 100 ms
        public const double RtoRttMultiplier = 1.5;        // RTO = max(floor, 1.5 x smoothed RTT)

        // Reliable windows. Sender admits new msgIds only while (newest - oldest unacked) < SendWindow,
        // which guarantees the receiver never sees a fragment beyond its (larger) reassembly window.
        public const int SendWindowMessages = 64;
        public const int RecvWindowMessages = 256;

        // Reassembly abuse guards (review M1): the receive window alone would let a peer that never
        // completes the head message pin ~77 MB of fragments (255 msgs x 254 frags x 1183 B). Legit
        // traffic never buffers more than one max-size message (join snapshot <= MaxReliableMessageBytes/2)
        // plus a window of small events, so a few x MaxReliableMessageBytes is generous; and a message a
        // peer can't complete within 10 s of RTO retransmits is dead anyway. Exceeding either marks the
        // session (NetSession.ReassemblyBudgetExceeded) -- the server kicks such peers.
        public const int MaxReassemblyBufferBytes = 4 * MaxReliableMessageBytes; // ~1.2 MB per peer
        public const int ReassemblyTtlTicks = 500;                               // 10 s to complete a message

        public struct Header
        {
            public byte MagicByte;
            public byte Version;
            public NetChannel Channel;
            public ushort Seq;     // per-datagram, connection-wide; 0 is reserved for "none" and never sent
            public ushort Ack;     // newest remote seq seen (0 = nothing received yet)
            public uint AckBits;   // bit n set => seq (Ack - 1 - n) was received
        }

        public static bool WriteHeader(NetPakWriter writer, in Header h)
        {
            bool ok = writer.WriteBits(h.MagicByte, 8);
            ok &= writer.WriteBits(h.Version, 8);
            ok &= writer.WriteBits((uint)h.Channel, 3);
            ok &= writer.WriteBits(h.Seq, 16);
            ok &= writer.WriteBits(h.Ack, 16);
            ok &= writer.WriteBits(h.AckBits, 32);
            return ok;
        }

        public static bool TryReadHeader(NetPakReader reader, out Header h)
        {
            h = default;
            if (!reader.ReadBits(8, out uint magic)) return false;
            if (!reader.ReadBits(8, out uint version)) return false;
            if (!reader.ReadBits(3, out uint channel)) return false;
            if (!reader.ReadBits(16, out uint seq)) return false;
            if (!reader.ReadBits(16, out uint ack)) return false;
            if (!reader.ReadBits(32, out uint ackBits)) return false;
            h = new Header
            {
                MagicByte = (byte)magic,
                Version = (byte)version,
                Channel = (NetChannel)channel,
                Seq = (ushort)seq,
                Ack = (ushort)ack,
                AckBits = ackBits,
            };
            return true;
        }
    }

    /// <summary>Serial (wrap-around) arithmetic for 16-bit sequence numbers and msgIds.</summary>
    public static class NetSeq
    {
        /// <summary>True if a is strictly after b in wrap-around order.</summary>
        public static bool IsNewer(ushort a, ushort b) => a != b && (ushort)(a - b) < 32768;

        public static bool IsNewerOrEqual(ushort a, ushort b) => a == b || IsNewer(a, b);

        /// <summary>Signed distance a-b in wrap-around order (positive when a is newer).</summary>
        public static int Diff(ushort a, ushort b)
        {
            int d = (ushort)(a - b);
            return d < 32768 ? d : d - 65536;
        }
    }
}
