# Optimum Cairo Native C Shim (`libcairo.so.2`)

A lightweight, standalone drop-in C shared library providing ABI compatibility with standard Cairo (`libcairo.so.2`) for unmanaged third-party C binaries, external native libraries, and direct mod P/Invokes (`[DllImport("libcairo-2")]`).

## Architecture & Scope

Optimum's managed `cairo-sharp.dll` wrapper intercepts managed API consumers without requiring native Cairo. However, if an external unmanaged binary or unintercepted P/Invoke dynamically links directly against `libcairo.so.2` (bypassing the managed runtime), dynamic linkers (`ld.so`) require a physical shared object providing the standard Cairo symbols.

This native shim implements the complete surface, pattern, context, transform, and state APIs using direct in-memory rasterization and geometry transforms, completely free of any dependencies on system Cairo or X11.

## Exported Functions

- **Surfaces**: `cairo_image_surface_create`, `cairo_image_surface_create_for_data`, `cairo_image_surface_get_data`, `cairo_image_surface_get_format`, `cairo_image_surface_get_width`, `cairo_image_surface_get_height`, `cairo_image_surface_get_stride`, `cairo_surface_reference`, `cairo_surface_destroy`, `cairo_surface_status`, `cairo_surface_write_to_png`.
- **Context**: `cairo_create`, `cairo_reference`, `cairo_destroy`, `cairo_status`, `cairo_save`, `cairo_restore`, `cairo_get_target`, `cairo_get_source`.
- **Sources & Patterns**: `cairo_set_source_rgb`, `cairo_set_source_rgba`, `cairo_set_source`, `cairo_set_source_surface`, `cairo_pattern_create_rgb`, `cairo_pattern_create_rgba`, `cairo_pattern_create_for_surface`, `cairo_pattern_create_linear`, `cairo_pattern_create_radial`, `cairo_pattern_add_color_stop_rgb`, `cairo_pattern_add_color_stop_rgba`, `cairo_pattern_set_matrix`, `cairo_pattern_set_extend`.
- **Transforms**: `cairo_translate`, `cairo_scale`, `cairo_rotate`, `cairo_transform`, `cairo_set_matrix`, `cairo_get_matrix`, `cairo_identity_matrix`, `cairo_user_to_device`, `cairo_device_to_user`.
- **Paths**: `cairo_new_path`, `cairo_move_to`, `cairo_line_to`, `cairo_curve_to`, `cairo_rectangle`, `cairo_close_path`, `cairo_copy_path`, `cairo_append_path`, `cairo_path_destroy`.
- **Drawing**: `cairo_paint`, `cairo_paint_with_alpha`, `cairo_fill`, `cairo_fill_preserve`, `cairo_stroke`, `cairo_stroke_preserve`, `cairo_mask`, `cairo_mask_surface`, `cairo_clip`.
- **Font Stubs**: `cairo_select_font_face`, `cairo_set_font_size`, `cairo_text_extents`, `cairo_font_extents`, `cairo_show_text`, `cairo_show_glyphs`, `cairo_ft_font_face_create_for_ft_face`.

## Compilation & Verification

```bash
make clean
make test
```

Builds `libcairo.so.2` and executes `test-shim` verifying:
1. ARGB32 memory surface creation and stride alignment.
2. Background solid color painting with little-endian BGRA pixel layout validation.
3. Sub-rectangle path geometry and compositing fill.
4. Matrix affine transformations across save/restore scopes.
5. Surface writeback and memory cleanup.
