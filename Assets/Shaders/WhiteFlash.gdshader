shader_type canvas_item;
uniform float flash_amount : hint_range(0.0, 1.0) = 0.0;

void fragment() {
    vec4 col = texture(TEXTURE, UV);
    col.rgb = mix(col.rgb, vec3(1.0), flash_amount * col.a);
    COLOR = col;
}
