#include "cairo.h"
#include <stdio.h>
#include <stdlib.h>
#include <assert.h>

static int destroyed1 = 0;
static int destroyed2 = 0;

static void destroy_cb1(void *data) {
    assert(data == (void *)&destroyed1);
    destroyed1 = 1;
}

static void destroy_cb2(void *data) {
    assert(data == (void *)&destroyed2);
    destroyed2 = 1;
}

int main(void) {
    printf("Testing Cairo Native Shim ABI...\n");
    printf("Cairo version: %d, string: %s\n", cairo_version(), cairo_version_string());

    int width = 64, height = 64;
    cairo_surface_t *surf = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, width, height);
    assert(surf != NULL);
    assert(cairo_surface_status(surf) == CAIRO_STATUS_SUCCESS);
    assert(cairo_image_surface_get_width(surf) == width);
    assert(cairo_image_surface_get_height(surf) == height);

    cairo_t *cr = cairo_create(surf);
    assert(cr != NULL);
    assert(cairo_status(cr) == CAIRO_STATUS_SUCCESS);

    /* 1. Paint background with Red (1, 0, 0, 1) */
    cairo_set_source_rgba(cr, 1.0, 0.0, 0.0, 1.0);
    cairo_paint(cr);

    unsigned char *data = cairo_image_surface_get_data(surf);
    assert(data != NULL);

    /* In Little-Endian BGRA: byte 0=B(0), 1=G(0), 2=R(255), 3=A(255) */
    assert(data[0] == 0);
    assert(data[1] == 0);
    assert(data[2] == 255);
    assert(data[3] == 255);
    printf("PASS: Red paint test passed.\n");

    /* 2. Draw a Blue rectangle in the middle (16, 16, 32, 32) */
    cairo_set_source_rgba(cr, 0.0, 0.0, 1.0, 1.0);
    cairo_rectangle(cr, 16, 16, 32, 32);
    cairo_fill(cr);

    /* Pixel at (32, 32) must be Blue: byte 0=B(255), 1=G(0), 2=R(0), 3=A(255) */
    int mid_idx = 32 * cairo_image_surface_get_stride(surf) + 32 * 4;
    assert(data[mid_idx + 0] == 255);
    assert(data[mid_idx + 1] == 0);
    assert(data[mid_idx + 2] == 0);
    assert(data[mid_idx + 3] == 255);

    /* Corner pixel at (0, 0) must remain Red */
    assert(data[0] == 0);
    assert(data[1] == 0);
    assert(data[2] == 255);
    assert(data[3] == 255);
    printf("PASS: Rectangle fill test passed.\n");

    /* 3. Save/Restore & Transform */
    cairo_save(cr);
    cairo_translate(cr, 10.0, 20.0);
    cairo_scale(cr, 2.0, 2.0);
    double x = 5.0, y = 5.0;
    cairo_user_to_device(cr, &x, &y);
    assert(x == 20.0);
    assert(y == 30.0);
    cairo_restore(cr);

    /* After restore, transform is reset */
    x = 5.0; y = 5.0;
    cairo_user_to_device(cr, &x, &y);
    assert(x == 5.0);
    assert(y == 5.0);
    printf("PASS: Save/Restore and UserToDevice passed.\n");

    /* 4. Write image to PPM / PNG */
    cairo_status_t write_status = cairo_surface_write_to_png(surf, "output.ppm");
    assert(write_status == CAIRO_STATUS_SUCCESS);
    printf("PASS: Surface write passed.\n");

    /* 5. User data test */
    static cairo_user_data_key_t key1;
    static cairo_user_data_key_t key2;

    assert(cairo_surface_set_user_data(surf, &key1, &destroyed1, destroy_cb1) == CAIRO_STATUS_SUCCESS);
    assert(cairo_surface_get_user_data(surf, &key1) == &destroyed1);
    assert(cairo_set_user_data(cr, &key2, &destroyed2, destroy_cb2) == CAIRO_STATUS_SUCCESS);
    assert(cairo_get_user_data(cr, &key2) == &destroyed2);

    cairo_destroy(cr);
    assert(destroyed2 == 1);
    cairo_surface_destroy(surf);
    assert(destroyed1 == 1);
    printf("PASS: User data and destroy callbacks passed.\n");

    printf("ALL TESTS PASSED SUCCESSFULLY!\n");
    return 0;
}
