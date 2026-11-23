// Per-file facts the editor pieces share: is it fixed format, is it numbered,
// and a list-of-lines view of a snapshot for the Core code.
using System;
using System.Collections;
using System.Collections.Generic;
using CobolDotNet.Editor.Core;
using Microsoft.VisualStudio.Text;

namespace CobolDotNet.Editor
{
    /// <summary>
    /// The lines of a snapshot as strings, read on demand. A final empty line (the
    /// file ends with a line break) isn't counted, so it never gets a number.
    /// </summary>
    internal sealed class SnapshotLines : IReadOnlyList<string>
    {
        readonly ITextSnapshot snapshot;

        public SnapshotLines(ITextSnapshot snapshot)
        {
            this.snapshot = snapshot;
            int n = snapshot.LineCount;
            if (n > 1 && snapshot.GetLineFromLineNumber(n - 1).Length == 0) n--;
            Count = n;
        }

        public int Count { get; }
        public string this[int index] => snapshot.GetLineFromLineNumber(index).GetText();

        public IEnumerator<string> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class CobolBuffer
    {
        readonly ITextBuffer buffer;
        int version = -1;
        bool isFixed = true;

        CobolBuffer(ITextBuffer buffer)
        {
            this.buffer = buffer;
        }

        public static CobolBuffer For(ITextBuffer buffer)
            => buffer.Properties.GetOrCreateSingletonProperty(typeof(CobolBuffer), () => new CobolBuffer(buffer));

        /// <summary>Fixed format (columns 1-6, 7, 8-72) unless the file plainly is free format.</summary>
        public bool IsFixed(ITextSnapshot snapshot)
        {
            if (snapshot.Version.VersionNumber != version)
            {
                isFixed = SequenceNumbers.LooksFixed(new SnapshotLines(snapshot));
                version = snapshot.Version.VersionNumber;
            }
            return isFixed;
        }

        /// <summary>Numbered files (first line starts with 6 digits) get automatic numbering.</summary>
        public static bool IsNumbered(ITextSnapshot snapshot)
            => SequenceNumbers.IsNumbered(new SnapshotLines(snapshot));

        /// <summary>Set while this extension edits the buffer, so the watcher ignores its own edits.</summary>
        [ThreadStatic] public static int Busy;
    }
}
