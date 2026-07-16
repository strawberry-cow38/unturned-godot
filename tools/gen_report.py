#!/usr/bin/env python3
"""Render the latest ./test.sh run to a static HTML dashboard for quick human review.

Reads the run's output grammar from .testresults/run.log (test.sh --report tees it there) and the
L2 visual artifacts under .testresults/visual/, then writes a self-contained page + a copy of every
scene image into the served dir (default /var/www/ugtests, behind Caddy at claw.bitvox.me/ugtests/).

  [SUITE]  -> the L0 unit suites + the L1/L2 rollups (pass/fail table)
  [TEST] visual.<name> -> per-scene status + mae; the thumbnail is the latest CAPTURE (falls back to
                          the committed golden if that scene wasn't rendered this run). Failures also
                          show golden + amplified diff side by side.
  [SUMMARY] -> the banner (green / failures / infra) + totals.

Usage:  tools/gen_report.py [--out DIR] [--log FILE]
Env:    UG_REPORT_DIR overrides --out. Defaults: out=/var/www/ugtests, log=.testresults/run.log.
"""
import html, json, os, re, shutil, sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import visual_tests as vt  # expand_angles + GOLDEN_DIR/WORK_DIR, so the scene list stays in one place

DEFAULT_OUT = os.environ.get("UG_REPORT_DIR", "/var/www/ugtests")
DEFAULT_LOG = os.path.join(ROOT, ".testresults/run.log")

SUITE_RE = re.compile(r"^\[SUITE\]\s+(.*?)\s+\|\s+(PASS|FAIL|ERROR)\s+\|\s+(.*)$")
VIS_RE = re.compile(r"^\[TEST\]\s+visual\.(\S+)\s+\|\s+(PASS|FAIL|ERROR)\s+\|\s+(.*)$")
SUMMARY_RE = re.compile(r"^\[SUMMARY\]\s+(.*)$")
MAE_RE = re.compile(r"mae=([0-9.]+)")


def parse_log(path):
    """-> (summary_text, [suite dicts], {scene_name: {status, detail, mae}})."""
    summary, suites, scenes = "", [], {}
    if not os.path.isfile(path):
        return summary, suites, scenes
    for line in open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\n")
        m = VIS_RE.match(line)
        if m:
            name, status, detail = m.group(1), m.group(2), m.group(3).strip()
            mm = MAE_RE.search(detail)
            scenes[name] = {"status": status, "detail": detail, "mae": float(mm.group(1)) if mm else None}
            continue
        m = SUITE_RE.match(line)
        if m:  # skip the L2 rollup -- the visual grid already shows every scene
            if m.group(1).startswith("L2"):
                continue
            suites.append({"name": m.group(1), "status": m.group(2), "detail": m.group(3).strip()})
            continue
        m = SUMMARY_RE.match(line)
        if m:
            summary = m.group(1).strip()
    return summary, suites, scenes


def scene_images(outdir):
    """For every manifest scene (angles expanded), copy the best thumbnail into outdir/img and return
    an ordered list of {name, tolerance, thumb, golden, diff} (paths relative to outdir, or None)."""
    entries = vt.expand_angles(json.load(open(vt.MANIFEST)))
    imgdir = os.path.join(outdir, "img")
    os.makedirs(imgdir, exist_ok=True)
    rows = []

    def stash(src, rel):
        if src and os.path.isfile(src):
            shutil.copyfile(src, os.path.join(outdir, rel))
            return rel
        return None

    for e in entries:
        name = e["name"]
        safe = re.sub(r"[^A-Za-z0-9._-]", "_", name)
        work = os.path.join(vt.WORK_DIR, name)
        cap = os.path.join(work, e.get("capture", "shot.png"))     # latest render
        golden = os.path.join(vt.GOLDEN_DIR, f"{name}.png")
        diff = os.path.join(work, f"{name}.diff.png")
        thumb = stash(cap, f"img/{safe}.png") or stash(golden, f"img/{safe}.png")
        rows.append({
            "name": name,
            "tolerance": e.get("tolerance", 0.02),
            "thumb": thumb,
            "golden": stash(golden, f"img/{safe}.golden.png"),
            "diff": stash(diff, f"img/{safe}.diff.png"),
        })
    return rows


PILL = {"PASS": "ok", "FAIL": "bad", "ERROR": "bad", None: "muted"}


def render_html(summary, suites, scenes, rows, when):
    # totals + status token straight off the [SUMMARY] grammar:
    #   TOTAL: P passed, F failed [| first failure: X] | <ok|FAILURES|INFRA-ERROR> | trx: DIR
    tp = (re.search(r"([0-9]+) passed", summary) or [None, "?"])[1]
    tf = (re.search(r"([0-9]+) failed", summary) or [None, "?"])[1]
    if "INFRA-ERROR" in summary:
        state, state_word = "bad", "infrastructure error"
    elif "FAILURES" in summary or (tf not in ("?", "0")):
        state, state_word = "bad", "failures"
    elif not summary:
        state, state_word = "muted", "no run recorded"
    else:
        state, state_word = "ok", "all green"

    def card(r):
        st = scenes.get(r["name"])
        status = st["status"] if st else None
        pill = PILL[status]
        badge = status or "not run"
        mae = f'mae {st["mae"]:.4f} · tol {r["tolerance"]}' if st and st.get("mae") is not None else \
              ("not rendered this run" if not st else html.escape(st["detail"])[:60])
        thumb = f'<a href="{r["thumb"]}" target="_blank"><img src="{r["thumb"]}" loading="lazy" alt=""></a>' \
                if r["thumb"] else '<div class="noimg">no image</div>'
        extra = ""
        if status in ("FAIL", "ERROR") and r["golden"] and r["diff"]:
            extra = (f'<div class="triptych"><figure><a href="{r["golden"]}" target="_blank">'
                     f'<img src="{r["golden"]}" loading="lazy" alt=""></a><figcaption>golden</figcaption></figure>'
                     f'<figure><a href="{r["diff"]}" target="_blank"><img src="{r["diff"]}" loading="lazy" alt="">'
                     f'</a><figcaption>diff ×8</figcaption></figure></div>')
        return (f'<article class="card {pill}"><div class="thumb">{thumb}</div>'
                f'<div class="meta"><span class="name">{html.escape(r["name"])}</span>'
                f'<span class="pill {pill}">{badge}</span></div>'
                f'<div class="sub">{mae}</div>{extra}</article>')

    def suite_row(s):
        pill = PILL[s["status"]]
        return (f'<tr class="{pill}"><td>{html.escape(s["name"])}</td>'
                f'<td><span class="pill {pill}">{s["status"]}</span></td>'
                f'<td class="detail">{html.escape(s["detail"])}</td></tr>')

    cards = "\n".join(card(r) for r in rows)
    suite_rows = "\n".join(suite_row(s) for s in suites) or \
        '<tr><td colspan="3" class="detail">no unit/in-engine suites in this run (visual-only)</td></tr>'
    n_pass = sum(1 for r in rows if scenes.get(r["name"], {}).get("status") == "PASS")
    n_vis = len(rows)

    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="120">
<title>unturned-godot · test results</title>
<style>
:root {{
  --bg:#eef0f4; --panel:#ffffff; --ink:#1a1d24; --muted:#5b616e; --line:#d6dae2;
  --accent:#d98a1f; --ok:#1f9d55; --ok-bg:#e6f5ec; --bad:#d33c3c; --bad-bg:#fbe8e8;
  --mono:ui-monospace,"SF Mono",Menlo,Consolas,monospace;
  --sans:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;
}}
@media (prefers-color-scheme: dark) {{
  :root {{
    --bg:#151821; --panel:#1e232e; --ink:#e7eaf0; --muted:#9aa2b1; --line:#2c333f;
    --accent:#e6a53a; --ok:#3ec16f; --ok-bg:#15281d; --bad:#f0605f; --bad-bg:#2c1717;
  }}
}}
:root[data-theme="light"] {{
  --bg:#eef0f4; --panel:#ffffff; --ink:#1a1d24; --muted:#5b616e; --line:#d6dae2;
  --accent:#d98a1f; --ok:#1f9d55; --ok-bg:#e6f5ec; --bad:#d33c3c; --bad-bg:#fbe8e8;
}}
:root[data-theme="dark"] {{
  --bg:#151821; --panel:#1e232e; --ink:#e7eaf0; --muted:#9aa2b1; --line:#2c333f;
  --accent:#e6a53a; --ok:#3ec16f; --ok-bg:#15281d; --bad:#f0605f; --bad-bg:#2c1717;
}}
* {{ box-sizing:border-box; }}
body {{ margin:0; background:var(--bg); color:var(--ink); font-family:var(--sans);
  line-height:1.45; -webkit-font-smoothing:antialiased; }}
.wrap {{ max-width:1180px; margin:0 auto; padding:0 20px 64px; }}
header.bar {{ position:sticky; top:0; z-index:5; background:var(--bg);
  border-bottom:1px solid var(--line); padding:18px 20px 16px; }}
header.bar .inner {{ max-width:1180px; margin:0 auto; display:flex; align-items:baseline;
  gap:16px; flex-wrap:wrap; }}
h1 {{ font-size:15px; letter-spacing:.02em; margin:0; font-weight:650; }}
h1 .dot {{ color:var(--accent); }}
.state {{ font-family:var(--mono); font-size:13px; font-weight:600; padding:3px 12px; border-radius:999px; }}
.state.ok {{ color:var(--ok); background:var(--ok-bg); }}
.state.bad {{ color:var(--bad); background:var(--bad-bg); }}
.state.muted {{ color:var(--muted); background:var(--bg); }}
.counts {{ font-family:var(--mono); font-size:12.5px; color:var(--muted); font-variant-numeric:tabular-nums; }}
.counts b {{ color:var(--ink); }}
.when {{ margin-left:auto; font-family:var(--mono); font-size:12px; color:var(--muted); }}
.summaryline {{ max-width:1180px; margin:8px auto 0; font-family:var(--mono); font-size:12px;
  color:var(--muted); word-break:break-word; }}
section {{ margin-top:34px; }}
h2 {{ font-size:12px; text-transform:uppercase; letter-spacing:.09em; color:var(--muted);
  font-weight:650; margin:0 0 14px; }}
h2 .n {{ color:var(--ink); font-variant-numeric:tabular-nums; }}
.grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(300px,1fr)); gap:16px; }}
.card {{ background:var(--panel); border:1px solid var(--line); border-radius:12px; overflow:hidden;
  border-left:3px solid var(--line); }}
.card.ok {{ border-left-color:var(--ok); }}
.card.bad {{ border-left-color:var(--bad); }}
.card .thumb img {{ width:100%; display:block; aspect-ratio:16/9; object-fit:cover; background:#0c0e14; }}
.card .noimg {{ aspect-ratio:16/9; display:grid; place-items:center; color:var(--muted);
  font-family:var(--mono); font-size:12px; background:var(--bg); }}
.card .meta {{ display:flex; align-items:center; gap:10px; padding:11px 13px 4px; }}
.card .name {{ font-family:var(--mono); font-size:12.5px; font-weight:600; word-break:break-all; }}
.card .sub {{ padding:0 13px 12px; font-family:var(--mono); font-size:11.5px; color:var(--muted);
  font-variant-numeric:tabular-nums; }}
.pill {{ margin-left:auto; font-family:var(--mono); font-size:10.5px; font-weight:700; letter-spacing:.05em;
  padding:2px 9px; border-radius:999px; text-transform:uppercase; white-space:nowrap; }}
.pill.ok {{ color:var(--ok); background:var(--ok-bg); }}
.pill.bad {{ color:var(--bad); background:var(--bad-bg); }}
.pill.muted {{ color:var(--muted); background:var(--bg); }}
.triptych {{ display:flex; gap:8px; padding:0 13px 13px; }}
.triptych figure {{ margin:0; flex:1; }}
.triptych img {{ width:100%; border-radius:6px; border:1px solid var(--line); display:block; }}
.triptych figcaption {{ font-family:var(--mono); font-size:10px; color:var(--muted); text-align:center; padding-top:3px; }}
table {{ width:100%; border-collapse:collapse; background:var(--panel);
  border:1px solid var(--line); border-radius:12px; overflow:hidden; }}
td {{ padding:9px 14px; border-top:1px solid var(--line); font-size:13px; vertical-align:middle; }}
tr:first-child td {{ border-top:none; }}
td:first-child {{ font-family:var(--mono); font-size:12.5px; font-weight:600; }}
td.detail {{ font-family:var(--mono); font-size:12px; color:var(--muted); }}
footer {{ margin-top:40px; font-family:var(--mono); font-size:11.5px; color:var(--muted); }}
a {{ color:inherit; }}
</style>
</head>
<body>
<header class="bar">
  <div class="inner">
    <h1>unturned<span class="dot">·</span>godot &nbsp;test results</h1>
    <span class="state {state}">{state_word}</span>
    <span class="counts"><b>{tp}</b> passed &middot; <b>{tf}</b> failed &middot; visual <b>{n_pass}</b>/<b>{n_vis}</b></span>
    <span class="when">{html.escape(when)}</span>
  </div>
  <div class="summaryline">{html.escape(summary) or "no [SUMMARY] captured &mdash; run ./test.sh --all --report"}</div>
</header>
<div class="wrap">
  <section>
    <h2>Visual scenes <span class="n">({n_vis})</span></h2>
    <div class="grid">
{cards}
    </div>
  </section>
  <section>
    <h2>Unit &amp; in-engine suites <span class="n">({len(suites)})</span></h2>
    <table>
{suite_rows}
    </table>
  </section>
  <footer>generated by tools/gen_report.py &middot; auto-refreshes every 120s &middot; ./test.sh --all --report</footer>
</div>
<script>
// honor a ?theme=dark|light override for quick side-by-side checks; otherwise follow the OS.
const t = new URLSearchParams(location.search).get("theme");
if (t === "dark" || t === "light") document.documentElement.dataset.theme = t;
</script>
</body>
</html>
"""


def main():
    out, log = DEFAULT_OUT, DEFAULT_LOG
    argv = sys.argv[1:]
    while argv:
        a = argv.pop(0)
        if a == "--out" and argv: out = argv.pop(0)
        elif a == "--log" and argv: log = argv.pop(0)
        elif a in ("-h", "--help"): print(__doc__); return 0
        else: print(f"unknown arg: {a} (see --help)"); return 2

    os.makedirs(out, exist_ok=True)
    summary, suites, scenes = parse_log(log)
    rows = scene_images(out)
    when = datetime.now(timezone.utc).astimezone().strftime("%Y-%m-%d %H:%M %Z")
    page = render_html(summary, suites, scenes, rows, when)
    with open(os.path.join(out, "index.html"), "w", encoding="utf-8") as f:
        f.write(page)
    n_pass = sum(1 for r in rows if scenes.get(r["name"], {}).get("status") == "PASS")
    print(f"[REPORT] wrote {os.path.join(out, 'index.html')} | {len(rows)} scenes ({n_pass} passed), "
          f"{len(suites)} suites | summary: {summary or '(none)'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
