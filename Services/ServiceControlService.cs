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

    /// <summary>The parts of a Windows service that the actions below use. The Windows service
    /// manager is the boundary; tests stand in for it here.</summary>
    internal interface IServiceHandle : IDisposable
    {
        ServiceControllerStatus Status { get; }
        void Refresh();
        void Stop();
        void Start();
        void WaitForStatus(ServiceControllerStatus desired, TimeSpan timeout);
    }

    internal sealed class ControllerHandle : IServiceHandle
    {
        private readonly ServiceController _controller;
        internal ControllerHandle(string serviceName) => _controller = new ServiceController(serviceName);
        public ServiceControllerStatus Status => _controller.Status;
        public void Refresh() => _controller.Refresh();
        public void Stop() => _controller.Stop();
        public void Start() => _controller.Start();
        public void WaitForStatus(ServiceControllerStatus desired, TimeSpan timeout) => _controller.WaitForStatus(desired, timeout);
        public void Dispose() => _controller.Dispose();
    }

    internal static class ServiceControlService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        internal static Task<ServiceControlResult> ExecuteAsync(
            string serviceName, ServiceControlAction action, CancellationToken cancellationToken)
            => ExecuteAsync(() => new ControllerHandle(serviceName), action, cancellationToken);

        /// <summary>
        /// Runs a service action. <paramref name="cancellationToken"/> belongs to the tab that
        /// asked and only counts before anything has changed: once Stop has been accepted, a
        /// restart finishes with Start (or reports why it could not) even if that tab closes,
        /// because a stopped service is not what was asked for.
        /// </summary>
        /// <remarks>
        /// The work runs on its own foreground thread, which keeps the process alive until it
        /// finishes. A thread-pool or background thread would be killed when the app exits and
        /// could leave a service stopped halfway through a restart. The wait is bounded by the
        /// stop and start timeouts.
        /// </remarks>
        internal static Task<ServiceControlResult> ExecuteAsync(
            Func<IServiceHandle> open, ServiceControlAction action, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<ServiceControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => done.SetResult(Execute(open, action, cancellationToken)))
            {
                IsBackground = false,
                Name = "KillerShell service control",
            };
            thread.Start();
            return done.Task;
        }

        private static ServiceControlResult Execute(
            Func<IServiceHandle> open, ServiceControlAction action, CancellationToken cancellationToken)
        {
            try
            {
                // The last point at which nothing has happened yet.
                cancellationToken.ThrowIfCancellationRequested();
                using var controller = open();

                if (action == ServiceControlAction.Restart)
                {
                    if (controller.Status != ServiceControllerStatus.Stopped)
                    {
                        controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                    }
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
                // Any failure is reported to the caller. This runs on its own thread, where an
                // escaping exception would end the whole app.
                return new ServiceControlResult(false, false, ex.Message);
            }
        }
    }
}
