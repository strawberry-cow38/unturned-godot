using Godot;

namespace UnturnedGodot
{
    // A barricade = a Deployable mounted on a structure surface (wall / floor / ceiling) rather than only flat ground.
    // We reuse Deployable wholesale — Deployable.Spawn gives us the body, box collider, health, damage / fire / wreck
    // lifecycle, ports and look-at billboard (Deployable.cs:162) — then re-seat the node for the mount family. The
    // collider + mesh are children in the same flat frame, so they rotate with the node and a wall barricade gets a
    // correctly-oriented collider for free.
    //
    // HP is per-asset from the def (src ItemBarricadeAsset.health) — a barricade's own number, NOT any structure-tier
    // value. This mirrors the src split: BarricadeManager owns barricade HP, StructureManager owns structure HP.
    public static class Barricade
    {
        // Place a barricade at a surface hit. point/normal come from a BarricadePlacer aim (or a placement message);
        // yawDeg is the FINAL yaw (for Wall, the caller passes BarricadePlacer.YawFacing(normal) + any manual spin —
        // the placer already resolves that into placer.Yaw). Returns the live Deployable node (parented + in the tree).
        public static Deployable PlaceOnSurface(Node parent, DeployableDef def, Vector3 point, Vector3 normal, float yawDeg,
                                                BarricadeMount? mount = null, SDG.Unturned.Item backing = null)
        {
            var m = mount ?? def.Mount;   // default to the def's own mount family; explicit arg overrides (harness/tests)
            var d = Deployable.Spawn(parent, def, point, yawDeg, backing);   // full lifecycle; HP = def.Health, seats upright on the point
            normal = normal.Normalized();
            var tmp = Deployable.BuildMesh(def, out Aabb ab);   // throwaway: recover the base-to-origin lift (a def/mesh property)
            tmp.QueueFree();
            // seat per mount family: Floor stands the base on the point along up; Wall/Sticky hug the surface by a
            // small standoff along the normal (DeployableDef.Offset is a GROUND clearance, too big as a wall standoff
            // -- see BarricadePlacer.WallStandoff; src point = hit + normal*offset, UseableBarricade.cs:817).
            Vector3 origin = m == BarricadeMount.Floor
                ? point + Vector3.Up * (def.Upright ? -ab.Position.Y : DeployableDef.GroundLift(ab))
                : point + normal * BarricadePlacer.Standoff(m, def);   // one definition, so the ghost and the placed object agree
            d.GlobalTransform = new Transform3D(BarricadePlacer.MountBasis(m, normal, yawDeg, def.Upright), origin);
            d.ReanchorLamp();   // the re-seat just moved the body out from under its TopLevel LampLight (LampLight.Reanchor)
            d.AddToGroup("barricades");   // a surface barricade (still a "deployable" too — look-at / repair target it either way)
            return d;
        }

        /// <summary>The surface normal a REPLICATED placement implies, recovered from what the wire actually
        /// carries. PlaceDeployableCommand sends (DefId, Pos, YawDegrees) and the server stores Pos verbatim as the
        /// raw surface point -- so how a def SITS on that point is a client-side decision, and the client was making
        /// the Floor one for everything.
        ///
        /// ⚠ THIS IS WHY CEILING PENDANTS ENDED UP INSIDE THE SLAB IN MULTIPLAYER while looking perfect in
        /// singleplayer (strawberry 2026-09-13: "the light gets placed in the ceiling, but orientation is correct").
        /// DeployableReplicaView spawns through Deployable.Spawn, which lifts a body UP by GroundLift so its base
        /// rests on the point -- correct for a crate on the ground, and for a pendant it shoves the whole fixture up
        /// by its own height into the ceiling. The ORIENTATION survived because MountBasis returns plain
        /// StandBasis(yaw) for Ceiling, so only the seat was wrong, which is exactly the half a look-at-it check
        /// passes. And peidrive is a listen server, so this IS the mode the game is played in.
        ///
        /// Ceiling is (0,-1,0) by definition. Wall is exact rather than assumed: BarricadePlacer.ResolveYaw sets a
        /// wall barricade's yaw to YawFacing(n) = atan2(n.x, n.z) AND NOTHING ELSE (R never reaches the wall family),
        /// so the horizontal normal inverts straight back out of the yaw that is already on the wire.
        ///
        /// STICKY IS THE GAP AND IT IS NOT FIXED HERE. Its yaw is the player's manual spin, not the surface, so the
        /// normal is genuinely absent from the wire -- a charge stuck to a slope replicates flat. Recovering that
        /// needs the normal sent, which is a protocol change; left alone rather than papered over with a guess.</summary>
        public static Vector3 NormalFromWire(BarricadeMount mount, float yawDeg) => mount switch
        {
            BarricadeMount.Ceiling => Vector3.Down,
            BarricadeMount.Wall => new Vector3(Mathf.Sin(Mathf.DegToRad(yawDeg)), 0f, Mathf.Cos(Mathf.DegToRad(yawDeg))),
            _ => Vector3.Up,
        };

        /// <summary>True if a replicated placement of this def has to be re-seated against a surface rather than
        /// stood on the ground. Window is excluded because it needs a wall + opening index the wire has no room for,
        /// and Sticky because its normal is not recoverable (see NormalFromWire).</summary>
        public static bool SeatsOnSurface(BarricadeMount m) => m == BarricadeMount.Ceiling || m == BarricadeMount.Wall;

        // Place a window barricade INTO a building-editor window opening, on one face (inside/outside). Spawned as a
        // CHILD of the WallSurface + stamped with the opening index + face, so BarricadePlacer.SlotFilled sees that
        // slot as taken; scaled to the opening (flat X=width, Z=height) and seated just proud of the aimed face.
        // Reuses Deployable.Spawn for the full body/health/salvage/net lifecycle, same as PlaceOnSurface. (master 2026-08-31)
        public static Deployable PlaceInWindow(WallSurface wall, int openingIndex, int face, DeployableDef def, SDG.Unturned.Item backing = null)
        {
            var op = wall.Openings[openingIndex];
            Vector3 wn = wall.GlobalTransform.Basis.Z.Normalized() * face;                // outward from the aimed face
            Vector3 center = wall.UVToWorld(op.U + op.Width * 0.5f, op.V + op.Height * 0.5f);
            var meshRoot = WindowBarricadeMesh.Build(def.WindowStyle, op.Width, op.Height, out Vector3 colSize, out float thick);   // planks/bars/plate, built to THIS opening
            Vector3 seat = center + wn * (wall.Thickness * 0.5f + thick * 0.5f + 0.005f);   // panel sits flat ON the wall FACE (half-wall + half-panel + hair), not in the centre plane
            float yaw = BarricadePlacer.YawFacing(wn);
            var d = Deployable.Spawn(wall, def, seat, yaw, backing);                       // full lifecycle (HP/damage/salvage/net)
            d.SetWindowMesh(meshRoot, colSize);                                            // swap the ProcBox for the fitted panel + resize the collider
            d.GlobalTransform = new Transform3D(DeployableDef.StandBasis(yaw), seat);      // no node scale: the mesh is already opening-sized (no shear/flatten)
            d.AddToGroup("barricades");
            d.SetMeta("ug_wb_opening", openingIndex);   // the slot this fills; BarricadePlacer.SlotFilled reads these back
            d.SetMeta("ug_wb_face", face);
            return d;
        }

        // Baked-prop variant: place a window barricade snapped to a WindowOpeningMarker (a baked building has NO
        // WallSurface). Parented onto the marker's PROP root + stamped with the marker id + face (read back by
        // BarricadePlacer.MarkerSlotFilled); uses the ghost transform AimWindow already computed (point/yaw/scale).
        public static Deployable PlaceInWindowMarker(WindowOpeningMarker marker, int face, Vector3 point, float yawDeg, Vector3 windowScale, DeployableDef def, SDG.Unturned.Item backing = null)
        {
            var host = (Node)marker.GetParent() ?? marker;
            float w = def.Size.X * windowScale.X;   // AimWindow's fitted scale = (2*halfW/Size.X, 1, 2*halfH/Size.Z) -> recover the opening size
            float h = def.Size.Z * windowScale.Z;
            var meshRoot = WindowBarricadeMesh.Build(def.WindowStyle, w, h, out Vector3 colSize, out float _);   // planks/bars/plate, built to the baked opening
            var d = Deployable.Spawn(host, def, point, yawDeg, backing);
            d.SetWindowMesh(meshRoot, colSize);
            d.GlobalTransform = new Transform3D(DeployableDef.StandBasis(yawDeg), point);   // no node scale: the mesh is already opening-sized
            d.AddToGroup("barricades");
            d.SetMeta("ug_wb_marker", (long)marker.GetInstanceId());
            d.SetMeta("ug_wb_face", face);
            return d;
        }

        // Map a sim-level WallBarricade (engine-free, carried on a building-editor opening) to the game DeployableDef
        // whose WindowBarricadeMesh style it drives; null for None. (master 2026-09-01: pre-barricaded openings.)
        public static DeployableDef DefFor(UnturnedSim.WallBarricade t) => t switch
        {
            UnturnedSim.WallBarricade.Planks => DeployableDef.WindowBarricade,
            UnturnedSim.WallBarricade.Bars   => DeployableDef.WindowBars,
            UnturnedSim.WallBarricade.Plate  => DeployableDef.WindowPlate,
            _ => null,
        };
    }
}
