# Cross-builds with mingw-w64 (Linux) or builds natively with MinGW on Windows.
ifeq ($(origin CC),default)
CC = x86_64-w64-mingw32-gcc
endif
ifeq ($(origin CXX),default)
CXX = x86_64-w64-mingw32-g++
endif
HOSTCC ?= cc
MH = ../SaveRedirect/third_party/minhook
IMGUI = third_party/imgui
INCLUDES = -Isrc -I$(MH)/include -I$(IMGUI)
CFLAGS = -O2 -Wall -Wextra -Wno-unused-parameter -Wno-cast-function-type $(INCLUDES)
CXXFLAGS = -O2 -std=c++17 -Wall -Wno-unused-parameter -Wno-cast-function-type $(INCLUDES)
LDFLAGS = -shared -static -s
LIBS = -ld3d12 -ldxgi -ld3dcompiler -ldxguid -luser32 -lgdi32 -ldwmapi
C_SRC = src/game.c src/items.c $(MH)/src/buffer.c $(MH)/src/hook.c $(MH)/src/trampoline.c $(MH)/src/hde/hde64.c
CXX_SRC = src/overlay.cpp $(IMGUI)/imgui.cpp $(IMGUI)/imgui_draw.cpp $(IMGUI)/imgui_tables.cpp $(IMGUI)/imgui_widgets.cpp \
          $(IMGUI)/backends/imgui_impl_dx12.cpp $(IMGUI)/backends/imgui_impl_win32.cpp
OBJ = $(patsubst %.c,build/obj/%.o,$(notdir $(C_SRC))) $(patsubst %.cpp,build/obj/%.o,$(notdir $(CXX_SRC)))
VPATH = src $(MH)/src $(MH)/src/hde $(IMGUI) $(IMGUI)/backends

build/dksign_testmenu.dll: $(OBJ)
	$(CXX) -o $@ $(OBJ) $(LDFLAGS) $(LIBS)

build/obj/%.o: %.c src/items.h src/game.h | build/obj
	$(CC) $(CFLAGS) -c -o $@ $<

build/obj/%.o: %.cpp src/items.h src/game.h | build/obj
	$(CXX) $(CXXFLAGS) -c -o $@ $<

build/obj/imgui_impl_dx12.o: imgui_impl_dx12.cpp src/mingw_compat.h | build/obj
	$(CXX) $(CXXFLAGS) -include src/mingw_compat.h -c -o $@ $<

build/obj:
	mkdir -p $@

test:
	mkdir -p build
	$(HOSTCC) -Wall -Wextra -Isrc tests/test_items.c src/items.c -o build/test_items
	./build/test_items

clean:
	rm -rf build
