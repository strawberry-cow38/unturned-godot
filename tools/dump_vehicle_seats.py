#!/usr/bin/env python3
"""Dump every vehicle prefab's Seats/Seat_* transforms, root-relative and Z-negated into Godot space.

The port has only ever carried Seat_0 (the driver), hardcoded per vehicle class in Vehicle.SeatOf. Passenger
seats need the rest, and they need to come from the prefabs rather than from guessing where a back seat
probably is -- a seat placed by eye puts the passenger's head through the roof on exactly the vehicles with
the least headroom.

Reuses extract_heli_parts.seat_points (same walker cow tools used for the heli manifest) so car seats and
heli seats land in one coordinate convention rather than two.

Usage: python3 tools/dump_vehicle_seats.py [--bundle PATH] [--out tools/vehicle_seats.json]
"""
import argparse, os, sys, json
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_huey import tt, build_index
from extract_heli_parts import seat_points, CANDS


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bundle", default=next((c for c in CANDS if os.path.exists(c)), CANDS[0]))
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "vehicle_seats.json"))
    args = ap.parse_args()

    env = UnityPy.load(args.bundle)
    by_id = build_index(env)

    # Every vehicle prefab in the bundle, not a hand-listed set: a list I curate is a list that silently
    # omits whatever I forgot, and "that vehicle has no passenger seats" would look identical to "I never
    # looked at that vehicle".
    prefabs = sorted(p for p in env.container
                     if p.startswith("assets/coremasterbundle/vehicles/") and p.endswith("/vehicle.prefab"))
    print(f"{len(prefabs)} vehicle prefabs in the bundle")

    out = {}
    for pref in prefabs:
        name = pref.split("/")[-2]
        try:
            root = tt(env.container[pref])
            pts = seat_points(by_id, root)
        except Exception as e:                      # one unreadable prefab must not lose the other eighty
            print(f"!! {name}: {e}", file=sys.stderr)
            continue
        out[name] = pts
        if pts:
            print(f"{name:28s} {len(pts):2d} seat(s)  " +
                  " ".join(f"{n}({x:+.2f},{y:+.2f},{z:+.2f})" for n, (x, y, z) in pts[:4]) +
                  (" ..." if len(pts) > 4 else ""))
        else:
            print(f"{name:28s}  no Seats node")

    with open(args.out, "w") as f:
        json.dump(out, f, indent=1)
    multi = sum(1 for v in out.values() if len(v) > 1)
    print(f"\n-> {args.out}: {len(out)} vehicles, {multi} with more than one seat")


if __name__ == "__main__":
    main()
