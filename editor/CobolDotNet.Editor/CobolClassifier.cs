// Syntax colors: each line goes through Core.CobolScanner.
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using CobolDotNet.Editor.Core;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace CobolDotNet.Editor
{
    [Export(typeof(IClassifierProvider))]
    [ContentType(CobolContentType.Name)]
    internal sealed class CobolClassifierProvider : IClassifierProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry = null;
        [Import] internal IStandardClassificationService Standard = null;

        public IClassifier GetClassifier(ITextBuffer buffer)
            => buffer.Properties.GetOrCreateSingletonProperty(() => new CobolClassifier(buffer, Registry, Standard));
    }

    internal sealed class CobolClassifier : IClassifier
    {
        readonly ITextBuffer buffer;
        readonly IClassificationType[] types;
        bool lastFixed;

        public CobolClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry, IStandardClassificationService std)
        {
            this.buffer = buffer;
            types = new IClassificationType[Enum.GetValues(typeof(TokenKind)).Length];
            types[(int)TokenKind.Sequence] = registry.GetClassificationType(CobolClassifications.Sequence);
            types[(int)TokenKind.Identification] = registry.GetClassificationType(CobolClassifications.Identification);
            types[(int)TokenKind.Comment] = std.Comment;
            types[(int)TokenKind.Keyword] = std.Keyword;
            types[(int)TokenKind.Function] = registry.GetClassificationType(CobolClassifications.Function);
            types[(int)TokenKind.String] = std.StringLiteral;
            types[(int)TokenKind.Number] = std.NumberLiteral;
            types[(int)TokenKind.Picture] = registry.GetClassificationType(CobolClassifications.Picture);
            types[(int)TokenKind.ParagraphName] = registry.GetClassificationType(CobolClassifications.Paragraph);
            types[(int)TokenKind.Identifier] = std.Identifier;
            types[(int)TokenKind.Operator] = std.Operator;

            lastFixed = CobolBuffer.For(buffer).IsFixed(buffer.CurrentSnapshot);
            buffer.Changed += OnChanged;
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        void OnChanged(object sender, TextContentChangedEventArgs e)
        {
            // if the file flips between fixed and free format, recolor all of it
            bool now = CobolBuffer.For(buffer).IsFixed(e.After);
            if (now != lastFixed)
            {
                lastFixed = now;
                ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(
                    new SnapshotSpan(e.After, 0, e.After.Length)));
            }
        }

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            var result = new List<ClassificationSpan>();
            var snapshot = span.Snapshot;
            bool isFixed = CobolBuffer.For(buffer).IsFixed(snapshot);

            int firstLine = snapshot.GetLineNumberFromPosition(span.Start);
            int lastLine = snapshot.GetLineNumberFromPosition(span.End);
            for (int n = firstLine; n <= lastLine; n++)
            {
                var line = snapshot.GetLineFromLineNumber(n);
                if (line.Length == 0) continue;
                string text = line.GetText();
                foreach (var t in CobolScanner.ScanLine(text, isFixed))
                {
                    var s = new SnapshotSpan(snapshot, line.Start + t.Start, t.Length);
                    if (s.IntersectsWith(span))
                        result.Add(new ClassificationSpan(s, types[(int)t.Kind]));
                }
            }
            return result;
        }
    }
}
