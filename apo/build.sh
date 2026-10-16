#!/bin/sh
# Сборка GolosAPO.dll компилятором MinGW-w64 из MSYS2 (UCRT64). Visual Studio не нужна.
#   ./build.sh        — DLL в ../build/
#   ./build.sh test   — DLL + проверки (математика цепочки и COM-контракт)
set -e
cd "$(dirname "$0")"
# свой путь к MinGW — в build.local.sh (не в git): export MINGW_BIN=/c/msys64/ucrt64/bin
[ -f ./build.local.sh ] && . ./build.local.sh
if ! command -v g++ >/dev/null 2>&1; then
    export PATH="${MINGW_BIN:-/c/msys64/ucrt64/bin}:$PATH"
fi
OUT=../build
mkdir -p "$OUT"

FLAGS="-O2 -std=c++17 -Wall -Wextra -Wno-unused-parameter -fno-exceptions -fno-rtti -msse2 -mfpmath=sse"

g++ $FLAGS -shared -static -static-libgcc -static-libstdc++ \
    -o "$OUT/GolosAPO.dll" apo.cpp GolosAPO.def \
    -lole32 -loleaut32 -luuid -ladvapi32 -Wl,--enable-stdcall-fixup -Wl,--no-insert-timestamp
echo "built $OUT/GolosAPO.dll"

g++ $FLAGS -shared -static -static-libgcc -static-libstdc++ -o "$OUT/GolosDsp.dll" dsp_export.cpp -Wl,--no-insert-timestamp
echo "built $OUT/GolosDsp.dll"

if [ "$1" = "test" ]; then
    g++ $FLAGS -static -o test/test_apo.exe test/test_main.cpp -lole32 -luuid
    TMPD="$(cygpath -w "${TEMP:-/tmp}")\\golos-test"
    ./test/test_apo.exe "$(cygpath -w "$OUT/GolosAPO.dll")" "$TMPD"
fi

g++ $FLAGS -static -o "$OUT/golosctl.exe" test/golosctl.cpp -lole32 -luuid -loleaut32 -lpropsys
echo "built $OUT/golosctl.exe"
