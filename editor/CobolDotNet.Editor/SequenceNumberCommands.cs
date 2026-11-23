// Keeps the sequence numbers right as you type (decision of 2026-11-22):
// Enter, Backspace, Delete, Tab, Home and typing in columns 1-7 follow the plans
// in Core.SequenceNumbers; paste, cut and the line commands run normally and
// are then renumbered. Each keystroke is ONE undo step, numbers included.
//
// Only files that are already numbered (first line starts with 6 digits) are
// managed. Edit > Renumber COBOL Lines numbers a file that isn't.
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using CobolDotNet.Editor.Core;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace CobolDotNet.Editor
{
    internal static class Edits
    {
        public static void Apply(ITextBuffer buffer, IEnumerable<TextChange> changes)
        {
            var snap = buffer.CurrentSnapshot;
            using (var edit = buffer.CreateEdit())
            {
                foreach (var c in changes)
                {
                    int s = snap.GetLineFromLineNumber(c.Line).Start + c.Column;
                    int e = snap.GetLineFromLineNumber(c.EndLine).Start + c.EndColumn;
                    edit.Replace(Span.FromBounds(s, e), c.Text);
                }
                edit.Apply();
            }
        }

        public static void Renumber(ITextBuffer buffer)
        {
            var changes = SequenceNumbers.Renumber(new SnapshotLines(buffer.CurrentSnapshot));
            if (changes.Count > 0) Apply(buffer, changes);
        }

        /// <summary>Renumber the whole buffer as one undo step (the Renumber command).</summary>
        public static void RenumberAll(ITextBuffer buffer, ITextBufferUndoManagerProvider undo)
        {
            var history = undo.GetTextBufferUndoManager(buffer).TextBufferUndoHistory;
            CobolBuffer.Busy++;
            try
            {
                using (var tx = history.CreateTransaction("Renumber COBOL Lines"))
                {
                    Renumber(buffer);
                    tx.Complete();
                }
            }
            finally { CobolBuffer.Busy--; }
        }

        public static void MoveCaret(ITextView view, ITextBuffer buffer, int lineNumber, int column)
        {
            var snap = buffer.CurrentSnapshot;
            var line = snap.GetLineFromLineNumber(Math.Min(lineNumber, snap.LineCount - 1));
            var point = new SnapshotPoint(snap, line.Start + Math.Min(column, line.Length));
            var inView = view.BufferGraph.MapUpToBuffer(point, PointTrackingMode.Positive,
                                                        PositionAffinity.Successor, view.TextBuffer);
            if (inView.HasValue)
            {
                view.Caret.MoveTo(inView.Value);
                view.Caret.EnsureVisible();
            }
        }
    }

    [Export(typeof(ICommandHandler))]
    [Name("CobolSequenceNumbers")]
    [ContentType(CobolContentType.Name)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class SequenceNumberCommands :
        IChainedCommandHandler<ReturnKeyCommandArgs>,
        IChainedCommandHandler<BackspaceKeyCommandArgs>,
        IChainedCommandHandler<DeleteKeyCommandArgs>,
        IChainedCommandHandler<TypeCharCommandArgs>,
        IChainedCommandHandler<TabKeyCommandArgs>,
        IChainedCommandHandler<LineStartCommandArgs>,
        IChainedCommandHandler<PasteCommandArgs>,
        IChainedCommandHandler<CutCommandArgs>,
        IChainedCommandHandler<DuplicateSelectionCommandArgs>,
        IChainedCommandHandler<MoveSelectedLinesUpCommandArgs>,
        IChainedCommandHandler<MoveSelectedLinesDownCommandArgs>,
        IChainedCommandHandler<OpenLineAboveCommandArgs>,
        IChainedCommandHandler<OpenLineBelowCommandArgs>
    {
        [Import] internal ITextBufferUndoManagerProvider UndoProvider = null;

        public string DisplayName => "COBOL sequence numbers";

        // ---------------- keystrokes with a plan ----------------

        public void ExecuteCommand(ReturnKeyCommandArgs args, Action next, CommandExecutionContext context)
        {
            if (!TryPlan(args, next, (lines, line, col, nl) => SequenceNumbers.PlanEnter(lines, line, col, nl)))
                Wrapped(args, next);
        }

        public void ExecuteCommand(BackspaceKeyCommandArgs args, Action next, CommandExecutionContext context)
        {
            if (!TryPlan(args, next, (lines, line, col, nl) => SequenceNumbers.PlanBackspace(lines, line, col)))
                WrappedIfSelection(args, next);
        }

        public void ExecuteCommand(DeleteKeyCommandArgs args, Action next, CommandExecutionContext context)
        {
            if (!TryPlan(args, next, (lines, line, col, nl) => SequenceNumbers.PlanDelete(lines, line, col)))
                WrappedIfSelection(args, next);
        }

        public void ExecuteCommand(TypeCharCommandArgs args, Action next, CommandExecutionContext context)
        {
            char ch = args.TypedChar;
            if (!TryPlan(args, next, (lines, line, col, nl) => SequenceNumbers.PlanTypeChar(lines, line, col, ch)))
                WrappedIfSelection(args, next);
        }

        public void ExecuteCommand(TabKeyCommandArgs args, Action next, CommandExecutionContext context)
        {
            if (!TryPlan(args, next, (lines, line, col, nl) => SequenceNumbers.PlanTab(lines, line, col)))
                next();
        }

        public void ExecuteCommand(LineStartCommandArgs args, Action next, CommandExecutionContext context)
        {
            if (!TryPlan(args, next, (lines, line, col, nl) => SequenceNumbers.PlanHome(lines, line, col)))
                next();
        }

        // ---------------- commands that run normally, then renumber ----------------

        public void ExecuteCommand(PasteCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);
        public void ExecuteCommand(CutCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);
        public void ExecuteCommand(DuplicateSelectionCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);
        public void ExecuteCommand(MoveSelectedLinesUpCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);
        public void ExecuteCommand(MoveSelectedLinesDownCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);
        public void ExecuteCommand(OpenLineAboveCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);
        public void ExecuteCommand(OpenLineBelowCommandArgs args, Action next, CommandExecutionContext context) => Wrapped(args, next);

        public CommandState GetCommandState(ReturnKeyCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(BackspaceKeyCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(DeleteKeyCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(TypeCharCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(TabKeyCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(LineStartCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(PasteCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(CutCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(DuplicateSelectionCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(MoveSelectedLinesUpCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(MoveSelectedLinesDownCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(OpenLineAboveCommandArgs args, Func<CommandState> next) => next();
        public CommandState GetCommandState(OpenLineBelowCommandArgs args, Func<CommandState> next) => next();

        // ---------------- plumbing ----------------

        delegate EditPlan Planner(IReadOnlyList<string> lines, int line, int column, string newline);

        bool TryPlan(EditorCommandArgs args, Action next, Planner planner)
        {
            var view = args.TextView;
            var buffer = args.SubjectBuffer;
            if (!view.Selection.IsEmpty || view.GetMultiSelectionBroker().HasMultipleSelections) return false;

            var caret = view.Caret.Position.Point.GetPoint(buffer, PositionAffinity.Successor);
            if (!caret.HasValue) return false;
            var snap = buffer.CurrentSnapshot;
            if (!CobolBuffer.IsNumbered(snap)) return false;

            var lines = new SnapshotLines(snap);
            var line = snap.GetLineFromPosition(caret.Value);
            if (line.LineNumber >= lines.Count) return false;
            int col = caret.Value - line.Start + view.Caret.Position.VirtualSpaces;

            string newline = line.LineBreakLength > 0 ? line.GetLineBreakText()
                           : (view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId) ?? "\r\n");
            var plan = planner(lines, line.LineNumber, col, newline);
            if (plan == null) return false;

            if (plan.Changes.Count == 0 && !plan.Renumber && !plan.ThenDefault)
            {
                Edits.MoveCaret(view, buffer, plan.CaretLine, plan.CaretColumn);   // Home, or a refused key
                return true;
            }

            var history = UndoProvider.GetTextBufferUndoManager(buffer).TextBufferUndoHistory;
            CobolBuffer.Busy++;
            try
            {
                using (var tx = history.CreateTransaction("COBOL edit"))
                {
                    if (plan.Changes.Count > 0) Edits.Apply(buffer, plan.Changes);
                    if (plan.Renumber) Edits.Renumber(buffer);
                    Edits.MoveCaret(view, buffer, plan.CaretLine, plan.CaretColumn);
                    if (plan.ThenDefault) next();
                    tx.Complete();
                }
            }
            finally { CobolBuffer.Busy--; }
            return true;
        }

        void WrappedIfSelection(EditorCommandArgs args, Action next)
        {
            if (args.TextView.Selection.IsEmpty) next();
            else Wrapped(args, next);
        }

        /// <summary>Run the editor's own command, then renumber, as one undo step.</summary>
        void Wrapped(EditorCommandArgs args, Action next)
        {
            var buffer = args.SubjectBuffer;
            if (!CobolBuffer.IsNumbered(buffer.CurrentSnapshot)) { next(); return; }

            var history = UndoProvider.GetTextBufferUndoManager(buffer).TextBufferUndoHistory;
            CobolBuffer.Busy++;
            try
            {
                using (var tx = history.CreateTransaction("COBOL edit"))
                {
                    next();
                    Edits.Renumber(buffer);
                    tx.Complete();
                }
            }
            finally { CobolBuffer.Busy--; }
        }
    }

    /// <summary>
    /// Anything else that adds or removes lines (Find and Replace, drag and drop,
    /// another extension) is renumbered right after, as its own undo step.
    /// Undo and redo are left alone: they restore numbers on their own.
    /// </summary>
    [Export(typeof(ITextViewCreationListener))]
    [ContentType(CobolContentType.Name)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class RenumberWatcherFactory : ITextViewCreationListener
    {
        [Import] internal ITextBufferUndoManagerProvider UndoProvider = null;

        public void TextViewCreated(ITextView view)
        {
            var buffer = view.TextBuffer;
            buffer.Properties.GetOrCreateSingletonProperty(typeof(RenumberWatcher),
                () => new RenumberWatcher(buffer, UndoProvider.GetTextBufferUndoManager(buffer).TextBufferUndoHistory));
        }
    }

    internal sealed class RenumberWatcher
    {
        readonly ITextBuffer buffer;
        readonly Microsoft.VisualStudio.Text.Operations.ITextUndoHistory history;
        bool pending;

        public RenumberWatcher(ITextBuffer buffer, Microsoft.VisualStudio.Text.Operations.ITextUndoHistory history)
        {
            this.buffer = buffer;
            this.history = history;
            buffer.Changed += OnChanged;
            buffer.PostChanged += OnPostChanged;
        }

        void OnChanged(object sender, TextContentChangedEventArgs e)
        {
            if (CobolBuffer.Busy > 0 || history.State != TextUndoHistoryState.Idle) return;
            foreach (var c in e.Changes)
            {
                if (c.LineCountDelta != 0 || c.OldText.IndexOf('\n') >= 0 || c.NewText.IndexOf('\n') >= 0)
                {
                    pending = true;
                    return;
                }
            }
        }

        void OnPostChanged(object sender, EventArgs e)
        {
            if (!pending) return;
            pending = false;
            if (CobolBuffer.Busy > 0 || buffer.EditInProgress) return;
            if (!CobolBuffer.IsNumbered(buffer.CurrentSnapshot)) return;
            CobolBuffer.Busy++;
            try { Edits.Renumber(buffer); }
            finally { CobolBuffer.Busy--; }
        }
    }
}
