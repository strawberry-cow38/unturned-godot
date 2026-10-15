using System.Text.Json;
using System.Numerics;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Immutable export of the actual Vehicle.BuildByName/TurretDef table.
/// The native registration supplies a type id, never a gun, belt or muzzle.</summary>
public sealed class MountedWeaponCatalog
{
    public sealed class Mount
    {
        public int Seat { get; set; }
        public float[] Pivot { get; set; } = [0,0,0];
        public float[] PitchOffset { get; set; } = [0,0,0];
        public float[] Muzzle { get; set; } = [0,0,0];
        public float[] GunnerAt { get; set; } = [0,0,0];
        public float YawMin { get; set; } = -180; public float YawMax { get; set; } = 180;
        public float PitchMin { get; set; } = -20; public float PitchMax { get; set; } = 60;
        public string GunId { get; set; }
        public int Belt { get; set; }
        public float Cycle { get; set; }
        public bool YawFree { get; set; }
        public bool DriverFallback { get; set; }
        public bool CrosshairAim { get; set; }
        public bool HoldToAim { get; set; }
        public int ParentMount { get; set; } = -1;
        public float YawRate { get; set; } public float PitchRate { get; set; }
        public GunDef Gun;
    }
    public sealed record Fleet(byte TypeId,string Key,Mount[] Mounts,bool Tank);
    readonly Dictionary<byte,Fleet> _fleet = new();
    public float RearDepressionScale { get; }
    public MountedWeaponCatalog(string contentRoot)
    {
        using var stream = typeof(MountedWeaponCatalog).Assembly.GetManifestResourceStream("UnturnedBevy.VehicleSpecs.json")!;
        using var doc = JsonDocument.Parse(stream);
        RearDepressionScale = doc.RootElement.GetProperty("tank").GetProperty("rearDepressionScale").GetSingle();
        foreach(var row in doc.RootElement.GetProperty("vehicles").EnumerateArray())
        {
            if(!row.GetProperty("spec").TryGetProperty("Turrets",out var turrets)||turrets.ValueKind!=JsonValueKind.Array||turrets.GetArrayLength()==0)continue;
            var mounts=turrets.Deserialize<Mount[]>();
            if(mounts.Length>VehicleMountReplication.MaxMounts)throw new InvalidDataException("Too many source mounts");
            for(int i=0;i<mounts.Length;i++)
            {
                var m=mounts[i];
                if(m.ParentMount>=i||m.ParentMount< -1||m.Belt<0||m.Belt>ushort.MaxValue||m.Cycle<=0||m.Cycle>60)
                    throw new InvalidDataException("Invalid source mount topology/belt/cycle");
                string path=Path.Combine(contentRoot,m.GunId+".dat");
                m.Gun=File.Exists(path)?GunDef.FromDatText(File.ReadAllText(path)):null;
            }
            byte type=row.GetProperty("typeId").GetByte();string key=row.GetProperty("key").GetString();
            _fleet.Add(type,new(type,key,mounts,key=="tank"));
        }
    }
    public Fleet For(byte type)=>_fleet.GetValueOrDefault(type);
    public object GunMetadata()=>_fleet.Values.SelectMany(f=>f.Mounts).DistinctBy(m=>m.GunId)
        .ToDictionary(m=>m.GunId,m=>m.Gun?.BlastRadius??0);
    public static Vector3 V(float[] v)=>new(v[0],v[1],v[2]);
}
