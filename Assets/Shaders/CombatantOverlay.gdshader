shader_type canvas_item;

// Four independent per-combatant effects composited into a single shader so
// all can run concurrently without material-slot collision on the sprite.
//
// flash_amount  — "learnable signal flash" — used by FlashCombatantLearnable
//                 to signal an enemy committing to their learnable signature
//                 move at turn-start. Pulses; high amplitude. Mix target is
//                 the flash_color uniform (default white if unset by the
//                 caller) so the visual color is caller-configurable.
// tint_amount   — Phase 5 "threat reveal" — used by FlashCombatantThreatened
//                 to signal incoming enemy attacks on the target combatant.
// active_amount — Phase 6 C8 "active turn highlight" — sustained subtle white
//                 wash on the active combatant's sprite for the duration of
//                 their turn. Driven by the AdvanceTurn lifecycle hub via
//                 ApplyActiveSpriteTint / ClearActiveSpriteTint. Effective
//                 visual weight is tuned via the ActiveTintAmount caller-side
//                 constant (no in-shader softening multiplier).
// target_amount — Phase 6 C11.1 "selected target highlight" — yellow tint
//                 on the picker's currently-selected combatant. Driven by
//                 EnterSelectingTarget / cycle handlers / Confirm / Cancel
//                 via ApplyTargetHighlight / ClearTargetHighlight. Replaces
//                 the pointer as the primary "selected" indicator;
//                 amplitude tuned via the TargetTintAmount caller-side
//                 constant.
//
// Color uniforms:
// tint_color    — mix target color for tint_amount (red by default; set
//                 once at the shader level, not currently caller-overridden).
// flash_color   — mix target color for flash_amount (white by default;
//                 written caller-side by FlashCombatantLearnable to purple
//                 for the learnable signal). Persists across calls — future
//                 flash-event authors set their own color explicitly.
//
// Animation uniforms:
// active_breathing_hz       — sine frequency (Hz) of the active-turn tint's
//                             "breathing" oscillation. Default 0.5 (2.0s per
//                             cycle, reads as alert-and-attentive). Tunable
//                             via Godot's inspector on the ShaderMaterial.
// active_breathing_amplitude — peak sine offset added to active_amount before
//                             the mix. Default 0.07 — at ActiveTintAmount =
//                             0.20 this swings the effective amplitude between
//                             0.13 and 0.27 (~70% peak-to-peak around the
//                             baseline). The breathing baseline was bumped
//                             from C8's static 0.15 to 0.20 because the eye
//                             accepts a higher peak when the value falls back
//                             dynamically — the wash isn't pinned at maximum.
//                             Tunable via inspector.
//
// The active-tint mix uses TIME (Godot canvas_item built-in, seconds since
// rendering started). First use of TIME in the project; doesn't reset within
// a scene. The breathing factor is smoothstep-gated on active_amount itself
// so non-active combatants (active_amount = 0) don't render the positive
// half-cycle as 0→0.05→0 flicker — see the gate inside the fragment body.

uniform float flash_amount              : hint_range(0.0, 1.0) = 0.0;
uniform float tint_amount               : hint_range(0.0, 1.0) = 0.0;
uniform float active_amount             : hint_range(0.0, 1.0) = 0.0;
uniform float target_amount             : hint_range(0.0, 1.0) = 0.0;
uniform vec4  tint_color                : source_color         = vec4(1.0, 0.2, 0.2, 1.0);
uniform vec4  flash_color               : source_color         = vec4(1.0, 1.0, 1.0, 1.0);
uniform float active_breathing_hz       : hint_range(0.0, 4.0) = 0.5;
uniform float active_breathing_amplitude: hint_range(0.0, 0.2) = 0.07;

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
    // (~0.20) rather than an in-shader softening multiplier so the visual is
    // tuned in one place. The held amplitude breathes via a TIME-driven sine
    // (active_breathing_hz / active_breathing_amplitude uniforms) so the
    // sustained tint reads as a living "thinking" pulse rather than a static
    // wash. Breathing is smoothstep-gated on active_amount so non-active
    // combatants (active_amount = 0) don't flicker on the positive half-cycle;
    // the gate also ramps breathing on/off smoothly across the fade-in /
    // fade-out tweens. The trailing max(0.0, ...) clamp is self-documenting —
    // most drivers already treat negative mix factors as 0, but the explicit
    // clamp removes ambiguity at the sine trough.
    //
    // Composed AFTER threat tint so when both are active the white wash
    // slightly lightens the red — acceptable given threat's 0.85 amplitude
    // dominates over active's ~0.20 (post-breathing-bump baseline; was 0.15
    // at C8 ship time). If interactive verification shows the stack reads as
    // washout, gate this on (1.0 - tint_amount) — see C8 plan-doc note.
    // (Phase 6 polish: deliberately preserved as design history; the case
    // remains unaddressed by construction.)
    float active_breathing_gate = smoothstep(0.0, 0.05, active_amount);
    float active_breath = sin(TIME * 6.28318 * active_breathing_hz)
                        * active_breathing_amplitude
                        * active_breathing_gate;
    float effective_active = max(0.0, active_amount + active_breath);
    col.rgb = mix(col.rgb, vec3(1.0), effective_active * col.a);

    // Selected-target highlight — partial mix toward yellow. Composed AFTER
    // active so cure-on-self (active player highlighting themselves as target)
    // shows yellow on top of the white-active wash and reads as "selected"
    // rather than muddy. Composed BEFORE flash so a concurrent learnable-flash
    // pulse still overrides — preserves "flash = highest priority" invariant.
    col.rgb = mix(col.rgb, vec3(1.0, 1.0, 0.0), target_amount * col.a);

    // Learnable signal flash — applied last so a concurrent flash overrides
    // everything else (highest priority). Color comes from flash_color (white
    // by default; FlashCombatantLearnable writes purple for the learnable
    // signal). Per-pulse amplitude tween lives on flash_amount.
    col.rgb = mix(col.rgb, flash_color.rgb, flash_amount * col.a);

    COLOR = col;
}
