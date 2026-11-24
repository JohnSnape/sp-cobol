// ORGANIZATION INDEXED.
//
// Placeholder: the on-disk format for indexed files is still to be decided (see
// DECISIONS.md). Until then every indexed operation reports status 91, "runtime library is
// not configured for this operation", exactly as stock GnuCOBOL does when it is built
// without an indexed-file library.

namespace CobolDotNet.Runtime;

internal sealed class IndexedFile
{
    internal static int Open(CobFile f, int mode) => 91;
    internal static void DeleteFiles(string name) { }

    internal void Close() { }
    internal int Start(int cond, CobField key, int keylen) => 91;
    internal int Read(CobField key) => 91;
    internal int ReadNext(int opts) => 91;
    internal int Write() => 91;
    internal int Rewrite(long size) => 91;
    internal int Delete() => 91;
}
