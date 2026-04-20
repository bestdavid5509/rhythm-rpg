shader_type canvas_item;

// Subtle vertical gradient: navy center fading to darker at top and bottom edges.
// Colors are computed from sRGB hex values — #0d1829 (center) and #060d14 (edges).
void fragment() {
    float d = abs(UV.y - 0.5) * 2.0;
    COLOR = mix(vec4(0.051, 0.094, 0.161, 1.0), vec4(0.024, 0.051, 0.078, 1.0), d);
}
