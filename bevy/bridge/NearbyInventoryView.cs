using System.Numerics;
using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot.Net;
using V = UnityEngine.Vector3;

namespace UnturnedBevy;

/// <summary>Original WorldItem.HasLineOfSightFrom: current model hitbox centre
/// and eight padded corners, any clear source-world ray. The current native
/// dropped pose is deterministic by net ID; accepted stuck arrows override it.</summary>
public sealed class NearbyGeometry
{
    readonly WorldGeometry _geometry;
    readonly Func<uint,WarheadServices.Pose> _pose;
    readonly Dictionary<ushort,(Vector3 Center,Vector3 Size)> _boxes = new();
    public NearbyGeometry(string root,WorldGeometry geometry,Func<uint,WarheadServices.Pose> pose)
    {
        _geometry=geometry;_pose=pose;
        string path=Path.Combine(root,"items/items_manifest.json");
        if(!File.Exists(path))return;
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        foreach(var p in doc.RootElement.EnumerateObject())
        {
            if(!ushort.TryParse(p.Name,out var id)||!p.Value.TryGetProperty("obj",out var obj)
                ||!File.Exists(Path.Combine(root,"items",obj.GetString()??"")))continue;
            Vector3 Read(string name) {var a=p.Value.GetProperty(name).EnumerateArray().Select(v=>v.GetSingle()).ToArray();return new(a[0],a[1],a[2]);}
            var center=Read("center");var size=Vector3.Max(Vector3.Abs(Read("box")),new(.001f)) * 1.15f;
            _boxes[id]=(center,size);
        }
    }
    public V[] Samples(WorldItemReplication.WorldItemEntity item)
    {
        var box=_boxes.GetValueOrDefault(item.ItemId,(Vector3.Zero,new Vector3(.24f)*1.15f));
        var pose=_pose?.Invoke(item.NetIdValue);
        Quaternion rotation;
        // Match native Vec3::try_normalize and Transform::look_to exactly:
        // collinear UnitY picks UnitY.any_orthonormal_vector() == -Z.
        static bool Unit(Vector3 v,out Vector3 unit) {float inverse=1/v.Length();
            unit=v*inverse;return float.IsFinite(inverse)&&inverse>0;}
        if(pose?.Direction is { Length:3 } d && Unit(new(d[0],d[1],d[2]),out var forward))
        {
            if(!Unit(Vector3.Cross(forward,Vector3.UnitY),out var right))right=-Vector3.UnitZ;
            var up=Vector3.Cross(right,forward);
            rotation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(right.X,right.Y,right.Z,0,up.X,up.Y,up.Z,0,
                -forward.X,-forward.Y,-forward.Z,0,0,0,0,1));
            if(item.ItemId is 347 or 348 or 351 or 352)
            {
                box.Item1=Vector3.Transform(box.Item1,Quaternion.CreateFromAxisAngle(Vector3.UnitX,-MathF.PI/2));
                box.Item2=new(box.Item2.X,box.Item2.Z,box.Item2.Y);
            }
        }
        else if(pose?.Euler is { Length:3 } e) rotation=Quaternion.CreateFromYawPitchRoll(e[1],e[0],e[2]);
        else rotation=Quaternion.CreateFromYawPitchRoll(unchecked(item.NetIdValue*137)%360*MathF.PI/180,MathF.PI/2,0);
        var points=new V[9];int at=0;
        void Add(Vector3 p) {p=Vector3.Transform(p,rotation);points[at++]=item.Pos+new V(p.X,p.Y,p.Z);}
        Add(box.Item1);var half=box.Item2*.5f;
        foreach(int z in new[]{1,-1})foreach(int y in new[]{1,-1})foreach(int x in new[]{1,-1})
            Add(box.Item1+half*new Vector3(x,y,z));
        return points;
    }
    public bool Visible(WorldItemReplication.WorldItemEntity item,V eye) => Samples(item).Any(p=>!_geometry.SourceWorldBlocked(eye,p));
}

public sealed partial class WorldGeometry
{
    public bool SourceWorldBlocked(V from,V to) => RayContact(from,to,out _,out _,out _,out _,sourceWorldOnly:true);
}

public sealed partial class BridgeHost
{
    void ConfigureNearby() => _server.NearbyVisible = new NearbyGeometry(_catalog.Root,_geometry,id=>_warheads?.PoseOf(id)).Visible;
    uint _nearbyRequestId;
    object RequestNearby(JsonElement args)
    {
        var action=Text(args,"action","Open");
        if(!Enum.TryParse<NearbyAction>(action,true,out var kind))return new{queued=false};
        uint U(string key)=>args.TryGetProperty(key,out var v)&&v.TryGetUInt32(out uint n)?n:0;
        ulong token=args.TryGetProperty("token",out var t)&&t.TryGetUInt64(out ulong n)?n:0;
        if (kind != NearbyAction.Close && !args.TryGetProperty("eye",out _)) return new {queued=false};
        uint request=U("requestId");
        if(request==0) {request=++_nearbyRequestId;if(request==0)request=++_nearbyRequestId;}
        else if(unchecked((int)(request-_nearbyRequestId))>0)_nearbyRequestId=request;
        var command=new NearbyInventoryCommand { Action=kind,RequestId=request,ScanId=U("scanId"),Revision=U("revision"),
            WorldId=U("worldId"),Token=token,X=checked((byte)Int(args,"x")),Y=checked((byte)Int(args,"y")),
            CrateId=U("crateId"),Destination=checked((byte)Int(args,"destination")),
            Eye=kind==NearbyAction.Close?default:Vec(args,"eye") };
        return new {queued=_client.SendNearbyInventory(command)};
    }
    PlayerInventory NearbyInventory(PlayerInventory inventory)
    {
        if(inventory==null||_client.Nearby.ScanId==0)return inventory;
        var copy=new PlayerInventory();
        for(int p=0;p<inventory.items.Length;p++)if(p!=PlayerInventory.AREA)copy.items[p]=inventory.items[p];
        copy.wornHat=inventory.wornHat;copy.wornGlasses=inventory.wornGlasses;copy.wornMask=inventory.wornMask;
        copy.wornShirt=inventory.wornShirt;copy.wornVest=inventory.wornVest;copy.wornBackpack=inventory.wornBackpack;copy.wornPants=inventory.wornPants;
        var area=copy.items[PlayerInventory.AREA];byte side=_client.Nearby.HasCandidates?(byte)6:(byte)0;area.loadSize(side,side);
        foreach(var row in _client.Nearby.Rows??[])area.addItem(row.Jar.x,row.Jar.y,row.Jar.rot,row.Jar.item);
        return copy;
    }
    object NearbyState() => new {scanId=_client.Nearby.ScanId,revision=_client.Nearby.Revision,requestId=_client.Nearby.RequestId,
        result=_client.Nearby.Result.ToString(),rows=(_client.Nearby.Rows??[]).Select(r=>new{x=r.Jar.x,y=r.Jar.y,
            sources=r.Sources.Select(s=>new{worldId=s.WorldId,token=s.Token,itemId=s.ItemId,amount=s.Amount}).ToArray()}).ToArray()};
}
