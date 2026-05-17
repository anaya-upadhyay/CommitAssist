// Static Instance accessor so WPF controls can reach the package without a
// service-locator call (which requires the UI thread and an IServiceProvider
// they don't always have at hand).

using Microsoft.VisualStudio.Shell;

namespace CommitAssist
{
    public sealed partial class CommitAssistPackage
    {
        private static CommitAssistPackage? _instance;

        // Safe to read from any thread after InitialiseAsync completes.
        public static CommitAssistPackage? Instance => _instance;

        private void RegisterInstance() => _instance = this;
    }
}
