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

    // Red tint — multiplicative blend that preserves the sprite's internal tonal
    // structure. col.rgb * tint_color.rgb gives each pixel its original luminance
    // variation but skewed toward the tint color (red channel preserved at full
    // 1.0 multiplier; green/blue channels suppressed by the tint_color values).
    // mix() interpolates between "original" (tint_amount=0) and "fully multiplied"
    // (tint_amount=1) so fade-in/out edges are smooth. col.a in the factor keeps
    // the tint within the sprite silhouette (no effect on transparent pixels).
    vec3 tinted = col.rgb * tint_color.rgb;
    col.rgb = mix(col.rgb, tinted, tint_amount * col.a);

    // White flash — applied after the tint so a concurrent flash overrides
    // the tint (white = learnable signal, higher priority).
    col.rgb = mix(col.rgb, vec3(1.0), flash_amount * col.a);

    COLOR = col;
}
