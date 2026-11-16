[README.md](https://github.com/user-attachments/files/34928864/README.md)
# Black Star Swiss Audio

WPF / .NET 10 multitrack audio editor with a Lament-Configuration-inspired black-lacquer-and-gold UI.

## Build
1. Install the .NET 10 SDK and Windows 10/11 (x64).
2. `dotnet restore && dotnet build -c Release`
3. Single-folder release: `dotnet publish -c Release -r win-x64 --self-contained -o publish`
4. Video import beyond Windows Media Foundation-supported MP4/M4V, FLAC/OGG/Opus, and video export need FFmpeg: put `ffmpeg.exe` in `ffmpeg/` next to the exe, or on PATH.

## Features
- Drag & drop audio/video files; multitrack timeline with per-track gain, pan, mute, solo, start offset.
- 35 audio tools, including Cut, Merge, fades, EQ, dynamics, modulation, stereo tools, and saturation. Drag a tool onto a track or select it and press Apply.
- Record / Play / Pause / Stop; Bounce Mix; export WAV, MP3 (Media Foundation), FLAC/OGG/Opus/AAC (FFmpeg); export edited audio muxed into the source video.
- FX library: 100 built-in procedural sounds across categories, cached HTTPS downloader, links to free archives (check each licence).
- Live-feed-only spectrogram built from samples sent to audio playback or captured from the microphone; it stays blank until playback/recording begins and analyzes stereo channels independently to avoid phase cancellation.
- Optional English (UK) speech recognition using NAudio microphone capture (16 kHz, mono, PCM16), bounded audio buffering, live mic-level feedback, and transport, import/export, track selection/management, effect actions, and common-folder commands. Recognition requires an installed Windows English (UK) speech recognizer and microphone; voice listening starts only when enabled.
- Optional startup WAV: place a recording in the project `Assets` folder. The first WAV found is played before the main window appears; missing, invalid, or unavailable audio playback never prevents launch.

### Voice commands

Enable voice commands with the **Voice** button, then use phrases such as `open downloads`, `open documents`, `open music`, `open pictures`, `open file explorer`, `import audio`, `play`, `pause`, `stop`, `record`, `load song <track name>`, `play song <track name>`, `mute track`, `solo track`, `remove track`, `next track`, `apply effect`, `undo`, `redo`, `add fx`, `mend join`, `bounce mix`, `export audio`, `export video`, and `stop listening`. The startup recording supplies the spoken welcome voice; the application does not synthesize a particular voice.
- Timeline ruler, horizontal zoom/pan, fit-to-project, stereo waveforms, seekable playhead, loop range, time selection/trim, snapping, and playback speed presets.
- Save/open `.bssa` projects with embedded track audio and mixer settings; periodic autosave with startup recovery prompt.
- Eight-step undo/redo, effect preview, import failure summaries/cancellation, export preflight and MP3 bitrate choices.
- Resizable/collapsible side panels, dark/light palettes, accent colors, keyboard shortcuts, focus states, and accessibility labels.
- Per-track peak/RMS meters and clipping indicators; optional memory/track diagnostics and cache/temp usage controls.

## Operations
- Logs: `%LOCALAPPDATA%\BlackStarSwissAudio\logs` (14-day rolling, per-process files). Cache is size/age pruned. Temp files use per-process folders; current files are removed on exit and folders from exited processes are pruned on startup.
- Re-importing unchanged media reuses a bounded weak-reference decode cache; buffers are not pinned after tracks, undo history, and playback release them.
- Audio is held as float PCM in RAM (about 21 MB per stereo minute at 44.1 kHz); undo keeps 8 steps per track by reference. Very large mixdowns and effects still require in-memory buffers; chunked/off-disk processing and editable non-destructive effect chains are not yet implemented.
