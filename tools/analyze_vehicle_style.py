#!/usr/bin/env python3
"""Derive style from the KEPT fleet table; inspect only three greenhouses.

Does not rerun the fleet survey or change vehicle_measurements.md.
"""
import math
import re
import statistics as stats
from measure_vehicles import ROOT, CONTENT, ROAD, obj, table


def baseline():
    text = (ROOT / 'notes/vehicle_measurements.md').read_text()
    bodies, wheels = {}, {}
    for line in text.split('## Wheels')[0].splitlines():
        if ' / `' not in line:
            continue
        c = [v.strip() for v in line.split('|')[1:-1]]
        name = c[0].split(' /')[0]
        if name not in ROAD:
            continue
        bounds = [tuple(map(float, v.split(' .. '))) for v in c[4:7]]
        bodies[name] = dict(length=float(c[1]), width=float(c[2]), height=float(c[3]),
                            bounds=bounds, vertices=int(c[7]), triangles=int(c[8]))
    for line in text.split('## Wheels')[1].split('## Collider')[0].splitlines():
        c = [v.strip() for v in line.split('|')[1:-1]]
        if not c or c[0] not in ROAD:
            continue
        zs = [float(z) for z in re.findall(r'Z (-?[\d.]+):', c[6])]
        wheels[c[0]] = dict(wheelbase=float(c[1]), front=zs[0], rear=zs[-1],
                            track=float(c[2].split(';')[0]), ride=float(c[5].split(';')[0]))
    assert len(bodies) == len(wheels) == 20
    return bodies, wheels


def greenhouse(name, body):
    # Selected body topology inspection, not a second whole-body measurement.
    mesh = obj(CONTENT / f'{name}_body.txt')
    v = mesh['vertices']
    belt = 1.005861 if name == 'hatchback' else 1.125
    roof_faces = []
    for face in mesh['faces']:
        p = [v[int(c.split('/')[0])-1] for c in face[:3]]
        # Cross-car top skin; excludes underside and vertical headers.
        if min(q[1] for q in p) >= 2.0058 and min(q[0] for q in p) < -.9 and max(q[0] for q in p) > .9:
            roof_faces.extend(p)
    panes = {p.stem.removeprefix(name+'_glass_'): obj(p)
             for p in sorted(CONTENT.glob(name+'_glass_*.txt'))}
    wind, rear = panes['windshield'], panes['rear']
    span = rear['hi'][2] - wind['lo'][2]
    top = max(p[1] for p in roof_faces)
    return dict(belt=belt, top=top, rise=(top-belt)/body['height'],
                slab=.25/body['height'], span=span, fraction=span/body['length'],
                roof_run=max(p[2] for p in roof_faces)-min(p[2] for p in roof_faces),
                crown=top-min(p[1] for p in roof_faces),
                rear_slope=math.degrees(math.atan2(rear['size'][2], rear['size'][1])),
                panes=panes)


def main():
    bodies, wheels = baseline()
    out = ['# Fleet style inputs', '',
           'Authored before the replacement body. Fleet dimensions/counts/anchors come only from the retained '
           '`vehicle_measurements.md`; its old wagon rows are excluded. The only new geometry sampling is '
           'the sedan, hatchback and van greenhouse requested for this revision. Units: metres, +Y up, forward −Z.', '',
           '## Complexity distribution', '',
           'Population: all 20 road specs in the retained survey, including tank, APC and trailer. '
           'Counts mean OBJ v records / loaded triangles; percentiles use statistics.quantiles(method="exclusive").', '']
    table(out, ['Vehicle', 'Vertices', 'Triangles'], [[n,bodies[n]['vertices'],bodies[n]['triangles']] for n in sorted(ROAD)])
    table(out, ['Metric','Minimum','Q1','Median','Q3','Maximum'], [
        [k,min(a),*stats.quantiles(a),max(a)] for k in ['vertices','triangles']
        for a in [[b[k] for b in bodies.values()]]])
    out += ['', 'Vertex serialization varies: the source meshes duplicate positions at panel seams. '
            'Use face-normal/UV seams for the new OBJ position records and report unique positions separately; '
            'do not inflate the count with unused vertices.', '', '## Greenhouse construction', '',
            'All three roofs are thick top/underside skins in the body asset (no separate roof asset). '
            'Sedan and hatchback have A/B/C pillars per side and two open side apertures; van has A/B '
            'around the front aperture, a solid B-to-rear side panel, and a rear corner post. '
            'Their pillar longitudinal sections are 0.250 m and side shell X thickness is 0.250 m '
            '(outer |X|≈1.23096, inner |X|≈0.98096). Roof skin-to-underside thickness is 0.250 m. '
            'The belt is the top edge of the thick lower side wall; apertures are actual absent body faces, '
            'filled by independent glass meshes at |X|≈1.22697 (about 0.004 m inside the outer skin).', '',
            'Body landmark examples, (Y,Z): sedan B edges (1.125,0.0797)/(1.125,0.3297), '
            'roof underside (1.9101,0.0797); hatch B edges (1.8759,0.7756)/(1.8759,1.0256); '
            'van B edges (1.125,−0.0186)/(1.125,0.2314), roof underside Y=1.875. '
            'These establish section dimensions; no coordinates are passed into the body generator.', '',
            'Roof rise / height = (roof top Y − belt Y) / body AABB height. Slab / height = 0.25 / body height. '
            'Glasshouse length is the longitudinal envelope from windshield minimum Z to rear pane maximum Z '
            '(including the van solid cargo sides). Roof run is the top skin Z envelope. '
            'Tail slope is rear-pane inclination from vertical, using ΔZ/ΔY. These are mesh-local ratios, not loaded road heights.', '']
    rows=[]
    for n in ['sedan','hatchback','van']:
        b,w=bodies[n],wheels[n];g=greenhouse(n,b)
        front=w['front']-b['bounds'][2][0];rear=b['bounds'][2][1]-w['rear']
        rows.append([n,f"{g['belt']:.6f}",f"{g['rise']:.6f}",f"{g['slab']:.6f}",
                     f"{g['span']:.6f} / {g['fraction']:.6f}",f"{g['roof_run']:.6f}",
                     f"{g['crown']:.6f}",f"{g['rear_slope']:.3f}°",
                     f"{front:.6f} / {rear:.6f}",f"{w['wheelbase']/b['length']:.6f}",f"{w['ride']:.6f}"])
    table(out,['Body','Belt Y','Roof rise/H','Slab/H','Glasshouse L / L-body','Roof run','Roof crown ΔY',
               'Tail slope','Front/rear overhang','Wheelbase/L','Ride Y−r'],rows)
    rows=[]
    for n in ['sedan','hatchback','van']:
        for label,m in greenhouse(n,bodies[n])['panes'].items():
            if label.startswith('r_'):
                continue
            rows.append([n,label,len(m['vertices']),len(m['faces']),
                         f"{m['lo'][1]:.6f} .. {m['hi'][1]:.6f}",f"{m['lo'][2]:.6f} .. {m['hi'][2]:.6f}"])
    table(out,['Body','Pane (opposite side symmetric)','Outline vertices','Triangles','Y span','Z span'],rows)
    out += ['', 'The many pane outline vertices include near-collinear points from the glass extraction tool; '
            'they are not extra pillars. A new four-corner planar aperture can therefore use two triangles '
            'without discarding a styling feature.', '', '## Proportions across the road population', '']
    rows=[]
    for n in sorted(ROAD):
        b,w=bodies[n],wheels[n]
        front=w['front']-b['bounds'][2][0];rear=b['bounds'][2][1]-w['rear']
        rows.append([n,f"{w['wheelbase']/b['length']:.6f}",f"{w['track']/b['width']:.6f}",
                     f"{front:.6f}",f"{rear:.6f}",f"{front/rear:.6f}",f"{w['ride']:.6f}"])
    table(out,['Vehicle','Wheelbase/L','Front track/W','Front overhang','Rear overhang','Front/rear ratio','Front ride Y−r'],rows)
    out += ['', 'The trailer wheelbase describes its rear bogie, and tank length includes its barrel; '
            'neither is an appropriate passenger-car axle template. Use the three enclosed car/van specimens '
            'for cabin and axle ratios, while using the whole road distribution for overall size and mesh budget.', '',
            '## Design targets fixed before authoring', '',
            '- Length 5.80: between road median 5.735214 and sedan 5.952190.',
            '- Width 2.52 and height 2.44: rounded road medians 2.522099 and 2.446013.',
            '- Wheelbase 3.02: 5.80 × mean(sedan, hatchback, van wheelbase/L) = 3.014707, rounded to 0.02 m grid.',
            '- Track 2.60: road per-axle median; track/width 1.031746 matches the sampled ≈1.030889.',
            '- Front/rear overhang 1.34/1.44: front/rear 0.930556 inside the sample 0.889549–0.936201.',
            '- Body min Y −0.27, axle Y 0.25, tyre radius 0.60: rounded dominant lower bound and modal ride −0.35.',
            '- Belt Y 1.10, roof underside 1.92, top 2.17: belt inside 1.005861–1.125; slab 0.25; roof rise/H 0.438525 inside the sample.',
            '- Greenhouse Z −1.25..2.68: length fraction 0.677586, between hatchback and van.',
            '- Roof Z −0.80..2.56: flat run 3.36, length fraction 0.579310, between hatchback and van roof-run fractions.',
            '- Three side apertures separated by four pillars per side; rear pane ΔZ=0.12 over ΔY=0.82 (8.326° from vertical).',
            '- Two existing seat rows retained; a new unobstructed load floor behind the rear seatbacks.',
            '- Aim near the 368-triangle median, with both counts within the central road spread; every lower and upper panel authored anew.', '']
    (ROOT/'notes/vehicle_style.md').write_text('\n'.join(out))
    print('Wrote notes/vehicle_style.md; retained fleet survey untouched')


if __name__ == '__main__':
    main()
