#!/bin/bash
# Build our modified cobc (C# output mode) from the project's overlay.
# Usage (MSYS2 UCRT64 shell):  bash tools/build-cobc-cs.sh
#
# The build tree must live in a path WITHOUT spaces (libtool), so it is
# D:/vcobol-build/gnucobol-cs; installed to D:/vcobol-cs.  The project
# keeps the source of truth: compiler/cobc/* is copied over the pristine
# GnuCOBOL 3.2 tree before every build.
set -e
PROJ="${PROJ:-/d/CALUDE CODE/visual-cobol}"   # override to build from a worktree
TREE=/d/vcobol-build/gnucobol-cs

if [ ! -d "$TREE" ]; then
  mkdir -p "$TREE"
  tar -xf "$PROJ/src-download/gnucobol-3.2.tar.xz" -C "$TREE" --strip-components=1
  ( cd "$TREE" && ./configure --prefix=/d/vcobol-cs --without-db --with-curses=no \
      --without-xml2 --with-json=no CFLAGS='-O2 -std=gnu17' > ../configure-cs.log )
fi

cp "$PROJ"/compiler/cobc/*.c "$PROJ"/compiler/cobc/*.def "$TREE/cobc/"
# flag.def changes the help text; skip regenerating man/info pages (no help2man here)
touch "$TREE/cobc/cobc.1" "$TREE/doc/gnucobol.info" 2>/dev/null || true
make -C "$TREE/cobc" > /d/vcobol-build/make-cs.log 2>&1 || { tail -30 /d/vcobol-build/make-cs.log; exit 1; }
mkdir -p /d/vcobol-cs/bin
cp "$TREE/cobc/.libs/cobc.exe" /d/vcobol-cs/bin/cobc.exe
echo "cobc (C# mode) built and installed to /d/vcobol-cs/bin"
