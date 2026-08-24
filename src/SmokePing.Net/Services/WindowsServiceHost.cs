using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SmokePing.Net.Services;

/// <summary>
/// Runs the application under the Windows Service Control Manager.
///
/// A plain console process registered with sc.exe fails to start ("the service did
/// not respond in a timely fashion") because it never answers the SCM. This talks to
/// the SCM directly rather than depending on the
/// Microsoft.Extensions.Hosting.WindowsServices package, keeping the project free of
/// NuGet dependencies.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceHost
{
    private const int ServiceWin32OwnProcess = 0x00000010;

    private const int ServiceStopped = 0x00000001;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;

    private const int AcceptStop = 0x00000001;
    private const int AcceptShutdown = 0x00000004;

    private const int ControlStop = 0x00000001;
    private const int ControlShutdown = 0x00000005;
    private const int ControlInterrogate = 0x00000004;

    private const int NoError = 0;

    /// <summary>How long the SCM should wait for a state change before giving up on us.</summary>
    private const int WaitHintMs = 30_000;

    /// <summary>
    /// Hands control to the SCM and runs <paramref name="body"/> as the service.
    /// The body receives a token that is cancelled when the service is asked to stop,
    /// and a callback it must invoke once it is ready to serve requests.
    /// </summary>
    /// <returns>The exit code the body produced.</returns>
    public static int Run(string serviceName, Func<CancellationToken, Action, Task<int>> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(body);

        var service = new ServiceInstance(serviceName, body);
        return service.Dispatch();
    }

    private sealed class ServiceInstance
    {
        private readonly string _name;
        private readonly Func<CancellationToken, Action, Task<int>> _body;
        private readonly CancellationTokenSource _stopping = new();

        // The delegates are passed to native code, so they must stay reachable for
        // as long as the SCM might call them.
        private readonly ServiceMainCallback _serviceMain;
        private readonly HandlerExCallback _handler;

        private IntPtr _statusHandle;
        private int _checkPoint = 1;
        private int _exitCode;

        public ServiceInstance(string name, Func<CancellationToken, Action, Task<int>> body)
        {
            _name = name;
            _body = body;
            _serviceMain = ServiceMain;
            _handler = HandleControl;
        }

        /// <summary>Blocks until the SCM has run and finished <see cref="ServiceMain"/>.</summary>
        public int Dispatch()
        {
            var namePointer = Marshal.StringToHGlobalUni(_name);
            try
            {
                var table = new[]
                {
                    new ServiceTableEntry
                    {
                        Name = namePointer,
                        ServiceMain = Marshal.GetFunctionPointerForDelegate(_serviceMain),
                    },
                    default, // the table is terminated by a zeroed entry
                };

                if (!StartServiceCtrlDispatcher(table))
                {
                    throw new InvalidOperationException(
                        "Could not connect to the service control manager. " +
                        "Start the process without --service to run it as a console application.",
                        new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                }

                return _exitCode;
            }
            finally
            {
                Marshal.FreeHGlobal(namePointer);
                GC.KeepAlive(_serviceMain);
                GC.KeepAlive(_handler);
            }
        }

        /// <summary>The SCM's entry point; the service lives for as long as this runs.</summary>
        private void ServiceMain(int argumentCount, IntPtr argumentPointers)
        {
            _statusHandle = RegisterServiceCtrlHandlerEx(_name, _handler, IntPtr.Zero);
            if (_statusHandle == IntPtr.Zero)
            {
                return;
            }

            Report(ServiceStartPending);

            try
            {
                _exitCode = _body(_stopping.Token, () => Report(ServiceRunning)).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Report a failure so the SCM's recovery actions kick in; the exception
                // itself has already been logged by the host.
                _exitCode = 1;
            }

            Report(ServiceStopped, _exitCode);
        }

        private int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
        {
            switch (control)
            {
                case ControlStop:
                case ControlShutdown:
                    Report(ServiceStopPending);
                    _stopping.Cancel();
                    break;

                case ControlInterrogate:
                    Report(ServiceRunning);
                    break;
            }

            return NoError;
        }

        private void Report(int state, int exitCode = NoError)
        {
            if (_statusHandle == IntPtr.Zero)
            {
                return;
            }

            var pending = state is ServiceStartPending or ServiceStopPending;

            var status = new ServiceStatus
            {
                ServiceType = ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = state == ServiceRunning ? AcceptStop | AcceptShutdown : 0,
                Win32ExitCode = exitCode,
                ServiceSpecificExitCode = 0,
                CheckPoint = pending ? _checkPoint++ : 0,
                WaitHint = pending ? WaitHintMs : 0,
            };

            SetServiceStatus(_statusHandle, ref status);
        }
    }

    private delegate void ServiceMainCallback(int argumentCount, IntPtr argumentPointers);

    private delegate int HandlerExCallback(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr Name;
        public IntPtr ServiceMain;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerExCallback handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);
}
