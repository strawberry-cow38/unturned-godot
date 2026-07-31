"""Extract the airdrop's landing marker -- retail's "Carepackage Flare" effect.

Carepackage.OnCollisionEnter triggers effect GUID 2c17fbd0f0ce49aeb3bc4637b68809a2 at the landed
crate, reliable and at EffectManager.INSANE relevant distance: everyone on the map gets it. That
one asset is BOTH the ground marker and the drop sound, which is why they are extracted together.

  effects/explosions/carepackage/effect.prefab  the particle system + its AudioSource
  effects/explosions/carepackage/smoke.png      a 32x32 additive puff
  effects/explosions/carepackage/carepackage.mp3  the landing thump (UnityPy hands back RIFF/WAV)

Writes content/carepackage_smoke.png, content/carepackage_land.wav and content/carepackage_fx.json
so the runtime reads the prefab's real numbers rather than numbers that looked about right.
"""
import UnityPy, os, sys, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ug_paths

BUND = ug_paths.bundles()
OUT = os.path.dirname(ug_paths.objects_out())          # content/, not content/objects/
env = UnityPy.load(os.path.join(BUND, "core.masterbundle"))
by_id = {o.path_id: o for o in env.objects}

PRE = "assets/coremasterbundle/effects/explosions/carepackage/effect.prefab"
prefab = next((o for p, o in env.container.items()
               if o.type.name == "GameObject" and p.lower() == PRE), None)
if prefab is None:
    print("carepackage effect.prefab not in core.masterbundle"); sys.exit(1)


def comps(tt):
    for comp in tt.get("m_Component", []):
        c = comp.get("component", comp) if isinstance(comp, dict) else comp
        co = by_id.get(c.get("m_PathID")) if isinstance(c, dict) else None
        if co: yield co


def mm(node, default=0.0):
    """A Unity MinMaxCurve as (min, max). minMaxState 3 = random between two constants, where the
    LOW end lives in minScalar and the high end in scalar; anything else is the constant scalar.
    Reading only `scalar` is the trap -- it silently turns a 1.5..3 size range into a flat 3."""
    if not isinstance(node, dict): return (default, default)
    hi = node.get("scalar", default)
    lo = node.get("minScalar", hi) if node.get("minMaxState") == 3 else hi
    # Ordered, because Unity does not enforce it: the rise speed is authored minScalar=2.2 /
    # scalar=1.8, so taking them as (low, high) hands the consumer a backwards range.
    return (float(min(lo, hi)), float(max(lo, hi)))


ps = next((c for c in comps(prefab.read_typetree()) if c.type.name == "ParticleSystem"), None)
au = next((c for c in comps(prefab.read_typetree()) if c.type.name == "AudioSource"), None)
if ps is None:
    print("no ParticleSystem on the effect prefab"); sys.exit(1)

p = ps.read_typetree()
ini, emi, shp, vel, rot = (p.get(k, {}) for k in
                           ("InitialModule", "EmissionModule", "ShapeModule", "VelocityModule", "RotationModule"))
man = {
    "source": PRE,
    "duration": float(p.get("lengthInSec", 0.0)),
    "looping": bool(p.get("looping", False)),
    "max_particles": int(ini.get("maxNumParticles", 0)),
    "lifetime": mm(ini.get("startLifetime")),
    "start_speed": mm(ini.get("startSpeed")),
    "size": mm(ini.get("startSize"), 1.0),
    "rate_per_second": mm(emi.get("rateOverTime"))[1],
    "cone_angle": float(shp.get("angle", 0.0)),
    # The key is `radius` here, not `m_Radius` -- reading the wrong one returns 0.0 rather than
    # raising, which is a silent "the cone is a point" for anyone who does not check the output.
    "cone_radius": float((shp.get("radius") or shp.get("m_Radius") or {}).get("value", 0.0)),
    # Velocity-over-lifetime, not start speed: the column rises at a steady ~2 m/s with a hair of
    # sideways drift. startSpeed is 0, so a port that only read startSpeed gets a motionless blob.
    "rise": mm(vel.get("y")) if vel.get("enabled") else (0.0, 0.0),
    "drift": mm(vel.get("x")) if vel.get("enabled") else (0.0, 0.0),
    "spin_rad_per_sec": mm(rot.get("curve")) if rot.get("enabled") else (0.0, 0.0),
}
if au is not None:
    a = au.read_typetree()
    man["audio"] = {"volume": float(a.get("m_Volume", 1.0)), "pitch": float(a.get("m_Pitch", 1.0)),
                    "min_distance": float(a.get("MinDistance", 1.0)),
                    "max_distance": float(a.get("MaxDistance", 500.0))}

wrote = []
for path, o in env.container.items():
    lp = path.lower()
    if lp.endswith("effects/explosions/carepackage/smoke.png") and o.type.name == "Texture2D":
        img = o.read().image
        img.save(os.path.join(OUT, "carepackage_smoke.png"))
        man["smoke_size"] = [img.width, img.height]
        wrote.append("carepackage_smoke.png")
    elif lp.endswith("effects/explosions/carepackage/carepackage.mp3") and o.type.name == "AudioClip":
        clip = o.read()
        for _, data in clip.samples.items():
            open(os.path.join(OUT, "carepackage_land.wav"), "wb").write(data)
            wrote.append("carepackage_land.wav")
            break

open(os.path.join(OUT, "carepackage_fx.json"), "w").write(json.dumps(man, indent=1))
print("wrote carepackage_fx.json +", ", ".join(wrote) or "NO assets")
for k in ("duration", "max_particles", "lifetime", "size", "rate_per_second", "rise", "audio"):
    print(f"  {k}: {man.get(k)}")
