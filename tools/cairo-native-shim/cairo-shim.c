#define _GNU_SOURCE
#include "cairo.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#define CAIRO_SHIM_EXPORT __attribute__((visibility("default")))

/* -------------------------------------------------------------------------- */
/* Memory Structures                                                          */
/* -------------------------------------------------------------------------- */

typedef struct _cairo_user_data_slot {
    const cairo_user_data_key_t *key;
    void *user_data;
    cairo_destroy_func_t destroy;
    struct _cairo_user_data_slot *next;
} cairo_user_data_slot_t;

static cairo_status_t user_data_set(cairo_user_data_slot_t **head, const cairo_user_data_key_t *key, void *user_data, cairo_destroy_func_t destroy) {
    if (!head || !key) return CAIRO_STATUS_NULL_POINTER;
    cairo_user_data_slot_t **curr = head;
    while (*curr) {
        if ((*curr)->key == key) {
            cairo_user_data_slot_t *old = *curr;
            void *old_data = old->user_data;
            cairo_destroy_func_t old_destroy = old->destroy;
            if (user_data == NULL) {
                *curr = old->next;
                free(old);
                if (old_destroy && old_data) old_destroy(old_data);
                return CAIRO_STATUS_SUCCESS;
            } else {
                old->user_data = user_data;
                old->destroy = destroy;
                if (old_destroy && old_data) old_destroy(old_data);
                return CAIRO_STATUS_SUCCESS;
            }
        }
        curr = &(*curr)->next;
    }
    if (user_data == NULL) return CAIRO_STATUS_SUCCESS;
    cairo_user_data_slot_t *slot = (cairo_user_data_slot_t *)malloc(sizeof(cairo_user_data_slot_t));
    if (!slot) return CAIRO_STATUS_NO_MEMORY;
    slot->key = key;
    slot->user_data = user_data;
    slot->destroy = destroy;
    slot->next = *head;
    *head = slot;
    return CAIRO_STATUS_SUCCESS;
}

static void *user_data_get(cairo_user_data_slot_t *head, const cairo_user_data_key_t *key) {
    if (!key) return NULL;
    for (cairo_user_data_slot_t *s = head; s != NULL; s = s->next) {
        if (s->key == key) return s->user_data;
    }
    return NULL;
}

static void user_data_destroy_all(cairo_user_data_slot_t *head) {
    cairo_user_data_slot_t *curr = head;
    while (curr) {
        cairo_user_data_slot_t *next = curr->next;
        if (curr->destroy && curr->user_data) {
            curr->destroy(curr->user_data);
        }
        free(curr);
        curr = next;
    }
}

struct _cairo_surface {
    int ref_count;
    cairo_status_t status;
    cairo_surface_type_t type;
    cairo_format_t format;
    int width;
    int height;
    int stride;
    unsigned char *data;
    int owns_data;
    double device_offset_x;
    double device_offset_y;
    cairo_user_data_slot_t *user_data;
};

typedef struct _cairo_color_stop {
    double offset;
    double r, g, b, a;
} cairo_color_stop_t;

struct _cairo_pattern {
    int ref_count;
    cairo_status_t status;
    cairo_pattern_type_t type;
    cairo_matrix_t matrix;
    cairo_extend_t extend;
    cairo_filter_t filter;
    double r, g, b, a; /* For solid */
    cairo_surface_t *surface; /* For surface pattern */
    double x0, y0, x1, y1; /* For linear */
    double cx0, cy0, r0, cx1, cy1, r1; /* For radial */
    cairo_color_stop_t *stops;
    int num_stops;
    int stops_capacity;
    cairo_user_data_slot_t *user_data;
};

struct _cairo_font_options {
    cairo_antialias_t antialias;
    cairo_subpixel_order_t subpixel_order;
    cairo_hint_style_t hint_style;
    cairo_hint_metrics_t hint_metrics;
};

struct _cairo_font_face {
    int ref_count;
    cairo_status_t status;
    cairo_font_type_t type;
    char *family;
    cairo_font_slant_t slant;
    cairo_font_weight_t weight;
    cairo_user_data_slot_t *user_data;
};

struct _cairo_scaled_font {
    int ref_count;
    cairo_status_t status;
    cairo_font_face_t *font_face;
    cairo_matrix_t font_matrix;
    cairo_matrix_t ctm;
    cairo_font_options_t options;
    cairo_user_data_slot_t *user_data;
};

typedef struct _rect_cmd {
    double x, y, w, h;
} rect_cmd_t;

typedef struct _cairo_state {
    cairo_matrix_t matrix;
    cairo_pattern_t *source;
    cairo_operator_t op;
    double line_width;
    cairo_line_cap_t line_cap;
    cairo_line_join_t line_join;
    cairo_fill_rule_t fill_rule;
    cairo_antialias_t antialias;
    double miter_limit;
    double tolerance;
    double current_x, current_y;
    int has_current_point;
    struct _cairo_state *prev;
} cairo_state_t;

struct _cairo {
    int ref_count;
    cairo_status_t status;
    cairo_surface_t *target;
    cairo_state_t state;
    rect_cmd_t *rects;
    int num_rects;
    int rects_capacity;
    cairo_user_data_slot_t *user_data;
};

/* -------------------------------------------------------------------------- */
/* Matrix Helpers                                                             */
/* -------------------------------------------------------------------------- */

static void matrix_init_identity(cairo_matrix_t *m) {
    m->xx = 1.0; m->yx = 0.0;
    m->xy = 0.0; m->yy = 1.0;
    m->x0 = 0.0; m->y0 = 0.0;
}

static void matrix_multiply(cairo_matrix_t *r, const cairo_matrix_t *a, const cairo_matrix_t *b) {
    cairo_matrix_t tmp;
    tmp.xx = a->xx * b->xx + a->yx * b->xy;
    tmp.yx = a->xx * b->yx + a->yx * b->yy;
    tmp.xy = a->xy * b->xx + a->yy * b->xy;
    tmp.yy = a->xy * b->yx + a->yy * b->yy;
    tmp.x0 = a->x0 * b->xx + a->y0 * b->xy + b->x0;
    tmp.y0 = a->x0 * b->yx + a->y0 * b->yy + b->y0;
    *r = tmp;
}

static void matrix_transform_point(const cairo_matrix_t *m, double *x, double *y) {
    double px = *x, py = *y;
    *x = m->xx * px + m->xy * py + m->x0;
    *y = m->yx * px + m->yy * py + m->y0;
}

static void matrix_transform_distance(const cairo_matrix_t *m, double *dx, double *dy) {
    double pdx = *dx, pdy = *dy;
    *dx = m->xx * pdx + m->xy * pdy;
    *dy = m->yx * pdx + m->yy * pdy;
}

static int matrix_invert(cairo_matrix_t *m) {
    double det = m->xx * m->yy - m->yx * m->xy;
    if (fabs(det) < 1e-12) return 0;
    double inv_det = 1.0 / det;
    cairo_matrix_t inv;
    inv.xx =  m->yy * inv_det;
    inv.yx = -m->yx * inv_det;
    inv.xy = -m->xy * inv_det;
    inv.yy =  m->xx * inv_det;
    inv.x0 = (m->xy * m->y0 - m->yy * m->x0) * inv_det;
    inv.y0 = (m->yx * m->x0 - m->xx * m->y0) * inv_det;
    *m = inv;
    return 1;
}

/* -------------------------------------------------------------------------- */
/* Surface Implementation                                                     */
/* -------------------------------------------------------------------------- */

static int format_stride(cairo_format_t format, int width) {
    switch (format) {
        case CAIRO_FORMAT_ARGB32:
        case CAIRO_FORMAT_RGB24:
        case CAIRO_FORMAT_RGB30:
            return (width * 4 + 3) & ~3;
        case CAIRO_FORMAT_A8:
            return (width + 3) & ~3;
        case CAIRO_FORMAT_A1:
            return ((width + 7) / 8 + 3) & ~3;
        case CAIRO_FORMAT_RGB16_565:
            return (width * 2 + 3) & ~3;
        case CAIRO_FORMAT_RGB96F:
            return width * 12;
        case CAIRO_FORMAT_RGBA128F:
            return width * 16;
        default:
            return (width * 4 + 3) & ~3;
    }
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_image_surface_create(cairo_format_t format, int width, int height) {
    if (width < 0 || height < 0) return NULL;
    cairo_surface_t *s = (cairo_surface_t *)calloc(1, sizeof(cairo_surface_t));
    if (!s) return NULL;
    s->ref_count = 1;
    s->status = CAIRO_STATUS_SUCCESS;
    s->type = CAIRO_SURFACE_TYPE_IMAGE;
    s->format = format;
    s->width = width;
    s->height = height;
    s->stride = format_stride(format, width);
    if (width > 0 && height > 0) {
        size_t size = (size_t)s->stride * height;
        s->data = (unsigned char *)calloc(1, size);
        s->owns_data = 1;
    }
    return s;
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_image_surface_create_for_data(unsigned char *data, cairo_format_t format, int width, int height, int stride) {
    if (width < 0 || height < 0) return NULL;
    cairo_surface_t *s = (cairo_surface_t *)calloc(1, sizeof(cairo_surface_t));
    if (!s) return NULL;
    s->ref_count = 1;
    s->status = CAIRO_STATUS_SUCCESS;
    s->type = CAIRO_SURFACE_TYPE_IMAGE;
    s->format = format;
    s->width = width;
    s->height = height;
    s->stride = stride;
    s->data = data;
    s->owns_data = 0;
    return s;
}

CAIRO_SHIM_EXPORT unsigned char *cairo_image_surface_get_data(cairo_surface_t *surface) {
    return surface ? surface->data : NULL;
}

CAIRO_SHIM_EXPORT cairo_format_t cairo_image_surface_get_format(cairo_surface_t *surface) {
    return surface ? surface->format : CAIRO_FORMAT_INVALID;
}

CAIRO_SHIM_EXPORT int cairo_image_surface_get_width(cairo_surface_t *surface) {
    return surface ? surface->width : 0;
}

CAIRO_SHIM_EXPORT int cairo_image_surface_get_height(cairo_surface_t *surface) {
    return surface ? surface->height : 0;
}

CAIRO_SHIM_EXPORT int cairo_image_surface_get_stride(cairo_surface_t *surface) {
    return surface ? surface->stride : 0;
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_surface_reference(cairo_surface_t *surface) {
    if (surface) surface->ref_count++;
    return surface;
}

CAIRO_SHIM_EXPORT void cairo_surface_destroy(cairo_surface_t *surface) {
    if (!surface) return;
    if (--surface->ref_count <= 0) {
        user_data_destroy_all(surface->user_data);
        if (surface->owns_data && surface->data) free(surface->data);
        free(surface);
    }
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_surface_status(cairo_surface_t *surface) {
    return surface ? surface->status : CAIRO_STATUS_NULL_POINTER;
}

CAIRO_SHIM_EXPORT cairo_surface_type_t cairo_surface_get_type(cairo_surface_t *surface) {
    return surface ? surface->type : CAIRO_SURFACE_TYPE_IMAGE;
}

CAIRO_SHIM_EXPORT void cairo_surface_flush(cairo_surface_t *surface) { (void)surface; }
CAIRO_SHIM_EXPORT void cairo_surface_mark_dirty(cairo_surface_t *surface) { (void)surface; }
CAIRO_SHIM_EXPORT void cairo_surface_mark_dirty_rectangle(cairo_surface_t *surface, int x, int y, int width, int height) {
    (void)surface; (void)x; (void)y; (void)width; (void)height;
}

CAIRO_SHIM_EXPORT void cairo_surface_set_device_offset(cairo_surface_t *surface, double x_offset, double y_offset) {
    if (surface) { surface->device_offset_x = x_offset; surface->device_offset_y = y_offset; }
}

CAIRO_SHIM_EXPORT void cairo_surface_get_device_offset(cairo_surface_t *surface, double *x_offset, double *y_offset) {
    if (surface) {
        if (x_offset) *x_offset = surface->device_offset_x;
        if (y_offset) *y_offset = surface->device_offset_y;
    }
}

CAIRO_SHIM_EXPORT unsigned int cairo_surface_get_reference_count(cairo_surface_t *surface) {
    return surface ? (unsigned int)surface->ref_count : 0;
}

CAIRO_SHIM_EXPORT void cairo_surface_finish(cairo_surface_t *surface) {
    if (surface) surface->status = CAIRO_STATUS_SURFACE_FINISHED;
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_surface_create_similar(cairo_surface_t *other, cairo_content_t content, int width, int height) {
    (void)other;
    cairo_format_t fmt = (content == CAIRO_CONTENT_COLOR) ? CAIRO_FORMAT_RGB24 :
                         (content == CAIRO_CONTENT_ALPHA) ? CAIRO_FORMAT_A8 : CAIRO_FORMAT_ARGB32;
    return cairo_image_surface_create(fmt, width, height);
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_surface_create_for_rectangle(cairo_surface_t *target, double x, double y, double width, double height) {
    if (!target) return NULL;
    int ix = (int)x, iy = (int)y, iw = (int)width, ih = (int)height;
    if (ix < 0 || iy < 0 || ix + iw > target->width || iy + ih > target->height)
        return cairo_image_surface_create(target->format, iw, ih);
    unsigned char *sub_data = target->data + iy * target->stride + ix * 4;
    return cairo_image_surface_create_for_data(sub_data, target->format, iw, ih, target->stride);
}

/* -------------------------------------------------------------------------- */
/* Pattern Implementation                                                     */
/* -------------------------------------------------------------------------- */

static cairo_pattern_t *pattern_alloc(cairo_pattern_type_t type) {
    cairo_pattern_t *p = (cairo_pattern_t *)calloc(1, sizeof(cairo_pattern_t));
    if (!p) return NULL;
    p->ref_count = 1;
    p->status = CAIRO_STATUS_SUCCESS;
    p->type = type;
    p->extend = (type == CAIRO_PATTERN_TYPE_SURFACE) ? CAIRO_EXTEND_NONE : CAIRO_EXTEND_PAD;
    p->filter = CAIRO_FILTER_GOOD;
    matrix_init_identity(&p->matrix);
    return p;
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pattern_create_rgba(double r, double g, double b, double a) {
    cairo_pattern_t *p = pattern_alloc(CAIRO_PATTERN_TYPE_SOLID);
    if (!p) return NULL;
    p->r = fmax(0.0, fmin(1.0, r));
    p->g = fmax(0.0, fmin(1.0, g));
    p->b = fmax(0.0, fmin(1.0, b));
    p->a = fmax(0.0, fmin(1.0, a));
    return p;
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pattern_create_rgb(double r, double g, double b) {
    return cairo_pattern_create_rgba(r, g, b, 1.0);
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pattern_create_for_surface(cairo_surface_t *surface) {
    cairo_pattern_t *p = pattern_alloc(CAIRO_PATTERN_TYPE_SURFACE);
    if (!p) return NULL;
    p->surface = cairo_surface_reference(surface);
    return p;
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pattern_create_linear(double x0, double y0, double x1, double y1) {
    cairo_pattern_t *p = pattern_alloc(CAIRO_PATTERN_TYPE_LINEAR);
    if (!p) return NULL;
    p->x0 = x0; p->y0 = y0; p->x1 = x1; p->y1 = y1;
    return p;
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pattern_create_radial(double cx0, double cy0, double r0, double cx1, double cy1, double r1) {
    cairo_pattern_t *p = pattern_alloc(CAIRO_PATTERN_TYPE_RADIAL);
    if (!p) return NULL;
    p->cx0 = cx0; p->cy0 = cy0; p->r0 = r0;
    p->cx1 = cx1; p->cy1 = cy1; p->r1 = r1;
    return p;
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pattern_reference(cairo_pattern_t *pattern) {
    if (pattern) pattern->ref_count++;
    return pattern;
}

CAIRO_SHIM_EXPORT void cairo_pattern_destroy(cairo_pattern_t *pattern) {
    if (!pattern) return;
    if (--pattern->ref_count <= 0) {
        user_data_destroy_all(pattern->user_data);
        if (pattern->surface) cairo_surface_destroy(pattern->surface);
        if (pattern->stops) free(pattern->stops);
        free(pattern);
    }
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_status(cairo_pattern_t *pattern) {
    return pattern ? pattern->status : CAIRO_STATUS_NULL_POINTER;
}

CAIRO_SHIM_EXPORT cairo_pattern_type_t cairo_pattern_get_type(cairo_pattern_t *pattern) {
    return pattern ? pattern->type : CAIRO_PATTERN_TYPE_SOLID;
}

CAIRO_SHIM_EXPORT void cairo_pattern_add_color_stop_rgba(cairo_pattern_t *pattern, double offset, double r, double g, double b, double a) {
    if (!pattern) return;
    if (pattern->num_stops >= pattern->stops_capacity) {
        int new_cap = pattern->stops_capacity == 0 ? 8 : pattern->stops_capacity * 2;
        cairo_color_stop_t *new_stops = (cairo_color_stop_t *)realloc(pattern->stops, new_cap * sizeof(cairo_color_stop_t));
        if (!new_stops) return;
        pattern->stops = new_stops;
        pattern->stops_capacity = new_cap;
    }
    pattern->stops[pattern->num_stops].offset = offset;
    pattern->stops[pattern->num_stops].r = r;
    pattern->stops[pattern->num_stops].g = g;
    pattern->stops[pattern->num_stops].b = b;
    pattern->stops[pattern->num_stops].a = a;
    pattern->num_stops++;
}

CAIRO_SHIM_EXPORT void cairo_pattern_add_color_stop_rgb(cairo_pattern_t *pattern, double offset, double r, double g, double b) {
    cairo_pattern_add_color_stop_rgba(pattern, offset, r, g, b, 1.0);
}

CAIRO_SHIM_EXPORT void cairo_pattern_set_matrix(cairo_pattern_t *pattern, const cairo_matrix_t *matrix) {
    if (pattern && matrix) pattern->matrix = *matrix;
}

CAIRO_SHIM_EXPORT void cairo_pattern_get_matrix(cairo_pattern_t *pattern, cairo_matrix_t *matrix) {
    if (pattern && matrix) *matrix = pattern->matrix;
}

CAIRO_SHIM_EXPORT void cairo_pattern_set_extend(cairo_pattern_t *pattern, cairo_extend_t extend) {
    if (pattern) pattern->extend = extend;
}

CAIRO_SHIM_EXPORT cairo_extend_t cairo_pattern_get_extend(cairo_pattern_t *pattern) {
    return pattern ? pattern->extend : CAIRO_EXTEND_NONE;
}

CAIRO_SHIM_EXPORT void cairo_pattern_set_filter(cairo_pattern_t *pattern, cairo_filter_t filter) {
    if (pattern) pattern->filter = filter;
}

CAIRO_SHIM_EXPORT cairo_filter_t cairo_pattern_get_filter(cairo_pattern_t *pattern) {
    return pattern ? pattern->filter : CAIRO_FILTER_GOOD;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_get_rgba(cairo_pattern_t *pattern, double *red, double *green, double *blue, double *alpha) {
    if (!pattern || pattern->type != CAIRO_PATTERN_TYPE_SOLID) return CAIRO_STATUS_PATTERN_TYPE_MISMATCH;
    if (red) *red = pattern->r;
    if (green) *green = pattern->g;
    if (blue) *blue = pattern->b;
    if (alpha) *alpha = pattern->a;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_get_surface(cairo_pattern_t *pattern, cairo_surface_t **surface) {
    if (!pattern || pattern->type != CAIRO_PATTERN_TYPE_SURFACE) return CAIRO_STATUS_PATTERN_TYPE_MISMATCH;
    if (surface) *surface = pattern->surface;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_get_color_stop_count(cairo_pattern_t *pattern, int *count) {
    if (!pattern || (pattern->type != CAIRO_PATTERN_TYPE_LINEAR && pattern->type != CAIRO_PATTERN_TYPE_RADIAL))
        return CAIRO_STATUS_PATTERN_TYPE_MISMATCH;
    if (count) *count = pattern->num_stops;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_get_color_stop_rgba(cairo_pattern_t *pattern, int index, double *offset, double *red, double *green, double *blue, double *alpha) {
    if (!pattern || index < 0 || index >= pattern->num_stops) return CAIRO_STATUS_INVALID_INDEX;
    if (offset) *offset = pattern->stops[index].offset;
    if (red) *red = pattern->stops[index].r;
    if (green) *green = pattern->stops[index].g;
    if (blue) *blue = pattern->stops[index].b;
    if (alpha) *alpha = pattern->stops[index].a;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_get_linear_points(cairo_pattern_t *pattern, double *x0, double *y0, double *x1, double *y1) {
    if (!pattern || pattern->type != CAIRO_PATTERN_TYPE_LINEAR) return CAIRO_STATUS_PATTERN_TYPE_MISMATCH;
    if (x0) *x0 = pattern->x0;
    if (y0) *y0 = pattern->y0;
    if (x1) *x1 = pattern->x1;
    if (y1) *y1 = pattern->y1;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_get_radial_circles(cairo_pattern_t *pattern, double *x0, double *y0, double *r0, double *x1, double *y1, double *r1) {
    if (!pattern || pattern->type != CAIRO_PATTERN_TYPE_RADIAL) return CAIRO_STATUS_PATTERN_TYPE_MISMATCH;
    if (x0) *x0 = pattern->cx0;
    if (y0) *y0 = pattern->cy0;
    if (r0) *r0 = pattern->r0;
    if (x1) *x1 = pattern->cx1;
    if (y1) *y1 = pattern->cy1;
    if (r1) *r1 = pattern->r1;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT unsigned int cairo_pattern_get_reference_count(cairo_pattern_t *pattern) {
    return pattern ? (unsigned int)pattern->ref_count : 0;
}

/* -------------------------------------------------------------------------- */
/* Context Implementation                                                     */
/* -------------------------------------------------------------------------- */

static void init_cairo_state(cairo_state_t *st) {
    matrix_init_identity(&st->matrix);
    st->source = cairo_pattern_create_rgba(0, 0, 0, 1.0);
    st->op = CAIRO_OPERATOR_OVER;
    st->line_width = 2.0;
    st->line_cap = CAIRO_LINE_CAP_BUTT;
    st->line_join = CAIRO_LINE_JOIN_MITER;
    st->fill_rule = CAIRO_FILL_RULE_WINDING;
    st->antialias = CAIRO_ANTIALIAS_DEFAULT;
    st->miter_limit = 10.0;
    st->tolerance = 0.1;
    st->current_x = 0; st->current_y = 0;
    st->has_current_point = 0;
    st->prev = NULL;
}

CAIRO_SHIM_EXPORT cairo_t *cairo_create(cairo_surface_t *target) {
    cairo_t *cr = (cairo_t *)calloc(1, sizeof(cairo_t));
    if (!cr) return NULL;
    cr->ref_count = 1;
    cr->status = CAIRO_STATUS_SUCCESS;
    cr->target = cairo_surface_reference(target);
    init_cairo_state(&cr->state);
    return cr;
}

CAIRO_SHIM_EXPORT cairo_t *cairo_reference(cairo_t *cr) {
    if (cr) cr->ref_count++;
    return cr;
}

CAIRO_SHIM_EXPORT void cairo_destroy(cairo_t *cr) {
    if (!cr) return;
    if (--cr->ref_count <= 0) {
        user_data_destroy_all(cr->user_data);
        cairo_state_t *st = cr->state.prev;
        while (st) {
            cairo_state_t *prev = st->prev;
            if (st->source) cairo_pattern_destroy(st->source);
            free(st);
            st = prev;
        }
        if (cr->state.source) cairo_pattern_destroy(cr->state.source);
        if (cr->target) cairo_surface_destroy(cr->target);
        if (cr->rects) free(cr->rects);
        free(cr);
    }
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_status(cairo_t *cr) {
    return cr ? cr->status : CAIRO_STATUS_NULL_POINTER;
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_get_target(cairo_t *cr) {
    return cr ? cr->target : NULL;
}

CAIRO_SHIM_EXPORT unsigned int cairo_get_reference_count(cairo_t *cr) {
    return cr ? (unsigned int)cr->ref_count : 0;
}

CAIRO_SHIM_EXPORT void cairo_save(cairo_t *cr) {
    if (!cr) return;
    cairo_state_t *saved = (cairo_state_t *)malloc(sizeof(cairo_state_t));
    if (!saved) return;
    *saved = cr->state;
    if (saved->source) cairo_pattern_reference(saved->source);
    saved->prev = cr->state.prev;
    cr->state.prev = saved;
}

CAIRO_SHIM_EXPORT void cairo_restore(cairo_t *cr) {
    if (!cr || !cr->state.prev) {
        if (cr) cr->status = CAIRO_STATUS_INVALID_RESTORE;
        return;
    }
    cairo_state_t *top = cr->state.prev;
    if (cr->state.source) cairo_pattern_destroy(cr->state.source);
    cr->state = *top;
    free(top);
}

CAIRO_SHIM_EXPORT void cairo_set_source_rgba(cairo_t *cr, double r, double g, double b, double a) {
    if (!cr) return;
    if (cr->state.source) cairo_pattern_destroy(cr->state.source);
    cr->state.source = cairo_pattern_create_rgba(r, g, b, a);
}

CAIRO_SHIM_EXPORT void cairo_set_source_rgb(cairo_t *cr, double r, double g, double b) {
    cairo_set_source_rgba(cr, r, g, b, 1.0);
}

CAIRO_SHIM_EXPORT void cairo_set_source(cairo_t *cr, cairo_pattern_t *source) {
    if (!cr || !source) return;
    if (cr->state.source) cairo_pattern_destroy(cr->state.source);
    cr->state.source = cairo_pattern_reference(source);
}

CAIRO_SHIM_EXPORT void cairo_set_source_surface(cairo_t *cr, cairo_surface_t *surface, double x, double y) {
    if (!cr || !surface) return;
    cairo_pattern_t *pat = cairo_pattern_create_for_surface(surface);
    cairo_matrix_t m;
    matrix_init_identity(&m);
    m.x0 = -x; m.y0 = -y;
    cairo_pattern_set_matrix(pat, &m);
    cairo_set_source(cr, pat);
    cairo_pattern_destroy(pat);
}

CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_get_source(cairo_t *cr) {
    return cr ? cr->state.source : NULL;
}

/* -------------------------------------------------------------------------- */
/* Transforms                                                                 */
/* -------------------------------------------------------------------------- */

CAIRO_SHIM_EXPORT void cairo_translate(cairo_t *cr, double tx, double ty) {
    if (!cr) return;
    cairo_matrix_t m;
    matrix_init_identity(&m);
    m.x0 = tx; m.y0 = ty;
    matrix_multiply(&cr->state.matrix, &m, &cr->state.matrix);
}

CAIRO_SHIM_EXPORT void cairo_scale(cairo_t *cr, double sx, double sy) {
    if (!cr) return;
    cairo_matrix_t m;
    matrix_init_identity(&m);
    m.xx = sx; m.yy = sy;
    matrix_multiply(&cr->state.matrix, &m, &cr->state.matrix);
}

CAIRO_SHIM_EXPORT void cairo_rotate(cairo_t *cr, double angle) {
    if (!cr) return;
    cairo_matrix_t m;
    double c = cos(angle), s = sin(angle);
    m.xx = c;  m.yx = s;
    m.xy = -s; m.yy = c;
    m.x0 = 0;  m.y0 = 0;
    matrix_multiply(&cr->state.matrix, &m, &cr->state.matrix);
}

CAIRO_SHIM_EXPORT void cairo_transform(cairo_t *cr, const cairo_matrix_t *matrix) {
    if (!cr || !matrix) return;
    matrix_multiply(&cr->state.matrix, matrix, &cr->state.matrix);
}

CAIRO_SHIM_EXPORT void cairo_set_matrix(cairo_t *cr, const cairo_matrix_t *matrix) {
    if (cr && matrix) cr->state.matrix = *matrix;
}

CAIRO_SHIM_EXPORT void cairo_get_matrix(cairo_t *cr, cairo_matrix_t *matrix) {
    if (cr && matrix) *matrix = cr->state.matrix;
}

CAIRO_SHIM_EXPORT void cairo_identity_matrix(cairo_t *cr) {
    if (cr) matrix_init_identity(&cr->state.matrix);
}

CAIRO_SHIM_EXPORT void cairo_user_to_device(cairo_t *cr, double *x, double *y) {
    if (cr && x && y) matrix_transform_point(&cr->state.matrix, x, y);
}

CAIRO_SHIM_EXPORT void cairo_user_to_device_distance(cairo_t *cr, double *dx, double *dy) {
    if (cr && dx && dy) matrix_transform_distance(&cr->state.matrix, dx, dy);
}

CAIRO_SHIM_EXPORT void cairo_device_to_user(cairo_t *cr, double *x, double *y) {
    if (!cr || !x || !y) return;
    cairo_matrix_t inv = cr->state.matrix;
    if (matrix_invert(&inv)) matrix_transform_point(&inv, x, y);
}

CAIRO_SHIM_EXPORT void cairo_device_to_user_distance(cairo_t *cr, double *dx, double *dy) {
    if (!cr || !dx || !dy) return;
    cairo_matrix_t inv = cr->state.matrix;
    if (matrix_invert(&inv)) matrix_transform_distance(&inv, dx, dy);
}

/* -------------------------------------------------------------------------- */
/* Paths                                                                      */
/* -------------------------------------------------------------------------- */

CAIRO_SHIM_EXPORT void cairo_new_path(cairo_t *cr) {
    if (!cr) return;
    cr->num_rects = 0;
    cr->state.has_current_point = 0;
}

CAIRO_SHIM_EXPORT void cairo_new_sub_path(cairo_t *cr) {
    if (cr) cr->state.has_current_point = 0;
}

CAIRO_SHIM_EXPORT void cairo_move_to(cairo_t *cr, double x, double y) {
    if (!cr) return;
    cr->state.current_x = x;
    cr->state.current_y = y;
    cr->state.has_current_point = 1;
}

CAIRO_SHIM_EXPORT void cairo_line_to(cairo_t *cr, double x, double y) {
    if (!cr) return;
    cr->state.current_x = x;
    cr->state.current_y = y;
    cr->state.has_current_point = 1;
}

CAIRO_SHIM_EXPORT void cairo_curve_to(cairo_t *cr, double x1, double y1, double x2, double y2, double x3, double y3) {
    (void)x1; (void)y1; (void)x2; (void)y2;
    cairo_line_to(cr, x3, y3);
}

CAIRO_SHIM_EXPORT void cairo_rel_move_to(cairo_t *cr, double dx, double dy) {
    if (cr && cr->state.has_current_point) cairo_move_to(cr, cr->state.current_x + dx, cr->state.current_y + dy);
}

CAIRO_SHIM_EXPORT void cairo_rel_line_to(cairo_t *cr, double dx, double dy) {
    if (cr && cr->state.has_current_point) cairo_line_to(cr, cr->state.current_x + dx, cr->state.current_y + dy);
}

CAIRO_SHIM_EXPORT void cairo_rel_curve_to(cairo_t *cr, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3) {
    if (cr && cr->state.has_current_point)
        cairo_curve_to(cr, cr->state.current_x + dx1, cr->state.current_y + dy1,
                           cr->state.current_x + dx2, cr->state.current_y + dy2,
                           cr->state.current_x + dx3, cr->state.current_y + dy3);
}

CAIRO_SHIM_EXPORT void cairo_rectangle(cairo_t *cr, double x, double y, double width, double height) {
    if (!cr) return;
    if (cr->num_rects >= cr->rects_capacity) {
        int new_cap = cr->rects_capacity == 0 ? 16 : cr->rects_capacity * 2;
        rect_cmd_t *new_rects = (rect_cmd_t *)realloc(cr->rects, new_cap * sizeof(rect_cmd_t));
        if (!new_rects) return;
        cr->rects = new_rects;
        cr->rects_capacity = new_cap;
    }
    /* Transform rectangle bounds to device coordinates */
    double x0 = x, y0 = y;
    double x1 = x + width, y1 = y + height;
    cairo_user_to_device(cr, &x0, &y0);
    cairo_user_to_device(cr, &x1, &y1);
    cr->rects[cr->num_rects].x = fmin(x0, x1);
    cr->rects[cr->num_rects].y = fmin(y0, y1);
    cr->rects[cr->num_rects].w = fabs(x1 - x0);
    cr->rects[cr->num_rects].h = fabs(y1 - y0);
    cr->num_rects++;
    cairo_move_to(cr, x, y);
}

CAIRO_SHIM_EXPORT void cairo_close_path(cairo_t *cr) {
    (void)cr;
}

CAIRO_SHIM_EXPORT cairo_bool_t cairo_has_current_point(cairo_t *cr) {
    return cr ? cr->state.has_current_point : 0;
}

CAIRO_SHIM_EXPORT void cairo_get_current_point(cairo_t *cr, double *x, double *y) {
    if (cr && x && y) {
        *x = cr->state.current_x;
        *y = cr->state.current_y;
    }
}

/* -------------------------------------------------------------------------- */
/* Pixel Rasterization & Compositing                                          */
/* -------------------------------------------------------------------------- */

static inline void blend_pixel_argb32(unsigned char *dst, uint32_t src_color, cairo_operator_t op) {
    uint8_t sa = (src_color >> 24) & 0xFF;
    uint8_t sr = (src_color >> 16) & 0xFF;
    uint8_t sg = (src_color >> 8) & 0xFF;
    uint8_t sb = src_color & 0xFF;

    if (op == CAIRO_OPERATOR_CLEAR) {
        dst[0] = dst[1] = dst[2] = dst[3] = 0;
        return;
    }
    if (op == CAIRO_OPERATOR_SOURCE) {
        dst[0] = sb; dst[1] = sg; dst[2] = sr; dst[3] = sa;
        return;
    }

    /* CAIRO_OPERATOR_OVER (premultiplied Little-Endian BGRA) */
    uint8_t da = dst[3];
    uint8_t dr = dst[2];
    uint8_t dg = dst[1];
    uint8_t db = dst[0];

    uint32_t inv_sa = 255 - sa;
    dst[0] = (uint8_t)(sb + (db * inv_sa + 127) / 255);
    dst[1] = (uint8_t)(sg + (dg * inv_sa + 127) / 255);
    dst[2] = (uint8_t)(sr + (dr * inv_sa + 127) / 255);
    dst[3] = (uint8_t)(sa + (da * inv_sa + 127) / 255);
}

static uint32_t get_solid_source_color(const cairo_pattern_t *pat) {
    if (!pat) return 0xFF000000;
    uint8_t a = (uint8_t)(pat->a * 255.0 + 0.5);
    uint8_t r = (uint8_t)(pat->r * pat->a * 255.0 + 0.5);
    uint8_t g = (uint8_t)(pat->g * pat->a * 255.0 + 0.5);
    uint8_t b = (uint8_t)(pat->b * pat->a * 255.0 + 0.5);
    return ((uint32_t)a << 24) | ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;
}

static void fill_rect_surface(cairo_surface_t *surf, int rx, int ry, int rw, int rh, uint32_t color, cairo_operator_t op) {
    if (!surf || !surf->data) return;
    int x0 = fmax(0, rx);
    int y0 = fmax(0, ry);
    int x1 = fmin(surf->width, rx + rw);
    int y1 = fmin(surf->height, ry + rh);
    if (x0 >= x1 || y0 >= y1) return;

    if (surf->format == CAIRO_FORMAT_ARGB32 || surf->format == CAIRO_FORMAT_RGB24) {
        for (int y = y0; y < y1; y++) {
            unsigned char *row = surf->data + y * surf->stride;
            for (int x = x0; x < x1; x++) {
                blend_pixel_argb32(&row[x * 4], color, op);
            }
        }
    } else if (surf->format == CAIRO_FORMAT_A8) {
        uint8_t sa = (color >> 24) & 0xFF;
        for (int y = y0; y < y1; y++) {
            unsigned char *row = surf->data + y * surf->stride;
            for (int x = x0; x < x1; x++) {
                if (op == CAIRO_OPERATOR_CLEAR) row[x] = 0;
                else if (op == CAIRO_OPERATOR_SOURCE) row[x] = sa;
                else row[x] = (uint8_t)(sa + (row[x] * (255 - sa) + 127) / 255);
            }
        }
    }
}

CAIRO_SHIM_EXPORT void cairo_paint(cairo_t *cr) {
    if (!cr || !cr->target) return;
    uint32_t color = get_solid_source_color(cr->state.source);
    fill_rect_surface(cr->target, 0, 0, cr->target->width, cr->target->height, color, cr->state.op);
}

CAIRO_SHIM_EXPORT void cairo_paint_with_alpha(cairo_t *cr, double alpha) {
    if (!cr || !cr->target) return;
    cairo_pattern_t *src = cr->state.source;
    if (src && src->type == CAIRO_PATTERN_TYPE_SOLID) {
        cairo_pattern_t tmp = *src;
        tmp.a *= fmax(0.0, fmin(1.0, alpha));
        uint32_t color = get_solid_source_color(&tmp);
        fill_rect_surface(cr->target, 0, 0, cr->target->width, cr->target->height, color, cr->state.op);
    } else {
        cairo_paint(cr);
    }
}

CAIRO_SHIM_EXPORT void cairo_fill_preserve(cairo_t *cr) {
    if (!cr || !cr->target) return;
    uint32_t color = get_solid_source_color(cr->state.source);
    for (int i = 0; i < cr->num_rects; i++) {
        fill_rect_surface(cr->target,
                          (int)floor(cr->rects[i].x),
                          (int)floor(cr->rects[i].y),
                          (int)ceil(cr->rects[i].w),
                          (int)ceil(cr->rects[i].h),
                          color, cr->state.op);
    }
}

CAIRO_SHIM_EXPORT void cairo_fill(cairo_t *cr) {
    cairo_fill_preserve(cr);
    cairo_new_path(cr);
}

CAIRO_SHIM_EXPORT void cairo_stroke_preserve(cairo_t *cr) {
    if (!cr || !cr->target) return;
    uint32_t color = get_solid_source_color(cr->state.source);
    int half = (int)ceil(cr->state.line_width / 2.0);
    for (int i = 0; i < cr->num_rects; i++) {
        int x = (int)floor(cr->rects[i].x);
        int y = (int)floor(cr->rects[i].y);
        int w = (int)ceil(cr->rects[i].w);
        int h = (int)ceil(cr->rects[i].h);
        /* Top & Bottom */
        fill_rect_surface(cr->target, x, y - half, w, half * 2, color, cr->state.op);
        fill_rect_surface(cr->target, x, y + h - half, w, half * 2, color, cr->state.op);
        /* Left & Right */
        fill_rect_surface(cr->target, x - half, y, half * 2, h, color, cr->state.op);
        fill_rect_surface(cr->target, x + w - half, y, half * 2, h, color, cr->state.op);
    }
}

CAIRO_SHIM_EXPORT void cairo_stroke(cairo_t *cr) {
    cairo_stroke_preserve(cr);
    cairo_new_path(cr);
}

CAIRO_SHIM_EXPORT void cairo_clip(cairo_t *cr) { cairo_new_path(cr); }
CAIRO_SHIM_EXPORT void cairo_clip_preserve(cairo_t *cr) { (void)cr; }
CAIRO_SHIM_EXPORT void cairo_reset_clip(cairo_t *cr) { (void)cr; }

CAIRO_SHIM_EXPORT void cairo_mask(cairo_t *cr, cairo_pattern_t *pattern) {
    (void)pattern;
    cairo_paint(cr);
}

CAIRO_SHIM_EXPORT void cairo_mask_surface(cairo_t *cr, cairo_surface_t *surface, double surface_x, double surface_y) {
    (void)surface; (void)surface_x; (void)surface_y;
    cairo_paint(cr);
}

/* -------------------------------------------------------------------------- */
/* State Getters & Setters                                                    */
/* -------------------------------------------------------------------------- */

CAIRO_SHIM_EXPORT void cairo_set_operator(cairo_t *cr, cairo_operator_t op) { if (cr) cr->state.op = op; }
CAIRO_SHIM_EXPORT cairo_operator_t cairo_get_operator(cairo_t *cr) { return cr ? cr->state.op : CAIRO_OPERATOR_OVER; }

CAIRO_SHIM_EXPORT void cairo_set_line_width(cairo_t *cr, double width) { if (cr) cr->state.line_width = width; }
CAIRO_SHIM_EXPORT double cairo_get_line_width(cairo_t *cr) { return cr ? cr->state.line_width : 2.0; }

CAIRO_SHIM_EXPORT void cairo_set_line_cap(cairo_t *cr, cairo_line_cap_t line_cap) { if (cr) cr->state.line_cap = line_cap; }
CAIRO_SHIM_EXPORT cairo_line_cap_t cairo_get_line_cap(cairo_t *cr) { return cr ? cr->state.line_cap : CAIRO_LINE_CAP_BUTT; }

CAIRO_SHIM_EXPORT void cairo_set_line_join(cairo_t *cr, cairo_line_join_t line_join) { if (cr) cr->state.line_join = line_join; }
CAIRO_SHIM_EXPORT cairo_line_join_t cairo_get_line_join(cairo_t *cr) { return cr ? cr->state.line_join : CAIRO_LINE_JOIN_MITER; }

CAIRO_SHIM_EXPORT void cairo_set_fill_rule(cairo_t *cr, cairo_fill_rule_t fill_rule) { if (cr) cr->state.fill_rule = fill_rule; }
CAIRO_SHIM_EXPORT cairo_fill_rule_t cairo_get_fill_rule(cairo_t *cr) { return cr ? cr->state.fill_rule : CAIRO_FILL_RULE_WINDING; }

CAIRO_SHIM_EXPORT void cairo_set_antialias(cairo_t *cr, cairo_antialias_t antialias) { if (cr) cr->state.antialias = antialias; }
CAIRO_SHIM_EXPORT cairo_antialias_t cairo_get_antialias(cairo_t *cr) { return cr ? cr->state.antialias : CAIRO_ANTIALIAS_DEFAULT; }

CAIRO_SHIM_EXPORT void cairo_set_miter_limit(cairo_t *cr, double limit) { if (cr) cr->state.miter_limit = limit; }
CAIRO_SHIM_EXPORT double cairo_get_miter_limit(cairo_t *cr) { return cr ? cr->state.miter_limit : 10.0; }

CAIRO_SHIM_EXPORT void cairo_set_tolerance(cairo_t *cr, double tolerance) { if (cr) cr->state.tolerance = tolerance; }
CAIRO_SHIM_EXPORT double cairo_get_tolerance(cairo_t *cr) { return cr ? cr->state.tolerance : 0.1; }

CAIRO_SHIM_EXPORT void cairo_set_dash(cairo_t *cr, const double *dashes, int num_dashes, double offset) {
    (void)cr; (void)dashes; (void)num_dashes; (void)offset;
}

CAIRO_SHIM_EXPORT int cairo_get_dash_count(cairo_t *cr) { (void)cr; return 0; }
CAIRO_SHIM_EXPORT void cairo_get_dash(cairo_t *cr, double *dashes, double *offset) {
    (void)cr; (void)dashes; if (offset) *offset = 0;
}

/* -------------------------------------------------------------------------- */
/* Fonts & Text Stubs                                                         */
/* -------------------------------------------------------------------------- */

CAIRO_SHIM_EXPORT void cairo_select_font_face(cairo_t *cr, const char *family, cairo_font_slant_t slant, cairo_font_weight_t weight) {
    (void)cr; (void)family; (void)slant; (void)weight;
}

CAIRO_SHIM_EXPORT void cairo_set_font_size(cairo_t *cr, double size) { (void)cr; (void)size; }

CAIRO_SHIM_EXPORT void cairo_set_font_matrix(cairo_t *cr, const cairo_matrix_t *matrix) { (void)cr; (void)matrix; }
CAIRO_SHIM_EXPORT void cairo_get_font_matrix(cairo_t *cr, cairo_matrix_t *matrix) { if (matrix) matrix_init_identity(matrix); (void)cr; }

CAIRO_SHIM_EXPORT void cairo_set_font_options(cairo_t *cr, const cairo_font_options_t *options) { (void)cr; (void)options; }
CAIRO_SHIM_EXPORT void cairo_get_font_options(cairo_t *cr, cairo_font_options_t *options) { (void)cr; (void)options; }

CAIRO_SHIM_EXPORT void cairo_set_font_face(cairo_t *cr, cairo_font_face_t *font_face) { (void)cr; (void)font_face; }
CAIRO_SHIM_EXPORT cairo_font_face_t *cairo_get_font_face(cairo_t *cr) { (void)cr; return NULL; }

CAIRO_SHIM_EXPORT void cairo_font_extents(cairo_t *cr, cairo_font_extents_t *extents) {
    (void)cr;
    if (extents) {
        extents->ascent = 12.0; extents->descent = 3.0; extents->height = 15.0;
        extents->max_x_advance = 15.0; extents->max_y_advance = 0.0;
    }
}

CAIRO_SHIM_EXPORT void cairo_text_extents(cairo_t *cr, const char *utf8, cairo_text_extents_t *extents) {
    (void)cr;
    if (extents) {
        double len = utf8 ? (double)strlen(utf8) : 0.0;
        extents->x_bearing = 0.0; extents->y_bearing = -10.0;
        extents->width = len * 8.0; extents->height = 12.0;
        extents->x_advance = len * 8.0; extents->y_advance = 0.0;
    }
}

CAIRO_SHIM_EXPORT void cairo_glyph_extents(cairo_t *cr, const cairo_glyph_t *glyphs, int num_glyphs, cairo_text_extents_t *extents) {
    (void)cr; (void)glyphs;
    if (extents) {
        extents->x_bearing = 0.0; extents->y_bearing = -10.0;
        extents->width = num_glyphs * 8.0; extents->height = 12.0;
        extents->x_advance = num_glyphs * 8.0; extents->y_advance = 0.0;
    }
}

CAIRO_SHIM_EXPORT void cairo_show_text(cairo_t *cr, const char *utf8) { (void)cr; (void)utf8; }
CAIRO_SHIM_EXPORT void cairo_show_glyphs(cairo_t *cr, const cairo_glyph_t *glyphs, int num_glyphs) { (void)cr; (void)glyphs; (void)num_glyphs; }
CAIRO_SHIM_EXPORT void cairo_text_path(cairo_t *cr, const char *utf8) { (void)cr; (void)utf8; }
CAIRO_SHIM_EXPORT void cairo_glyph_path(cairo_t *cr, const cairo_glyph_t *glyphs, int num_glyphs) { (void)cr; (void)glyphs; (void)num_glyphs; }

CAIRO_SHIM_EXPORT cairo_font_face_t *cairo_font_face_reference(cairo_font_face_t *font_face) {
    if (font_face) font_face->ref_count++;
    return font_face;
}

CAIRO_SHIM_EXPORT void cairo_font_face_destroy(cairo_font_face_t *font_face) {
    if (!font_face) return;
    if (--font_face->ref_count <= 0) {
        user_data_destroy_all(font_face->user_data);
        if (font_face->family) free(font_face->family);
        free(font_face);
    }
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_font_face_status(cairo_font_face_t *font_face) {
    return font_face ? font_face->status : CAIRO_STATUS_NULL_POINTER;
}

CAIRO_SHIM_EXPORT cairo_font_type_t cairo_font_face_get_type(cairo_font_face_t *font_face) {
    return font_face ? font_face->type : CAIRO_FONT_TYPE_TOY;
}

CAIRO_SHIM_EXPORT unsigned int cairo_font_face_get_reference_count(cairo_font_face_t *font_face) {
    return font_face ? (unsigned int)font_face->ref_count : 0;
}

CAIRO_SHIM_EXPORT cairo_font_face_t *cairo_toy_font_face_create(const char *family, cairo_font_slant_t slant, cairo_font_weight_t weight) {
    cairo_font_face_t *f = (cairo_font_face_t *)calloc(1, sizeof(cairo_font_face_t));
    if (!f) return NULL;
    f->ref_count = 1;
    f->status = CAIRO_STATUS_SUCCESS;
    f->type = CAIRO_FONT_TYPE_TOY;
    if (family) f->family = strdup(family);
    f->slant = slant;
    f->weight = weight;
    return f;
}

CAIRO_SHIM_EXPORT const char *cairo_toy_font_face_get_family(cairo_font_face_t *font_face) {
    return font_face && font_face->family ? font_face->family : "sans-serif";
}

CAIRO_SHIM_EXPORT cairo_font_slant_t cairo_toy_font_face_get_slant(cairo_font_face_t *font_face) {
    return font_face ? font_face->slant : CAIRO_FONT_SLANT_NORMAL;
}

CAIRO_SHIM_EXPORT cairo_font_weight_t cairo_toy_font_face_get_weight(cairo_font_face_t *font_face) {
    return font_face ? font_face->weight : CAIRO_FONT_WEIGHT_NORMAL;
}

CAIRO_SHIM_EXPORT cairo_font_face_t *cairo_ft_font_face_create_for_ft_face(void *ft_face, int load_flags) {
    (void)ft_face; (void)load_flags;
    return cairo_toy_font_face_create("FreeType", CAIRO_FONT_SLANT_NORMAL, CAIRO_FONT_WEIGHT_NORMAL);
}

CAIRO_SHIM_EXPORT cairo_font_options_t *cairo_font_options_create(void) {
    cairo_font_options_t *o = (cairo_font_options_t *)calloc(1, sizeof(cairo_font_options_t));
    return o;
}

CAIRO_SHIM_EXPORT cairo_font_options_t *cairo_font_options_copy(const cairo_font_options_t *original) {
    if (!original) return cairo_font_options_create();
    cairo_font_options_t *o = (cairo_font_options_t *)malloc(sizeof(cairo_font_options_t));
    if (o) *o = *original;
    return o;
}

CAIRO_SHIM_EXPORT void cairo_font_options_destroy(cairo_font_options_t *options) {
    if (options) free(options);
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_font_options_status(cairo_font_options_t *options) {
    return options ? CAIRO_STATUS_SUCCESS : CAIRO_STATUS_NULL_POINTER;
}

CAIRO_SHIM_EXPORT void cairo_font_options_set_antialias(cairo_font_options_t *options, cairo_antialias_t antialias) {
    if (options) options->antialias = antialias;
}

CAIRO_SHIM_EXPORT cairo_antialias_t cairo_font_options_get_antialias(const cairo_font_options_t *options) {
    return options ? options->antialias : CAIRO_ANTIALIAS_DEFAULT;
}

CAIRO_SHIM_EXPORT void cairo_font_options_set_subpixel_order(cairo_font_options_t *options, cairo_subpixel_order_t subpixel_order) {
    if (options) options->subpixel_order = subpixel_order;
}

CAIRO_SHIM_EXPORT cairo_subpixel_order_t cairo_font_options_get_subpixel_order(const cairo_font_options_t *options) {
    return options ? options->subpixel_order : CAIRO_SUBPIXEL_ORDER_DEFAULT;
}

CAIRO_SHIM_EXPORT void cairo_font_options_set_hint_style(cairo_font_options_t *options, cairo_hint_style_t hint_style) {
    if (options) options->hint_style = hint_style;
}

CAIRO_SHIM_EXPORT cairo_hint_style_t cairo_font_options_get_hint_style(const cairo_font_options_t *options) {
    return options ? options->hint_style : CAIRO_HINT_STYLE_DEFAULT;
}

CAIRO_SHIM_EXPORT void cairo_font_options_set_hint_metrics(cairo_font_options_t *options, cairo_hint_metrics_t hint_metrics) {
    if (options) options->hint_metrics = hint_metrics;
}

CAIRO_SHIM_EXPORT cairo_hint_metrics_t cairo_font_options_get_hint_metrics(const cairo_font_options_t *options) {
    return options ? options->hint_metrics : CAIRO_HINT_METRICS_DEFAULT;
}

/* -------------------------------------------------------------------------- */
/* PNG / Output Export                                                        */
/* -------------------------------------------------------------------------- */

CAIRO_SHIM_EXPORT cairo_status_t cairo_surface_write_to_png(cairo_surface_t *surface, const char *filename) {
    if (!surface || !surface->data || !filename) return CAIRO_STATUS_NULL_POINTER;
    FILE *f = fopen(filename, "wb");
    if (!f) return CAIRO_STATUS_WRITE_ERROR;

    /* Write minimal uncompressed PPM format */
    fprintf(f, "P6\n%d %d\n255\n", surface->width, surface->height);
    for (int y = 0; y < surface->height; y++) {
        unsigned char *row = surface->data + y * surface->stride;
        for (int x = 0; x < surface->width; x++) {
            fputc(row[x * 4 + 2], f); /* R */
            fputc(row[x * 4 + 1], f); /* G */
            fputc(row[x * 4 + 0], f); /* B */
        }
    }
    fclose(f);
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_surface_write_to_png_stream(cairo_surface_t *surface, cairo_write_func_t write_func, void *closure) {
    (void)surface; (void)write_func; (void)closure;
    return CAIRO_STATUS_SUCCESS;
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_image_surface_create_from_png(const char *filename) {
    (void)filename;
    return cairo_image_surface_create(CAIRO_FORMAT_ARGB32, 1, 1);
}

CAIRO_SHIM_EXPORT cairo_surface_t *cairo_image_surface_create_from_png_stream(cairo_read_func_t read_func, void *closure) {
    (void)read_func; (void)closure;
    return cairo_image_surface_create(CAIRO_FORMAT_ARGB32, 1, 1);
}

/* -------------------------------------------------------------------------- */
/* Additional Stubs for Complete Native ABI Linkage                           */
/* -------------------------------------------------------------------------- */

CAIRO_SHIM_EXPORT void cairo_copy_page(cairo_t *cr) { (void)cr; }
CAIRO_SHIM_EXPORT void cairo_show_page(cairo_t *cr) { (void)cr; }
CAIRO_SHIM_EXPORT void cairo_surface_copy_page(cairo_surface_t *surface) { (void)surface; }
CAIRO_SHIM_EXPORT void cairo_surface_show_page(cairo_surface_t *surface) { (void)surface; }

CAIRO_SHIM_EXPORT void cairo_fill_extents(cairo_t *cr, double *x1, double *y1, double *x2, double *y2) {
    if (cr && cr->target) {
        if (x1) *x1 = 0;
        if (y1) *y1 = 0;
        if (x2) *x2 = cr->target->width;
        if (y2) *y2 = cr->target->height;
    }
}

CAIRO_SHIM_EXPORT void cairo_stroke_extents(cairo_t *cr, double *x1, double *y1, double *x2, double *y2) {
    cairo_fill_extents(cr, x1, y1, x2, y2);
}

CAIRO_SHIM_EXPORT cairo_bool_t cairo_in_fill(cairo_t *cr, double x, double y) {
    (void)cr; (void)x; (void)y; return 1;
}

CAIRO_SHIM_EXPORT cairo_bool_t cairo_in_stroke(cairo_t *cr, double x, double y) {
    (void)cr; (void)x; (void)y; return 1;
}

CAIRO_SHIM_EXPORT cairo_bool_t cairo_in_clip(cairo_t *cr, double x, double y) {
    (void)cr; (void)x; (void)y; return 1;
}

CAIRO_SHIM_EXPORT cairo_path_t *cairo_copy_path(cairo_t *cr) {
    (void)cr;
    cairo_path_t *p = (cairo_path_t *)calloc(1, sizeof(cairo_path_t));
    if (p) p->status = CAIRO_STATUS_SUCCESS;
    return p;
}

CAIRO_SHIM_EXPORT cairo_path_t *cairo_copy_path_flat(cairo_t *cr) {
    return cairo_copy_path(cr);
}

CAIRO_SHIM_EXPORT void cairo_append_path(cairo_t *cr, const cairo_path_t *path) {
    (void)cr; (void)path;
}

CAIRO_SHIM_EXPORT void cairo_path_destroy(cairo_path_t *path) {
    if (path) {
        if (path->data) free(path->data);
        free(path);
    }
}

CAIRO_SHIM_EXPORT void cairo_push_group(cairo_t *cr) { (void)cr; }
CAIRO_SHIM_EXPORT void cairo_push_group_with_content(cairo_t *cr, cairo_content_t content) { (void)cr; (void)content; }
CAIRO_SHIM_EXPORT cairo_pattern_t *cairo_pop_group(cairo_t *cr) { return cairo_get_source(cr); }
CAIRO_SHIM_EXPORT void cairo_pop_group_to_source(cairo_t *cr) { (void)cr; }
CAIRO_SHIM_EXPORT cairo_surface_t *cairo_get_group_target(cairo_t *cr) { return cairo_get_target(cr); }

CAIRO_SHIM_EXPORT int cairo_version(void) { return CAIRO_VERSION_MAJOR * 10000 + CAIRO_VERSION_MINOR * 100 + CAIRO_VERSION_MICRO; }
CAIRO_SHIM_EXPORT const char *cairo_version_string(void) { return CAIRO_VERSION_STRING; }
CAIRO_SHIM_EXPORT const char *cairo_status_to_string(cairo_status_t status) {
    switch (status) {
        case CAIRO_STATUS_SUCCESS: return "success";
        case CAIRO_STATUS_NO_MEMORY: return "out of memory";
        case CAIRO_STATUS_INVALID_RESTORE: return "invalid restore";
        default: return "cairo error";
    }
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_surface_set_user_data(cairo_surface_t *surface, const cairo_user_data_key_t *key, void *user_data, cairo_destroy_func_t destroy) {
    if (!surface) return CAIRO_STATUS_NULL_POINTER;
    return user_data_set(&surface->user_data, key, user_data, destroy);
}

CAIRO_SHIM_EXPORT void *cairo_surface_get_user_data(cairo_surface_t *surface, const cairo_user_data_key_t *key) {
    if (!surface) return NULL;
    return user_data_get(surface->user_data, key);
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_set_user_data(cairo_t *cr, const cairo_user_data_key_t *key, void *user_data, cairo_destroy_func_t destroy) {
    if (!cr) return CAIRO_STATUS_NULL_POINTER;
    return user_data_set(&cr->user_data, key, user_data, destroy);
}

CAIRO_SHIM_EXPORT void *cairo_get_user_data(cairo_t *cr, const cairo_user_data_key_t *key) {
    if (!cr) return NULL;
    return user_data_get(cr->user_data, key);
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_pattern_set_user_data(cairo_pattern_t *pattern, const cairo_user_data_key_t *key, void *user_data, cairo_destroy_func_t destroy) {
    if (!pattern) return CAIRO_STATUS_NULL_POINTER;
    return user_data_set(&pattern->user_data, key, user_data, destroy);
}

CAIRO_SHIM_EXPORT void *cairo_pattern_get_user_data(cairo_pattern_t *pattern, const cairo_user_data_key_t *key) {
    if (!pattern) return NULL;
    return user_data_get(pattern->user_data, key);
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_font_face_set_user_data(cairo_font_face_t *font_face, const cairo_user_data_key_t *key, void *user_data, cairo_destroy_func_t destroy) {
    if (!font_face) return CAIRO_STATUS_NULL_POINTER;
    return user_data_set(&font_face->user_data, key, user_data, destroy);
}

CAIRO_SHIM_EXPORT void *cairo_font_face_get_user_data(cairo_font_face_t *font_face, const cairo_user_data_key_t *key) {
    if (!font_face) return NULL;
    return user_data_get(font_face->user_data, key);
}

CAIRO_SHIM_EXPORT cairo_status_t cairo_scaled_font_set_user_data(cairo_scaled_font_t *scaled_font, const cairo_user_data_key_t *key, void *user_data, cairo_destroy_func_t destroy) {
    if (!scaled_font) return CAIRO_STATUS_NULL_POINTER;
    return user_data_set(&scaled_font->user_data, key, user_data, destroy);
}

CAIRO_SHIM_EXPORT void *cairo_scaled_font_get_user_data(cairo_scaled_font_t *scaled_font, const cairo_user_data_key_t *key) {
    if (!scaled_font) return NULL;
    return user_data_get(scaled_font->user_data, key);
}

CAIRO_SHIM_EXPORT void cairo_scaled_font_destroy(cairo_scaled_font_t *scaled_font) {
    if (!scaled_font) return;
    if (--scaled_font->ref_count <= 0) {
        user_data_destroy_all(scaled_font->user_data);
        if (scaled_font->font_face) cairo_font_face_destroy(scaled_font->font_face);
        free(scaled_font);
    }
}
