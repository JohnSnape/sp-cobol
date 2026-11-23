// What Visual Studio needs to know before anything else: which files are
// COBOL, and the names and default looks of the COBOL colors (they appear in
// Tools > Options > Environment > Fonts and Colors, where they can be changed).
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace CobolDotNet.Editor
{
    internal static class CobolContentType
    {
        public const string Name = "COBOL";

        [Export]
        [Name(Name)]
        [BaseDefinition("code")]
        internal static ContentTypeDefinition Definition = null;

        [Export]
        [FileExtension(".cbl")]
        [ContentType(Name)]
        internal static FileExtensionToContentTypeDefinition Cbl = null;

        [Export]
        [FileExtension(".cob")]
        [ContentType(Name)]
        internal static FileExtensionToContentTypeDefinition Cob = null;

        [Export]
        [FileExtension(".cpy")]
        [ContentType(Name)]
        internal static FileExtensionToContentTypeDefinition Cpy = null;
    }

    internal static class CobolClassifications
    {
        public const string Sequence = "COBOL.Sequence";
        public const string Identification = "COBOL.Identification";
        public const string Paragraph = "COBOL.Paragraph";
        public const string Picture = "COBOL.Picture";
        public const string Function = "COBOL.Function";

        [Export]
        [Name(Sequence)]
        [BaseDefinition(PredefinedClassificationTypeNames.ExcludedCode)]
        internal static ClassificationTypeDefinition SequenceType = null;

        [Export]
        [Name(Identification)]
        [BaseDefinition(PredefinedClassificationTypeNames.ExcludedCode)]
        internal static ClassificationTypeDefinition IdentificationType = null;

        [Export]
        [Name(Paragraph)]
        [BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition ParagraphType = null;

        [Export]
        [Name(Picture)]
        [BaseDefinition(PredefinedClassificationTypeNames.Number)]
        internal static ClassificationTypeDefinition PictureType = null;

        [Export]
        [Name(Function)]
        [BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition FunctionType = null;
    }

    // Gray works on both the light and the dark theme.

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = CobolClassifications.Sequence)]
    [Name(CobolClassifications.Sequence)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SequenceFormat : ClassificationFormatDefinition
    {
        public SequenceFormat()
        {
            DisplayName = "COBOL Sequence Number (columns 1-6)";
            ForegroundColor = Color.FromRgb(0x80, 0x80, 0x80);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = CobolClassifications.Identification)]
    [Name(CobolClassifications.Identification)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class IdentificationFormat : ClassificationFormatDefinition
    {
        public IdentificationFormat()
        {
            DisplayName = "COBOL Identification Area (columns 73-80)";
            ForegroundColor = Color.FromRgb(0x80, 0x80, 0x80);
            IsItalic = true;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = CobolClassifications.Paragraph)]
    [Name(CobolClassifications.Paragraph)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class ParagraphFormat : ClassificationFormatDefinition
    {
        public ParagraphFormat()
        {
            DisplayName = "COBOL Paragraph / Section Name";
            IsBold = true;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = CobolClassifications.Picture)]
    [Name(CobolClassifications.Picture)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class PictureFormat : ClassificationFormatDefinition
    {
        public PictureFormat()
        {
            DisplayName = "COBOL Picture String";
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = CobolClassifications.Function)]
    [Name(CobolClassifications.Function)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class FunctionFormat : ClassificationFormatDefinition
    {
        public FunctionFormat()
        {
            DisplayName = "COBOL Intrinsic Function";
            IsItalic = true;
        }
    }
}
