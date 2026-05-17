using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CommitAssist.UI
{
    [Guid(ToolWindowGuidString)]
    public sealed class CommitMessageToolWindow : ToolWindowPane
    {
        public const string ToolWindowGuidString = "D4E5F6A7-B8C9-0123-DEF0-234567890123";

        public CommitMessageToolWindow() : base(null)
        {
            Caption = "CommitAssist";
            Content = new CommitMessageToolWindowControl();
        }
    }
}

namespace CommitAssist
{
    // Wires up IVsSolution events so GitService gets swapped in/out as
    // the user opens and closes solutions.
    internal sealed class SolutionEventsSink : IVsSolutionEvents, IDisposable
    {
        private readonly CommitAssistPackage _package;
        private IVsSolution?  _solution;
        private uint          _cookie;

        public SolutionEventsSink(CommitAssistPackage package)
        {
            _package = package;
        }

        public void Advise(IVsSolution solution)
        {
            _solution = solution;
            _solution.AdviseSolutionEvents(this, out _cookie);
        }

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .SwitchToMainThreadAsync();

                if (_solution is null) return;
                _solution.GetSolutionInfo(out string dir, out _, out _);
                if (!string.IsNullOrEmpty(dir))
                    await _package.OnSolutionOpenedAsync(dir);
            });
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
        {
            _package.OnSolutionClosed();
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)           => 0;
        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy,
            IVsHierarchy pRealHierarchy)                                           => 0;
        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy,
            int fAdded)                                                            => 0;
        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy,
            int fRemoved)                                                          => 0;
        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy,
            IVsHierarchy pStubHierarchy)                                           => 0;
        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy,
            int fRemoving, ref int pfCancel)                                       => 0;
        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved,
            ref int pfCancel)                                                      => 0;
        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy,
            ref int pfCancel)                                                      => 0;

        public void Dispose()
        {
            if (_solution is not null && _cookie != 0)
            {
                _solution.UnadviseSolutionEvents(_cookie);
                _cookie = 0;
            }
        }
    }
}
