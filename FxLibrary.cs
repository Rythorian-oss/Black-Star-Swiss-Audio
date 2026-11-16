// Created by: Justin L. Ross | Waterville, Me. | Nov. 2026
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BlackStarSwissAudio.Core;

namespace BlackStarSwissAudio.Services;

public sealed record FxItem(string Name, string Category, Func<float[]>? Make = null, AudioBuffer? Buffer = null);
public sealed record FxSource(string Name, string Url, string Note);

/// <summary>
/// Built-in procedural FX (zero download, zero licensing risk), plus a cached URL downloader and
/// links to free sound archives. Always confirm each file's licence before redistributing.
/// </summary>
public static class FxLibrary
{
    const int SR = 44100;
    public const int MaxUserAudio = 500;
    const string DefaultUserCategory = "My Audio";
    static readonly string[] UserAudioExtensions = [".wav", ".mp3", ".aif", ".aiff", ".m4a", ".aac", ".wma", ".flac", ".ogg", ".opus"];
    static readonly ConcurrentDictionary<string, WeakReference<AudioBuffer>> Memo = new();
    static readonly List<FxItem> UserItems = [];
    static readonly Lock UserItemsLock = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static IReadOnlyList<FxSource> Sources { get; } =
    [
        new("BBC Sound Effects", "https://sound-effects.bbcrewind.co.uk", "Large archive, RemArc licence (personal/education/research)"),
        new("Freesound", "https://freesound.org", "Community library, per-file Creative Commons licences"),
        new("Pixabay Sound Effects", "https://pixabay.com/sound-effects/", "Pixabay content licence"),
        new("Mixkit Free SFX", "https://mixkit.co/free-sound-effects/", "Mixkit licence"),
        new("Wikimedia Commons", "https://commons.wikimedia.org/wiki/Category:Sound_effects", "Mostly CC / public domain"),
        new("Internet Archive Audio", "https://archive.org/details/audio", "Mixed licences, check each item"),
    ];

    static readonly (string Category, string[] Names)[] PresetGroups =
    [
        ("Haunted Winds", ["Cemetery Gust", "Frozen Corridor", "Whistling Eaves", "Cavern Draft", "Winter Window", "Hollow Passage", "Storm Through Stone", "Distant Gale", "Breath of the Vault"]),
        ("Ghostly Voices", ["Veiled Choir", "Phantom Murmur", "Distant Lament", "Spectral Vowels", "Whispering Room", "Lost Chorus", "Ethereal Call", "Wraith Breath", "Shivering Whisper"]),
        ("Chimes & Bells", ["Glass Constellation", "Midnight Chime", "Frost Bell", "Distant Carillon", "Silver Drops", "Broken Music Box", "Hollow Bell Array", "Celestial Tines", "Toll in the Fog"]),
        ("Strings & Violin", ["Lone String Drift", "Haunted Violin", "Bowed Glass", "Low Cello Shadow", "Dissonant Quartet", "Singing Wire", "Muted Viola Pulse", "Harmonic Bow", "Nocturne String"]),
        ("Dark Drones", ["Black Horizon", "Subterranean Bloom", "Abyssal Choir", "Slow Orbit", "Basalt Resonance", "Endless Room", "Low Nebula", "Iron Cathedral", "Pressure Below"]),
        ("Cinematic Impacts", ["Stone Door Slam", "Deep Trailer Hit", "Metallic Collapse", "Sub Impact", "Reverse Strike", "Cracked Monolith", "Ritual Thud", "Distant Explosion", "Granite Fall"]),
        ("Textures & Static", ["Analog Snow", "Tape Flutter", "Electric Dust", "Radio Ash", "Granular Hiss", "Signal Fracture", "Vinyl Storm", "Data Rain", "Crackling Current"]),
        ("Creatures & Whispers", ["Cave Squeal", "Night Creature", "Wingbeat Flutter", "Underwater Clicks", "Insect Chorus", "Beast Exhale", "Strange Call", "Feral Chitter", "Glass Growl"]),
        ("Risers & Transitions", ["Slow Reveal", "Tension Ascent", "Reverse Air", "Threshold Sweep", "Ritual Lift", "Falling Star", "Pressure Release", "Shimmering Rise", "Portal Bloom"]),
    ];

    static readonly IReadOnlyList<FxItem> BuiltInItems = CreateItems();
    public static IReadOnlyList<FxItem> Items
    {
        get { lock (UserItemsLock) return [.. BuiltInItems, .. UserItems]; }
    }

    public static int UserAudioCount => EnumerateUserAudioFiles().Length;

    public static IReadOnlyList<string> UserCategories
    {
        get
        {
            var categories = Directory.EnumerateDirectories(AppPaths.UserFx)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            categories.Add(DefaultUserCategory);
            return [.. categories.Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    public static IReadOnlyList<string> UserAudioFiles => EnumerateUserAudioFiles();

    public static string ValidateUserCategory(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        string name = category.Trim();
        if (name.Length > 64 || name is "." or ".." || name.Equals("All sounds", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Use a category name up to 64 characters without path separators or reserved characters.", nameof(category));
        return name;
    }

    public static string CreateUserCategory(string category)
    {
        string name = ValidateUserCategory(category);
        string path = Path.Combine(AppPaths.UserFx, name);
        Directory.CreateDirectory(path);
        return name;
    }

    public static FxItem RegisterUserAudio(string name, AudioBuffer buffer, string category = DefaultUserCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(buffer);
        string validCategory = CreateUserCategory(category);
        lock (UserItemsLock)
        {
            string baseName = name.Trim();
            string uniqueName = baseName;
            for (int suffix = 2; UserItems.Any(item => item.Category == validCategory && string.Equals(item.Name, uniqueName, StringComparison.OrdinalIgnoreCase)); suffix++)
                uniqueName = $"{baseName} ({suffix})";
            var item = new FxItem(uniqueName, validCategory, Buffer: buffer);
            UserItems.Add(item);
            return item;
        }
    }

    public static FxItem ImportUserAudio(string sourcePath, AudioBuffer buffer, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(buffer);
        string validCategory = ValidateUserCategory(category);
        string extension = Path.GetExtension(sourcePath);
        if (!UserAudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected audio file has an unsupported extension.");
        lock (UserItemsLock)
        {
            int existingCount = EnumerateUserAudioFiles().Length;
            if (existingCount >= MaxUserAudio) throw new InvalidOperationException($"The FX Library limit is {MaxUserAudio} user audio clips.");
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            if (string.IsNullOrWhiteSpace(name)) name = "Audio";
            string categoryPath = Path.Combine(AppPaths.UserFx, validCategory);
            Directory.CreateDirectory(categoryPath);
            string destination = Path.Combine(categoryPath, $"{name}_{Guid.NewGuid():N}{extension}");
            File.Copy(sourcePath, destination);
            try { return RegisterUserAudio(Path.GetFileNameWithoutExtension(sourcePath), buffer, validCategory); }
            catch { AppPaths.TryDelete(destination); throw; }
        }
    }

    static string[] EnumerateUserAudioFiles() =>
        [.. Directory.EnumerateFiles(AppPaths.UserFx, "*", SearchOption.AllDirectories)
            .Where(path => UserAudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];

    static FxItem[] CreateItems()
    {
        var items = new List<FxItem>(100);
        items.AddRange([
            new("White Burst", "Noise", WhiteBurst), new("Pink Noise Bed", "Noise", Pink), new("Brown Rumble", "Noise", () => Brown(4)),
            new("Cold Wind", "Atmos", Wind), new("Static Crackle", "Atmos", Crackle), new("Whisper Swell", "Atmos", Whisper),
            new("Sweep Up", "Synth", () => Sweep(true)), new("Sweep Down", "Synth", () => Sweep(false)), new("Laser Zap", "Synth", Zap),
            new("Riser", "Cinematic", Riser), new("Sub Boom", "Cinematic", Boom), new("Dark Drone", "Cinematic", Drone),
            new("Heartbeat", "Cinematic", Heart), new("Alarm Siren", "Cinematic", Siren),
            new("Bell Strike", "Metal", Bell), new("Metal Clang", "Metal", Clang), new("Chain Rattle", "Metal", Chain),
            new("Glitch Stutter", "Synth", Glitch), new("Clock Tick", "Foley", Tick),
        ]);

        int seed = 0;
        foreach (var (category, names) in PresetGroups)
        {
            foreach (var name in names)
            {
                int presetSeed = seed++;
                items.Add(new FxItem(name, category, () => ProceduralPreset(presetSeed, category)));
            }
        }
        return [.. items];
    }

    public static AudioBuffer Render(FxItem item)
    {
        if (item.Buffer is not null) return item.Buffer;
        if (Memo.TryGetValue(item.Name, out var w) && w.TryGetTarget(out var hit)) return hit;
        var make = item.Make ?? throw new InvalidOperationException($"FX item '{item.Name}' has no audio source.");
        var d = make(); float pk = 0; foreach (var s in d) pk = Math.Max(pk, Math.Abs(s));
        if (pk > 0) { float g = 0.8f / pk; for (int i = 0; i < d.Length; i++) d[i] *= g; }
        var b = new AudioBuffer(d, SR, 1);
        Memo[item.Name] = new WeakReference<AudioBuffer>(b); // weak: never pins memory
        return b;
    }

    public static async Task<string> DownloadAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only https:// URLs are accepted.");
        var ext = Path.GetExtension(uri.AbsolutePath); if (ext.Length is 0 or > 6) ext = ".wav";
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ext;
        var path = Path.Combine(AppPaths.Cache, name);
        if (File.Exists(path)) { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); return path; }

        var part = path + $".{Guid.NewGuid():N}.part";
        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            const long maxBytes = 200L * 1024 * 1024;
            if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("File exceeds 200 MB limit.");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new InvalidOperationException("File exceeds 200 MB limit.");
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            await dst.FlushAsync(ct);
            File.Move(part, path, true);
            Log.Info("Fx", $"Downloaded {uri.Host}{uri.AbsolutePath}");
            return path;
        }
        finally { AppPaths.TryDelete(part); }
    }

    // ---- generators (mono, 44.1 kHz) ----
    static float[] Gen(double sec, Func<int, double, float> f)
    { int n = (int)(sec * SR); var d = new float[n]; for (int i = 0; i < n; i++) d[i] = f(i, i / (double)SR); return d; }
    static float N(Random r) => (float)(r.NextDouble() * 2 - 1);

    static float[] WhiteBurst() { var r = new Random(1); return Gen(0.6, (i, t) => N(r) * (float)Math.Exp(-t * 8)); }
    static float[] Pink()
    {
        var r = new Random(2); double b0 = 0, b1 = 0, b2 = 0;
        return Gen(4, (i, t) => { double w = r.NextDouble() * 2 - 1; b0 = 0.99765 * b0 + w * 0.099046; b1 = 0.963 * b1 + w * 0.2965164; b2 = 0.57 * b2 + w * 1.0526913; return (float)((b0 + b1 + b2 + w * 0.1848) * 0.3); });
    }
    static float[] Brown(double sec) { var r = new Random(3); double b = 0; return Gen(sec, (i, t) => { b = (b + 0.02 * (r.NextDouble() * 2 - 1)) / 1.02; return (float)(b * 3.5); }); }
    static float[] Wind() { var br = Brown(6); return Gen(6, (i, t) => br[i] * (float)(0.6 + 0.4 * Math.Sin(2 * Math.PI * 0.3 * t))); }
    static float[] Crackle() { var r = new Random(6); return Gen(3, (i, t) => (r.NextDouble() < 0.0025 ? N(r) : 0) + N(r) * 0.01f); }
    static float[] Whisper() { var r = new Random(8); double lp = 0; return Gen(4, (i, t) => { double n = r.NextDouble() * 2 - 1; lp += 0.3 * (n - lp); double s = Math.Sin(Math.PI * t / 4); return (float)((n - lp) * s * s); }); }
    static float[] Sweep(bool up) { double ph = 0; return Gen(2, (i, t) => { double k = t / 2, f = up ? 100 * Math.Pow(40, k) : 4000 * Math.Pow(0.025, k); ph += 2 * Math.PI * f / SR; return (float)(Math.Sin(ph) * Math.Sin(Math.PI * k)); }); }
    static float[] Zap() { double ph = 0; return Gen(0.4, (i, t) => { ph += 2 * Math.PI * (150 + 2850 * Math.Exp(-t * 9)) / SR; return (float)(Math.Sin(ph) * Math.Exp(-t * 5)); }); }
    static float[] Riser() { var r = new Random(5); double ph = 0, lp = 0; return Gen(5, (i, t) => { double k = t / 5; ph += 2 * Math.PI * (200 + 1800 * k * k) / SR; lp += 0.15 * ((r.NextDouble() * 2 - 1) - lp); return (float)((Math.Sin(ph) * 0.5 + lp * 1.2) * k * k); }); }
    static float[] Boom() { var r = new Random(4); double ph = 0, lp = 0; return Gen(2.5, (i, t) => { ph += 2 * Math.PI * (25 + 45 * Math.Exp(-t * 1.4)) / SR; lp += 0.02 * ((r.NextDouble() * 2 - 1) - lp); return (float)((Math.Sin(ph) + lp * 6) * Math.Exp(-t * 1.6)); }); }
    static float[] Drone() => Gen(8, (i, t) => { double e = Math.Sqrt(Math.Sin(Math.PI * t / 8)), l = 0.7 + 0.3 * Math.Sin(2 * Math.PI * 0.2 * t); return (float)((Math.Sin(2 * Math.PI * 55 * t) + Math.Sin(2 * Math.PI * 55.4 * t) * 0.8 + Math.Sin(2 * Math.PI * 82.7 * t) * 0.4 + Math.Sin(2 * Math.PI * 110.9 * t) * 0.2) * e * l); });
    static double Thump(double t) => t < 0 ? 0 : Math.Sin(2 * Math.PI * (55 - 20 * t) * t) * Math.Exp(-t * 16);
    static float[] Heart() => Gen(1.2, (i, t) => (float)(Thump(t) + Thump(t - 0.3) * 0.7));
    static float[] Siren() { double ph = 0; return Gen(3, (i, t) => { ph += 2 * Math.PI * (900 + 300 * Math.Sin(2 * Math.PI * 0.7 * t)) / SR; return (float)Math.Sin(ph) * 0.8f; }); }
    static float[] Bell() { double[] r = [1, 2.76, 5.4, 8.93]; return Gen(5, (i, t) => { double s = 0; for (int k = 0; k < r.Length; k++) s += Math.Sin(2 * Math.PI * 220 * r[k] * t) * Math.Exp(-t * (0.9 + k * 1.3)) / (k + 1); return (float)s; }); }
    static float[] Clang() { var rn = new Random(9); double[] r = [1, 1.47, 2.09, 2.56, 3.17, 4.1]; return Gen(2.5, (i, t) => { double s = N(rn) * Math.Exp(-t * 40); for (int k = 0; k < r.Length; k++) s += Math.Sin(2 * Math.PI * 330 * r[k] * t) * Math.Exp(-t * (2 + k)) / (k + 1); return (float)s; }); }
    static float[] Chain()
    {
        var r = new Random(7); double env = 0; int hit = 0;
        var times = Enumerable.Range(0, 16).Select(_ => r.NextDouble() * 2.2).OrderBy(x => x).ToArray();
        return Gen(2.8, (i, t) => { while (hit < times.Length && t >= times[hit]) { env = 0.4 + r.NextDouble() * 0.6; hit++; } env *= 0.9985; return (float)(env * (r.NextDouble() * 2 - 1) * Math.Sin(2 * Math.PI * 3200 * t)); });
    }
    static float[] Glitch() { var r = new Random(10); double held = 0; return Gen(1.5, (i, t) => { if (i % (40 + (i / 3000) * 13) == 0) held = r.NextDouble() * 2 - 1; return (float)(held * ((i / 4000) % 2 == 0 ? 1 : 0)); }); }
    static float[] Tick() => Gen(0.08, (i, t) => (float)(Math.Sin(2 * Math.PI * 1800 * t) * Math.Exp(-t * 90)));

    static float[] ProceduralPreset(int seed, string category)
    {
        double seconds = 1.8 + seed % 4 * 0.55;
        int count = (int)(seconds * SR);
        var data = new float[count];
        var random = new Random(1000 + seed);
        double phase = 0, phase2 = 0, lowNoise = 0, envelope = 0;
        double baseFrequency = 55 + (seed * 37 % 500);
        for (int i = 0; i < count; i++)
        {
            double time = i / (double)SR;
            double progress = time / seconds;
            double noise = random.NextDouble() * 2 - 1;
            lowNoise += 0.012 * (noise - lowNoise);
            double attack = Math.Min(1, time * (category == "Cinematic Impacts" ? 90 : 1.8));
            double release = Math.Exp(-time * (category == "Chimes & Bells" ? 1.25 : category == "Cinematic Impacts" ? 3.8 : 0.55));
            double value;

            switch (category)
            {
                case "Haunted Winds":
                    phase += 2 * Math.PI * (95 + 22 * Math.Sin(time * (0.25 + seed % 5 * 0.07))) / SR;
                    value = lowNoise * (0.45 + 0.55 * Math.Sin(time * (0.3 + seed % 4 * 0.11))) + noise * 0.035;
                    value += Math.Sin(phase) * 0.13;
                    break;
                case "Ghostly Voices":
                    phase += 2 * Math.PI * (baseFrequency + 5 * Math.Sin(time * 2.1 + seed)) / SR;
                    value = Math.Sin(phase) * (0.35 + 0.3 * Math.Sin(time * 3.7)) + lowNoise * 0.28 + noise * 0.025;
                    value *= 0.55 + 0.45 * Math.Sin(Math.PI * progress);
                    break;
                case "Chimes & Bells":
                    phase = 2 * Math.PI * baseFrequency * time;
                    value = Math.Sin(phase) + 0.52 * Math.Sin(phase * 2.71) + 0.24 * Math.Sin(phase * 5.13 + 0.4) + 0.11 * Math.Sin(phase * 8.31);
                    value *= release * attack;
                    break;
                case "Strings & Violin":
                    phase += 2 * Math.PI * baseFrequency * (1 + 0.004 * Math.Sin(time * 5.1 + seed)) / SR;
                    value = Math.Sin(phase) + 0.42 * Math.Sin(phase * 2.01) + 0.19 * Math.Sin(phase * 3.04) + 0.08 * Math.Sin(phase * 4.9);
                    value = value * (0.28 + 0.5 * Math.Sin(Math.PI * progress)) + lowNoise * 0.12;
                    break;
                case "Dark Drones":
                    phase += 2 * Math.PI * baseFrequency / SR;
                    phase2 += 2 * Math.PI * (baseFrequency * 1.006 + 7) / SR;
                    value = Math.Sin(phase) * 0.42 + Math.Sin(phase2) * 0.31 + Math.Sin(phase * 0.503) * 0.18 + lowNoise * 0.18;
                    value *= 0.75 + 0.25 * Math.Sin(time * 0.7 + seed);
                    break;
                case "Cinematic Impacts":
                    phase += 2 * Math.PI * (baseFrequency * 2 + 120 * Math.Exp(-time * 9)) / SR;
                    value = (noise * 0.7 + Math.Sin(phase) * 0.55 + lowNoise * 0.35) * release * attack;
                    break;
                case "Textures & Static":
                    if (i % (32 + seed % 89) == 0) envelope = random.NextDouble() * 2 - 1;
                    value = noise * 0.25 + envelope * 0.35 + lowNoise * 0.25;
                    value *= 0.65 + 0.35 * Math.Sin(time * (4 + seed % 5));
                    break;
                case "Creatures & Whispers":
                    phase += 2 * Math.PI * (baseFrequency * (0.65 + 1.1 * progress) + 18 * Math.Sin(time * 7 + seed)) / SR;
                    phase2 += 2 * Math.PI * (baseFrequency * 2.17) / SR;
                    value = Math.Sin(phase) * 0.55 + Math.Sin(phase2) * 0.22 + noise * 0.17;
                    value *= 0.45 + 0.55 * Math.Pow(Math.Sin(Math.PI * progress), 2);
                    break;
                default:
                    phase += 2 * Math.PI * (baseFrequency * (0.3 + 3.5 * progress * progress)) / SR;
                    value = Math.Sin(phase) * (0.35 + 0.55 * progress) + lowNoise * (0.1 + 0.35 * progress) + noise * 0.025;
                    break;
            }
            data[i] = (float)value;
        }
        return data;
    }
}
