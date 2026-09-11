using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Testing
{
    /// <summary>Retail's own walkie-talkie (1445) in a hand, through the Tool held path.
    ///
    /// The item, its ripped mesh and its palette have all been on disk since 24 Aug. What was missing was a
    /// ToolDef entry -- KindOf asks ToolDef.ById, so without one the asset was never a Tool and nothing ever
    /// put it in a hand. That is a failure with no symptom: the item exists, the catalog lists it, the mesh
    /// parses, and it simply never appears.
    ///
    /// ⚠ THE CORRECT RESULT LOOKS LIKE A BROKEN ONE. items/1445.png is a 2x2 palette whose four texels are
    /// (66,66,66) (29,29,29) (55,55,55) (0,0,0) -- a black radio really is four shades of near-black. So
    /// "it rendered dark" is not evidence of a missing texture. The discriminator is the other way round:
    /// the flat fallback tint is 0.647 grey, which is LIGHTER than every texel in the palette. Lighter means
    /// the albedo did not land; darker means it did.</summary>
    public sealed class WalkieTalkieToolTests : GameTest
    {
        public override string Name => "items.walkie_tool";
        public override double TimeoutSimSeconds => 30;
        const ushort Id = 1445;

        /// <summary>A capture that is one flat colour is a capture of nothing. Astra's version of this test had
        /// this helper and mine did not, which is why my first run saved a BLANK png and still reported ten
        /// green checks -- I asserted that a file was written, never that anything was in it.</summary>
        static bool HasImageContent(Image image)
        {
            if (image == null || image.IsEmpty()) return false;
            var background = image.GetPixel(0, 0);
            for (int y = 0; y < image.GetHeight(); y += 4)
                for (int x = 0; x < image.GetWidth(); x += 4)
                    if (image.GetPixel(x, y) != background) return true;
            return false;
        }

        void SaveCapture(Image image, string path)
        {
            T.Check($"{Path.GetFileName(path)} actually contains geometry", HasImageContent(image));
            T.Check($"{Path.GetFileName(path)} saves", image?.SavePng(path) == Error.Ok);
        }

        Step DrawFrames(int n = 6)
        {
            ulong f = Engine.GetProcessFrames();
            return Until(() => Engine.GetProcessFrames() >= f + (ulong)n, 10);
        }

        static TNode Find<TNode>(Node root) where TNode : Node
        {
            if (root is TNode match) return match;
            foreach (Node child in root.GetChildren())
                if (Find<TNode>(child) is TNode found) return found;
            return null;
        }

        public override IEnumerable<Step> Run()
        {
            string capture = System.Environment.GetEnvironmentVariable("UG_WALKIE_CAPTURE");
            if (capture != null)
            {
                Directory.CreateDirectory(capture);
                Tree.Root.Mode = Window.ModeEnum.Windowed;
                Tree.Root.Size = Tree.Root.ContentScaleSize = new Vector2I(1280, 720);
            }

            var def = ToolDef.ById(Id);
            T.Check("1445 has a ToolDef, which is what makes KindOf call it a Tool", def != null);
            if (def == null) yield break;
            T.Check($"...pointing at the RIP, not an authored copy ({def.HeldMesh})", def.HeldMesh == "items/1445.txt");
            T.Check("...with no borrowed trigger behaviour", !def.IsRope && !def.IsHose && !def.IsDetonator);

            ItemCatalog.RegisterAll();
            var asset = Assets.find(Id);
            T.Check("the catalog still has the retail item", asset != null && asset.itemName == "Walkie Talkie");
            if (asset == null) yield break;

            var mesh = ContentProvider.ParseObj($"res://content/{def.HeldMesh}");
            T.Check("the ripped mesh loads", mesh != null && mesh.GetSurfaceCount() == 1);
            if (mesh == null) yield break;
            var box = mesh.GetAabb().Size;
            // The retail proportions, asserted rather than eyeballed -- this is the number that made the
            // authored reconstruction obviously wrong (it was 2.6x narrower).
            T.Check($"...at retail's own proportions ({box.X:0.###} x {box.Y:0.###} x {box.Z:0.###})",
                    Mathf.Abs(box.X - 0.2769f) < 0.01f && Mathf.Abs(box.Y - 0.6702f) < 0.01f && Mathf.Abs(box.Z - 0.1452f) < 0.01f);

            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0, 1, 0));
            yield return Ticks(3);
            T.Check("equipping it is accepted", p.EquipItemAsset(asset, new Item(Id)));
            yield return Ticks(5);

            var vm = Find<Viewmodel>(p);
            var attach = vm?.ArmsRig?.Skeleton?.GetNodeOrNull<BoneAttachment3D>("GunAttach");
            var held = attach == null ? null : Find<MeshInstance3D>(attach);
            T.Check("the first-person hand mounts the ripped mesh", held != null && held.Mesh == mesh);

            // THE POINT OF cow tools' ToolDef.HeldAlbedo: without it this material carries no texture at all
            // and the whole radio is one flat tint.
            var mat = held?.MaterialOverride as StandardMaterial3D;
            // ⚠ "IS 2x2" IS NOT "IS THE RIGHT 2x2". 114 of the ~400 item textures are 2x2 palettes, so a size
            // check passes against entirely the wrong item's swatch. Compare the actual texels against the
            // file on disk.
            T.Check("the held material carries a 2x2 palette",
                    mat?.AlbedoTexture?.GetWidth() == 2 && mat.AlbedoTexture.GetHeight() == 2);
            var onDisk = ContentProvider.LoadImage(ProjectSettings.GlobalizePath($"res://content/{def.HeldAlbedo}"));
            var onMat = mat?.AlbedoTexture?.GetImage();
            bool samePalette = onDisk != null && onMat != null
                               && onDisk.GetWidth() == onMat.GetWidth() && onDisk.GetHeight() == onMat.GetHeight();
            if (samePalette)
                for (int y = 0; y < onDisk.GetHeight(); y++)
                    for (int x = 0; x < onDisk.GetWidth(); x++)
                        if (!onDisk.GetPixel(x, y).IsEqualApprox(onMat.GetPixel(x, y))) samePalette = false;
            if (onMat != null)
                GD.Print($"[walkie] held palette texels: {onMat.GetPixel(0,0)} {onMat.GetPixel(1,0)} {onMat.GetPixel(0,1)} {onMat.GetPixel(1,1)}");
            if (onDisk != null)
                GD.Print($"[walkie] items/1445.png texels: {onDisk.GetPixel(0,0)} {onDisk.GetPixel(1,0)} {onDisk.GetPixel(0,1)} {onDisk.GetPixel(1,1)}");
            T.Check("...and it is 1445's OWN palette, texel for texel", samePalette);
            // ...and the tint must NOT still be multiplying it. AlbedoTint multiplies, so a leftover HeldColor
            // would darken every texel and the palette would get the blame.
            T.Check($"...un-multiplied by a leftover tint ({mat?.AlbedoColor})",
                    mat != null && mat.AlbedoColor.IsEqualApprox(Colors.White));

            if (capture != null && vm != null && held != null)
            {
                yield return DrawFrames();   // the viewmodel needs frames before it has drawn anything at all
                SaveCapture(vm.CaptureViewport(), Path.Combine(capture, "tool-held.png"));

                // Back-face culling on the same mesh: the production material is double-sided, which hides
                // inverted winding. The rips are stored winding-reversed and go through ParseObj, which does
                // NOT reverse -- so this is the check that matters, not a nicety. Still-visible geometry after
                // culling is the pass; a blank frame here would mean we had been looking at the inside.
                mat.CullMode = BaseMaterial3D.CullModeEnum.Back;
                yield return DrawFrames();
                SaveCapture(vm.CaptureViewport(), Path.Combine(capture, "tool-held-culled.png"));

                // A clean orthographic look at the held mesh itself, because the first-person frame crops it
                // and a picture you cannot read settles nothing.
                vm.SetShown(false);
                p.SetProcess(false); p.SetPhysicsProcess(false);
                var vp = new SubViewport { Size = new Vector2I(640, 640), OwnWorld3D = true,
                                           RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
                World.AddChild(vp);
                vp.AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                    BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.24f, 0.27f, 0.31f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = Colors.White, AmbientLightEnergy = 0.8f } });
                vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, -30, 0), LightEnergy = 1.2f });
                var solo = new MeshInstance3D { Mesh = mesh, MaterialOverride = (Material)mat.Duplicate() };
                vp.AddChild(solo);
                var cam = new Camera3D { Current = true, Projection = Camera3D.ProjectionType.Orthogonal, Size = 0.85f };
                vp.AddChild(cam);
                Vector3 target = mesh.GetAabb().GetCenter();
                cam.Position = target + new Vector3(0.55f, 0.30f, 0.75f);
                cam.LookAt(target, Vector3.Up);
                yield return DrawFrames();
                RenderingServer.ForceDraw();
                SaveCapture(vp.GetTexture().GetImage(), Path.Combine(capture, "tool-solo.png"));
            }
        }
    }
}
