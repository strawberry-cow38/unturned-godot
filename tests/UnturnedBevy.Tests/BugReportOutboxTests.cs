using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class BugReportOutboxTests
{
    string root;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "bugreport-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
    static JsonElement Call(BridgeHost host, string op, object args) =>
        JsonSerializer.SerializeToElement(host.Handle(op, J(args)));
    string Pcm(float amplitude, int frames)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "capture.pcm");
        var samples = Enumerable.Range(0, frames).Select(i => amplitude * MathF.Sin(i * .1f)).ToArray();
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
        return path;
    }
    string Shot()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "shot.png");
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4e, 0x47 });
        return path;
    }

    [Test]
    public void DiskFirstReportCarriesContextShotsAndAmplitudeVerdict()
    {
        using var host = new BridgeHost();
        string outbox = Path.Combine(root, "outbox");
        var loud = Call(host, "bugReport", new { root = outbox, audioStatus = "ok", pcm = Pcm(.5f, 9000), mixRate = 88200,
            shot = Shot(), shotAfter = Shot(), context = new { map = "PEI", player = new { health = 80 } } });
        string dir = loud.GetProperty("dir").GetString();
        Assert.That(loud.GetProperty("audioStatus").GetString(), Is.EqualTo("ok"));
        Assert.That(loud.GetProperty("toast").GetString(), Is.EqualTo("sending…"));
        Assert.That(File.Exists(Path.Combine(dir, "screenshot.png")) && File.Exists(Path.Combine(dir, "screenshot_after.png")));
        var wav = File.ReadAllBytes(Path.Combine(dir, "audio.wav"));
        Assert.That(BitConverter.ToInt32(wav, 24), Is.EqualTo(44100), "88.2 kHz is halved to fit the wire, not rejected");
        using var ctx = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "context.json")));
        Assert.That(ctx.RootElement.GetProperty("map").GetString(), Is.EqualTo("PEI"));
        Assert.That(ctx.RootElement.GetProperty("client_report_id").GetString(), Is.EqualTo(Path.GetFileName(dir)));
        Assert.That(ctx.RootElement.GetProperty("audio_status").GetString(), Is.EqualTo("ok"));
        Assert.That(ctx.RootElement.GetProperty("game_commit").GetString(), Is.Not.Empty);

        var silent = Call(host, "bugReport", new { root = outbox, audioStatus = "ok", pcm = Pcm(0, 4000), mixRate = 44100 });
        Assert.That(silent.GetProperty("audioStatus").GetString(), Is.EqualTo("no_mic"), "silence is not a microphone");
        Assert.That(silent.GetProperty("toast").GetString(), Is.EqualTo("no mic — screenshot only"));
        var tap = Call(host, "bugReport", new { root = outbox, audioStatus = "ok", pcm = Pcm(.5f, 4000), wasTap = true });
        Assert.That(tap.GetProperty("wav").GetBoolean(), Is.False);
        Assert.That(tap.GetProperty("toast").GetString(), Is.EqualTo("screenshot only"));
        var typed = Call(host, "bugReport", new { root = outbox, typed = "door clips", shot = Shot() });
        using var tctx = JsonDocument.Parse(File.ReadAllText(Path.Combine(typed.GetProperty("dir").GetString(), "context.json")));
        Assert.That(tctx.RootElement.GetProperty("typed_text").GetString(), Is.EqualTo("door clips"));
        Assert.That(tctx.RootElement.GetProperty("audio_status").GetString(), Is.EqualTo("typed"));
        Assert.That(typed.GetProperty("toast").GetString(), Is.EqualTo("report saved"), "never claims a send when upload is off");
    }

    [Test]
    public void PackagedReportCarriesTheMarkerCommitNotAnEnclosingCheckout()
    {
        // The package lies inside a checkout whose HEAD is a different build.
        string package = Path.Combine(root, "dist", "unturned-bevy");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");
        try
        {
            UnturnedGodot.BugReport.BuildStamp.Reset();
            using var host = new BridgeHost();
            string outbox = Path.Combine(root, "outbox");
            var report = Call(host, "bugReport", new { root = outbox, typed = "x", projectDir = package, buildCommit = "5707bbba9760" });
            using var ctx = JsonDocument.Parse(File.ReadAllText(Path.Combine(report.GetProperty("dir").GetString(), "context.json")));
            Assert.That(ctx.RootElement.GetProperty("game_commit").GetString(), Is.EqualTo("5707bbba9760"));
        }
        finally { UnturnedGodot.BugReport.BuildStamp.Reset(); }
    }

    [TestCase(200, false, false)]
    [TestCase(422, false, true)]
    [TestCase(401, true, false)]
    [TestCase(503, true, false)]
    public async Task UploadOutcomeDeletesParksOrKeepsTheReport(int status, bool kept, bool parked)
    {
        int port = ((IPEndPoint)new TcpListener(IPAddress.Loopback, 0) { }.LocalEndpoint).Port;
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string auth = null, body = null;
        var serve = Task.Run(async () =>
        {
            var c = await listener.GetContextAsync();
            auth = c.Request.Headers["Authorization"];
            using (var reader = new StreamReader(c.Request.InputStream)) body = await reader.ReadToEndAsync();
            c.Response.StatusCode = status; c.Response.Close();
        });
        using var host = new BridgeHost();
        string outbox = Path.Combine(root, "outbox");
        string dir = Call(host, "bugReport", new { root = outbox, shot = Shot(), context = new { map = "PEI" } }).GetProperty("dir").GetString();
        var toasts = new List<string>();
        await BugReportOutbox.UploadDir(dir, $"http://127.0.0.1:{port}/", "secret", toasts.Add);
        await serve;
        Assert.That(auth, Is.EqualTo("Bearer secret"));
        Assert.That(body, Does.Contain("name=context").And.Contain("name=screenshot"));
        Assert.That(Directory.Exists(dir), Is.EqualTo(kept));
        Assert.That(Directory.GetDirectories(outbox).Any(d => Path.GetFileName(d).StartsWith($"rejected_{status}_")), Is.EqualTo(parked));
        Assert.That(toasts, Has.Count.EqualTo(1));
        // Parked reports never re-enter the retry queue.
        var retry = Call(host, "bugReportRetry", new { root = outbox, endpoint = "http://127.0.0.1:1/" });
        Assert.That(retry.GetProperty("queued").GetInt32(), Is.EqualTo(kept ? 1 : 0));
    }
}
