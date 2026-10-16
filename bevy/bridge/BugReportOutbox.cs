using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnturnedGodot.BugReport;

namespace UnturnedBevy;

/// <summary>The engine-free half of game/BugReporter.cs: the outbox on disk and
/// its upload. Native code captures the screenshots and microphone (engine
/// work); this reuses BugReportCore's AudioBuffer/Wav/BuildStamp and ports
/// WriteOutbox, UploadDir, Park and RetryOutbox with their source semantics:
/// disk before network, dedupe on client_report_id, park permanent 4xx.</summary>
public sealed class BugReportOutbox
{
    public const string RejectedPrefix = "rejected_";
    public const int MaxRetryPerLaunch = 10;
    public const string DefaultEndpoint = "https://claw.bitvox.me/bugreport/report";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static readonly SemaphoreSlim UploadGate = new(1, 1);
    readonly ConcurrentQueue<string> _toasts = new();
    int _inFlight;

    public string[] Drain()
    {
        var list = new List<string>();
        while (_toasts.TryDequeue(out var t)) list.Add(t);
        return list.ToArray();
    }
    public bool Busy => Volatile.Read(ref _inFlight) > 0;

    /// <summary>FinishRecordingCore / SubmitTyped after the native capture: the
    /// context, both shots and the WAV (unless a tap) land on disk first.</summary>
    public object Write(JsonElement r)
    {
        string root = BridgeHost.Text(r, "root");
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("outbox root required");
        string typed = r.TryGetProperty("typed", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        bool wasTap = BridgeHost.Bool(r, "wasTap"), hitTimeout = BridgeHost.Bool(r, "hitTimeout");
        string projectDir = BridgeHost.Text(r, "projectDir");
        // A package sends the commit recorded in its marker instead of a directory to search upward from.
        string buildCommit = r.TryGetProperty("buildCommit", out var bc) && bc.ValueKind == JsonValueKind.String ? bc.GetString() : null;
        if (buildCommit != null) { if (!BuildStamp.Packaged || BuildStamp.Commit != buildCommit) BuildStamp.UsePackagedCommit(buildCommit); }
        else if (!string.IsNullOrEmpty(projectDir)) BuildStamp.ProjectDir ??= projectDir;
        var status = typed != null ? AudioStatus.Typed : Status(BridgeHost.Text(r, "audioStatus", "disabled"));
        AudioBuffer audio = null;
        string pcm = BridgeHost.Text(r, "pcm");
        int rate = BridgeHost.Int(r, "mixRate", 44100);
        if (typed == null && !string.IsNullOrEmpty(pcm) && File.Exists(pcm))
        {
            var bytes = File.ReadAllBytes(pcm);
            var mono = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, mono, 0, mono.Length * 4);
            audio = new AudioBuffer((int)(ReportSession.MaxSeconds * rate) + 1024);
            audio.AppendStereo(mono, null, mono.Length);
        }
        // The amplitude check decides, never a frame count or a device list.
        if (status == AudioStatus.Ok) status = audio?.Classify() ?? AudioStatus.NoMic;
        byte[] wav = null;
        if (audio != null && audio.Count > 0 && !wasTap)
        {
            var (samples, fitted) = Wav.FitRate(audio.ToArray(), rate);
            wav = Wav.Encode(samples, fitted);
        }
        var context = r.TryGetProperty("context", out var c) ? JsonNode.Parse(c.GetRawText())!.AsObject() : new JsonObject();
        string id = Guid.NewGuid().ToString();
        context["client_report_id"] = id;
        context["game_commit"] = BuildStamp.Commit;
        context["audio_status"] = AudioStatusWire.Of(status);
        context["audio_peak"] = audio?.Peak ?? 0f;
        context["audio_discarded_frames"] = (double)(audio?.DiscardedFrames ?? 0);
        context["audio_truncated"] = audio?.Full ?? false;
        if (typed != null) context["typed_text"] = typed;
        string dir = Path.Combine(root, id);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "context.json"), context.ToJsonString());
            Copy(BridgeHost.Text(r, "shot"), Path.Combine(dir, "screenshot.png"));
            Copy(BridgeHost.Text(r, "shotAfter"), Path.Combine(dir, "screenshot_after.png"));
            if (wav != null) File.WriteAllBytes(Path.Combine(dir, "audio.wav"), wav);
        }
        catch (Exception e)
        {
            return new { dir = (string)null, error = e.Message, toast = typed != null ? "could not save the report" : "report failed — see the log" };
        }
        bool upload = BridgeHost.Bool(r, "upload");
        if (upload) Start(dir, Endpoint(r), BridgeHost.Text(r, "key"));
        string toast = typed != null ? (upload ? "report sent ✓" : "report saved")
            : hitTimeout ? "60s max — sending"
            : wasTap ? "screenshot only"
            : status == AudioStatus.NoMic ? "no mic — screenshot only" : "sending…";
        return new { dir, id, audioStatus = AudioStatusWire.Of(status), wav = wav != null, toast };
    }
    static void Copy(string from, string to)
    {
        if (!string.IsNullOrEmpty(from) && File.Exists(from)) File.Copy(from, to, true);
    }
    static AudioStatus Status(string s) => s switch
    {
        "ok" => AudioStatus.Ok, "no_mic" => AudioStatus.NoMic, "error" => AudioStatus.Error, _ => AudioStatus.Disabled,
    };
    static string Endpoint(JsonElement r)
    {
        string e = BridgeHost.Text(r, "endpoint");
        return string.IsNullOrWhiteSpace(e) ? DefaultEndpoint : e;
    }

    /// <summary>RetryOutbox: up to ten unparked reports per launch, serialised.</summary>
    public object Retry(JsonElement r)
    {
        string root = BridgeHost.Text(r, "root");
        if (!Directory.Exists(root)) return new { queued = 0 };
        var dirs = Directory.GetDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith(RejectedPrefix))
            .Take(MaxRetryPerLaunch).ToArray();
        foreach (var d in dirs) Start(d, Endpoint(r), BridgeHost.Text(r, "key"));
        return new { queued = dirs.Length };
    }

    void Start(string dir, string endpoint, string key)
    {
        Interlocked.Increment(ref _inFlight);
        _ = Task.Run(async () =>
        {
            try { await UploadDir(dir, endpoint, key, _toasts.Enqueue); }
            finally { Interlocked.Decrement(ref _inFlight); }
        });
    }

    /// <summary>UploadDir: multipart context/screenshot/screenshot_after/audio,
    /// Bearer key in a header (never the URL). 2xx deletes; a permanent 4xx is
    /// parked under rejected_CODE_; 401/408/429/5xx/transport errors keep it.</summary>
    public static async Task UploadDir(string dir, string endpoint, string key, Action<string> toast)
    {
        await UploadGate.WaitAsync();
        try
        {
            string ctx = Path.Combine(dir, "context.json");
            if (!File.Exists(ctx)) return;
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(File.ReadAllText(ctx), System.Text.Encoding.UTF8, "application/json"), "context");
            AddFile(form, Path.Combine(dir, "screenshot.png"), "screenshot", "image/png");
            AddFile(form, Path.Combine(dir, "screenshot_after.png"), "screenshot_after", "image/png");
            AddFile(form, Path.Combine(dir, "audio.wav"), "audio", "audio/wav");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            if (!string.IsNullOrEmpty(key))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await Http.SendAsync(request);
            int code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) { Directory.Delete(dir, true); toast("report sent ✓"); }
            else if (code >= 400 && code < 500 && code != 401 && code != 408 && code != 429)
            {
                Park(dir, code);
                toast($"report rejected ({code}) — kept locally");
            }
            else if (code == 401) toast("report key rejected — update it in the launcher; report kept");
            else toast($"upload failed ({code}) — will retry");
        }
        catch (Exception) { toast("saved — upload failed, will retry"); }
        finally { UploadGate.Release(); }
    }
    static void AddFile(MultipartFormDataContent form, string path, string field, string mime)
    {
        if (!File.Exists(path)) return;
        var content = new ByteArrayContent(File.ReadAllBytes(path));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
        form.Add(content, field, Path.GetFileName(path));
    }
    static void Park(string dir, int code)
    {
        try
        {
            string parent = Path.GetDirectoryName(dir)!, name = Path.GetFileName(dir);
            Directory.Move(dir, Path.Combine(parent, RejectedPrefix + code + "_" + name));
        }
        catch { }
    }
}

public sealed partial class BridgeHost
{
    readonly BugReportOutbox _bugReports = new();
    object BugReportPoll() => new { toasts = _bugReports.Drain(), busy = _bugReports.Busy };
}
