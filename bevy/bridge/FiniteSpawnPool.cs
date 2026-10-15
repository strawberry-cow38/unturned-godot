using UnityEngine;

namespace UnturnedBevy;

/// <summary>EditorPlayMode's regular spawn pool and PlayerController's respawn
/// choice. This owns no player or scene and does not load generator metadata.</summary>
public sealed class FiniteSpawnPool
{
    public readonly record struct Point(Vector3 Position, float Yaw);
    readonly List<Point> _points;
    readonly Dictionary<ushort, Point> _pending = new();
    readonly Func<int, int> _pick;
    Vector3 _translation;

    public FiniteSpawnPool(IEnumerable<Point> points, Func<int, int> pick = null)
    {
        _points = points.ToList();
        if (_points.Any(p => !Finite(p.Position) || !float.IsFinite(p.Yaw)))
            throw new InvalidDataException("Non-finite authored player spawn");
        _pick = pick ?? Random.Shared.Next;
    }

    public static FiniteSpawnPool Load(string path, Func<int, int> pick = null)
    {
        var source = new FiniteIslandPlayerSpawns();
        source.Load(path);
        // Original EditorSpawns.PlayerSpawnPoints returns a COPY of regular
        // points, without grounding, origin filtering, or filtering by Type.
        return new(source.Points.Where(p => !p.IsAlt).Select(p =>
            new Point(new(p.Pos.X, p.Pos.Y, p.Pos.Z), p.Yaw)), pick);
    }

    public int Count => _points.Count;
    public Point[] Points => _points.ToArray();

    /// Entry sets only Position; unlike PickRandomSpawn it does not use Yaw.
    public Vector3? Initial() => _points.Count == 0 ? null : Choose().Position;

    /// Rebase preflight and the actual revive must see the SAME random point.
    /// Recheck the bed each time: changing/removing it during the death timer
    /// must not reuse a previously selected random fallback.
    public Vector3 Resolve(ushort owner, Vector3? bed, Vector3 fallback, Vector3 current)
    {
        Vector3 target;
        if (bed is Vector3 at)
        {
            _pending.Remove(owner);
            target = at + Vector3.up * 0.5f;
        }
        else if (_points.Count > 0)
        {
            if (!_pending.TryGetValue(owner, out var selected))
                _pending[owner] = selected = Choose();
            target = selected.Position;
        }
        else target = fallback;

        // Original Respawn guards the chosen point AFTER PickRandomSpawn has
        // selected its yaw. If a placeholder is replaced, that yaw survives.
        if ((target - _translation).sqrMagnitude < 4f)
        {
            Vector3 alternate = fallback;
            if ((alternate - _translation).sqrMagnitude < 4f)
                foreach (var point in _points)
                    if ((point.Position - _translation).sqrMagnitude >= 4f)
                    { alternate = point.Position; break; }
            if ((alternate - _translation).sqrMagnitude < 4f) alternate = current;
            target = alternate;
        }
        return target;
    }

    /// Only the regular-pool branch changes yaw. Bed.TryGetSpawn's out-yaw is
    /// deliberately discarded by original PlayerController.Respawn.
    public float? Complete(ushort owner) => _pending.Remove(owner, out var point) ? point.Yaw : null;
    public void Forget(ushort owner) => _pending.Remove(owner);

    public void Translate(Vector3 delta)
    {
        _translation += delta;
        for (int i = 0; i < _points.Count; i++)
            _points[i] = _points[i] with { Position = _points[i].Position + delta };
        foreach (var owner in _pending.Keys.ToArray())
            _pending[owner] = _pending[owner] with { Position = _pending[owner].Position + delta };
    }

    Point Choose()
    {
        int index = _pick(_points.Count);
        if ((uint)index >= (uint)_points.Count) throw new InvalidOperationException("Invalid spawn RNG result");
        return _points[index];
    }
    static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
}
