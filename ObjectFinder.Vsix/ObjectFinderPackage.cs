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
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ObjectFinder.Vsix.ObjectFinderToolWindow))]
    public sealed class ObjectFinderPackage : AsyncPackage
    {
        public const string PackageGuidString = "8b2e1f4e-c225-4688-aca7-88256cdbbd41";
        public const string SsmsContextGuid = "B7B07F42-6013-4C67-A504-C771CBC7625A";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                await ObjectFinder.Vsix.ObjectFinderToolWindowCommand.InitializeAsync(this);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await LogInitializationFailureAsync(ex);
            }
        }

        private async Task LogInitializationFailureAsync(Exception ex)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            IVsActivityLog activityLog = await GetServiceAsync(typeof(SVsActivityLog)) as IVsActivityLog;
            if (activityLog != null)
            {
                activityLog.LogEntry(
                    (uint)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                    nameof(ObjectFinderPackage),
                    "ObjectFinder failed to initialize: " + ex);
            }
        }
    }
}
