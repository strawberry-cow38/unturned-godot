//! Original RegionStreamer.Build road ribbons, widening, bevels and junction paint.
use super::{
    ObjData,
    procedural::{ProceduralRegion, f, rows, value},
    roads::triangle,
};
use bevy::prelude::*;
use serde_json::Value;
use std::collections::BTreeMap;

#[derive(Default)]
pub struct RoadMesh {
    pub visible: ObjData,
    pub solid: ObjData,
}
fn list<'a>(v: &'a Value, key: &str) -> &'a [Value] {
    value(v, key).as_array().map(Vec::as_slice).unwrap_or(&[])
}
fn a(v: &Value, key: &str, i: usize) -> f32 {
    list(v, key).get(i).and_then(Value::as_f64).unwrap_or(0.) as f32
}
fn flag(v: &Value, key: &str) -> bool {
    value(v, key).as_bool().unwrap_or(false)
}
fn half(kind: usize) -> f32 {
    [6.9, 9.2, 4.6, 4.6].get(kind).copied().unwrap_or(4.6)
}
fn lift(kind: usize) -> f32 {
    [0.045, 0.03, 0.015, 0.01].get(kind).copied().unwrap_or(0.)
}
fn repeat(kind: usize) -> f32 {
    if kind == 3 { 8. } else { 32. }
}
fn texture(kind: usize, ramp: bool) -> String {
    format!(
        "roads/road_{}.png",
        if ramp { 6 } else { [1, 0, 8, 5][kind.min(3)] }
    )
}
fn ground(r: &ProceduralRegion, x: f32, z: f32) -> f32 {
    let gx = ((x - r.origin_x) / r.spacing).clamp(0., r.cells as f32);
    let gz = ((z - r.origin_z) / r.spacing).clamp(0., r.cells as f32);
    let i = (gx as usize).min(r.cells - 1);
    let j = (gz as usize).min(r.cells - 1);
    let s = r.cells + 1;
    let (fx, fz) = (gx - i as f32, gz - j as f32);
    let (ha, hb, hc, he) = (
        r.heights[j * s + i],
        r.heights[j * s + i + 1],
        r.heights[(j + 1) * s + i],
        r.heights[(j + 1) * s + i + 1],
    );
    if fx + fz <= 1. {
        ha + (hb - ha) * fx + (hc - ha) * fz
    } else {
        he + (hc - he) * (1. - fx) + (hb - he) * (1. - fz)
    }
}
#[allow(clippy::too_many_arguments)]
fn quad(
    mesh: &mut RoadMesh,
    p: [Vec3; 4],
    normal: Option<[Vec3; 2]>,
    u: [f32; 2],
    v: [f32; 2],
    collision: bool,
) {
    let [a, b, c, d] = p;
    let ns = normal.unwrap_or([Vec3::NEG_Y; 2]);
    let uv = [[u[0], v[0]], [u[1], v[0]], [u[0], v[1]], [u[1], v[1]]];
    if normal.is_some() {
        triangle(
            &mut mesh.visible,
            [a, b, c],
            [uv[0], uv[1], uv[2]],
            [ns[0], ns[0], ns[1]],
        );
        triangle(
            &mut mesh.visible,
            [b, d, c],
            [uv[1], uv[3], uv[2]],
            [ns[0], ns[1], ns[1]],
        );
    }
    if collision {
        triangle(
            &mut mesh.solid,
            [a, b, c],
            [uv[0], uv[1], uv[2]],
            [ns[0], ns[0], ns[1]],
        );
        triangle(
            &mut mesh.solid,
            [b, d, c],
            [uv[1], uv[3], uv[2]],
            [ns[0], ns[1], ns[1]],
        );
    }
}
pub fn build(region: &ProceduralRegion) -> BTreeMap<String, RoadMesh> {
    let mut output = BTreeMap::<String, RoadMesh>::new();
    for p in rows(region, "roads") {
        let kind = f(p, "kind") as usize;
        if kind > 3 {
            continue;
        }
        let ramp = flag(p, "ramp");
        let tex = texture(kind, ramp);
        let out = output.entry(tex).or_default();
        let hw = half(kind);
        let bevel = if kind == 3 { 0.66 } else { 0.44 };
        let run = bevel * 3.;
        let h = |x: f32, z: f32, profile: f32| {
            let surface = profile - 0.12 + 0.22 + lift(kind);
            if flag(p, "inTunnel") {
                surface
            } else {
                surface.max(ground(region, x, z) + 0.02 + lift(kind))
            }
        };
        let ca = Vec3::new(f(p, "x0"), 0., f(p, "z0"));
        let cb = Vec3::new(f(p, "x1"), 0., f(p, "z1"));
        let na = Vec3::new(-f(p, "t0Z"), 0., f(p, "t0X"));
        let nb = Vec3::new(-f(p, "t1Z"), 0., f(p, "t1X"));
        let edge = |c: Vec3, n: Vec3, w: f32, profile: f32| {
            let mut p = c + n * w;
            p.y = h(p.x, p.z, profile);
            p
        };
        let la = edge(ca, na, hw, f(p, "h0"));
        let ra = edge(ca, -na, hw, f(p, "h0"));
        let lb = edge(cb, nb, hw, f(p, "h1"));
        let rb = edge(cb, -nb, hw, f(p, "h1"));
        let wla = f(p, "w0") + f(p, "wl0");
        let wra = f(p, "w0") + f(p, "wr0");
        let wlb = f(p, "w1") + f(p, "wl1");
        let wrb = f(p, "w1") + f(p, "wr1");
        let wide_l = wla > 1e-3 || wlb > 1e-3;
        let wide_r = wra > 1e-3 || wrb > 1e-3;
        let law = if wide_l {
            edge(ca, na, hw + wla, f(p, "h0"))
        } else {
            la
        };
        let raw = if wide_r {
            edge(ca, -na, hw + wra, f(p, "h0"))
        } else {
            ra
        };
        let lbw = if wide_l {
            edge(cb, nb, hw + wlb, f(p, "h1"))
        } else {
            lb
        };
        let rbw = if wide_r {
            edge(cb, -nb, hw + wrb, f(p, "h1"))
        } else {
            rb
        };
        let down = Vec3::NEG_Y * bevel;
        let no_l = flag(p, "noBevelL");
        let no_r = flag(p, "noBevelR");
        let loa = law + if no_l { Vec3::ZERO } else { na * run } + down;
        let roa = raw - if no_r { Vec3::ZERO } else { na * run } + down;
        let lob = lbw + if no_l { Vec3::ZERO } else { nb * run } + down;
        let rob = rbw - if no_r { Vec3::ZERO } else { nb * run } + down;
        let uv = [
            f(p, "s0") / if ramp { 32. } else { repeat(kind) },
            f(p, "s1") / if ramp { 32. } else { repeat(kind) },
        ];
        let lean = |n: Vec3| (n * (bevel / run) + Vec3::Y).normalize();
        quad(
            out,
            [la, ra, lb, rb],
            Some([Vec3::Y; 2]),
            [0., 1.],
            uv,
            true,
        );
        if wide_l {
            quad(
                out,
                [law, la, lbw, lb],
                Some([Vec3::Y; 2]),
                [0., 0.],
                uv,
                true,
            );
        }
        if wide_r {
            quad(
                out,
                [ra, raw, rb, rbw],
                Some([Vec3::Y; 2]),
                [1., 1.],
                uv,
                true,
            );
        }
        quad(
            out,
            [loa, law, lob, lbw],
            (!no_l).then_some([lean(na), lean(nb)]),
            [0., 0.],
            uv,
            true,
        );
        quad(
            out,
            [raw, roa, rbw, rob],
            (!no_r).then_some([lean(-na), lean(-nb)]),
            [1., 1.],
            uv,
            true,
        );
        quad(out, [loa, roa, lob, rob], None, [0., 0.], [0., 0.], true);
        for (open, l, r, lo, ro, dir, v) in [
            (
                flag(p, "openStart"),
                law,
                raw,
                loa,
                roa,
                Vec3::new(-f(p, "t0X"), 0., -f(p, "t0Z")),
                uv[0],
            ),
            (
                flag(p, "openEnd"),
                lbw,
                rbw,
                lob,
                rob,
                Vec3::new(f(p, "t1X"), 0., f(p, "t1Z")),
                uv[1],
            ),
        ] {
            if !open {
                continue;
            }
            let o = dir * run;
            let n = Some([lean(dir); 2]);
            quad(
                out,
                [l, r, l + o + down, r + o + down],
                n,
                [0., 1.],
                [v, v],
                true,
            );
            quad(
                out,
                [lo, l, lo + o, l + o + down],
                n,
                [0., 0.],
                [v, v],
                true,
            );
            quad(
                out,
                [r, ro, r + o + down, ro + o],
                n,
                [1., 1.],
                [v, v],
                true,
            );
        }
    }
    for patch in rows(region, "roadPatches") {
        let kind = f(patch, "kind") as usize;
        if kind > 3 {
            continue;
        }
        let ramp = flag(patch, "ramp");
        // Material uses a mirrored, marking-free band of the source road texture.
        let key = format!("patch:{}", texture(kind, ramp));
        let out = output.entry(key).or_default();
        let band = if kind == 3 {
            1.
        } else if kind < 2 {
            52. / 256. * 2.
        } else {
            104. / 256. * 2.
        };
        let tile_u = band * half(kind) * 2.;
        let tile_v = if ramp { 32. } else { repeat(kind) };
        let points: Vec<Vec3> = (0..list(patch, "x").len())
            .map(|i| {
                let (x, z) = (a(patch, "x", i), a(patch, "z", i));
                Vec3::new(
                    x,
                    a(patch, "y", i).max(ground(region, x, z) + 0.02 + a(patch, "lift", i)),
                    z,
                )
            })
            .collect();
        let uv: Vec<[f32; 2]> = points
            .iter()
            .map(|p| {
                [
                    (p.x - f(patch, "anchorX")) / tile_u,
                    (p.z - f(patch, "anchorZ")) / tile_v,
                ]
            })
            .collect();
        for tri in list(patch, "tris").chunks_exact(3) {
            let ids: [usize; 3] =
                std::array::from_fn(|i| tri[i].as_u64().unwrap_or(u64::MAX) as usize);
            if ids.iter().any(|i| *i >= points.len()) {
                continue;
            }
            let p = ids.map(|i| points[i]);
            let tex = ids.map(|i| uv[i]);
            triangle(&mut out.visible, p, tex, [Vec3::Y; 3]);
            triangle(&mut out.solid, p, tex, [Vec3::Y; 3]);
        }
        let bevel = if kind == 3 { 0.66 } else { 0.44 };
        let run = bevel * 3.;
        for (si, skirt) in list(patch, "skirts").iter().enumerate() {
            let Some(ids) = skirt.as_array() else {
                continue;
            };
            let nx = list(patch, "skirtNX").get(si).and_then(Value::as_array);
            let nz = list(patch, "skirtNZ").get(si).and_then(Value::as_array);
            for (j, pair) in ids.windows(2).enumerate() {
                let ia = pair[0].as_u64().unwrap_or(u64::MAX) as usize;
                let ib = pair[1].as_u64().unwrap_or(u64::MAX) as usize;
                if ia >= points.len() || ib >= points.len() {
                    continue;
                }
                let n = |i| {
                    Vec3::new(
                        nx.and_then(|v| v.get(i))
                            .and_then(Value::as_f64)
                            .unwrap_or(0.) as f32,
                        0.,
                        nz.and_then(|v| v.get(i))
                            .and_then(Value::as_f64)
                            .unwrap_or(0.) as f32,
                    )
                };
                let (na, nb) = (n(j), n(j + 1));
                let (pa, pb) = (points[ia], points[ib]);
                let fa = pa + na * run - Vec3::Y * bevel;
                let fb = pb + nb * run - Vec3::Y * bevel;
                let la = (na * (bevel / run) + Vec3::Y).normalize();
                let lb = (nb * (bevel / run) + Vec3::Y).normalize();
                let fua = [
                    uv[ia][0] + na.x * run / tile_u,
                    uv[ia][1] + na.z * run / tile_v,
                ];
                let fub = [
                    uv[ib][0] + nb.x * run / tile_u,
                    uv[ib][1] + nb.z * run / tile_v,
                ];
                for target in [&mut out.visible, &mut out.solid] {
                    triangle(target, [fa, pa, fb], [fua, uv[ia], fub], [la, la, lb]);
                    triangle(target, [pa, pb, fb], [uv[ia], uv[ib], fub], [la, lb, lb]);
                }
            }
        }
    }
    for mark in rows(region, "roadMarks") {
        let yellow = f(mark, "colour") == 1.;
        let key = if yellow {
            "paint:yellow"
        } else {
            "paint:white"
        };
        let out = output.entry(key.into()).or_default();
        let points: Vec<Vec3> = (0..list(mark, "x").len())
            .map(|i| {
                let (x, z) = (a(mark, "x", i), a(mark, "z", i));
                Vec3::new(
                    x,
                    a(mark, "y", i).max(ground(region, x, z) + 0.02 + a(mark, "lift", i)) + 0.015,
                    z,
                )
            })
            .collect();
        let color = if yellow {
            [210. / 255., 191. / 255., 34. / 255., 1.]
        } else {
            [200. / 255., 200. / 255., 200. / 255., 1.]
        };
        for p in points.chunks_exact(4) {
            quad(
                out,
                [p[0], p[1], p[2], p[3]],
                Some([Vec3::Y; 2]),
                [0., 0.],
                [0., 0.],
                false,
            );
        }
        out.visible.colors.fill(color);
    }
    output
}

/// RegionStreamer.RoadMat's marking-free mirrored asphalt band, retained as an
/// in-memory image asset so a junction's UV scale stays the same as its ribbon.
pub fn material(
    key: &str,
    root: &std::path::Path,
    cache: &mut crate::content::ContentCache,
    assets: &AssetServer,
    materials: &mut Assets<StandardMaterial>,
    images: &mut Assets<Image>,
) -> Result<Handle<StandardMaterial>, super::ContentError> {
    use bevy::{
        asset::RenderAssetUsages,
        image::{
            CompressedImageFormats, ImageAddressMode, ImageFilterMode, ImageSampler,
            ImageSamplerDescriptor, ImageType,
        },
        render::render_resource::{Extent3d, TextureDimension, TextureFormat},
    };
    if let Some(material) = cache.materials.get(key) {
        return Ok(material.clone());
    }
    let Some(path) = key.strip_prefix("patch:") else {
        return Ok(cache.material(
            (!key.starts_with("paint:")).then_some(key),
            false,
            assets,
            materials,
        ));
    };
    let bytes = std::fs::read(root.join(path))?;
    let image = Image::from_buffer(
        &bytes,
        ImageType::Extension("png"),
        CompressedImageFormats::NONE,
        true,
        ImageSampler::Default,
        RenderAssetUsages::default(),
    )
    .map_err(|e| super::ContentError(e.to_string()))?;
    let image = image
        .convert(TextureFormat::Rgba8UnormSrgb)
        .ok_or_else(|| super::ContentError("unsupported road texture format".into()))?;
    let width = image.width() as usize;
    let height = image.height() as usize;
    let trail = path.ends_with("road_5.png");
    let highway = path.ends_with("road_0.png") || path.ends_with("road_1.png");
    let (x0, x1) = if trail {
        (0, width)
    } else if highway {
        (width * 4 / 256, width * 56 / 256)
    } else {
        (width * 8 / 256, width * 112 / 256)
    };
    let band = x1 - x0;
    let repeat = if trail { 1 } else { 2 };
    let out_width = band * repeat;
    let raw = image
        .data
        .as_ref()
        .ok_or_else(|| super::ContentError("road image has no CPU pixels".into()))?;
    let mut data = vec![0; out_width * height * 4];
    for y in 0..height {
        for x in 0..out_width {
            let source = x0 + if x < band { x } else { out_width - 1 - x };
            data[(y * out_width + x) * 4..(y * out_width + x + 1) * 4]
                .copy_from_slice(&raw[(y * width + source) * 4..(y * width + source + 1) * 4]);
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: out_width as u32,
            height: height as u32,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::Descriptor(ImageSamplerDescriptor {
        address_mode_u: ImageAddressMode::Repeat,
        address_mode_v: ImageAddressMode::Repeat,
        min_filter: ImageFilterMode::Nearest,
        mag_filter: ImageFilterMode::Nearest,
        ..default()
    });
    let material = materials.add(StandardMaterial {
        base_color_texture: Some(images.add(image)),
        perceptual_roughness: 1.,
        ..default()
    });
    cache.materials.insert(key.into(), material.clone());
    Ok(material)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn generated_ribbon_keeps_source_sloping_bevel_and_closed_collision() {
        let region:ProceduralRegion=serde_json::from_value(serde_json::json!({"cells":1,"spacing":256.,"heights":[10.,10.,10.,10.],"trees":[],
            "roads":[{"kind":2,"x0":100.,"z0":100.,"x1":100.,"z1":104.,"h0":10.,"h1":10.,"s0":0.,"s1":4.,"t0X":0.,"t0Z":1.,"t1X":0.,"t1Z":1.,"openStart":true,"openEnd":true}]})).unwrap();
        let meshes = build(&region);
        let mesh = &meshes["roads/road_8.png"];
        assert!((mesh.visible.min.x - (100. - 4.6 - 1.32)).abs() < 1e-4);
        assert!((mesh.visible.max.y - 10.115).abs() < 1e-4);
        assert!((mesh.visible.min.y - (10.115 - 0.44)).abs() < 1e-4);
        assert!(mesh.solid.positions.len() > mesh.visible.positions.len());
        assert!(
            mesh.visible
                .uvs
                .iter()
                .any(|uv| (uv[1] - 0.125).abs() < 1e-6)
        );
    }
    #[test]
    fn mesh_height_uses_the_same_triangles_as_rendering() {
        let region: ProceduralRegion = serde_json::from_value(
            serde_json::json!({"cells":1,"spacing":4.,"heights":[0.,0.,0.,4.],"trees":[]}),
        )
        .unwrap();
        assert_eq!(ground(&region, 1., 1.), 0.);
        assert_eq!(ground(&region, 3., 3.), 2.);
    }
}
