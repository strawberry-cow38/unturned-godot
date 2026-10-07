// gear_wear.gdshader -- a WORN hat / vest / backpack / mask (v56 durability). The bone-attached gear normally wears a
// plain StandardMaterial3D (RiggedCharacter.AttachGear); once a piece is actually worn, it is swapped for this, which
// draws the same texture the same way (nearest filter, double-sided, rough, non-metal) plus the shared tatter look.
// Unlike the body's painted cloth, a hole here is a real hole: there is nothing under a hat but the head.
// Noise is in OBJECT space, snapped to a 2.5 cm grid: gear textures are tiny palettes, so UV would give every face
// one flat value. A piece with an emissive lens (nightvision, headlamp) keeps its StandardMaterial3D -- see RiggedCharacter.
shader_type spatial;
render_mode cull_disabled;

#include "res://content/tatter.gdshaderinc"

uniform sampler2D albedo_tex : source_color, filter_nearest;
uniform bool has_albedo = false;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform float wear : hint_range(0.0, 1.0) = 0.0;
varying vec3 obj_pos;

void vertex() {
	obj_pos = VERTEX;
}

void fragment() {
	vec4 a = has_albedo ? texture(albedo_tex, UV) * albedo_color : albedo_color;
	vec3 cell = floor(obj_pos / 0.02) * 0.02;
	vec3 t = tatter3(cell * 14.0 + vec3(0.41, 0.17, 0.83), wear);
	if (t.x < 0.5) discard;
	ALBEDO = tatter_tint(a.rgb, t);
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.5;
}
