using NUnit.Framework;
using UnturnedBevy;
using UnityEngine;

namespace UnturnedBevy.Tests;

public sealed class FiniteSpawnPoolTests
{
    static readonly FiniteSpawnPool.Point A = new(new(12.25f, 33.75f, -44.5f), 137.25f);
    static readonly FiniteSpawnPool.Point B = new(new(91, 2, -3), -46);

    [Test]
    public void OriginalTranslatorKeepsExactRegularPositionsAndInitialDoesNotGround()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "12.25 33.75 -44.5 137.25 0 5\n999 888 777 9 1 0\n91 2 -3 -46 0 0\n");
            var pool = FiniteSpawnPool.Load(path, n => n - 1);
            Assert.That(pool.Points, Is.EqualTo(new[] { A, B }));
            Assert.That(pool.Initial(), Is.EqualTo(B.Position));
            Assert.That(pool.Complete(1), Is.Null, "entry has no pending respawn yaw");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void PreflightAndReviveShareOneChoiceAndNextLifeRerolls()
    {
        int picks = 0;
        var pool = new FiniteSpawnPool(new[] { A, B }, n => picks++ % n);
        Assert.That(pool.Resolve(1, null, B.Position, Vector3.zero), Is.EqualTo(A.Position));
        Assert.That(pool.Resolve(1, null, B.Position, Vector3.zero), Is.EqualTo(A.Position));
        Assert.That(picks, Is.EqualTo(1));
        Assert.That(pool.Complete(1), Is.EqualTo(A.Yaw));
        Assert.That(pool.Complete(1), Is.Null);
        Assert.That(pool.Resolve(1, null, A.Position, Vector3.zero), Is.EqualTo(B.Position));
        Assert.That(picks, Is.EqualTo(2));
    }

    [Test]
    public void BedChangesRetireRandomChoiceAndNeverOverrideYaw()
    {
        int picks = 0;
        var pool = new FiniteSpawnPool(new[] { A, B }, n => picks++ % n);
        pool.Resolve(1, null, A.Position, A.Position);
        Assert.That(pool.Resolve(1, new(15, 20, 25), A.Position, A.Position), Is.EqualTo(new Vector3(15, 20.5f, 25)));
        Assert.That(pool.Resolve(1, new(25, 30, 35), A.Position, A.Position), Is.EqualTo(new Vector3(25, 30.5f, 35)));
        Assert.That(pool.Complete(1), Is.Null);
        Assert.That(pool.Resolve(1, null, A.Position, A.Position), Is.EqualTo(B.Position), "removed bed re-rolls");
        pool.Forget(1);
        Assert.That(pool.Complete(1), Is.Null, "disconnected owner leaves no pending life");
    }

    [Test]
    public void PlaceholderGuardPreservesChosenYawAndRebasePreservesWorldMeaning()
    {
        var pool = new FiniteSpawnPool(new[] { new FiniteSpawnPool.Point(Vector3.zero, 88), A }, _ => 0);
        Assert.That(pool.Resolve(1, null, Vector3.zero, B.Position), Is.EqualTo(A.Position));
        var delta = new Vector3(-256, 0, 512);
        pool.Translate(delta);
        Assert.That(pool.Resolve(1, null, delta, B.Position + delta), Is.EqualTo(A.Position + delta));
        Assert.That(pool.Complete(1), Is.EqualTo(88));
        var empty = new FiniteSpawnPool(Array.Empty<FiniteSpawnPool.Point>());
        Assert.That(empty.Initial(), Is.Null);
        Assert.That(empty.Resolve(1, Vector3.zero, Vector3.zero, B.Position), Is.EqualTo(B.Position));
        Assert.That(empty.Complete(1), Is.Null);
        Assert.Throws<InvalidDataException>(() => new FiniteSpawnPool(new[] { A with { Yaw = float.NaN } }));
    }
}
