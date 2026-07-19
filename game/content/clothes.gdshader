// clothes.gdshader -- Godot port of Unturned's "Standard/Clothes" (StandardClothes.shader).
// Paints a shirt + pants texture set onto the SINGLE skinned body mesh via its UV0 atlas, over a
// flat skin base. 1:1 with the source fragment composite EXCEPT the face path is OMITTED on purpose:
// the ripped body UV0 has 0 verts in the shader's face sub-cell, and the face is drawn instead by a
// separate Skull-bone face-quad decal in RiggedCharacter.cs. So this composites ONLY skin+shirt+pants.
//
// Source composite (StandardClothes.shader, face terms dropped):
//   Albedo    = lerp(lerp(SkinColor, shirtAlbedo.rgb, shirtAlbedo.a), pantsAlbedo.rgb, pantsAlbedo.a)
//   Emission  = lerp(shirtEmission.rgb * shirtAlbedo.a, pantsEmission.rgb, pantsAlbedo.a) * 2.0
//   Specular  = Albedo.rgb * lerp(shirtMetallic.r * shirtAlbedo.a, pantsMetallic.r, pantsAlbedo.a)
//   Smoothness= lerp(shirtMetallic.a * shirtAlbedo.a, pantsMetallic.a, pantsAlbedo.a)
// Unity's StandardSpecular Specular-color output maps to Godot's metallic workflow: METALLIC = the
// same metallic lerp (metallic 0 -> ~0.04 dielectric spec, 1 -> albedo-tinted spec, per Nelson's note in
// the source), ROUGHNESS = 1 - Smoothness. One UV set (the body mesh's UV0).
//
// Unassigned shirt/pants read as fully transparent (alpha 0 -> skin shows): every texture is gated by a
// has_* bool that defaults false, so a bare body renders as plain skin_color without binding any texture.
//
// depth_draw_always + a ghost_alpha uniform preserves RiggedCharacter's FLANKER_STALK ghost toggle
// through the ShaderMaterial (source is Opaque; at ghost_alpha=1.0 the depth write keeps it solid/
// self-occluding = opaque look, at 0.2 it's the faint stalker shimmer). cull_front replicates the old
// StandardMaterial3D CullMode.Front (the rig's Z-flip reverses winding).
shader_type spatial;
render_mode cull_front, depth_draw_always;

uniform vec4 skin_color : source_color = vec4(0.82, 0.66, 0.52, 1.0);
uniform bool flip_shirt = false;   // left-handed model mirror (SP single body: false)
uniform float ghost_alpha = 1.0;   // FLANKER_STALK ghost: 1.0 solid, 0.2 shimmer

uniform bool has_shirt_albedo = false;
uniform bool has_shirt_emission = false;
uniform bool has_shirt_metallic = false;
uniform sampler2D shirt_albedo : source_color, filter_nearest;
uniform sampler2D shirt_emission : source_color, filter_nearest;
uniform sampler2D shirt_metallic : hint_default_black, filter_nearest;

uniform bool has_pants_albedo = false;
uniform bool has_pants_emission = false;
uniform bool has_pants_metallic = false;
uniform sampler2D pants_albedo : source_color, filter_nearest;
uniform sampler2D pants_emission : source_color, filter_nearest;
uniform sampler2D pants_metallic : hint_default_black, filter_nearest;

void fragment() {
	// Shirt sub-atlas flip (front of shirt = upper-left 1/4, back = next 1/4). Ported verbatim from the
	// source; a no-op when flip_shirt=false (the SP single-body default).
	vec2 shirtUV = UV;
	float flipShirtU = ceil(shirtUV.x * 4.0) * 0.25 - fract(shirtUV.x * 4.0) * 0.25;
	float flipShirtAlpha = float(flip_shirt) * float(shirtUV.x < 0.5) * float(shirtUV.y > 0.75);
	shirtUV.x = mix(shirtUV.x, flipShirtU, flipShirtAlpha);

	vec4 shirtAlbedo = has_shirt_albedo ? texture(shirt_albedo, shirtUV) : vec4(0.0);
	vec4 pantsAlbedo = has_pants_albedo ? texture(pants_albedo, UV) : vec4(0.0);
	vec4 shirtEmission = has_shirt_emission ? texture(shirt_emission, shirtUV) : vec4(0.0);
	vec4 pantsEmission = has_pants_emission ? texture(pants_emission, UV) : vec4(0.0);
	vec4 shirtMetallic = has_shirt_metallic ? texture(shirt_metallic, shirtUV) : vec4(0.0);
	vec4 pantsMetallic = has_pants_metallic ? texture(pants_metallic, UV) : vec4(0.0);

	vec3 albedo = mix(mix(skin_color.rgb, shirtAlbedo.rgb, shirtAlbedo.a), pantsAlbedo.rgb, pantsAlbedo.a);
	ALBEDO = albedo;

	EMISSION = mix(shirtEmission.rgb * shirtAlbedo.a, pantsEmission.rgb, pantsAlbedo.a) * 2.0;

	METALLIC = mix(shirtMetallic.r * shirtAlbedo.a, pantsMetallic.r, pantsAlbedo.a);
	ROUGHNESS = 1.0 - mix(shirtMetallic.a * shirtAlbedo.a, pantsMetallic.a, pantsAlbedo.a);

	ALPHA = ghost_alpha;
}
