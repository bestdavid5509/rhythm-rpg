# Effect Spritesheet Manifest

All Size values are **per-frame dimensions** (width × height in pixels).

## Effects/Comet_Effects/

| File | Frames | Size (per frame) | Layout | Impact Frame |
|------|--------|-----------------|--------|--------------|
| Blue_Magic_Comet_Sheet.png | 16 | 96 × 160 | 2 rows, 8 cols | 5 |
| Energy comet_30_deg-Sheet.png | 20 | 148 × 232 | 1 row, 20 cols | 11 |
| Energy comet_45_deg-Sheet.png | 21 | 256 × 256 | 1 row, 21 cols | 12 |
| Energy comet_many-Sheet.png | 28 | 325 × 332 | 1 row, 28 cols | N/A |
| Energy comet_vertical-Sheet.png | 20 | 112 × 224 | 1 row, 20 cols | 11 |
| Red_Magic_Comet_Sheet.png | 16 | 96 × 160 | 2 rows, 8 cols | 5 |

## Effects/Support_Effects/

| File | Frames | Size (per frame) | Layout | Impact Frame |
|------|--------|-----------------|--------|--------------|
| Blue_Circle_Buff_Sheet.png | 16 | 144 × 144 | 2 rows, 8 cols | 9 |
| Blue_Descending_Circle_Buff_Sheet.png | 14 | 128 × 208 | 1 row, 14 cols | 2 |
| Yellow_Circle_Buff_Sheet.png | 16 | 144 × 144 | 2 rows, 8 cols | 9 |
| Yellow_Descending_Circle_Buff_Sheet.png | 14 | 128 × 208 | 1 row, 14 cols | 2 |

## Effects/Sword_Effects/

| File | Frames | Size (per frame) | Layout | Impact Frame |
|------|--------|-----------------|--------|--------------|
| Blue_Fire_Hammer_Swipe_Sheet.png | 14 | 96 × 96 | 2 rows, 7 cols | 6 |
| Blue_Sword_Plunge_Sheet.png | 18 | 192 × 256 | 2 rows, 9 cols | 6 |
| Blue_Triple_Sword_Plunge_Sheet.png | 19 | 320 × 256 | 4 rows, 5 cols | 6, 7, 8 |
| Ice_Sword_Swipe_Sheet.png | 16 | 176 × 160 | 2 rows, 8 cols | 10 |
| Red_Fire_Hammer_Swipe_Sheet.png | 14 | 96 × 96 | 2 rows, 7 cols | 6 |
| Red_Sword_Plunge_Sheet.png | 18 | 192 × 256 | 2 rows, 9 cols | 6 |
| Red_Triple_Sword_Plunge_Sheet.png | 19 | 320 × 256 | 4 rows, 5 cols | 6, 7, 8 |

| Anime_Slash_Grey_Sheet.png | 15 | 128 × 128 | 1 row, 15 cols | 0 |

## Notes

- `Energy comet_many` has no defined impact frame — it is a looping or ambient effect.
- Blue_Triple and Red_Triple have multi-impact frames `[6, 7, 8]` mapping to the three hits in a multi-step AttackData sequence.
- Res paths: `res://Assets/Effects/<subfolder>/<filename>`
