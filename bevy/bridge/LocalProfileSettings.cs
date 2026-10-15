using System.Globalization;
using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>PlayerProfile.cs environment and face preferences, without a Godot
/// dependency. Source rules validate the name and PNG before the unchanged wire
/// command; only accepted profile replicas change live character presentation.</summary>
sealed class LocalProfileSettings
{
    public readonly string Path;
    public readonly string Name;
    public readonly byte[] Avatar;
    public readonly string AvatarNotice;
    public byte Face { get; private set; }
    public string SaveNotice { get; private set; }
    public const int FreeFaces = 10;

    public LocalProfileSettings(string path, string legacyPath, string rawName)
    {
        Path = path;
        Name = ProfileRules.SanitizeName(rawName ?? Environment.GetEnvironmentVariable("UG_USERNAME"));
        Face = LoadFace(legacyPath);
        string avatarPath = Environment.GetEnvironmentVariable("UG_PROFILE_PNG");
        if (string.IsNullOrEmpty(avatarPath)) return;
        try
        {
            var info = new FileInfo(avatarPath);
            if (!info.Exists) { AvatarNotice = "Profile picture file is missing."; return; }
            // Source checks length before allocation, even for a user-owned path.
            if (info.Length > ProfileRules.MaxAvatarBytes)
            { AvatarNotice = $"Profile picture exceeds {ProfileRules.MaxAvatarBytes / 1024} KB."; return; }
            var bytes = File.ReadAllBytes(avatarPath);
            var verdict = ProfileRules.CheckAvatarPng(bytes);
            if (verdict != ProfileRules.AvatarVerdict.Ok)
            { AvatarNotice = $"Profile picture refused: {ProfileRules.Explain(verdict)}."; return; }
            Avatar = bytes;
        }
        catch (Exception e) { AvatarNotice = $"Could not read profile picture ({e.GetType().Name})."; }
    }

    static byte FaceValue(int value) => PlayerProfileReplication.ClampFace((byte)Math.Clamp(value, 0, 255));
    byte LoadFace(string legacyPath)
    {
        // Exactly the source priority: override, saved face, random free face.
        if (int.TryParse(Environment.GetEnvironmentVariable("UG_FACE"), out int forced)) return FaceValue(forced);
        try
        {
            if (TryReadFace(Path, out int saved)) return FaceValue(saved);
            if (!File.Exists(Path) && !string.IsNullOrEmpty(legacyPath) && TryReadFace(legacyPath, out saved))
            { byte imported = FaceValue(saved); SaveFace(imported); return imported; }
            byte fresh = (byte)Random.Shared.Next(FreeFaces);
            SaveFace(fresh);
            return fresh;
        }
        catch { return 0; } // PlayerProfile.LoadFace also treats inaccessible config as face 0.
    }

    static bool TryReadFace(string path, out int face)
    {
        face = 0;
        if (!File.Exists(path)) return false;
        bool character = false, found = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[')) { character = line == "[character]"; continue; }
            int equals = line.IndexOf('=');
            if (character && equals >= 0 && line[..equals].Trim() == "face")
            {
                // A non-integer Variant throws in original LoadFace and selects 0.
                found = true;
                if (!int.TryParse(line[(equals + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out face)) face = 0;
            }
        }
        return found;
    }

    void SaveFace(byte face)
    {
        var lines = File.Exists(Path) ? File.ReadAllLines(Path).ToList() : new List<string>();
        int section = -1, field = -1;
        bool character = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim().TrimStart('\uFEFF');
            if (line.StartsWith('['))
            { character = line == "[character]"; if (character) section = i; continue; }
            int equals = line.IndexOf('=');
            if (character && equals >= 0 && line[..equals].Trim() == "face") field = i;
        }
        string setting = $"face={face}";
        if (field >= 0) lines[field] = setting;
        else if (section >= 0) lines.Insert(section + 1, setting);
        else { if (lines.Count != 0) lines.Add(""); lines.Add("[character]"); lines.Add(setting); }
        string parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        Directory.CreateDirectory(parent);
        File.WriteAllLines(Path, lines);
    }
    public void SetFace(int value)
    {
        Face = FaceValue(value);
        try { SaveFace(Face); SaveNotice = null; }
        catch (Exception e) { SaveNotice = $"Could not save face ({e.GetType().Name})."; }
    }
}

public sealed partial class BridgeHost
{
    LocalProfileSettings _localProfile;
    bool _useLocalProfile, _localProfileSent;

    object ReadLocalProfile(JsonElement request)
    {
        string path = Text(request, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("localProfile requires a preference path");
        // The source environment/name/avatar cache is process-local. Changing the
        // explicit path is a test/import seam, not a network operation.
        if (_localProfile == null || _localProfile.Path != path)
            _localProfile = new(path, Text(request, "legacyPath"), request.TryGetProperty("name", out var name) ? name.GetString() : null);
        return LocalProfileSnapshot();
    }
    object LocalProfileSnapshot() => new {
        name = _localProfile.Name, face = _localProfile.Face,
        faceCount = PlayerProfileReplication.FaceCount, freeFaceCount = LocalProfileSettings.FreeFaces,
        hasAvatar = _localProfile.Avatar != null, avatarNotice = _localProfile.AvatarNotice, saveNotice = _localProfile.SaveNotice,
    };
    object SetLocalFace(JsonElement request)
    {
        if (_localProfile == null) throw new InvalidOperationException("Read localProfile before choosing a face");
        _localProfile.SetFace(Int(request, "face"));
        bool queued = _useLocalProfile && _client?.State == NetSessionState.Connected;
        if (queued) SendConfiguredProfile(); // same validated command in local and UDP sessions
        return new { queued, profile = LocalProfileSnapshot() };
    }
    void ConfigureLocalProfile(JsonElement request)
    {
        _useLocalProfile = Bool(request, "useLocalProfile");
        _localProfileSent = false;
        if (_useLocalProfile && _localProfile == null)
            throw new InvalidOperationException("Read localProfile before starting its session");
        _name = ProfileRules.SanitizeName(Text(request, "name", _useLocalProfile ? _localProfile.Name : "Survivor"));
    }
    void SendConfiguredProfile() => _client.SendSetProfile(_name,
        _useLocalProfile ? _localProfile.Avatar : null, _useLocalProfile ? _localProfile.Face : (byte)0);
    void PublishLocalProfile()
    {
        if (!_useLocalProfile || _localProfileSent || _client?.State != NetSessionState.Connected) return;
        SendConfiguredProfile();
        _localProfileSent = true;
    }
}
