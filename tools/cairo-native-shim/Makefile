CC ?= gcc
CFLAGS ?= -O2 -Wall -Wextra -fPIC -I.
LDFLAGS ?= -shared -Wl,-soname,libcairo.so.2 -lm

TARGET = libcairo.so.2
SYMLINKS = libcairo.so libcairo.so.2.11804.4

.PHONY: all clean test

all: $(TARGET) $(SYMLINKS)

$(TARGET): cairo-shim.o
	$(CC) $(CFLAGS) -o $@ $^ $(LDFLAGS)

libcairo.so: $(TARGET)
	ln -sf $(TARGET) libcairo.so

libcairo.so.2.11804.4: $(TARGET)
	ln -sf $(TARGET) libcairo.so.2.11804.4

cairo-shim.o: cairo-shim.c cairo.h
	$(CC) $(CFLAGS) -c cairo-shim.c -o $@

test-shim: test-shim.c libcairo.so
	$(CC) $(CFLAGS) test-shim.c -L. -lcairo -Wl,-rpath,. -lm -o $@

test: test-shim
	./test-shim

clean:
	rm -f *.o $(TARGET) $(SYMLINKS) test-shim output.ppm
