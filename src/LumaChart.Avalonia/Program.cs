using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia;

internal static class Program
{
    private static System.Threading.Timer? controlledFatalTimer;
    private const int EmergencyPersistTimeoutMilliseconds = 2_500;

    [STAThread]
    public static void Main(string[] args)
    {
        AvaloniaAppPaths.ConfigureSiblingClientProfileEnvironment();
        PlatformExternalUriServices.Current = new WindowsShellExternalUriService();
        PlatformTextInputServices.Current = new WindowsTextInputService();
        PlatformArchiveDirectoryCreators.Current = WindowsArchiveDirectoryCreator.Instance;
        PlatformAudioOutputs.Current = new WindowsAudioOutputFactory();
        using var diagnostics = WindowsDiagnosticLog.Start(
            AvaloniaAppPaths.DiagnosticLogRoot);
        diagnostics.WriteLifecycle("application-starting");

        WindowsSingleInstanceStartup singleInstanceStartup;
        try
        {
            singleInstanceStartup = WindowsSingleInstanceCoordinator
                .CreateOrForwardAsync(
                    args,
                    Environment.GetEnvironmentVariable(
                        WindowsSingleInstancePolicy.ScopeEnvironment),
                    Environment.GetEnvironmentVariable(
                        WindowsSingleInstancePolicy.DisableEnvironment))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            diagnostics.WriteException(
                "single-instance-startup-failed",
                exception,
                fatal: true);
            WindowsNativeMessageBox.Show(
                IntPtr.Zero,
                WindowsLibraryLauncherParityPolicy.CreateOperationError(
                    "无法启动 Windows Avalonia 客户端",
                    exception));
            Environment.ExitCode = 2;
            return;
        }

        if (!singleInstanceStartup.ContinueStartup)
        {
            diagnostics.WriteLifecycle("single-instance-request-forwarded");
            return;
        }

        using var singleInstanceCoordinator = singleInstanceStartup.Coordinator;
        MainWindow? mainWindow = null;
        void DrainSingleInstanceRequests()
        {
            if (mainWindow == null || singleInstanceCoordinator == null) return;
            while (singleInstanceCoordinator.TryDequeue(out var request))
                mainWindow.AcceptSingleInstanceRequest(request);
        }

        EventHandler singleInstanceRequestHandler = (_, _) =>
        {
            if (mainWindow == null) return;
            Dispatcher.UIThread.Post(
                DrainSingleInstanceRequests,
                DispatcherPriority.Input);
        };
        if (singleInstanceCoordinator != null)
            singleInstanceCoordinator.RequestAvailable += singleInstanceRequestHandler;

        UnhandledExceptionEventHandler appDomainHandler = (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                diagnostics.WriteException(
                    "runtime-unhandled-exception",
                    exception,
                    eventArgs.IsTerminating);
        };
        EventHandler<UnobservedTaskExceptionEventArgs> taskHandler = (_, eventArgs) =>
            diagnostics.WriteException(
                "unobserved-task-exception",
                eventArgs.Exception,
                fatal: false);
        DispatcherUnhandledExceptionEventHandler dispatcherHandler = (_, eventArgs) =>
        {
            diagnostics.WriteException(
                "application-fatal-exception",
                eventArgs.Exception,
                fatal: true);
            // Before the notice (which waits for the user) and the shutdown:
            // working copies are otherwise only written on save, close or
            // playtest, so the fault would discard every unsaved edit.
            TryEmergencyPersist(mainWindow, diagnostics);
            var request = WindowsFatalErrorPolicy.CreateNotice(
                diagnostics.CurrentLogPath);
            if (mainWindow != null)
                WindowsNativeMessageBox.ShowForLastActivePopup(
                    mainWindow,
                    request);
            else
                WindowsNativeMessageBox.Show(IntPtr.Zero, request);
            Environment.ExitCode = 1;
            eventArgs.Handled = true;
            eventArgs.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };
        AppDomain.CurrentDomain.UnhandledException += appDomainHandler;
        TaskScheduler.UnobservedTaskException += taskHandler;
        Dispatcher.UIThread.UnhandledException += dispatcherHandler;

        try
        {
            AppBuilder.Configure<global::Avalonia.Application>()
                .UseWin32()
                .With(WindowsRenderingConfiguration.Create())
                .UseSkia()
                .UseHarfBuzz()
                .LogToTrace(LogEventLevel.Warning)
                .Start((application, startupArgs) =>
                {
                    application.Styles.Add(new FluentTheme());
                    application.RequestedThemeVariant =
                        global::Avalonia.Styling.ThemeVariant.Dark;
                    WindowsWpf110ControlTheme.Apply(application);

                    mainWindow = new MainWindow(startupArgs);
                    mainWindow.Show();
                    DrainSingleInstanceRequests();
                    ScheduleControlledFatalFault(startupArgs, diagnostics);
                    application.Run(mainWindow);
                }, args);
            diagnostics.WriteLifecycle("application-stopped");
        }
        catch (Exception exception)
        {
            diagnostics.WriteException("application-fatal-exception", exception, fatal: true);
            var request = WindowsFatalErrorPolicy.CreateNotice(
                diagnostics.CurrentLogPath);
            if (mainWindow != null)
                WindowsNativeMessageBox.ShowForLastActivePopup(
                    mainWindow,
                    request);
            else
                WindowsNativeMessageBox.Show(IntPtr.Zero, request);
            Environment.ExitCode = 1;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= appDomainHandler;
            TaskScheduler.UnobservedTaskException -= taskHandler;
            Dispatcher.UIThread.UnhandledException -= dispatcherHandler;
            if (singleInstanceCoordinator != null)
                singleInstanceCoordinator.RequestAvailable -= singleInstanceRequestHandler;
            AvaloniaAppPaths.CleanupSessionScratchRoot();
        }
    }

    /// <summary>
    /// Bounded, exception-free emergency save for the fatal handler. The
    /// persistence itself runs off the UI thread (see
    /// MainWindow.TryEmergencyPersistWorkingCopies), so the short wait here
    /// cannot deadlock on work that needs the blocked UI thread; on timeout the
    /// shutdown proceeds and the atomic writes leave the previous copy intact.
    /// </summary>
    private static void TryEmergencyPersist(
        MainWindow? mainWindow,
        WindowsDiagnosticLog diagnostics)
    {
        if (mainWindow == null) return;
        try
        {
            var persisted = mainWindow.TryEmergencyPersistWorkingCopies(
                TimeSpan.FromMilliseconds(EmergencyPersistTimeoutMilliseconds));
            if (persisted == null) return;
            diagnostics.WriteLifecycle(
                persisted == true
                    ? "fatal-emergency-persist-completed"
                    : "fatal-emergency-persist-failed",
                persisted == true ? "information" : "warning");
        }
        catch (Exception)
        {
            // The fatal path must reach its notice and shutdown regardless.
        }
    }

    private static void ScheduleControlledFatalFault(
        IReadOnlyList<string> arguments,
        WindowsDiagnosticLog diagnostics)
    {
        var requested = arguments.Contains(
            WindowsFatalErrorPolicy.ControlledFaultArgument,
            StringComparer.OrdinalIgnoreCase);
        if (!WindowsFatalErrorPolicy.ShouldTriggerControlledFault(
                arguments,
                Environment.GetEnvironmentVariable(
                    WindowsFatalErrorPolicy.ControlledFaultEnvironment),
                Environment.GetEnvironmentVariable(
                    "LUMACHART_AVALONIA_LOCAL_DATA_ROOT")))
        {
            if (requested)
                diagnostics.WriteLifecycle("controlled-fatal-fault-rejected", "warning");
            return;
        }

        diagnostics.WriteLifecycle("controlled-fatal-fault-scheduled", "warning");
        controlledFatalTimer = new System.Threading.Timer(
            _ =>
            {
                controlledFatalTimer?.Dispose();
                controlledFatalTimer = null;
                Dispatcher.UIThread.Post(
                    () => throw new InvalidOperationException(
                        // Deliberately left untranslated: this fault is injected
                        // to prove the diagnostic log redacts a token and a room
                        // password. Translating it would change the very text the
                        // redaction patterns are matched against.
                        "Controlled Windows diagnostic fault; token=diagnostic-secret-token; 房间密码：diagnostic-room-password"),
                    DispatcherPriority.Send);
            },
            null,
            WindowsFatalErrorPolicy.ControlledFaultDelayMilliseconds,
            Timeout.Infinite);
    }
}
