using Godot;
using SDG.Unturned;

namespace UnturnedBevy;

public sealed partial class NpcHeliCrewBody
{
    public uint Vehicle;public int Index,Seat;public string Gun;
    public Vector3 LocalFeet;
    public float Health=new PlayerVitalsSim().MaxHealth;
    public bool Down=>Health<=0;
    public float LastDamage;public int TimesDowned;
    public enum HitZone {Legs,Torso,Head}
    public HitZone LastZone;
    public Func<Vector3,Vector3> ToLocal;
    public Action OnDown;
    public static object Metadata()=>new{rig=NpcCrewRules.Rig,face=NpcCrewRules.Face,
        tint=new[]{NpcCrewRules.Tint.R,NpcCrewRules.Tint.G,NpcCrewRules.Tint.B},
        gunRotationDegrees=V(NpcCrewRules.GunRotationDegrees),clip=(string)null,
        bodySize=V(NpcCrewRules.BodySize),bodyCenter=V(NpcCrewRules.BodyCenter)};
    static float[] V(Vector3 v)=>[v.X,v.Y,v.Z];
}
