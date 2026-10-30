# Cross-builds with mingw-w64 (Linux) or builds natively with MinGW on Windows.
ifeq ($(origin CC),default)
CC = x86_64-w64-mingw32-gcc
endif
HOSTCC ?= cc
CFLAGS = -O2 -Wall -Wextra -Wno-unused-parameter -Wno-cast-function-type -Isrc
LDFLAGS = -shared -static-libgcc -s
LIBS = -luser32 -lgdi32
SRC = src/testmenu.c src/items.c

build/dksign_testmenu.dll: $(SRC) src/items.h
	mkdir -p build
	$(CC) $(CFLAGS) -o $@ $(SRC) $(LDFLAGS) $(LIBS)

test:
	mkdir -p build
	$(HOSTCC) -Wall -Wextra -Isrc tests/test_items.c src/items.c -o build/test_items
	./build/test_items

clean:
	rm -rf build
