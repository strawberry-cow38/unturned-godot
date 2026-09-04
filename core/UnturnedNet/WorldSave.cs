using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// Whole-world persistence (master 2026-09-03: "work on saving and loading. player positions, facing, hp,
    /// stats etc etc. current day, player invs, globalpower").
    ///
    /// WHY THIS IS NOT THE WIRE FORMAT. Every system here already round-trips itself perfectly for snapshots --
    /// InventoryReplication's owner block is even documented as "full-on-dirty: the block IS the state" -- so the
    /// cheapest possible save would be to dump the NetPak blocks to a file. It is the wrong call: the wire is
    /// VERSIONED and re-goldened constantly (v17 -> v21 in a fortnight), and a save written in it becomes
    /// unreadable the next time someone adds a field. A save has to outlive the protocol, so it gets its own
    /// format with its own version, and pays for that with a second field list to keep in step.
    ///
    /// The field list is deliberately a MIRROR of what the wire writes -- see WriteJar in InventoryReplication,
    /// which carries a scar about exactly this: gun attachment ids "were added to Item after the schema was
    /// written and never joined it", so fitting a scope silently destroyed it. A field on Item that is missing
    /// HERE is destroyed the same way, just at save time instead of echo time. Add to both or neither.
    ///
    /// IDENTITY. Runtime PlayerId is a per-boot counter (NetServerSession._nextPlayerId starts at 1 every run),
    /// so it cannot key anything that outlives the process. The one stable handle the port has is the profile
    /// NAME -- PlayerProfile.Name, from UG_USERNAME, sanitised by ProfileRules -- which is what a client sends
    /// as its handshake name AND as its later SetProfile name, so it is available at PeerConnected, before the
    /// join snapshot composes. That timing is the point: restoring there means the joiner's first snapshot
    /// already carries their real state, instead of spawning them at defaults and correcting a moment later.
    /// </summary>
    public sealed class WorldSave
    {
        /// <summary>Bumped whenever a field is added, removed or reinterpreted. A file whose version this build
        /// does not understand is REFUSED, never partially read -- half-restoring a player is worse than a fresh
        /// start, because it looks like it worked.</summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>Which map this save belongs to. Loading is gated on it: a PEI save must never restore onto
        /// Washington, where the positions in it are arbitrary points in the sea.</summary>
        public string MapId { get; set; } = "";

        /// <summary>Unix seconds, for humans reading the file and for "which of these two saves is newer".
        /// Never used to order anything the game depends on.</summary>
        public long SavedAtUnix { get; set; }

        // ---- world ----
        public int Day { get; set; }
        public float TimeOfDay01 { get; set; }
        public float DayLengthSeconds { get; set; }
        /// <summary>The mains switch: every GridSource fixture's ToggledOn bit. `toggleglobalpower` sets them
        /// all together, so the whole grid state is one boolean -- see ServerTransactions.RunConsole.</summary>
        public bool GlobalPower { get; set; }

        public List<PlayerSave> Players { get; set; } = new List<PlayerSave>();

        // ---- world state, split by how it comes back ----
        //
        // RECREATED: things a player made, which do not exist until the save puts them back. These get FRESH
        // NetIds on load -- reusing the saved ones would collide with the minter, and nothing outside the save
        // refers to them anyway (wires are remapped through SaveId below).
        public List<DeployableSave> Deployables { get; set; } = new List<DeployableSave>();
        public List<WireSave> Wires { get; set; } = new List<WireSave>();
        public List<WorldItemSave> WorldItems { get; set; } = new List<WorldItemSave>();
        public List<CropSave> Crops { get; set; } = new List<CropSave>();
        //
        // OVERLAID: things the MAP BUILD already creates on every boot. The save holds the modification, not the
        // object -- restoring these as recreations would double the world (two of every car, every tree back but
        // also still felled). Keyed by whatever is stable across a rebuild: an index into the map's own load
        // order for the bitmaps, a quantised position for the fixtures.
        public List<VehicleSave> Vehicles { get; set; } = new List<VehicleSave>();
        public List<int> HarvestedResources { get; set; } = new List<int>();     // felled trees / mined rocks, by load-order index
        public List<int> BrokenDestructibles { get; set; } = new List<int>();    // smashed props, same shape
        public List<ContainerSave> Containers { get; set; } = new List<ContainerSave>();
        public List<DoorSave> Doors { get; set; } = new List<DoorSave>();

        public sealed class DeployableSave
        {
            /// <summary>Index of this entry within the saved list. Wires reference it instead of a NetId,
            /// because the NetId a deployable gets on load is not the one it had when saved.</summary>
            public int SaveId { get; set; }
            public ushort DefId { get; set; }
            /// <summary>Who placed it, by profile name. Resolved back to a live PlayerId only if that player is
            /// connected when the world loads; otherwise the piece comes back UNOWNED, because the id they will
            /// get on their next join does not exist yet. Known limitation, written down rather than hidden.</summary>
            public string OwnerName { get; set; } = "";
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float YawDegrees { get; set; }
            public float Health { get; set; }
            public float Fuel { get; set; }
            public bool ToggledOn { get; set; }
            public bool OnFire { get; set; }
            /// <summary>True for a deployable the MAP BUILD makes on every boot -- a grid source, a gas pump,
            /// anything with a FixtureKind. It is written to the save ONLY so wires can still reference it and
            /// so its toggle state carries; on load it is MATCHED to the existing one by position, never placed.
            /// Recreating these is what put two grid sources in one world: the map made one, the save made
            /// another on top of it, and both answered to "is the mains on".</summary>
            public bool IsMapFixture { get; set; }
            /// <summary>Contents, for a deployable that carries a storage grid (a fridge). Null when it has none.
            /// Attached to the OWNER rather than filed in a crate list of its own, because the crate is
            /// registered under the deployable's NetId -- which changes on load.</summary>
            public PageSave Storage { get; set; }
        }

        public sealed class WireSave
        {
            public int SrcSaveId { get; set; }
            public byte SrcPort { get; set; }
            public int DstSaveId { get; set; }
            public byte DstPort { get; set; }
        }

        public sealed class WorldItemSave
        {
            public ushort ItemId { get; set; }
            public byte Amount { get; set; }
            public byte Quality { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }

        public sealed class CropSave
        {
            public ushort SeedId { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public bool Grown { get; set; }
            /// <summary>Age at save time, NOT the absolute PlantedAtTick. The server tick restarts near zero
            /// every boot, so an absolute tick would come back as "planted 40 million ticks in the future" --
            /// the crop either never grows or is instantly ripe. Age is rebased against the load tick.</summary>
            public long TicksSincePlanted { get; set; }
        }

        public sealed class VehicleSave
        {
            /// <summary>Position in the map's spawn order. The map spawns the same cars in the same order every
            /// boot, so the ordinal identifies one -- guarded by TypeId below, so a changed spawn table is
            /// detected and skipped rather than teleporting a jeep onto a firetruck's saved position.</summary>
            public int SpawnIndex { get; set; }
            public byte TypeId { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float YawDegrees { get; set; }
            public float PitchDegrees { get; set; }
            public float RollDegrees { get; set; }
            public float Fuel { get; set; }
            public float Health { get; set; }
            public float Battery { get; set; }
            public bool Exploded { get; set; }
        }

        public sealed class ContainerSave
        {
            /// <summary>Quantised position, the stable handle for a map fixture across a rebuild -- more robust
            /// than a registration ordinal, which shifts the moment anyone edits the map.</summary>
            public int Qx { get; set; }
            public int Qy { get; set; }
            public int Qz { get; set; }
            public PageSave Contents { get; set; }
        }

        public sealed class DoorSave
        {
            public int Qx { get; set; }
            public int Qy { get; set; }
            public int Qz { get; set; }
            public bool Open { get; set; }
            public bool Locked { get; set; }
        }

        /// <summary>Centimetre quantisation for position-keyed fixtures. Coarse enough that float drift between
        /// two builds cannot miss a match, fine enough that two distinct doors never collide.</summary>
        public static void Quantize(Vector3 v, out int qx, out int qy, out int qz)
        {
            qx = (int)Mathf.Round(v.x * 100f);
            qy = (int)Mathf.Round(v.y * 100f);
            qz = (int)Mathf.Round(v.z * 100f);
        }

        public sealed class PlayerSave
        {
            /// <summary>The save key. Sanitised profile name; see the class comment on why not PlayerId.</summary>
            public string Name { get; set; } = "";

            // position + facing
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float YawDegrees { get; set; }
            public byte Stance { get; set; }

            // combat block
            public bool Alive { get; set; } = true;
            public byte Health { get; set; } = 100;
            public float HealthExact { get; set; } = 100f;
            public ushort Kills { get; set; }
            public ushort Deaths { get; set; }
            public ushort WornHat { get; set; }
            public ushort WornGlasses { get; set; }
            public ushort WornMask { get; set; }
            public ushort WornShirt { get; set; }
            public ushort WornVest { get; set; }
            public ushort WornBackpack { get; set; }
            public ushort WornPants { get; set; }
            public ushort HeldId { get; set; }

            // vitals
            public float Food { get; set; } = 1f;
            public float Water { get; set; } = 1f;
            public float Stamina { get; set; } = 1f;
            public float Infection { get; set; }
            public bool Bleeding { get; set; }
            public bool Broken { get; set; }

            // skills: total xp plus a level per (speciality, index), stored as a jagged array so a schema that
            // grows a skill does not silently shift every level along by one.
            public uint Experience { get; set; }
            public List<List<byte>> SkillLevels { get; set; } = new List<List<byte>>();

            // The seven GARMENTS THEMSELVES, as Items -- distinct from the WornHat/WornShirt/... ids above,
            // which are the replicated APPEARANCE and only say how you look to other people. PlayerInventory's
            // own wornX fields are what carries the garment's quality, what its armour reads, what "take it
            // off" removes, and what SIZES the clothing pages. Saving only the appearance ids brought a player
            // back rendering as dressed with an empty inventory underneath: nothing to remove, no armour, and
            // clothing pages that had to be re-sized from a garment that was no longer there.
            // The wire writes these (WriteOwnerBlock's seven WriteWorn calls) and this file's own header says
            // to mirror its field list. I missed them; this is that mirror completed.
            public JarSave WornHatItem { get; set; }
            public JarSave WornGlassesItem { get; set; }
            public JarSave WornMaskItem { get; set; }
            public JarSave WornShirtItem { get; set; }
            public JarSave WornVestItem { get; set; }
            public JarSave WornBackpackItem { get; set; }
            public JarSave WornPantsItem { get; set; }

            // inventory: one entry per page, each carrying its size so a save made while wearing a big backpack
            // restores into pages of the right shape before anything is placed in them.
            public List<PageSave> Pages { get; set; } = new List<PageSave>();
        }

        public sealed class PageSave
        {
            public byte Width { get; set; }
            public byte Height { get; set; }
            public List<JarSave> Items { get; set; } = new List<JarSave>();
        }

        /// <summary>One item in a grid. Mirrors InventoryReplication.WriteJar field for field -- read the note
        /// there before changing this, it is the one that cost a scope.</summary>
        public sealed class JarSave
        {
            public byte X { get; set; }
            public byte Y { get; set; }
            public byte Rot { get; set; }
            public ushort Id { get; set; }
            public byte Amount { get; set; }
            public byte Quality { get; set; }
            public short GunAmmo { get; set; } = -1;
            public sbyte GunFiremode { get; set; } = -1;
            public int GunMagId { get; set; } = -1;
            public int GunAttach { get; set; } = -1;
            public int GunSightId { get; set; } = -1;
            public int GunBarrelId { get; set; } = -1;
            public int GunGripId { get; set; } = -1;
            public int GunTacticalId { get; set; } = -1;
            public bool GunChambered { get; set; }
            public bool GunAttachSeeded { get; set; }
        }

        // ---------------------------------------------------------------- capture

        /// <summary>Snapshot every connected player plus the world clock and mains state. Players who are NOT
        /// connected keep whatever the previous save held for them: a server that saves while someone is offline
        /// must not erase them, which is why <paramref name="carryOver"/> exists.</summary>
        public static WorldSave Capture(NetWorldServer host, string mapId, int day, float timeOfDay01,
                                        float dayLengthSeconds, WorldSave carryOver = null)
        {
            var save = new WorldSave
            {
                Version = CurrentVersion,
                MapId = mapId ?? "",
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Day = day,
                TimeOfDay01 = timeOfDay01,
                DayLengthSeconds = dayLengthSeconds,
                GlobalPower = AnyGridSourceOn(host),
            };

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pe in host.Players.All)
            {
                string name = NameOf(host, pe.OwnerPlayerId);
                if (string.IsNullOrEmpty(name)) continue;   // nameless peer: nothing stable to file it under
                seen.Add(name);
                save.Players.Add(CapturePlayer(host, pe, name));
            }

            // Anyone in the previous save who is not online right now rides along untouched.
            if (carryOver != null)
                foreach (var old in carryOver.Players)
                    if (old != null && !string.IsNullOrEmpty(old.Name) && !seen.Contains(old.Name))
                        save.Players.Add(old);

            CaptureWorld(host, save);
            return save;
        }

        static void CaptureWorld(NetWorldServer host, WorldSave save)
        {
            long tick = host.Session.CurrentTick;

            // ---- RECREATED ----
            var saveIdByNetId = new Dictionary<uint, int>();
            foreach (var d in host.Deployables.All)
            {
                int saveId = save.Deployables.Count;
                saveIdByNetId[d.NetIdValue] = saveId;
                // A FixtureKind means the map build makes this one every boot. It still goes in the file --
                // wires reference it by SaveId and its toggle state matters -- but flagged, so ApplyWorld
                // matches it to the existing one instead of placing a second.
                bool isFixture = host.Deployables.Schema.TryGet(d.DefId, out var sdef)
                                 && sdef.FixtureKind != FixtureKind.None;
                var ds = new DeployableSave
                {
                    SaveId = saveId,
                    DefId = d.DefId,
                    OwnerName = NameOf(host, d.OwnerPlayerId) ?? "",
                    X = d.Pos.x, Y = d.Pos.y, Z = d.Pos.z,
                    YawDegrees = d.YawDegrees,
                    Health = d.Health, Fuel = d.Fuel,
                    ToggledOn = d.ToggledOn, OnFire = d.OnFire,
                    IsMapFixture = isFixture,
                };
                // A storage deployable's grid is registered under the deployable's OWN NetId, so this finds a
                // placed fridge's contents with no separate bookkeeping.
                if (host.Inventories.TryGetCrate(d.NetIdValue, out var crate) && crate.Storage != null)
                    ds.Storage = CapturePage(crate.Storage);
                save.Deployables.Add(ds);
            }

            foreach (var w in host.Deployables.AllWires)
            {
                // A wire whose either end did not survive into the save is dropped rather than written with a
                // dangling reference -- a wire to nothing reconnects to whatever later occupies that SaveId.
                if (!saveIdByNetId.TryGetValue(w.SrcId, out int src)) continue;
                if (!saveIdByNetId.TryGetValue(w.DstId, out int dst)) continue;
                save.Wires.Add(new WireSave { SrcSaveId = src, SrcPort = w.SrcPort, DstSaveId = dst, DstPort = w.DstPort });
            }

            foreach (var wi in host.WorldItems.All)
                save.WorldItems.Add(new WorldItemSave
                {
                    ItemId = wi.ItemId, Amount = wi.Amount, Quality = wi.Quality,
                    X = wi.Pos.x, Y = wi.Pos.y, Z = wi.Pos.z,
                });

            foreach (var c in host.Crops.All)
                save.Crops.Add(new CropSave
                {
                    SeedId = c.SeedId,
                    X = c.Pos.x, Y = c.Pos.y, Z = c.Pos.z,
                    Grown = c.Grown,
                    TicksSincePlanted = tick - c.PlantedAtTick,   // AGE, not the absolute tick -- see CropSave
                });

            // ---- OVERLAID ----
            int vi = 0;
            foreach (var v in host.Vehicles.All)
            {
                save.Vehicles.Add(new VehicleSave
                {
                    SpawnIndex = vi++, TypeId = v.TypeId,
                    X = v.Pos.x, Y = v.Pos.y, Z = v.Pos.z,
                    YawDegrees = v.YawDegrees, PitchDegrees = v.PitchDegrees, RollDegrees = v.RollDegrees,
                    Fuel = v.Fuel, Health = v.Health, Battery = v.Battery, Exploded = v.Exploded,
                });
            }

            // Only the DEAD indices are written. A map has tens of thousands of trees and a player fells a few
            // dozen, so the exceptions are the small list -- and an index the save does not mention is alive,
            // which is also the right default for a save made before the map grew a new one.
            for (int i = 0; i < host.Resources.Count; i++)
                if (!host.Resources.IsAlive(i)) save.HarvestedResources.Add(i);
            for (int i = 0; i < host.Destructibles.Count; i++)
                if (!host.Destructibles.IsAlive(i)) save.BrokenDestructibles.Add(i);

            foreach (var c in host.Containers.All)
            {
                if (!host.Inventories.TryGetCrate(c.NetIdValue, out var crate) || crate.Storage == null) continue;
                Quantize(c.Pos, out int qx, out int qy, out int qz);
                save.Containers.Add(new ContainerSave { Qx = qx, Qy = qy, Qz = qz, Contents = CapturePage(crate.Storage) });
            }

            foreach (var d in host.Interactables.Doors)
            {
                Quantize(d.Pos, out int qx, out int qy, out int qz);
                save.Doors.Add(new DoorSave { Qx = qx, Qy = qy, Qz = qz, Open = d.State.IsOpen, Locked = d.State.Locked });
            }
        }

        static PageSave CapturePage(Items page)
        {
            var ps = new PageSave { Width = page.width, Height = page.height };
            int n = page.getItemCount();
            for (byte i = 0; i < n; i++)
            {
                var jar = page.getItem(i);
                if (jar?.item == null) continue;
                ps.Items.Add(JarOf(jar));
            }
            return ps;
        }

        static JarSave JarOf(ItemJar jar)
        {
            var it = jar.item;
            return new JarSave
            {
                X = jar.x, Y = jar.y, Rot = jar.rot,
                Id = it.id, Amount = it.amount, Quality = it.quality,
                GunAmmo = (short)it.gunAmmo, GunFiremode = (sbyte)it.gunFiremode,
                GunMagId = it.gunMagId, GunAttach = it.gunAttach,
                GunSightId = it.gunSightId, GunBarrelId = it.gunBarrelId,
                GunGripId = it.gunGripId, GunTacticalId = it.gunTacticalId,
                GunChambered = it.gunChambered, GunAttachSeeded = it.gunAttachSeeded,
            };
        }

        /// <summary>RestorePage, reachable from the out-of-assembly save harness. Not used by the game.</summary>
        public static void RestorePageForTest(Items page, PageSave ps) => RestorePage(page, ps);

        static void RestorePage(Items page, PageSave ps)
        {
            if (ps == null) return;
            // Size FIRST, or anything past the default extent is dropped -- but NEVER shrink a page to 0x0.
            // A garment's page is sized by wearing it, from the item catalogue; a save written while that
            // catalogue was missing its Width/Height (which was true of 193 of the 195 garments with pockets)
            // carries 0x0, and forcing that back over a correctly-sized page would re-break every existing save
            // the moment the catalogue is fixed. A zero here means "the save does not know", not "it is empty".
            if (ps.Width > 0 && ps.Height > 0) page.loadSize(ps.Width, ps.Height);
            foreach (var j in ps.Items)
                if (j != null && j.Id != 0) page.addItem(j.X, j.Y, j.Rot, ToItem(j));
        }

        static PlayerSave CapturePlayer(NetWorldServer host, PlayerReplication.PlayerEntity pe, string name)
        {
            var p = new PlayerSave
            {
                Name = name,
                X = pe.Pos.x, Y = pe.Pos.y, Z = pe.Pos.z,
                YawDegrees = pe.YawDegrees,
                Stance = pe.Stance,
            };

            if (host.CombatState.TryGet(pe.OwnerPlayerId, out var ce))
            {
                p.Alive = ce.Alive; p.Health = ce.Health; p.HealthExact = ce.HealthExact;
                p.Kills = ce.Kills; p.Deaths = ce.Deaths;
                p.WornHat = ce.WornHat; p.WornGlasses = ce.WornGlasses; p.WornMask = ce.WornMask;
                p.WornShirt = ce.WornShirt; p.WornVest = ce.WornVest; p.WornBackpack = ce.WornBackpack;
                p.WornPants = ce.WornPants; p.HeldId = ce.HeldId;
            }

            if (host.Vitals.TryGet(pe.OwnerPlayerId, out var ve) && ve.Sim != null)
            {
                p.Food = ve.Sim.Food; p.Water = ve.Sim.Water; p.Stamina = ve.Sim.Stamina;
                p.Infection = ve.Sim.Infection;
                p.Bleeding = ve.Bleeding; p.Broken = ve.Broken;
                // Health lives in TWO places -- the coarse wire byte on the combat block and the exact float on
                // the vitals sim. The vitals one is the authority the server steps, so it wins on capture.
                p.HealthExact = ve.Sim.Health;
                p.Health = (byte)Mathf.Clamp(Mathf.RoundToInt(ve.Sim.Health), 0, 255);
            }

            if (host.Skills.TryGet(pe.OwnerPlayerId, out var se) && se.Skills != null)
            {
                p.Experience = se.Skills.experience;
                var all = se.Skills.skills;
                if (all != null)
                    foreach (var spec in all)
                    {
                        var row = new List<byte>();
                        if (spec != null) foreach (var sk in spec) row.Add(sk?.level ?? 0);
                        p.SkillLevels.Add(row);
                    }
            }

            if (host.Inventories.TryGet(pe.OwnerPlayerId, out var ie) && ie.Inventory != null)
            {
                var wi = ie.Inventory;
                p.WornHatItem = ItemOf(wi.wornHat); p.WornGlassesItem = ItemOf(wi.wornGlasses);
                p.WornMaskItem = ItemOf(wi.wornMask); p.WornShirtItem = ItemOf(wi.wornShirt);
                p.WornVestItem = ItemOf(wi.wornVest); p.WornBackpackItem = ItemOf(wi.wornBackpack);
                p.WornPantsItem = ItemOf(wi.wornPants);
                for (byte pg = 0; pg < PlayerInventory.PAGES; pg++)
                {
                    var page = ie.Inventory.items[pg];
                    var ps = new PageSave { Width = page.width, Height = page.height };
                    int n = page.getItemCount();
                    for (byte i = 0; i < n; i++)
                    {
                        var jar = page.getItem(i);
                        if (jar?.item == null) continue;
                        var it = jar.item;
                        ps.Items.Add(new JarSave
                        {
                            X = jar.x, Y = jar.y, Rot = jar.rot,
                            Id = it.id, Amount = it.amount, Quality = it.quality,
                            GunAmmo = (short)it.gunAmmo, GunFiremode = (sbyte)it.gunFiremode,
                            GunMagId = it.gunMagId, GunAttach = it.gunAttach,
                            GunSightId = it.gunSightId, GunBarrelId = it.gunBarrelId,
                            GunGripId = it.gunGripId, GunTacticalId = it.gunTacticalId,
                            GunChambered = it.gunChambered, GunAttachSeeded = it.gunAttachSeeded,
                        });
                    }
                    p.Pages.Add(ps);
                }
            }

            return p;
        }

        /// <summary>A worn garment as a JarSave. x/y/rot are unused -- a garment is not in a grid cell -- but the
        /// ITEM half is the same field list, so reusing the type keeps one place to add an Item field to.</summary>
        static JarSave ItemOf(Item it)
        {
            if (it == null) return null;
            return new JarSave
            {
                Id = it.id, Amount = it.amount, Quality = it.quality,
                GunAmmo = (short)it.gunAmmo, GunFiremode = (sbyte)it.gunFiremode,
                GunMagId = it.gunMagId, GunAttach = it.gunAttach,
                GunSightId = it.gunSightId, GunBarrelId = it.gunBarrelId,
                GunGripId = it.gunGripId, GunTacticalId = it.gunTacticalId,
                GunChambered = it.gunChambered, GunAttachSeeded = it.gunAttachSeeded,
            };
        }

        // ---------------------------------------------------------------- restore

        /// <summary>Restore one player's block, by name, into the state PeerConnected has just created at
        /// defaults. Returns false when this save has nothing for them -- a first-time joiner on an existing
        /// server, which is not an error. Call BEFORE the join snapshot composes so the restored state is what
        /// the client is first told, rather than a correction applied a tick later.</summary>
        public bool TryApplyPlayer(NetWorldServer host, ushort playerId, string name, long tick)
        {
            var p = FindPlayer(name);
            if (p == null) return false;

            // POSITION. ServerTeleport bumps TeleportSeq, which is what tells the client to hard-snap rather
            // than glide -- exactly right here: the player is materialising, not moving. Yaw and stance are not
            // on that call, so they are set directly (this type lives in the same assembly, which is why the
            // entity's internal setters are reachable at all).
            host.Players.ServerTeleport(playerId, new Vector3(p.X, p.Y, p.Z), tick);
            if (host.Players.TryGetByOwner(playerId, out var pe))
            {
                pe.YawDegrees = p.YawDegrees;
                pe.Stance = p.Stance;
                pe.LastChangedTick = tick;
            }

            if (host.CombatState.TryGet(playerId, out var ce))
            {
                ce.Alive = p.Alive; ce.Health = p.Health; ce.HealthExact = p.HealthExact;
                ce.Kills = p.Kills; ce.Deaths = p.Deaths;
                ce.WornHat = p.WornHat; ce.WornGlasses = p.WornGlasses; ce.WornMask = p.WornMask;
                ce.WornShirt = p.WornShirt; ce.WornVest = p.WornVest; ce.WornBackpack = p.WornBackpack;
                ce.WornPants = p.WornPants; ce.HeldId = p.HeldId; ce.Stance = p.Stance;
                ce.LastChangedTick = tick;
            }

            if (host.Vitals.TryGet(playerId, out var ve) && ve.Sim != null)
            {
                ve.Sim.Health = p.HealthExact;
                ve.Sim.Food = p.Food; ve.Sim.Water = p.Water; ve.Sim.Stamina = p.Stamina;
                ve.Sim.Infection = p.Infection;
                ve.Bleeding = p.Bleeding; ve.Broken = p.Broken;
                ve.LastChangedTick = tick;
            }

            if (host.Skills.TryGet(playerId, out var se) && se.Skills != null)
            {
                // ServerAdd just built a fresh PlayerSkills at zero xp, so awarding the saved total lands on
                // exactly it. Levels are written straight onto the Skill objects: TryUpgrade would SPEND the xp
                // we just restored, which would make every load a little poorer than the save.
                if (p.Experience > 0) host.Skills.ServerAward(playerId, p.Experience, tick);
                var all = se.Skills.skills;
                if (all != null)
                    for (int s = 0; s < all.Length && s < p.SkillLevels.Count; s++)
                    {
                        var row = p.SkillLevels[s];
                        var spec = all[s];
                        if (spec == null || row == null) continue;
                        for (int i = 0; i < spec.Length && i < row.Count; i++)
                            if (spec[i] != null) spec[i].level = Math.Min(row[i], spec[i].max);
                    }
                se.LastChangedTick = tick;
            }

            if (host.Inventories.TryGet(playerId, out var ie) && ie.Inventory != null && p.Pages.Count > 0)
            {
                var inv = ie.Inventory;
                // GARMENTS FIRST. wearBackpack/Vest/Shirt/Pants call Resize on their page, so wearing after
                // filling would re-size a page that already had the saved contents in it. Hat/glasses/mask have
                // no page and are order-independent, but they go here too so the seven read as one step.
                if (p.WornHatItem != null) inv.wearHat(ToItem(p.WornHatItem));
                if (p.WornGlassesItem != null) inv.wearGlasses(ToItem(p.WornGlassesItem));
                if (p.WornMaskItem != null) inv.wearMask(ToItem(p.WornMaskItem));
                if (p.WornShirtItem != null) inv.wearShirt(ToItem(p.WornShirtItem));
                if (p.WornVestItem != null) inv.wearVest(ToItem(p.WornVestItem));
                if (p.WornBackpackItem != null) inv.wearBackpack(ToItem(p.WornBackpackItem));
                if (p.WornPantsItem != null) inv.wearPants(ToItem(p.WornPantsItem));

                for (byte pg = 0; pg < PlayerInventory.PAGES && pg < p.Pages.Count; pg++)
                {
                    var ps = p.Pages[pg];
                    var page = inv.items[pg];
                    // Size FIRST: a page loaded at its default 5x3 silently drops anything the save placed at
                    // x=6, and the drop is invisible because addItem just returns false.
                    page.loadSize(ps.Width, ps.Height);
                    for (int i = 0; i < ps.Items.Count; i++)
                    {
                        var j = ps.Items[i];
                        if (j == null || j.Id == 0) continue;
                        page.addItem(j.X, j.Y, j.Rot, ToItem(j));
                    }
                }
                host.Inventories.ServerMarkDirty(playerId);
            }

            return true;
        }

        static Item ToItem(JarSave j)
        {
            var it = new Item(j.Id, j.Amount, j.Quality)
            {
                gunAmmo = j.GunAmmo, gunFiremode = j.GunFiremode,
                gunMagId = j.GunMagId, gunAttach = j.GunAttach,
                gunSightId = j.GunSightId, gunBarrelId = j.GunBarrelId,
                gunGripId = j.GunGripId, gunTacticalId = j.GunTacticalId,
                gunChambered = j.GunChambered, gunAttachSeeded = j.GunAttachSeeded,
            };
            return it;
        }

        public PlayerSave FindPlayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Players)
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
            return null;
        }

        // ---------------------------------------------------------------- helpers

        static string NameOf(NetWorldServer host, ushort playerId)
            => host.Profiles.TryGet(playerId, out var e) ? e.Name : null;

        static bool AnyGridSourceOn(NetWorldServer host)
        {
            foreach (var e in host.Deployables.All)
                if (host.Deployables.Schema.TryGet(e.DefId, out var d)
                    && d.FixtureKind == FixtureKind.GridSource && e.ToggledOn) return true;
            return false;
        }

        /// <summary>Put the world back. Runs ONCE, after the map has built (so the overlaid fixtures -- vehicles,
        /// containers, doors, the resource and destructible bitmaps -- already exist to be modified) and before
        /// anyone joins. Players are NOT restored here; they come back one at a time in TryApplyPlayer as they
        /// connect, because their state is keyed by a name that has no PlayerId until then.</summary>
        public void ApplyWorld(NetWorldServer host, long tick)
        {
            // ---- RECREATED: player-placed things that do not exist until this puts them back ----
            // The mains bool first, as the coarse answer for every GridSource. The per-fixture toggles in the
            // block below then refine it -- that order matters, because the reverse flattens a world where one
            // source is on and another is off back onto a single bit.
            ApplyGlobalPower(host, tick);

            var netIdBySaveId = new Dictionary<int, uint>();

            // Map fixtures come back as the ones the map build ALREADY made: matched by quantised position,
            // not placed. They still enter netIdBySaveId, so a wire a player ran from their base to the mains
            // reconnects to the real source rather than being dropped for having a dangling end.
            if (Deployables.Exists(d => d != null && d.IsMapFixture))
            {
                var fixtureAt = new Dictionary<(int, int, int), uint>();
                foreach (var e in host.Deployables.All)
                {
                    if (!host.Deployables.Schema.TryGet(e.DefId, out var fd) || fd.FixtureKind == FixtureKind.None) continue;
                    Quantize(e.Pos, out int fx, out int fy, out int fz);
                    fixtureAt[(fx, fy, fz)] = e.NetIdValue;
                }
                foreach (var ds in Deployables)
                {
                    if (ds == null || !ds.IsMapFixture) continue;
                    Quantize(new Vector3(ds.X, ds.Y, ds.Z), out int qx, out int qy, out int qz);
                    if (!fixtureAt.TryGetValue((qx, qy, qz), out uint existing)) continue;   // map changed under the save
                    netIdBySaveId[ds.SaveId] = existing;
                    host.Deployables.ServerSetScalars(existing, ds.Health, ds.Fuel, ds.OnFire, tick);
                    host.Deployables.ServerToggle(existing, ds.ToggledOn, tick);
                }
            }

            foreach (var ds in Deployables)
            {
                if (ds == null || ds.IsMapFixture) continue;   // handled above -- placing one here is the duplicate bug
                // Owner is a NAME in the file. It resolves only if that player happens to be connected; the
                // usual case at world load is nobody is, so the piece comes back unowned. See DeployableSave.
                ushort owner = FindOnlinePlayerId(host, ds.OwnerName);
                var id = host.Ids.Mint();
                var e = host.Deployables.ServerPlace(id, ds.DefId, owner, new Vector3(ds.X, ds.Y, ds.Z), ds.YawDegrees, tick);
                if (e == null) continue;   // a def that no longer exists in the schema: skip it, do not abort the load
                netIdBySaveId[ds.SaveId] = e.NetIdValue;
                host.Deployables.ServerSetScalars(e.NetIdValue, ds.Health, ds.Fuel, ds.OnFire, tick);
                host.Deployables.ServerToggle(e.NetIdValue, ds.ToggledOn, tick);
                if (ds.Storage != null && host.Deployables.Schema.TryGet(ds.DefId, out var pdef)
                    && pdef.StorageWidth > 0 && pdef.StorageHeight > 0)
                {
                    var crate = host.Inventories.ServerRegisterCrate(new NetId(e.NetIdValue),
                                                                     pdef.StorageWidth, pdef.StorageHeight, e.Pos);
                    if (crate?.Storage != null) RestorePage(crate.Storage, ds.Storage);
                }
            }

            foreach (var w in Wires)
            {
                if (w == null) continue;
                if (!netIdBySaveId.TryGetValue(w.SrcSaveId, out uint src)) continue;
                if (!netIdBySaveId.TryGetValue(w.DstSaveId, out uint dst)) continue;
                host.Deployables.ServerConnectWire(host.Ids.Mint(), src, w.SrcPort, dst, w.DstPort, tick);
            }

            foreach (var wi in WorldItems)
            {
                if (wi == null || wi.ItemId == 0) continue;
                host.WorldItems.ServerSpawn(host.Ids.Mint(), new Item(wi.ItemId, wi.Amount, wi.Quality),
                                            new Vector3(wi.X, wi.Y, wi.Z), tick);
            }

            foreach (var c in Crops)
            {
                if (c == null || c.SeedId == 0) continue;
                var e = host.Crops.ServerPlant(host.Ids.Mint(), c.SeedId, new Vector3(c.X, c.Y, c.Z), tick, c.Grown);
                // Rebase the planting tick so the crop resumes at the AGE it was saved at. Without this the
                // saved absolute tick lands in the future relative to a freshly-started server and the crop
                // either never ripens or is ripe on sight.
                if (e != null) e.PlantedAtTick = tick - c.TicksSincePlanted;
            }

            // ---- OVERLAID: modifications to what the map build already made ----
            int vi = 0;
            foreach (var v in host.Vehicles.All)
            {
                var vs = FindVehicle(vi++);
                if (vs == null || vs.TypeId != v.TypeId) continue;   // spawn table changed under the save: leave this one alone
                host.Vehicles.ServerPublish(new NetId(v.NetIdValue), new Vector3(vs.X, vs.Y, vs.Z),
                                            new Vector3(vs.PitchDegrees, vs.YawDegrees, vs.RollDegrees),
                                            Vector3.zero, Vector3.zero, 0f, vs.Fuel, vs.Health, vs.Battery, 0, tick);
                host.Vehicles.ServerPublishVitals(new NetId(v.NetIdValue), vs.Fuel, vs.Health, vs.Battery, vs.Exploded, tick);
            }

            foreach (int i in HarvestedResources) host.Resources.ServerSetAlive(i, false, tick);
            foreach (int i in BrokenDestructibles) host.Destructibles.ServerSetAlive(i, false, tick);

            if (Containers.Count > 0)
            {
                var byPos = new Dictionary<(int, int, int), ContainerSave>();
                foreach (var cs in Containers) if (cs != null) byPos[(cs.Qx, cs.Qy, cs.Qz)] = cs;
                foreach (var c in host.Containers.All)
                {
                    Quantize(c.Pos, out int qx, out int qy, out int qz);
                    if (!byPos.TryGetValue((qx, qy, qz), out var cs)) continue;
                    if (!host.Inventories.TryGetCrate(c.NetIdValue, out var crate) || crate.Storage == null) continue;
                    RestorePage(crate.Storage, cs.Contents);
                }
            }

            if (Doors.Count > 0)
            {
                var byPos = new Dictionary<(int, int, int), DoorSave>();
                foreach (var d in Doors) if (d != null) byPos[(d.Qx, d.Qy, d.Qz)] = d;
                foreach (var d in host.Interactables.Doors)
                {
                    Quantize(d.Pos, out int qx, out int qy, out int qz);
                    if (!byPos.TryGetValue((qx, qy, qz), out var ds)) continue;
                    host.Interactables.ServerRestoreDoor(d.NetId, ds.Open, ds.Locked);
                }
            }
        }

        void ApplyGlobalPower(NetWorldServer host, long tick)
        {
            foreach (var e in host.Deployables.All)
            {
                if (!host.Deployables.Schema.TryGet(e.DefId, out var d) || d.FixtureKind != FixtureKind.GridSource) continue;
                if (e.ToggledOn == GlobalPower) continue;
                e.ToggledOn = GlobalPower;
                e.LastChangedTick = tick;
            }
        }

        VehicleSave FindVehicle(int spawnIndex)
        {
            foreach (var v in Vehicles) if (v != null && v.SpawnIndex == spawnIndex) return v;
            return null;
        }

        static ushort FindOnlinePlayerId(NetWorldServer host, string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            foreach (var pe in host.Players.All)
                if (string.Equals(NameOf(host, pe.OwnerPlayerId), name, StringComparison.Ordinal))
                    return pe.OwnerPlayerId;
            return 0;
        }

        // ---------------------------------------------------------------- file format

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,   // a save you can open and read is a save you can debug
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        /// <summary>Parse a save, refusing anything this build cannot read in full. <paramref name="error"/>
        /// carries the reason for the log -- a corrupt or foreign save must say so, not vanish into a silent
        /// fresh world that looks like the save never existed.</summary>
        public static bool TryParse(string json, string expectMapId, out WorldSave save, out string error)
        {
            save = null; error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "empty file"; return false; }
            WorldSave parsed;
            try { parsed = JsonSerializer.Deserialize<WorldSave>(json, Json); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
            if (parsed == null) { error = "parsed to null"; return false; }
            if (parsed.Version != CurrentVersion)
            {
                error = $"save is format v{parsed.Version}, this build reads v{CurrentVersion}";
                return false;
            }
            if (!string.IsNullOrEmpty(expectMapId) && !string.IsNullOrEmpty(parsed.MapId)
                && !string.Equals(parsed.MapId, expectMapId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"save belongs to map '{parsed.MapId}', loading '{expectMapId}'";
                return false;
            }
            parsed.Players ??= new List<PlayerSave>();
            save = parsed;
            return true;
        }
    }
}
