#!/bin/bash
# Run the STOCK GnuCOBOL 3.2 build (COBOL -> C -> native exe) for reference/comparison.
# Usage, from an MSYS2 UCRT64 shell:  source tools/stock-cobc.sh ; cobc -x PROG.cbl
# The build bakes in MSYS-style paths that native Windows exes can't read, hence these overrides.
export PATH=/d/vcobol-stock/bin:$PATH
export COB_CONFIG_DIR=D:/vcobol-stock/share/gnucobol/config
export COB_COPY_DIR=D:/vcobol-stock/share/gnucobol/copy
export COB_CFLAGS='-std=gnu17 -pipe -ID:/vcobol-stock/include -Wno-unused -fsigned-char -Wno-pointer-sign'
export COB_LIBS='-LD:/vcobol-stock/lib -lcob'
