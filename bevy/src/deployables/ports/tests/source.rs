//! Differential observations from unmodified real Godot ConnectionPort and
//! AxisBillboard, rather than expectations copied from the native renderer.
use super::*;
use bevy::color::ColorToComponents;

fn vec(value: &Value) -> Vec3 {
    crate::player::vector(value).unwrap()
}
fn color(value: &Value) -> Color {
    let a = value.as_array().unwrap();
    Color::srgba(
        a[0].as_f64().unwrap() as f32,
        a[1].as_f64().unwrap() as f32,
        a[2].as_f64().unwrap() as f32,
        a[3].as_f64().unwrap() as f32,
    )
}
#[test]
fn original_godot_port_geometry_texture_all_material_states_and_axis_billboard_match() {
    let fixture: Value =
        serde_json::from_str(include_str!("../../../../data/port-source-reference.json")).unwrap();
    let mut meshes = Assets::<Mesh>::default();
    let mut images = Assets::<Image>::default();
    let art = art::Art::load(&mut meshes, &mut images);
    let pixels = images.get(&art.texture).unwrap().data.as_ref().unwrap();
    for (i, alpha) in fixture["alpha"].as_array().unwrap().iter().enumerate() {
        assert_eq!(
            pixels[i * 4 + 3] as u64,
            alpha.as_u64().unwrap(),
            "source arrow texel{i}"
        );
        assert_eq!(&pixels[i * 4..i * 4 + 3], &[255, 255, 255]);
    }
    for row in fixture["rows"].as_array().unwrap() {
        let input = &row["input"];
        let position = vec(&input["position"]);
        let port: wires::Port =
            serde_json::from_value(json!({"kind":input["kind"],"role":input["role"],
            "pos":{"x":position.x,"y":position.y,"z":position.z},"watts":250}))
            .unwrap();
        let pose = art::arrow_pose(&port, position);
        assert!(pose.translation.distance(vec(&row["arrowPosition"])) < 2e-6);
        for (i, axis) in [Vec3::X, Vec3::Y, Vec3::Z].into_iter().enumerate() {
            assert!(
                (pose.rotation * axis).distance(vec(&row["arrowBasis"][i])) < 2e-6,
                "source arrow flow basis{input}"
            );
        }
        for state in row["states"].as_array().unwrap() {
            let hi = match state["highlight"].as_str().unwrap() {
                "Focus" => Highlight::Focus,
                "WireOk" => Highlight::Good,
                "WireBad" => Highlight::Bad,
                _ => Highlight::None,
            };
            let native = art::cube(
                &port,
                state["occupied"].as_bool().unwrap(),
                state["dataLive"].as_bool().unwrap(),
                hi,
            );
            let expected = color(&state["cube"]).to_srgba();
            let actual = native.base_color.to_srgba();
            for (a, b) in actual
                .to_f32_array()
                .into_iter()
                .zip(expected.to_f32_array())
            {
                assert!((a - b).abs() < 2e-6, "source port color {input}/{state}");
            }
            let expected = if state["enabled"] == true {
                color(&state["emission"]).to_linear() * (state["energy"].as_f64().unwrap() as f32)
            } else {
                LinearRgba::BLACK
            };
            for (a, b) in native.emissive.to_f32_array()[..3]
                .iter()
                .zip(&expected.to_f32_array()[..3])
            {
                assert!((a - b).abs() < 2e-6, "source port emission {input}/{state}");
            }
            assert_eq!(
                native.alpha_mode == AlphaMode::Blend,
                state["alpha"] == "Alpha"
            );
        }
        let mut native = Transform::default();
        for state in row["spins"].as_array().unwrap() {
            art::face_camera(&mut native, vec(&state["camera"]));
            for (i, axis) in [Vec3::X, Vec3::Y, Vec3::Z].into_iter().enumerate() {
                assert!(
                    (native.rotation * axis).distance(vec(&state["basis"][i])) < 2e-6,
                    "source axis billboard {input}/{state}"
                );
            }
        }
    }
}
