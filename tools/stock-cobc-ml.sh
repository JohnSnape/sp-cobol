#!/bin/bash
# Run the STOCK GnuCOBOL 3.2 build WITH XML (libxml2) and JSON (cJSON) for reference.
# Usage, from an MSYS2 UCRT64 shell:  source tools/stock-cobc-ml.sh ; cobc -x PROG.cbl
# Same as stock-cobc.sh but the D:/vcobol-stock-ml install (see BUILD.md); used by run-tests.ps1.
export PATH=/ucrt64/bin:$PATH
export PATH=/d/vcobol-stock-ml/bin:$PATH
export COB_CONFIG_DIR=D:/vcobol-stock-ml/share/gnucobol/config
export COB_COPY_DIR=D:/vcobol-stock-ml/share/gnucobol/copy
export COB_CFLAGS='-std=gnu17 -pipe -ID:/vcobol-stock-ml/include -Wno-unused -fsigned-char -Wno-pointer-sign'
export COB_LIBS='-LD:/vcobol-stock-ml/lib -lcob'
