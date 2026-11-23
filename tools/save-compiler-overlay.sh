#!/bin/bash
# Copy the modified cobc sources from the build tree back into the project
# and refresh compiler/gnucobol-3.2-csharp.patch (diff vs. pristine 3.2).
set -e
P="/d/CALUDE CODE/visual-cobol"
T=/d/vcobol-build/gnucobol-cs/cobc
for f in codegen.c cobc.c typeck.c flag.def; do cp "$T/$f" "$P/compiler/cobc/$f"; done
tmp=$(mktemp -d)
tar -xf "$P/src-download/gnucobol-3.2.tar.xz" -C "$tmp"
( cd "$tmp/gnucobol-3.2/cobc"
  for f in codegen.c cobc.c typeck.c flag.def; do
    diff -u "$f" "$P/compiler/cobc/$f" --label "a/cobc/$f" --label "b/cobc/$f" || true
  done ) > "$P/compiler/gnucobol-3.2-csharp.patch"
rm -rf "$tmp"
wc -l "$P/compiler/gnucobol-3.2-csharp.patch"
