shader_type canvas_item;

// Three independent per-combatant effects composited into a single shader so
// all can run concurrently without material-slot collision on the sprite.
//
// flash_amount  — "white flash" — used by FlashCombatantWhite to signal
//                 learnable moves on the enemy. Pulses; high amplitude.
// tint_amount   — Phase 5 "threat reveal" — used by FlashCombatantThreatened
//                 to signal incoming enemy attacks on the target combatant.
// active_amount — Phase 6 C8 "active turn highlight" — sustained subtle white
//                 wash on the active combatant's sprite for the duration of
//                 their turn. Driven by the AdvanceTurn lifecycle hub via
//                 ApplyActiveSpriteTint / ClearActiveSpriteTint. Effective
//                 visual weight is tuned via the ActiveTintAmount caller-side
//                 constant (no in-shader softening multiplier).

uniform float flash_amount  : hint_range(0.0, 1.0) = 0.0;
uniform float tint_amount   : hint_range(0.0, 1.0) = 0.0;
uniform float active_amount : hint_range(0.0, 1.0) = 0.0;
uniform vec4  tint_color    : source_color         = vec4(1.0, 0.2, 0.2, 1.0);

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

    // Active-turn whitening — partial mix toward pure white. Sustained for the
    // entire active turn; subtlety lives in the caller-side ActiveTintAmount
    // (~0.15) rather than an in-shader softening multiplier so the visual is
    // tuned in one place. Composed AFTER threat tint so when both are active
    // the white wash slightly lightens the red — acceptable given threat's
    // 0.85 amplitude dominates over active's ~0.15. If interactive verification
    // shows the stack reads as washout, gate this on (1.0 - tint_amount) — see
    // C8 plan-doc note.
    col.rgb = mix(col.rgb, vec3(1.0), active_amount * col.a);

    // White flash — applied last so a concurrent flash overrides everything
    // else (white = learnable signal, highest priority).
    col.rgb = mix(col.rgb, vec3(1.0), flash_amount * col.a);

    COLOR = col;
}
