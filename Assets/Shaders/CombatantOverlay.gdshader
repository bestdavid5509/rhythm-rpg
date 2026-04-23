shader_type canvas_item;

// Two independent per-combatant effects composited into a single shader so
// both can run concurrently without material-slot collision on the sprite.
//
// flash_amount — existing "white flash" — used by FlashCombatantWhite to
//                signal learnable moves on the enemy.
// tint_amount  — Phase 5 "threat reveal" — used by FlashCombatantThreatened
//                to signal incoming enemy attacks on the target combatant.

uniform float flash_amount : hint_range(0.0, 1.0) = 0.0;
uniform float tint_amount  : hint_range(0.0, 1.0) = 0.0;
uniform vec4  tint_color   : source_color         = vec4(1.0, 0.2, 0.2, 1.0);

void fragment() {
    vec4 col = texture(TEXTURE, UV);

    // Red tint — mixes the sprite's RGB toward tint_color within the sprite
    // silhouette (tint_amount * col.a keeps the tint inside opaque pixels).
    col.rgb = mix(col.rgb, tint_color.rgb, tint_amount * col.a);

    // White flash — applied after the tint so a concurrent flash overrides
    // the tint (white = learnable signal, higher priority).
    col.rgb = mix(col.rgb, vec3(1.0), flash_amount * col.a);

    COLOR = col;
}
