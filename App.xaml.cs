// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BlackStarSwissAudio.Services;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace BlackStarSwissAudio;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureAll();
        Log.Init();
        Log.Info("Startup", $"Black Star Swiss Audio {typeof(App).Assembly.GetName().Version}");

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("UI", a.Exception);
            MessageBox.Show($"An unexpected error occurred and was logged.\n\n{a.Exception.Message}",
                "Black Star Swiss Audio", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Fatal", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Task", a.Exception); a.SetObserved(); };

        base.OnStartup(e);
        await PlayStartupAudioIfAvailableAsync();
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    static async Task PlayStartupAudioIfAvailableAsync()
    {
        string? path;
        try { path = FindStartupAudio(); }
        catch (Exception ex)
        {
            Log.Warn("StartupAudio", $"Could not inspect Assets; launching normally. {ex.Message}");
            return;
        }
        if (path is null)
        {
            Log.Info("StartupAudio", "No WAV found in Assets; continuing without startup audio.");
            return;
        }

        try
        {
            using var reader = new AudioFileReader(path);
            using var output = new WaveOutEvent();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPlaybackStopped(object? sender, StoppedEventArgs args)
            {
                if (args.Exception is null) completion.TrySetResult();
                else completion.TrySetException(args.Exception);
            }

            output.PlaybackStopped += OnPlaybackStopped;
            try
            {
                output.Init(reader);
                output.Play();
                Log.Info("StartupAudio", $"Playing {Path.GetFileName(path)} before showing the window.");
                await completion.Task;
            }
            finally { output.PlaybackStopped -= OnPlaybackStopped; }
        }
        catch (Exception ex)
        {
            Log.Warn("StartupAudio", $"Could not play the startup WAV; launching normally. {ex.Message}");
        }
    }

    static string? FindStartupAudio()
    {
        string[] assetFolders =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets")),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets")
        ];

        foreach (string folder in assetFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            string? path = Directory.EnumerateFiles(folder, "*.wav", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (path is not null) return path;
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { MediaFoundationApi.Shutdown(); } catch { /* not started */ }
        AppPaths.CleanTemp();
        Log.Info("Exit", "Clean shutdown");
        Log.Shutdown();
        base.OnExit(e);
    }
}
