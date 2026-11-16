// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using BlackStarSwissAudio.Core;

namespace BlackStarSwissAudio.Services;

public sealed record ProjectTrack(string Name, double Offset, double GainDb, double Pan, bool Mute, bool Solo, string? VideoSource, string? SourcePath, AudioBuffer Buffer);
public sealed record ProjectSnapshot(IReadOnlyList<ProjectTrack> Tracks);

public static class ProjectService
{
    sealed record ProjectDocument(int Version, List<ProjectTrackMetadata> Tracks);
    sealed record ProjectTrackMetadata(string Name, double Offset, double GainDb, double Pan, bool Mute, bool Solo, string? VideoSource, string? SourcePath, string AudioEntry);

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task SaveAsync(ProjectSnapshot snapshot, string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Project path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        var document = new ProjectDocument(1, []);

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                for (int i = 0; i < snapshot.Tracks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var track = snapshot.Tracks[i];
                    string entryName = $"audio/{i:D4}.wav";
                    string audioPath = Path.Combine(AppPaths.Temp, $"project-save-{Guid.NewGuid():N}.wav");
                    try
                    {
                        await AudioIO.SaveFloatWaveAsync(track.Buffer, audioPath, ct);
                        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                        await using var entryStream = entry.Open();
                        await using var audioStream = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        await audioStream.CopyToAsync(entryStream, ct);
                    }
                    finally { AppPaths.TryDelete(audioPath); }
                    document.Tracks.Add(new ProjectTrackMetadata(track.Name, track.Offset, track.GainDb, track.Pan, track.Mute, track.Solo, track.VideoSource, track.SourcePath, entryName));
                }

                var metadata = archive.CreateEntry("project.json", CompressionLevel.Fastest);
                await using var metadataStream = metadata.Open();
                await JsonSerializer.SerializeAsync(metadataStream, document, JsonOptions, ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, true);
        }
        finally { AppPaths.TryDelete(temporaryPath); }
    }

    public static async Task<ProjectSnapshot> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var archive = ZipFile.OpenRead(path);
        var metadataEntry = archive.GetEntry("project.json") ?? throw new InvalidDataException("Project metadata is missing.");
        ProjectDocument? document;
        await using (var metadataStream = metadataEntry.Open())
            document = await JsonSerializer.DeserializeAsync<ProjectDocument>(metadataStream, JsonOptions, ct);
        if (document is null || document.Version != 1) throw new InvalidDataException("Unsupported or invalid project file.");

        var tracks = new List<ProjectTrack>(document.Tracks.Count);
        foreach (var item in document.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(item.AudioEntry) ?? throw new InvalidDataException($"Project audio is missing: {item.AudioEntry}");
            string extractedPath = Path.Combine(AppPaths.Temp, $"project-{Guid.NewGuid():N}.wav");
            try
            {
                await using (var source = entry.Open())
                await using (var destination = new FileStream(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await source.CopyToAsync(destination, ct);
                var buffer = await AudioIO.LoadAsync(extractedPath, ct);
                tracks.Add(new ProjectTrack(item.Name, item.Offset, item.GainDb, item.Pan, item.Mute, item.Solo, item.VideoSource, item.SourcePath, buffer));
            }
            finally { AppPaths.TryDelete(extractedPath); }
        }
        return new ProjectSnapshot(tracks);
    }
}
