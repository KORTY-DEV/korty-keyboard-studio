using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Korty.Core.Model;
using Korty.Core.Protocol;

namespace Korty.Core.Storage;

public static class AtomicFile
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Write to a temp file in the same directory, flush to disk, then swap. A crash never leaves a half-written file.</summary>
    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    public static void WriteJson<T>(string path, T value) =>
        WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
}

public sealed class AppSettings
{
    /// <summary>True = no hardware interaction at all; a simulated keyboard is shown instead.</summary>
    public bool SimulationMode { get; set; }
    /// <summary>Protocol family used for plans, the simulator and (after stage activation) real writes. Feature04 = the family that works on the owner's keyboard in USB mode.</summary>
    public ProtocolFamily SimulatedFamily { get; set; } = ProtocolFamily.Feature04;
    /// <summary>Byte 8 of SAVE (reference default 0).</summary>
    public int SaveFlag { get; set; }
    /// <summary>Send FINISH after the lighting SAVE (reference default: off).</summary>
    public bool FinishAfterLighting { get; set; }
    /// <summary>false = AKS075 image variant (reference default), true = gohv.</summary>
    public bool GohvImage { get; set; }
    public string? LastDesignId { get; set; }
    public string Language { get; set; } = "ru";
    /// <summary>User-saved colours (#RRGGBB), newest first, max 24.</summary>
    public List<string> CustomColors { get; set; } = new();
    public bool AnimatePreview { get; set; } = true;
    /// <summary>Keyboard settings block as last chosen by the user (sent in full on every apply).</summary>
    public KeyboardSettings Keyboard { get; set; } = new();
}

public sealed class ConnectionProfile
{
    public string? DesignId { get; set; }
    public RgbSettings Rgb { get; set; } = new();
    /// <summary>
    /// The user's WISH to re-apply after reconnecting. It does nothing unless a verified, gated control path exists;
    /// the Profiles page states this explicitly.
    /// </summary>
    public bool RestoreOnReconnect { get; set; }
}

/// <summary>USB and 2.4 GHz keep independent settings: the keyboard may store/forget them independently.</summary>
public sealed class StudioProfile
{
    public const int CurrentSchema = 2;
    public const string UsbKey = "usb";
    public const string WirelessKey = "wireless24";

    public int SchemaVersion { get; set; } = CurrentSchema;
    public AppSettings Settings { get; set; } = new();
    public Dictionary<string, ConnectionProfile> Connections { get; set; } = new()
    {
        [UsbKey] = new ConnectionProfile(),
        [WirelessKey] = new ConnectionProfile(),
    };

    public ConnectionProfile Get(string key)
    {
        if (!Connections.TryGetValue(key, out var p)) Connections[key] = p = new ConnectionProfile();
        return p;
    }

    public void Sanitize()
    {
        foreach (var k in new[] { UsbKey, WirelessKey }) Get(k);
        foreach (var c in Connections.Values) c.Rgb = (c.Rgb ?? new RgbSettings()).Clamped();
    }
}

public sealed class ProfileStore
{
    private readonly object _lock = new();
    public string Directory { get; }
    public string ProfilePath => Path.Combine(Directory, "profile.json");
    public string? LastLoadWarning { get; private set; }

    public ProfileStore(string directory) { Directory = directory; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KORTY", "KeyboardStudio");

    public StudioProfile Load()
    {
        lock (_lock)
        {
            LastLoadWarning = null;
            if (!File.Exists(ProfilePath)) return Fresh();
            try
            {
                var p = JsonSerializer.Deserialize<StudioProfile>(File.ReadAllBytes(ProfilePath), AtomicFile.Json) ?? throw new InvalidDataException("empty");
                if (p.SchemaVersion > StudioProfile.CurrentSchema)
                    LastLoadWarning = $"Профиль создан более новой версией (схема {p.SchemaVersion}); неизвестные поля будут потеряны при сохранении.";
                if (p.SchemaVersion < 2)
                {
                    // v1 profiles were created when the AA family was the default; the working family is Feature04.
                    p.Settings.SimulatedFamily = ProtocolFamily.Feature04;
                    p.SchemaVersion = 2;
                }
                p.Settings.SaveFlag = Math.Clamp(p.Settings.SaveFlag, 0, 1);
                p.Settings.Keyboard = (p.Settings.Keyboard ?? new KeyboardSettings()).Clamped();
                p.Sanitize();
                return p;
            }
            catch (Exception ex)
            {
                // Keep the broken file for forensics instead of silently overwriting it.
                var bad = ProfilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(ProfilePath, bad); } catch { /* ignore */ }
                LastLoadWarning = $"profile.json повреждён ({ex.Message}); создан новый, старый сохранён как {Path.GetFileName(bad)}.";
                return Fresh();
            }
        }
    }

    private static StudioProfile Fresh() { var p = new StudioProfile(); p.Sanitize(); return p; }

    public void Save(StudioProfile profile)
    {
        lock (_lock) AtomicFile.WriteJson(ProfilePath, profile);
    }
}

/// <summary>Strict validation of ids and asset names read from disk, so a tampered JSON cannot traverse the file system.</summary>
public static partial class LibraryPaths
{
    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex IdRegex();
    [GeneratedRegex(@"^[0-9a-f]{32}\.(png|jpg|jpeg|gif|bmp)$")] private static partial Regex AssetRegex();
    public static bool IsValidId(string? id) => id != null && IdRegex().IsMatch(id);
    public static bool IsValidAsset(string? name) => name != null && AssetRegex().IsMatch(name);
}

public sealed record DesignSummary(string Id, string Name, DateTimeOffset Modified, int LayerCount, string? ThumbnailPath);

/// <summary>On-disk library of compositions: <c>designs/&lt;id&gt;/composition.json</c>, <c>assets/*</c>, <c>thumb.png</c>.</summary>
public sealed class DesignLibrary
{
    public const long MaxAssetBytes = 25L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };
    private readonly string _root;
    private readonly object _lock = new();

    public DesignLibrary(string profileDirectory) { _root = Path.Combine(profileDirectory, "designs"); }

    private string DesignDir(string id)
    {
        if (!LibraryPaths.IsValidId(id)) throw new ArgumentException("Недопустимый идентификатор оформления.", nameof(id));
        return Path.Combine(_root, id);
    }

    public string AssetPath(string designId, string asset)
    {
        if (!LibraryPaths.IsValidAsset(asset)) throw new ArgumentException("Недопустимое имя файла ресурса.", nameof(asset));
        return Path.Combine(DesignDir(designId), "assets", asset);
    }

    public string ThumbnailPath(string id) => Path.Combine(DesignDir(id), "thumb.png");

    public IReadOnlyList<DesignSummary> List()
    {
        var result = new List<DesignSummary>();
        if (!Directory.Exists(_root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            if (!LibraryPaths.IsValidId(id)) continue;
            try
            {
                var c = Load(id);
                var thumb = Path.Combine(dir, "thumb.png");
                result.Add(new DesignSummary(id, c.Name, c.Modified, c.Layers.Count, File.Exists(thumb) ? thumb : null));
            }
            catch { /* unreadable design is skipped, never crashes the list */ }
        }
        return result.OrderByDescending(r => r.Modified).ToList();
    }

    public DisplayComposition Load(string id)
    {
        lock (_lock)
        {
            var path = Path.Combine(DesignDir(id), "composition.json");
            var c = JsonSerializer.Deserialize<DisplayComposition>(File.ReadAllBytes(path), AtomicFile.Json)
                    ?? throw new InvalidDataException("Пустой файл оформления.");
            if (c.Id != id) c.Id = id;
            // Drop layers whose asset name is not a plain library file name.
            c.Layers.RemoveAll(l => l is ImageLayer il && !LibraryPaths.IsValidAsset(il.Asset));
            c.Sanitize();
            return c;
        }
    }

    public void Save(DisplayComposition composition, byte[]? thumbnailPng = null)
    {
        lock (_lock)
        {
            composition.Sanitize();
            composition.Modified = DateTimeOffset.UtcNow;
            var dir = DesignDir(composition.Id);
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            AtomicFile.WriteJson(Path.Combine(dir, "composition.json"), composition);
            if (thumbnailPng != null) AtomicFile.WriteAllBytes(Path.Combine(dir, "thumb.png"), thumbnailPng);
            // Remove assets no layer references any more.
            var used = composition.Layers.OfType<ImageLayer>().Select(l => l.Asset).ToHashSet();
            foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "assets")))
                if (!used.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch { /* in use */ }
        }
    }

    /// <summary>Copies a user file into the design's assets folder under a random name. Returns the stored asset name.</summary>
    public string ImportAsset(string designId, string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath);
        if (!AllowedExt.Contains(ext)) throw new NotSupportedException($"Формат «{ext}» не поддерживается (PNG, JPEG, GIF, BMP).");
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Файл не найден.", sourcePath);
        if (info.Length > MaxAssetBytes) throw new InvalidDataException($"Файл больше {MaxAssetBytes / 1024 / 1024} МБ.");
        var name = Guid.NewGuid().ToString("N") + ext.ToLowerInvariant();
        var dest = AssetPath(designId, name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourcePath, dest, overwrite: false);
        return name;
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            var dir = DesignDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Deep copy including assets, new id.</summary>
    public DisplayComposition Duplicate(string id)
    {
        lock (_lock)
        {
            var src = Load(id);
            var copy = JsonSerializer.Deserialize<DisplayComposition>(JsonSerializer.Serialize(src, AtomicFile.Json), AtomicFile.Json)!;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = src.Name + " (копия)";
            var newDir = DesignDir(copy.Id);
            Directory.CreateDirectory(Path.Combine(newDir, "assets"));
            var srcAssets = Path.Combine(DesignDir(id), "assets");
            if (Directory.Exists(srcAssets))
                foreach (var f in Directory.EnumerateFiles(srcAssets))
                    File.Copy(f, Path.Combine(newDir, "assets", Path.GetFileName(f)));
            Save(copy);
            return copy;
        }
    }
}
