#!/bin/bash
# Translate a COBOL program to C# with our modified cobc.
# Usage (MSYS2 UCRT64 shell):  tools/cobnet.sh PROG.cbl OUTDIR [sub]
#   "sub": a CALLed program (no -x, so PROCEDURE DIVISION USING is allowed)
#   writes OUTDIR/PROG.cs  (then build it in a .NET project that
#   references runtime/CobolDotNet.Runtime)
set -e
src="$1"; out="${2:-.}"; kind="${3:-main}"
mode=-x; [ "$kind" = sub ] && mode=
export PATH=/d/vcobol-cs/bin:$PATH
export COB_CONFIG_DIR=D:/vcobol-cs/share/gnucobol/config
export COB_COPY_DIR=D:/vcobol-cs/share/gnucobol/copy
base=$(basename "$src" .cbl)
work=$(mktemp -d)
cp "$src" "$work/"
( cd "$work" && cobc -C $mode -fcsharp "$(basename "$src")" )
mkdir -p "$out"
cp "$work/$base.cs" "$out/$base.cs"
rm -rf "$work"
echo "wrote $out/$base.cs"
