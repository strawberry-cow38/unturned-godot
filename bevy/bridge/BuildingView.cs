using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using UnturnedSim;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    // Read-only source assembly. This does not initialize a session, register
    // entities, write files or mutate map/world state. Native editor integration
    // must separately own placement and authority when it becomes reachable.
    object ReadBuildingView(JsonElement request)
    {
        string contentRoot = Path.GetFullPath(Text(request, "contentRoot", "game/content"));
        var scene = new BuildingScene(contentRoot);
        var openings = new BuildingOpenings(contentRoot);
        string palette = null;
        if (request.TryGetProperty("text", out var text))
        {
            if (request.TryGetProperty("spec", out _)) throw new ArgumentException("Supply source text or generator spec, not both");
            scene.LoadText(text.GetString() ?? throw new ArgumentException("Building text must be a string"));
        }
        else
        {
            var options = new JsonSerializerOptions(JsonOptions);
            options.Converters.Add(new JsonStringEnumConverter());
            var spec = request.TryGetProperty("spec", out var input)
                ? input.Deserialize<BuildingGenSpec>(options) ?? throw new ArgumentException("Building spec is null")
                : new BuildingGenSpec();
            palette = scene.Generate(spec).Palette;
        }
        static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
        static float[][] B(Basis b) => [V(b.X), V(b.Y), V(b.Z)];
        static object T(BuildingTransform t) => new { position = V(t.Position), basis = B(t.Basis) };
        static float[] C(Color c) => [c.R, c.G, c.B, c.A];
        static object Shape(BuildingCollision c) => new {
            position = V(c.Position),
            box = c.Shape is BuildingBox box ? V(box.Size) : null,
            hull = c.Shape is BuildingHull hull ? hull.Points.Select(V).ToArray() : null,
        };
        return new {
            version = 1, readOnly = true, contentRoot, palette, stageOrigin = V(BuildingScene.StageOrigin),
            text = scene.SaveText(),
            walls = scene.Walls.Select((wall, index) => {
                var geometry = wall.Build();
                return new {
                    index, kind = wall.Kind.ToString(), position = V(wall.Position), basis = B(wall.Basis),
                    front = new { triangles = geometry.Front.Select(V).ToArray(), color = C(wall.Tint) },
                    back = new { triangles = geometry.Back.Select(V).ToArray(), color = C(wall.BackTint) },
                    trim = new { triangles = geometry.Trim.Select(V).ToArray(), color = C(wall.TrimTint) },
                    // Original WallSurface bodies: default world and bit6 trim.
                    solids = new { layer = 1u, shapes = geometry.Solids.Select(Shape).ToArray() },
                    trimSolids = new { layer = 64u, shapes = geometry.TrimSolids.Select(Shape).ToArray() },
                    openings = wall.Openings,
                    doorModels = wall.Openings.Select((o, openingIndex) => {
                        var door = openings.Door(wall,o);
                        return door == null ? null : new {
                            openingIndex, name=door.Name, pose=T(door.Transform),
                            leaves=door.Leaves.Select(leaf => new {
                                mesh=leaf.MeshFile, scale=V(leaf.Scale), pivot=V(leaf.Pivot), axis=V(leaf.Axis),
                                angleDegrees=leaf.Angle,durationSeconds=leaf.Duration,sound=leaf.Sound,
                                openCurve=leaf.OpenCurve?.Select(p=>new[]{p.X,p.Y}).ToArray(),
                                closeCurve=leaf.CloseCurve?.Select(p=>new[]{p.X,p.Y}).ToArray(),
                                body=T(leaf.Body),slide=V(leaf.Slide),action=leaf.Action.ToString(),
                                colliderCenter=V(leaf.ColliderCenter),colliderSize=V(leaf.ColliderSize),
                            }).ToArray(),
                        };
                    }).Where(d=>d!=null).ToArray(),
                    glassModels = wall.Openings.Select((o, openingIndex) => {
                        var pane=BuildingOpenings.Pane(wall,o);
                        return pane==null?null:new {openingIndex,position=V(pane.Position),size=V(pane.Size),
                            axis=V(pane.Axis),color=C(pane.Color),health=pane.Health,indestructible=pane.Indestructible,
                            covered=pane.Covered,layer=64u,mask=0u};
                    }).Where(p=>p!=null).ToArray(),
                };
            }).ToArray(),
            ramps = scene.Ramps.Select(r => new { position = V(r.Position), basis = B(r.Basis), size = V(r.Size) }).ToArray(),
            rooms = scene.Designations, doors = scene.DoorMarks, furniture = scene.Furniture,
        };
    }
}
