using System.Threading;
using System.Windows;
using System.Windows.Threading;
using OpenReo.App.Services;
using OpenReo.Core.Configuration;
using OpenReo.Core.Diagnostics;
using OpenReo.Core.Models;
using OpenReo.Video;
using OpenReo.Core.Localization;

namespace OpenReo.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Optional: --config <file> (portable use / testing with a separate configuration).
        string? configPath = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                configPath = System.IO.Path.GetFullPath(e.Args[i + 1]);

        // One instance per configuration: a second one would open every camera stream twice.
        // The default name is also used by the installer (AppMutex) to detect a running viewer.
        var mutexName = "OpenReo.Viewer.SingleInstance";
        if (configPath != null)
            mutexName += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(configPath.ToLowerInvariant())))[..16];
        Lang.Initialize(Lang.Auto); // final language is applied after the configuration was loaded
        _singleInstance = new Mutex(true, mutexName, out bool isFirst);
        // After a restart (e.g. language change) wait until the previous instance has exited.
        if (!isFirst && e.Args.Contains("--restarted", StringComparer.OrdinalIgnoreCase))
        {
            try { isFirst = _singleInstance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            MessageBox.Show(Lang.T("OpenReo läuft bereits."), "OpenReo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        Infrastructure.DarkTitleBar.RegisterForAllWindows();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };

        base.OnStartup(e);
        Log.Info($"OpenReo {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");

        try
        {
            FFmpegLoader.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("FFmpeg could not be loaded", ex);
            MessageBox.Show(Lang.F("Die Video-Bibliotheken (FFmpeg) konnten nicht geladen werden:\n\n{0}\n\nBitte installieren Sie OpenReo neu.", ex.Message),
                "OpenReo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var config = new ConfigurationManager(configPath);
        var cfg = config.Load();
        Lang.Initialize(cfg.Settings.Language);
        if (!cfg.Settings.RestoreCameraQuality)
            foreach (var cam in cfg.Cameras) cam.Quality = StreamQuality.Mid;

        _services = new AppServices(config);
        var window = new MainWindow(_services);
        MainWindow = window;
        window.Show();

        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunSelfTest(window));
    }

    /// <summary>
    /// Diagnostics: opens every dialog once (template + layout pass) and exits with the number of failures.
    /// Usage: OpenReo.exe --config test.json --selftest
    /// </summary>
    private void RunSelfTest(MainWindow owner)
    {
        int failures = 0;
        var cam = new CameraConfig { Name = "Selbsttest", Host = "192.0.2.1" };
        var factories = new (string Name, Func<Window> Create)[]
        {
            ("CameraDialog (neu)", () => new Dialogs.CameraDialog(null, RtspTransport.Tcp, _services!.Cameras.Cameras)),
            ("CameraDialog (bearbeiten)", () => new Dialogs.CameraDialog(cam, RtspTransport.Tcp, new[] { cam })),
            ("DiscoveryDialog", () => new Dialogs.DiscoveryDialog(_services!.Cameras, new AppSettings())),
            ("SettingsDialog", () => new Dialogs.SettingsDialog(new AppSettings())),
            ("LayoutEditorDialog", () => new Dialogs.LayoutEditorDialog(new Core.Layouts.LayoutManager(new AppConfiguration()))),
        };
        foreach (var (name, create) in factories)
        {
            try
            {
                var w = create();
                w.Owner = owner;
                w.Show();
                w.UpdateLayout();
                w.Close();
                Log.Info($"Selftest OK: {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error($"Selftest FAILED: {name}", ex);
            }
        }
        Log.Info($"Selftest finished: {failures} failure(s)");
        Shutdown(failures);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true; // keep the viewer running – a single failure must not close all cameras
        MessageBox.Show(Lang.F("Ein unerwarteter Fehler ist aufgetreten:\n\n{0}\n\nDetails stehen im Protokoll ({1}).", e.Exception.Message, AppPaths.LogDirectory),
            "OpenReo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Dispose();
            HardwareAcceleration.Shared.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }
        Log.Info("OpenReo stopped.");
        Log.Shutdown();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
