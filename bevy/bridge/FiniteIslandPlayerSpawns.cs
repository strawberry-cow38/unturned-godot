using Godot;

namespace UnturnedBevy;

/// <summary>Original EditorSpawns player translator; positions already use
/// runtime X/Z and regular versus alternate remains explicit.</summary>
public sealed partial class FiniteIslandPlayerSpawns
{
    public sealed record Spawn(Vector3 Pos,float Yaw,bool IsAlt,int Type);
    public List<Spawn> Points { get; } = new();
}
