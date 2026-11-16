// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.Globalization;
using System.Speech.Recognition;

namespace BlackStarSwissAudio.Services;

public sealed class SpeechRecognitionService(CultureInfo culture) : IDisposable
{
    readonly SpeechRecognitionEngine _engine = CreateEngine(culture);
    bool _handlersAttached;
    volatile bool _listening, _stopping, _disposed;

    public event Action<string>? CommandRecognized;
    public event Action<Exception>? ListeningError;
    public event Action<float>? InputLevelChanged;
    public event Action? ListeningStopped;

    static SpeechRecognitionEngine CreateEngine(CultureInfo culture)
    {
        var engine = new SpeechRecognitionEngine(culture);
        engine.LoadGrammar(new Grammar(BuildCommandGrammar(culture)) { Name = "Studio commands" });
        engine.LoadGrammar(new Grammar(BuildTrackGrammar(culture, "load song")) { Name = "Load song" });
        engine.LoadGrammar(new Grammar(BuildTrackGrammar(culture, "play song")) { Name = "Play song" });
        return engine;
    }

    void AttachHandlers()
    {
        if (_handlersAttached) return;
        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.AudioLevelUpdated += OnAudioLevelUpdated;
        _engine.RecognizeCompleted += OnRecognizeCompleted;
        _handlersAttached = true;
    }

    static GrammarBuilder BuildCommandGrammar(CultureInfo culture)
    {
        var commands = new Choices(
        [
            "open file explorer", "open folder explorer", "open downloads", "open documents", "open music", "open pictures",
            "import audio", "open audio", "play", "pause", "stop", "record", "finish recording", "undo", "redo",
            "apply effect", "bounce mix", "export audio", "export video", "cancel", "mute track", "unmute track",
            "solo track", "unsolo track", "remove track", "delete track", "next track", "previous track", "add fx",
            "mend join", "help", "stop listening"
        ]);
        var builder = new GrammarBuilder { Culture = culture };
        builder.Append(commands);
        return builder;
    }

    static GrammarBuilder BuildTrackGrammar(CultureInfo culture, string prefix)
    {
        var builder = new GrammarBuilder(prefix) { Culture = culture };
        builder.AppendDictation();
        return builder;
    }

    void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence >= 0.55f && !string.IsNullOrWhiteSpace(e.Result.Text))
            CommandRecognized?.Invoke(e.Result.Text);
    }

    void OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs e) => InputLevelChanged?.Invoke(Math.Clamp(e.AudioLevel / 100f, 0, 1));

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listening) return;
        if (_stopping) throw new InvalidOperationException("The previous voice session is still stopping. Please try again in a moment.");

        AttachHandlers();
        _engine.SetInputToDefaultAudioDevice();
        try
        {
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            _listening = true;
        }
        catch
        {
            _listening = false;
            _engine.SetInputToNull();
            throw;
        }
    }

    public void Stop()
    {
        if (!_listening) return;
        _listening = false;
        _stopping = true;
        try { _engine.RecognizeAsyncCancel(); }
        catch (Exception ex)
        {
            Log.Error("SpeechRecognition", ex);
            ListeningError?.Invoke(ex);
            _stopping = false;
            _engine.SetInputToNull();
        }
    }

    void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error is not null)
        {
            Log.Error("SpeechRecognition", e.Error);
            ListeningError?.Invoke(e.Error);
        }
        _listening = false;
        _stopping = false;
        _engine.SetInputToNull();
        InputLevelChanged?.Invoke(0);
        ListeningStopped?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handlersAttached)
        {
            _engine.SpeechRecognized -= OnSpeechRecognized;
            _engine.AudioLevelUpdated -= OnAudioLevelUpdated;
            _engine.RecognizeCompleted -= OnRecognizeCompleted;
        }
        try { _engine.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
        _engine.Dispose();
    }
}

