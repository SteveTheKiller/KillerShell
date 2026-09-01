using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace KillerShell.Services
{
    internal enum ServiceControlAction
    {
        Start,
        Stop,
        Restart,
    }

    internal readonly struct ServiceControlResult
    {
        internal bool Succeeded { get; }
        internal bool Canceled { get; }
        internal string Error { get; }

        internal ServiceControlResult(bool succeeded, bool canceled, string error)
        {
            Succeeded = succeeded;
            Canceled = canceled;
            Error = error;
        }
    }

    internal static class ServiceControlService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        internal static Task<ServiceControlResult> ExecuteAsync(
            string serviceName, ServiceControlAction action, CancellationToken cancellationToken)
            => Task.Factory.StartNew(
                () => Execute(serviceName, action, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

        private static ServiceControlResult Execute(
            string serviceName, ServiceControlAction action, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var controller = new ServiceController(serviceName);

                if (action == ServiceControlAction.Restart)
                {
                    if (controller.Status != ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    controller.Refresh();
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
                }
                else if (action == ServiceControlAction.Start)
                {
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
                }
                else
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                }

                return new ServiceControlResult(true, false, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return new ServiceControlResult(false, true, string.Empty);
            }
            catch (Exception ex)
            {
                return new ServiceControlResult(false, false, ex.Message);
            }
        }
    }
}
