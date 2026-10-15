using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using UnturnedBevy;
using UnturnedGodot.Net;
using UnityEngine;

namespace UnturnedBevyTests;

[TestFixture, NonParallelizable]
public sealed class DeployableSnapshotTests
{
    [Test]
    public void NativeViewRetainsReplicatedTopMountBitAlongsideAcceptedSurfacePoint()
    {
        string content = null;
        for (var p = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); p != null; p = p.Parent)
            if (Directory.Exists(Path.Combine(p.FullName,"game/content"))) { content = Path.Combine(p.FullName,"game/content"); break; }
        Assert.That(content, Is.Not.Null);
        using var host = new BridgeHost();
        host.Handle("init",JsonSerializer.SerializeToElement(new { contentRoot=content }));
        var server = (NetWorldServer)typeof(BridgeHost).GetField("_server",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(host)!;
        var top = server.Deployables.ServerPlace(server.Ids.Mint(),9216,0,new Vector3(0,5,-2),37,server.Session.CurrentTick);
        top.MountUp = true;
        var side = server.Deployables.ServerPlace(server.Ids.Mint(),9216,0,new Vector3(2,5,-2),37,server.Session.CurrentTick);
        for (int i=0;i<20;i++) host.Handle("tick",JsonSerializer.SerializeToElement(new {}));
        var state = JsonSerializer.SerializeToElement(host.State());
        var rows = state.GetProperty("deployables").EnumerateArray().ToDictionary(r=>r.GetProperty("netId").GetUInt32());
        Assert.That(rows[top.NetIdValue].GetProperty("mountUp").GetBoolean(), Is.True,
            "Dropping the accepted bit makes a top-mounted adapter render on a side");
        Assert.That(rows[side.NetIdValue].GetProperty("mountUp").GetBoolean(), Is.False);
        Assert.That(rows[top.NetIdValue].GetProperty("position")[1].GetSingle(), Is.EqualTo(5),
            "The bridge exposes the accepted surface point, never a pre-lifted render origin");
    }
}
