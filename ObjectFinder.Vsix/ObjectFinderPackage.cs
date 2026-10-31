using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ObjectFinder.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(SsmsContextGuid, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ObjectFinderPackage : AsyncPackage
    {
        public const string PackageGuidString = "8b2e1f4e-c225-4688-aca7-88256cdbbd41";
        public const string SsmsContextGuid = "B7B07F42-6013-4C67-A504-C771CBC7625A";

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            VsShellUtilities.ShowMessageBox(
                this, "ObjectFinder package loaded.", "ObjectFinder",
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
