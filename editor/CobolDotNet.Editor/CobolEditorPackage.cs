// The one menu command: Edit > Renumber COBOL Lines (also on the editor's
// right-click menu). It numbers a file that has no sequence numbers yet, or
// repairs one whose numbers got out of order. Shown only in COBOL files.
using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Task = System.Threading.Tasks.Task;

namespace CobolDotNet.Editor
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideUIContextRule(CobolEditorContextString,
        name: "COBOL file in the editor",
        expression: "Cobol",
        termNames: new[] { "Cobol" },
        termValues: new[] { "ActiveEditorContentType:" + CobolContentType.Name })]
    public sealed class CobolEditorPackage : AsyncPackage
    {
        public const string PackageGuidString = "4075a763-530a-4c55-b13b-b96cfa2b93fe";
        public const string CommandSetString = "a87c31ec-044d-4222-bbfd-9e2ca872a790";
        public const string CobolEditorContextString = "e183fa04-44a0-4cf2-b9d6-a809ac0ad822";
        public const int RenumberCommandId = 0x0100;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                var id = new CommandID(new Guid(CommandSetString), RenumberCommandId);
                commands.AddCommand(new OleMenuCommand(OnRenumber, id));
            }
        }

        void OnRenumber(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var textManager = (IVsTextManager)GetService(typeof(SVsTextManager));
            var model = (IComponentModel)GetService(typeof(SComponentModel));
            if (textManager == null || model == null) return;
            if (textManager.GetActiveView(1, null, out IVsTextView active) != 0 || active == null) return;

            var view = model.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(active);
            if (view == null || !view.TextBuffer.ContentType.IsOfType(CobolContentType.Name)) return;
            Edits.RenumberAll(view.TextBuffer, model.GetService<ITextBufferUndoManagerProvider>());
        }
    }
}
