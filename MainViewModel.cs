// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BlackStarSwissAudio.Core;
using BlackStarSwissAudio.Services;
using Microsoft.Win32;

namespace BlackStarSwissAudio.ViewModels;

public sealed class MainViewModel : Obs, IDisposable
{
    readonly AudioPlayer _player = new();
    readonly Recorder _rec = new();
    readonly DispatcherTimer _timer;
    readonly Process _process = Process.GetCurrentProcess();
    readonly Stopwatch _diagnosticClock = Stopwatch.StartNew();
    readonly Stopwatch _spectrogramLiveClock = new();
    AudioBuffer? _mix, _loaded;
    SpeechRecognitionService? _speech;
    SpectrogramColumn? _spectrogramLiveColumn;
    long _mixVersion;
    int _spectrogramLiveSequence;
    CancellationTokenSource? _cts;
    TimeSpan _lastDiagnosticCpu;
    string _status = "Ready — drop audio or video files anywhere to begin.", _pos = "00:00.0 / 00:00.0", _fxUrl = "", _fxCategory = "All sounds", _diagnosticsText = "";
    bool _busy, _recording, _spectrogramLive, _voiceListening, _disposed, _loopEnabled, _snapSelection = true, _toolsCollapsed, _fxCollapsed, _diagnosticsEnabled;
    double _level, _voiceInputLevel, _positionSeconds, _timelineZoom = 1, _timelineStartSeconds, _selectedStart, _selectedEnd, _loopStart, _loopEnd;
    int _exportBitrate = 192000;
    string _selectedSpeed = "1×", _userFxCategory = "My Audio", _newFxCategoryName = "";
    string _fxLibraryUsage = $"{FxLibrary.UserAudioCount} / {FxLibrary.MaxUserAudio} user clips";
    IReadOnlyList<string> _fxCategories = BuildFxCategories();
    IReadOnlyList<string> _userFxCategories = FxLibrary.UserCategories;
    TrackVM? _track; ToolDef? _tool; FxItem? _fx;

    public ObservableCollection<TrackVM> Tracks { get; } = [];
    public ObservableCollection<ParamVM> Params { get; } = [];
    public ObservableCollection<FxItem> FilteredFxItems { get; } = [];
    public static IReadOnlyList<ToolDef> Tools => ToolRegistry.All;
    public IReadOnlyList<string> FxCategories => _fxCategories;
    public IReadOnlyList<string> UserFxCategories => _userFxCategories;
    public string UserFxCategory { get => _userFxCategory; set => Set(ref _userFxCategory, value); }
    public string NewFxCategoryName
    {
        get => _newFxCategoryName;
        set
        {
            if (Set(ref _newFxCategoryName, value)) CommandManager.InvalidateRequerySuggested();
        }
    }
    public string FxLibraryUsage => _fxLibraryUsage;
    public IReadOnlyList<FxSource> FxSources { get; } = FxLibrary.Sources;

    static IReadOnlyList<string> BuildFxCategories() => ["All sounds", .. FxLibrary.Items.Select(item => item.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    void RefreshFxCategories()
    {
        _fxCategories = BuildFxCategories();
        _userFxCategories = FxLibrary.UserCategories;
        _fxLibraryUsage = $"{FxLibrary.UserAudioCount} / {FxLibrary.MaxUserAudio} user clips";
        Raise(nameof(FxCategories));
        Raise(nameof(UserFxCategories));
        Raise(nameof(FxLibraryUsage));
    }
    public bool SpectrogramLive
    {
        get => _spectrogramLive;
        private set
        {
            if (Set(ref _spectrogramLive, value)) Raise(nameof(SpectrogramCaption));
        }
    }
    public SpectrogramColumn? SpectrogramLiveColumn
    {
        get => _spectrogramLiveColumn;
        private set
        {
            if (Set(ref _spectrogramLiveColumn, value)) Raise(nameof(SpectrogramCaption));
        }
    }
    public string SpectrogramCaption => _rec.IsRecording ? "LIVE SPECTROGRAM  /  MICROPHONE INPUT" :
        SpectrogramLive ? "LIVE SPECTROGRAM  /  PLAYBACK FEED" : "SPECTROGRAM  /  WAITING FOR LIVE FEED";
    public string FxCategory
    {
        get => _fxCategory;
        set
        {
            if (Set(ref _fxCategory, value)) RefreshFxItems();
        }
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public string PositionText { get => _pos; set => Set(ref _pos, value); }
    public string FxUrl { get => _fxUrl; set => Set(ref _fxUrl, value); }
    public string DiagnosticsText { get => _diagnosticsText; private set => Set(ref _diagnosticsText, value); }
    public double Level { get => _level; set => Set(ref _level, value); }
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool IsRecording { get => _recording; set => Set(ref _recording, value); }
    public double VoiceInputLevel { get => _voiceInputLevel; private set => Set(ref _voiceInputLevel, value); }
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(value, 0, MixDurationSeconds);
            if (Set(ref _positionSeconds, value)) _player.Seek(value);
        }
    }
    public double MixDurationSeconds => Tracks.Count == 0 ? 0 : Tracks.Max(track => track.Offset + track.Buffer.Seconds);
    public double TimelineZoom
    {
        get => _timelineZoom;
        set
        {
            if (!double.IsFinite(value) || !Set(ref _timelineZoom, Math.Clamp(value, 1, 16))) return;
            Raise(nameof(TimelineViewSeconds));
            TimelineStartSeconds = _timelineStartSeconds;
        }
    }
    public double TimelineStartSeconds
    {
        get => _timelineStartSeconds;
        set
        {
            if (!double.IsFinite(value)) return;
            double max = Math.Max(0, MixDurationSeconds - TimelineViewSeconds);
            Set(ref _timelineStartSeconds, Math.Clamp(value, 0, max));
        }
    }
    public double TimelineViewSeconds => Math.Max(0.1, MixDurationSeconds / TimelineZoom);
    public GridLength ToolsColumnWidth => new(_toolsCollapsed ? 0 : 240);
    public GridLength FxColumnWidth => new(_fxCollapsed ? 0 : 280);
    public bool ToolsCollapsed { get => _toolsCollapsed; set { if (Set(ref _toolsCollapsed, value)) Raise(nameof(ToolsColumnWidth)); } }
    public bool FxCollapsed { get => _fxCollapsed; set { if (Set(ref _fxCollapsed, value)) Raise(nameof(FxColumnWidth)); } }
    public bool DiagnosticsEnabled
    {
        get => _diagnosticsEnabled;
        set
        {
            if (!Set(ref _diagnosticsEnabled, value)) return;
            if (value) { _lastDiagnosticCpu = _process.TotalProcessorTime; _diagnosticClock.Restart(); _timer.Start(); UpdateDiagnostics(); }
            else { DiagnosticsText = ""; if (!_recording && _player.State == PlayState.Stopped) _timer.Stop(); }
        }
    }
    public bool SnapSelection { get => _snapSelection; set => Set(ref _snapSelection, value); }
    public double SelectedStart { get => _selectedStart; set => Set(ref _selectedStart, FiniteNonNegative(value)); }
    public double SelectedEnd { get => _selectedEnd; set => Set(ref _selectedEnd, FiniteNonNegative(value)); }
    public double LoopStart { get => _loopStart; set { if (Set(ref _loopStart, FiniteNonNegative(value))) UpdateLoop(); } }
    public double LoopEnd { get => _loopEnd; set { if (Set(ref _loopEnd, FiniteNonNegative(value))) UpdateLoop(); } }
    public bool LoopEnabled
    {
        get => _loopEnabled;
        set
        {
            if (!Set(ref _loopEnabled, value)) return;
            if (!value) _player.ClearLoop();
            else UpdateLoop();
        }
    }
    public string SelectedSpeed { get => _selectedSpeed; set => Set(ref _selectedSpeed, value); }
    public int ExportBitrate => _exportBitrate;
    public int ExportBitrateIndex
    {
        get => _exportBitrate switch { 128000 => 0, 256000 => 2, _ => 1 };
        set
        {
            int bitrate = value switch { 0 => 128000, 1 => 192000, 2 => 256000, _ => _exportBitrate };
            if (Set(ref _exportBitrate, bitrate)) Raise(nameof(ExportBitrate));
        }
    }
    static double FiniteNonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    void UpdateLoop()
    {
        if (!LoopEnabled || _loaded is null) return;
        double start = Math.Clamp(LoopStart, 0, _loaded.Seconds);
        double end = Math.Clamp(LoopEnd, 0, _loaded.Seconds);
        if (end > start) _player.SetLoop(start, end);
        else _player.ClearLoop();
    }
    public bool VoiceListening
    {
        get => _voiceListening;
        private set
        {
            if (Set(ref _voiceListening, value)) Raise(nameof(VoiceButtonText));
        }
    }
    public string VoiceButtonText => VoiceListening ? "Stop Voice" : "Voice";
    public FxItem? SelectedFx { get => _fx; set => Set(ref _fx, value); }
    public TrackVM? SelectedTrack
    {
        get => _track;
        set
        {
            if (!Set(ref _track, value)) return;
        }
    }
    public ToolDef? SelectedTool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value)) return;
            Params.Clear();
            if (value is not null) foreach (var p in value.Params) Params.Add(new ParamVM(p));
            Raise(nameof(ToolDescription));
        }
    }
    public string ToolDescription => _tool is null ? "" : $"{_tool.Name} — {_tool.Description}";

    public ICommand ImportCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand CacheInfoCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand FitTimelineCommand { get; }
    public ICommand TrimSelectionCommand { get; }
    public ICommand ApplySpeedCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand VoiceCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BounceCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportVideoCommand { get; }
    public ICommand RemoveTrackCommand { get; }
    public ICommand AddFxCommand { get; }
    public ICommand AddLocalFxCommand { get; }
    public ICommand CreateFxCategoryCommand { get; }
    public ICommand MendJoinCommand { get; }
    public ICommand DownloadFxCommand { get; }
    public ICommand OpenSourceCommand { get; }

    public MainViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _player.Stopped += () =>
        {
            if (!_rec.IsRecording) StopLiveSpectrogram();
            if (!_rec.IsRecording && !DiagnosticsEnabled) _timer.Stop();
            UpdatePos();
            Status = "Stopped";
        };

        ImportCommand = new AsyncCommand(async _ =>
        {
            var d = new OpenFileDialog { Multiselect = true, Filter = AudioIO.OpenFilter };
            if (d.ShowDialog() == true) await ImportFilesAsync(d.FileNames);
        }, Fail, _ => !Busy);
        OpenProjectCommand = new AsyncCommand(_ => RunBusy("Open project", OpenProjectAsync), Fail, _ => !Busy);
        SaveProjectCommand = new AsyncCommand(_ => RunBusy("Save project", SaveProjectAsync), Fail, _ => !Busy && Tracks.Count > 0);
        CacheInfoCommand = new RelayCommand(_ => Status = $"Downloaded cache: {AppPaths.CacheBytes() / 1024.0 / 1024.0:F1} MB");
        ClearCacheCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show("Remove downloaded sound-effect cache files?", "Clear cache", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Status = $"Removed {AppPaths.ClearCache()} cached file(s).";
        });
        FitTimelineCommand = new RelayCommand(_ => { TimelineZoom = 1; TimelineStartSeconds = 0; });
        TrimSelectionCommand = new AsyncCommand(_ => TrimSelectionAsync(), Fail, _ => !Busy && SelectedTrack is not null && SelectedEnd > SelectedStart);
        ApplySpeedCommand = new AsyncCommand(_ => ApplySpeedAsync(), Fail, _ => !Busy && SelectedTrack is not null);
        PreviewCommand = new AsyncCommand(_ => PreviewAsync(), Fail, _ => !Busy && SelectedTrack is not null && SelectedTool is not null);
        PlayCommand = new AsyncCommand(_ => PlayAsync(), Fail);
        VoiceCommand = new RelayCommand(_ => ToggleVoiceListening());
        PauseCommand = new RelayCommand(_ => { _player.Pause(); Status = "Paused"; });
        StopCommand = new RelayCommand(_ => _player.Stop());
        RecordCommand = new AsyncCommand(_ => ToggleRecordAsync(), Fail);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(), Fail, _ => !Busy && SelectedTrack is not null && SelectedTool is not null);
        UndoCommand = new RelayCommand(_ => { if (SelectedTrack?.Undo() == true) Status = "Undone"; }, _ => SelectedTrack?.CanUndo == true);
        RedoCommand = new RelayCommand(_ => { if (SelectedTrack?.Redo() == true) Status = "Redone"; }, _ => SelectedTrack?.CanRedo == true);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel());
        BounceCommand = new AsyncCommand(_ => BounceAsync(), Fail, _ => !Busy && Tracks.Count > 0);
        ExportCommand = new AsyncCommand(_ => RunBusy("Export", ExportAsync), Fail, _ => !Busy && Tracks.Count > 0);
        ExportVideoCommand = new AsyncCommand(_ => RunBusy("Video export", ExportVideoAsync), Fail, _ => !Busy && SelectedTrack?.VideoSource is not null);
        RemoveTrackCommand = new RelayCommand(t => { if (t is TrackVM tv) RemoveTrack(tv); });
        AddFxCommand = new AsyncCommand(_ => RunBusy("FX render", async ct =>
        {
            var item = SelectedFx ?? throw new InvalidOperationException("Select a sound from the FX library first.");
            var buffer = await Task.Run(() => FxLibrary.Render(item), ct);
            AddTrack(item.Name, buffer, null);
            Status = $"Added FX: {item.Name}";
        }), Fail, _ => !Busy && SelectedFx is not null);
        MendJoinCommand = new AsyncCommand(_ => RunBusy("Mend join", MendJoinAsync), Fail,
            _ => !Busy && SelectedTrack is not null && SelectedFx is not null && Tracks.Count > 1);
        DownloadFxCommand = new AsyncCommand(_ => RunBusy("FX download", async ct =>
        {
            var path = await FxLibrary.DownloadAsync(FxUrl.Trim(), ct);
            AddTrack(Path.GetFileNameWithoutExtension(path), await AudioIO.LoadAsync(path, ct), null);
            Status = "FX downloaded and cached";
        }), Fail, _ => !Busy && FxUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        AddLocalFxCommand = new AsyncCommand(async _ =>
        {
            if (FxLibrary.UserAudioCount >= FxLibrary.MaxUserAudio) throw new InvalidOperationException($"The FX Library limit is {FxLibrary.MaxUserAudio} user audio clips.");
            string category = FxLibrary.ValidateUserCategory(UserFxCategory);
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Audio files|*.wav;*.mp3;*.aif;*.aiff;*.m4a;*.aac;*.wma;*.flac;*.ogg;*.opus|All files|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            await RunBusy("Add audio to FX library", async ct =>
            {
                foreach (string path in dialog.FileNames)
                {
                    ct.ThrowIfCancellationRequested();
                    if (FxLibrary.UserAudioCount >= FxLibrary.MaxUserAudio) throw new InvalidOperationException($"The FX Library limit is {FxLibrary.MaxUserAudio} user audio clips; the remaining files were not added.");
                    if (AudioIO.IsVideo(path)) throw new InvalidOperationException("Choose an audio file, not a video file.");
                    AudioBuffer buffer = await AudioIO.LoadAsync(path, ct);
                    var item = await Task.Run(() => FxLibrary.ImportUserAudio(path, buffer, category), ct);
                    SelectedFx = item;
                }
                RefreshFxCategories();
                RefreshFxItems();
                CommandManager.InvalidateRequerySuggested();
                Status = $"Added {dialog.FileNames.Length} audio file(s) to FX category “{category}”";
            });
        }, Fail, _ => !Busy && FxLibrary.UserAudioCount < FxLibrary.MaxUserAudio);
        CreateFxCategoryCommand = new AsyncCommand(_ =>
        {
            string category = FxLibrary.CreateUserCategory(NewFxCategoryName);
            RefreshFxCategories();
            UserFxCategory = category;
            NewFxCategoryName = "";
            Status = $"Created FX category “{category}”";
            return Task.CompletedTask;
        }, Fail, _ => !Busy && !string.IsNullOrWhiteSpace(NewFxCategoryName));
        OpenSourceCommand = new RelayCommand(u => { if (u is string s) Process.Start(new ProcessStartInfo(s) { UseShellExecute = true }); });

        RefreshFxItems();
        _ = LoadUserFxAsync();
        SelectedTool = Tools[0];
    }

    void ToggleVoiceListening()
    {
        if (VoiceListening)
        {
            _speech?.Stop();
            VoiceListening = false;
            Status = "Voice commands stopped";
            return;
        }

        try
        {
            _speech ??= new SpeechRecognitionService(CultureInfo.GetCultureInfo("en-GB"));
            _speech.CommandRecognized -= OnVoiceCommandRecognized;
            _speech.CommandRecognized += OnVoiceCommandRecognized;
            _speech.ListeningError -= OnVoiceListeningError;
            _speech.ListeningError += OnVoiceListeningError;
            _speech.InputLevelChanged -= OnVoiceInputLevelChanged;
            _speech.InputLevelChanged += OnVoiceInputLevelChanged;
            _speech.ListeningStopped -= OnVoiceListeningStopped;
            _speech.ListeningStopped += OnVoiceListeningStopped;
            _speech.Start();
            VoiceListening = true;
            Status = "Listening in English (UK). Try ‘play’, ‘import audio’, ‘load song’ plus a track name, or ‘mute track’.";
        }
        catch (Exception ex)
        {
            _speech?.Dispose();
            _speech = null;
            VoiceListening = false;
            Fail(ex);
        }
    }

    void OnVoiceInputLevelChanged(float level)
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) VoiceInputLevel = level;
        });
    }

    void OnVoiceListeningStopped()
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            VoiceListening = false;
            VoiceInputLevel = 0;
        });
    }

    void OnVoiceListeningError(Exception exception)
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            VoiceListening = false;
            Fail(exception);
        });
    }

    void OnVoiceCommandRecognized(string phrase)
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) ExecuteVoiceCommand(phrase);
        });
    }

    void ExecuteVoiceCommand(string phrase)
    {
        string command = phrase.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
        if (command.StartsWith("load song ", StringComparison.Ordinal))
        {
            SelectTrackByVoiceName(command[10..]);
            return;
        }
        if (command.StartsWith("play song ", StringComparison.Ordinal))
        {
            if (SelectTrackByVoiceName(command[10..])) PlayCommand.Execute(null);
            return;
        }

        switch (command)
        {
            case "open file explorer": OpenExplorer(null); break;
            case "open folder explorer": OpenExplorer("shell:MyComputerFolder"); break;
            case "open downloads": OpenExplorer("shell:Downloads"); break;
            case "open documents": OpenExplorer(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)); break;
            case "open music": OpenExplorer(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)); break;
            case "open pictures": OpenExplorer(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)); break;
            case "import audio":
            case "open audio": ImportCommand.Execute(null); break;
            case "play": PlayCommand.Execute(null); break;
            case "pause": PauseCommand.Execute(null); break;
            case "stop": StopCommand.Execute(null); break;
            case "record":
            case "finish recording": RecordCommand.Execute(null); break;
            case "undo": UndoCommand.Execute(null); break;
            case "redo": RedoCommand.Execute(null); break;
            case "apply effect": ApplyCommand.Execute(null); break;
            case "bounce mix": BounceCommand.Execute(null); break;
            case "export audio": ExportCommand.Execute(null); break;
            case "export video": ExportVideoCommand.Execute(null); break;
            case "cancel": CancelCommand.Execute(null); break;
            case "mute track": SetTrackMute(true); break;
            case "unmute track": SetTrackMute(false); break;
            case "solo track": SetTrackSolo(true); break;
            case "unsolo track": SetTrackSolo(false); break;
            case "remove track":
            case "delete track":
                if (SelectedTrack is { } track) RemoveTrack(track);
                else Status = "Select a track first.";
                break;
            case "next track": SelectAdjacentTrack(1); break;
            case "previous track": SelectAdjacentTrack(-1); break;
            case "add fx": AddFxCommand.Execute(null); break;
            case "mend join": MendJoinCommand.Execute(null); break;
            case "help": Status = "Voice: open folders, import audio, play/pause/stop, load song <name>, mute/solo/remove track, next/previous, apply effect, undo/redo, add FX, mend join, bounce, export, stop listening."; break;
            case "stop listening": ToggleVoiceListening(); break;
            default: Status = $"Voice command not recognised: {phrase}"; break;
        }
    }

    bool SelectTrackByVoiceName(string name)
    {
        string search = name.Trim();
        if (search.Length == 0)
        {
            Status = "Say ‘load song’ followed by a track name.";
            return false;
        }

        var matches = Tracks.Where(track => string.Equals(track.Name, search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = [.. Tracks.Where(track => track.Name.Contains(search, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count == 1)
        {
            SelectedTrack = matches[0];
            Status = $"Selected track: {matches[0].Name}";
            return true;
        }
        Status = matches.Count == 0 ? $"No uploaded track matches ‘{search}’." : $"More than one track matches ‘{search}’; say a more specific name.";
        return false;
    }

    void SetTrackMute(bool muted)
    {
        if (SelectedTrack is not { } track) { Status = "Select a track first."; return; }
        track.Mute = muted;
        Status = muted ? $"Muted {track.Name}" : $"Unmuted {track.Name}";
    }

    void SetTrackSolo(bool solo)
    {
        if (SelectedTrack is not { } track) { Status = "Select a track first."; return; }
        track.Solo = solo;
        Status = solo ? $"Soloed {track.Name}" : $"Unsoloed {track.Name}";
    }

    void SelectAdjacentTrack(int direction)
    {
        int index = SelectedTrack is null ? -1 : Tracks.IndexOf(SelectedTrack);
        int next = index + direction;
        if ((uint)next >= (uint)Tracks.Count) { Status = "There is no track in that direction."; return; }
        SelectedTrack = Tracks[next];
        Status = $"Selected track: {SelectedTrack.Name}";
    }

    static void OpenExplorer(string? path)
    {
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(path)) startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    async Task OpenProjectAsync(CancellationToken ct)
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"Black Star project (*.bssa)|*.bssa|{AudioIO.OpenFilter}",
            DefaultExt = ".bssa",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        string[] selectedFiles = dialog.FileNames;
        string[] projectFiles = [.. selectedFiles.Where(path => string.Equals(Path.GetExtension(path), ".bssa", StringComparison.OrdinalIgnoreCase))];
        if (projectFiles.Length == 0)
        {
            await ImportFilesCoreAsync(selectedFiles, ct);
            return;
        }
        if (selectedFiles.Length != 1)
            throw new InvalidOperationException("Open one project at a time. Select audio or video files together to import them as tracks.");

        var snapshot = await ProjectService.LoadAsync(projectFiles[0], ct);
        _player.Stop();
        foreach (var track in Tracks.ToArray()) { track.Edited -= Invalidate; track.Dispose(); }
        Tracks.Clear();
        foreach (var item in snapshot.Tracks)
        {
            var track = AddTrack(item.Name, item.Buffer, item.VideoSource, item.SourcePath);
            track.RestoreState(item.Offset, item.GainDb, item.Pan, item.Mute, item.Solo);
        }
        _mix = null;
        _loaded = null;
        PositionSeconds = 0;
        TimelineStartSeconds = 0;
        Raise(nameof(MixDurationSeconds));
        Raise(nameof(TimelineViewSeconds));
        Status = $"Opened {Path.GetFileName(projectFiles[0])}";
    }

    async Task SaveProjectAsync(CancellationToken ct)
    {
        var dialog = new SaveFileDialog { Filter = "Black Star project (*.bssa)|*.bssa", DefaultExt = ".bssa", FileName = "Black Star Project" };
        if (dialog.ShowDialog() != true) return;
        var tracks = Tracks.Select(track => new ProjectTrack(track.Name, track.Offset, track.GainDb, track.Pan, track.Mute, track.Solo, track.VideoSource, track.SourcePath, track.Buffer)).ToArray();
        await ProjectService.SaveAsync(new ProjectSnapshot(tracks), dialog.FileName, ct);
        Status = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    async Task LoadUserFxAsync()
    {
        var paths = FxLibrary.UserAudioFiles;
        foreach (string path in paths.Take(FxLibrary.MaxUserAudio))
        {
            try
            {
                var buffer = await AudioIO.LoadAsync(path);
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length > 33 && name[^33] == '_' && Guid.TryParseExact(name[^32..], "N", out _)) name = name[..^33];
                string? directory = Path.GetDirectoryName(path);
                string category = string.Equals(directory, AppPaths.UserFx, StringComparison.OrdinalIgnoreCase)
                    ? "My Audio"
                    : Path.GetFileName(directory) ?? "My Audio";
                FxLibrary.RegisterUserAudio(name, buffer, category);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("FxLibrary", ex, $"Could not load {Path.GetFileName(path)}");
                Status = $"Error loading FX file {Path.GetFileName(path)}: {ex.Message}";
            }
        }
        if (paths.Count > FxLibrary.MaxUserAudio)
        {
            Log.Warn("FxLibrary", $"Only the first {FxLibrary.MaxUserAudio} user audio clips were loaded; the library limit is {FxLibrary.MaxUserAudio}.");
            Status = $"FX Library limit reached: loaded {FxLibrary.MaxUserAudio} of {paths.Count} clips.";
        }
        RefreshFxCategories();
        RefreshFxItems();
        CommandManager.InvalidateRequerySuggested();
    }

    void RefreshFxItems()
    {
        FilteredFxItems.Clear();
        foreach (var item in FxLibrary.Items)
            if (FxCategory == "All sounds" || string.Equals(item.Category, FxCategory, StringComparison.Ordinal))
                FilteredFxItems.Add(item);
        if (SelectedFx is not null && !FilteredFxItems.Contains(SelectedFx)) SelectedFx = null;
    }

    async Task MendJoinAsync(CancellationToken ct)
    {
        var selected = SelectedTrack ?? throw new InvalidOperationException("Select the first track in the join.");
        var selectedFx = SelectedFx ?? throw new InvalidOperationException("Select a bridge sound from the FX library.");
        int index = Tracks.IndexOf(selected);
        if (index < 0 || Tracks.Count < 2) throw new InvalidOperationException("A join requires two adjacent tracks.");
        TrackVM first = index + 1 < Tracks.Count ? selected : Tracks[index - 1];
        TrackVM second = index + 1 < Tracks.Count ? Tracks[index + 1] : selected;
        AudioBuffer firstBuffer = first.Buffer, secondBuffer = second.Buffer;
        Status = $"Mending {first.Name} → {second.Name} with {selectedFx.Name}…";
        var mended = await Task.Run(() => Mixer.MendJoin(firstBuffer, secondBuffer, FxLibrary.Render(selectedFx), ct), ct);
        ct.ThrowIfCancellationRequested();
        if (!Tracks.Contains(first) || !Tracks.Contains(second) || !ReferenceEquals(first.Buffer, firstBuffer) || !ReferenceEquals(second.Buffer, secondBuffer))
            throw new InvalidOperationException("The selected tracks changed before the mend could be applied.");
        first.Replace(mended);
        RemoveTrack(second);
        SelectedTrack = first;
        Status = $"Mended join with {selectedFx.Name}";
    }

    void Fail(Exception ex) { Log.Error("Command", ex); Status = $"Error: {ex.Message}"; }
    void Invalidate()
    {
        _mix = null;
        Interlocked.Increment(ref _mixVersion);
        Raise(nameof(MixDurationSeconds));
        Raise(nameof(TimelineViewSeconds));
        TimelineStartSeconds = _timelineStartSeconds;
    }

    async Task RunBusy(string what, Func<CancellationToken, Task> f)
    {
        if (Busy) return;
        Busy = true; _cts = new CancellationTokenSource();
        try { await f(_cts.Token); }
        catch (OperationCanceledException) { Status = $"{what} cancelled"; }
        finally { Busy = false; _cts?.Dispose(); _cts = null; }
    }

    public Task ImportFilesAsync(IEnumerable<string> paths) =>
        RunBusy("Import", ct => ImportFilesCoreAsync(paths, ct));

    async Task ImportFilesCoreAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            Status = $"Loading {Path.GetFileName(path)}…";
            try { AddTrack(Path.GetFileNameWithoutExtension(path), await AudioIO.LoadAsync(path, ct), AudioIO.IsVideo(path) ? path : null, path); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Fail(ex); }
        }
        if (!Status.StartsWith("Error", StringComparison.Ordinal)) Status = $"Loaded {Tracks.Count} track(s)";
    }

    TrackVM AddTrack(string name, AudioBuffer b, string? video, string? sourcePath = null)
    {
        var t = new TrackVM(name, b, video, sourcePath); t.Edited += Invalidate;
        Tracks.Add(t); SelectedTrack = t; Invalidate(); Raise(nameof(MixDurationSeconds)); Raise(nameof(TimelineViewSeconds)); return t;
    }

    void RemoveTrack(TrackVM t)
    {
        t.Edited -= Invalidate; Tracks.Remove(t); t.Dispose(); Invalidate();
        if (ReferenceEquals(SelectedTrack, t)) SelectedTrack = Tracks.LastOrDefault();
    }

    public void ApplyToolTo(TrackVM t, ToolDef tool) { SelectedTrack = t; SelectedTool = tool; ApplyCommand.Execute(null); }

    Task ApplyAsync() => RunBusy("Apply", async ct =>
    {
        var t = SelectedTrack!; var tool = SelectedTool!;
        TrackVM? other = null; bool otherBefore = false;
        if (tool.NeedsSecondary)
        {
            int i = Tracks.IndexOf(t);
            if (i + 1 < Tracks.Count) other = Tracks[i + 1]; else if (i > 0) { other = Tracks[i - 1]; otherBefore = true; }
            if (other is null) { Status = "Merge needs at least two tracks."; return; }
        }
        var p = Params.ToDictionary(x => x.Key, x => x.Value);
        (AudioBuffer a, AudioBuffer o) = (t.Buffer, other?.Buffer ?? t.Buffer);
        if (otherBefore) (a, o) = (o, a);
        Status = $"Applying {tool.Name}…";
        var res = await Task.Run(() => tool.Run(a, p, other is null ? null : o, ct), ct);
        t.Replace(res);
        if (other is not null) RemoveTrack(other);
        Status = $"{tool.Name} applied to “{t.Name}”";
    });

    // ---- mixing / transport ----
    List<MixItem> Snapshot()
    {
        bool anySolo = Tracks.Any(t => t.Solo);
        return [.. Tracks.Where(t => anySolo ? t.Solo : !t.Mute).Select(t => new MixItem(t.Buffer, t.Offset, (float)Math.Pow(10, t.GainDb / 20), (float)t.Pan))];
    }

    async Task<AudioBuffer?> GetMixAsync(CancellationToken ct = default)
    {
        if (_mix is not null) return _mix;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long version = Volatile.Read(ref _mixVersion);
            var snap = Snapshot();
            var mix = await Task.Run(() => Mixer.Mixdown(snap, ct), ct);
            if (version != Volatile.Read(ref _mixVersion)) continue;
            _mix = mix;
            return mix;
        }
    }

    async Task PlayAsync()
    {
        if (_player.State == PlayState.Paused)
        {
            if (!SpectrogramLive) StartLiveSpectrogram();
            _player.Play();
            _timer.Start();
            Status = "Playing";
            return;
        }
        if (_player.State == PlayState.Playing) return;
        var mix = await GetMixAsync();
        if (mix is null || mix.Frames == 0) { Status = "Nothing to play."; return; }
        if (!ReferenceEquals(mix, _loaded)) { _player.Load(mix); _loaded = mix; }
        if (LoopEnabled) UpdateLoop();
        StartLiveSpectrogram();
        _player.Play(); _timer.Start(); Status = "Playing";
    }

    async Task ToggleRecordAsync()
    {
        if (!_rec.IsRecording)
        {
            _player.Stop();
            _rec.Start();
            StartLiveSpectrogram();
            IsRecording = true; Raise(nameof(SpectrogramCaption)); _timer.Start(); Status = "Recording… press Record again to finish.";
            return;
        }
        var buf = await _rec.StopAsync(); IsRecording = false; Level = 0;
        StopLiveSpectrogram();
        Raise(nameof(SpectrogramCaption));
        if (buf is not null) { AddTrack($"Recording {DateTime.Now:HH-mm-ss}", buf, null); Status = "Recording added as a new track"; }
    }

    Task TrimSelectionAsync() => RunBusy("Trim", async ct =>
    {
        var track = SelectedTrack ?? throw new InvalidOperationException("Select a track first.");
        AudioBuffer source = track.Buffer;
        double start = SelectedStart, end = SelectedEnd;
        if (SnapSelection) { start = Math.Round(start * 10) / 10; end = Math.Round(end * 10) / 10; }
        if (end <= start || start >= source.Seconds) throw new InvalidOperationException("Selection must be a non-empty range within the track.");
        int first = Math.Clamp((int)(start * source.Rate), 0, source.Frames);
        int last = Math.Clamp((int)(end * source.Rate), first, source.Frames);
        AudioBuffer trimmed = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var data = new float[(last - first) * source.Channels];
            Array.Copy(source.Data, first * source.Channels, data, 0, data.Length);
            ct.ThrowIfCancellationRequested();
            return source.With(data);
        }, ct);
        if (!ReferenceEquals(track.Buffer, source)) throw new InvalidOperationException("The selected track changed before trimming completed.");
        track.Replace(trimmed);
        SelectedStart = 0;
        SelectedEnd = trimmed.Seconds;
        Status = $"Trimmed selection on “{track.Name}”";
    });

    Task ApplySpeedAsync() => RunBusy("Set speed", async ct =>
    {
        var track = SelectedTrack ?? throw new InvalidOperationException("Select a track first.");
        string factorText = SelectedSpeed.Replace("×", "", StringComparison.Ordinal).Trim();
        if (!double.TryParse(factorText, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) || factor <= 0)
            throw new InvalidOperationException("Choose a valid speed preset.");
        var tool = Tools.First(item => item.Name == "Speed");
        var result = await Task.Run(() => tool.Run(track.Buffer, new Dictionary<string, double> { ["factor"] = factor }, null, ct), ct);
        track.Replace(result);
        Status = $"Speed set to {SelectedSpeed}";
    });

    Task PreviewAsync() => RunBusy("Preview", async ct =>
    {
        var track = SelectedTrack ?? throw new InvalidOperationException("Select a track first.");
        var tool = SelectedTool ?? throw new InvalidOperationException("Select an effect first.");
        AudioBuffer? other = null;
        if (tool.NeedsSecondary)
        {
            int index = Tracks.IndexOf(track);
            other = (index + 1 < Tracks.Count ? Tracks[index + 1].Buffer : index > 0 ? Tracks[index - 1].Buffer : null) ?? throw new InvalidOperationException($"{tool.Name} needs a second track to preview.");
        }
        var parameters = Params.ToDictionary(parameter => parameter.Key, parameter => parameter.Value);
        var result = await Task.Run(() => tool.Run(track.Buffer, parameters, other, ct), ct);
        _player.Load(result);
        _loaded = null;
        StartLiveSpectrogram();
        _player.Play();
        _timer.Start();
        Status = $"Previewing {tool.Name} on “{track.Name}”";
    });

    void StartLiveSpectrogram()
    {
        _spectrogramLiveSequence = 0;
        _spectrogramLiveClock.Restart();
        SpectrogramLiveColumn = null;
        SpectrogramLive = true;
    }

    void StopLiveSpectrogram()
    {
        SpectrogramLive = false;
        _spectrogramLiveSequence = 0;
        SpectrogramLiveColumn = null;
    }

    void UpdateLiveSpectrogram(AudioBuffer? source)
    {
        if (source is null || _spectrogramLiveClock.Elapsed < TimeSpan.FromMilliseconds(50)) return;
        _spectrogramLiveClock.Restart();
        double centerSeconds = Math.Max(0, source.Seconds - (double)(SpectrogramAnalysis.FftSize / 2) / source.Rate);
        SpectrogramLiveColumn = SpectrogramAnalysis.ComputeLiveColumn(source, centerSeconds, _spectrogramLiveSequence++);
    }

    void OnTick(object? s, EventArgs e)
    {
        if (_diagnosticsEnabled && _diagnosticClock.Elapsed >= TimeSpan.FromSeconds(1)) UpdateDiagnostics();
        if (_rec.IsRecording)
        {
            var recent = _rec.GetRecentAudio();
            if (recent.Frames > 0) UpdateLiveSpectrogram(recent);
            Level = _rec.Level;
            PositionText = $"{Fmt(_rec.Seconds)}  ● REC";
            return;
        }
        if (_player.State == PlayState.Playing)
            UpdateLiveSpectrogram(_player.GetRecentAudio());
        Level = 0; UpdatePos();
        if (_player.State == PlayState.Stopped && !DiagnosticsEnabled) _timer.Stop();
    }

    void UpdateDiagnostics()
    {
        _process.Refresh();
        TimeSpan elapsed = _diagnosticClock.Elapsed;
        TimeSpan cpu = _process.TotalProcessorTime;
        double cpuPercent = elapsed.TotalMilliseconds <= 0 ? 0 : (cpu - _lastDiagnosticCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        _lastDiagnosticCpu = cpu;
        _diagnosticClock.Restart();
        long privateBytes = _process.PrivateMemorySize64;
        long managedBytes = GC.GetTotalMemory(false);
        DiagnosticsText = $"CPU {cpuPercent:F0}%  Private {privateBytes / 1024.0 / 1024:F0} MB  Managed {managedBytes / 1024.0 / 1024:F0} MB";
    }

    void UpdatePos()
    {
        PositionSeconds = _player.Seconds;
        PositionText = $"{Fmt(_player.Seconds)} / {Fmt(_loaded?.Seconds ?? 0)}";
    }
    static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

    async Task BounceAsync()
    {
        Status = "Bouncing mixdown…";
        var m = await GetMixAsync();
        if (m is not null) { AddTrack("Mixdown", m, null); Status = "Mixdown added as a new track"; }
    }

    async Task ExportAsync(CancellationToken ct)
    {
        var mix = await GetMixAsync(ct);
        if (mix is null || mix.Frames == 0) { Status = "Nothing to export."; return; }
        var d = new SaveFileDialog { Filter = AudioIO.SaveFilter, FileName = "Black Star Mix" };
        if (d.ShowDialog() != true) return;
        Status = "Exporting…"; await AudioIO.SaveAsync(mix, d.FileName, ExportBitrate, ct); Status = $"Exported {Path.GetFileName(d.FileName)}";
    }

    async Task ExportVideoAsync(CancellationToken ct)
    {
        var video = SelectedTrack?.VideoSource; var mix = await GetMixAsync(ct);
        if (video is null || mix is null) return;
        var d = new SaveFileDialog { Filter = "MP4 video (*.mp4)|*.mp4|Matroska (*.mkv)|*.mkv", FileName = "Black Star Video" };
        if (d.ShowDialog() != true) return;
        var tmp = Path.Combine(AppPaths.Temp, $"v-{Guid.NewGuid():N}.wav");
        try
        {
            Status = "Muxing edited audio into video…";
            await AudioIO.SaveAsync(mix, tmp, ct: ct);
            await Ffmpeg.MuxAsync(video, tmp, d.FileName, ct);
            Status = $"Exported {Path.GetFileName(d.FileName)}";
        }
        finally { AppPaths.TryDelete(tmp); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _speech?.CommandRecognized -= OnVoiceCommandRecognized;
        _speech?.ListeningError -= OnVoiceListeningError;
        _speech?.InputLevelChanged -= OnVoiceInputLevelChanged;
        _speech?.ListeningStopped -= OnVoiceListeningStopped;
        _speech?.Dispose();
        _speech = null;
        _timer.Stop(); _timer.Tick -= OnTick;
        _process.Dispose();
        _player.Dispose(); _rec.Dispose();
        foreach (var t in Tracks) { t.Edited -= Invalidate; t.Dispose(); }
        Tracks.Clear(); _mix = null; _loaded = null;
    }
}
