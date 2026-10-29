# Cross-builds with mingw-w64 (Linux) or builds natively with MinGW on Windows.
ifeq ($(origin CC),default)
CC = x86_64-w64-mingw32-gcc
endif
HOSTCC ?= cc
MH = third_party/minhook
CFLAGS = -O2 -Wall -Wextra -Wno-unused-parameter -Wno-cast-function-type -I$(MH)/include -Isrc
LDFLAGS = -shared -static-libgcc -Wl,--enable-stdcall-fixup -s
LIBS = -ladvapi32 -luser32
SRC = src/dllmain.c src/redirect.c $(MH)/src/buffer.c $(MH)/src/hook.c $(MH)/src/trampoline.c $(MH)/src/hde/hde64.c

build/dksign_saveredirect.dll: $(SRC) src/redirect.h src/dinput8.def
	mkdir -p build
	$(CC) $(CFLAGS) -o $@ $(SRC) src/dinput8.def $(LDFLAGS) $(LIBS)

test:
	mkdir -p build
	$(HOSTCC) -Wall -Wextra -Isrc tests/test_redirect.c src/redirect.c -o build/test_redirect
	./build/test_redirect

clean:
	rm -rf build
