using Godot;
using System;
using System.IO;
using System.Linq;

namespace UnturnedGodot
{
    /// <summary>Small local editor workbench for visual review. Not loaded by the game or test runner.</summary>
    public partial class TextPropsPreview : Node3D
    {
        string _out;
        public override async void _Ready()
        {
            _out = System.Environment.GetEnvironmentVariable("UG_TEXT_REVIEW_DIR");
            GrassDisplacers.EnsureGlobals();
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.46f,.5f,.53f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .65f };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30,-25,0), LightEnergy = .8f, ShadowEnabled = true });
            Box("Ground",new Vector3(50,.2f,40),new Vector3(0,-.1f,0),new Color(.49f,.52f,.48f));
            Box("Facade",new Vector3(12,9,.4f),new Vector3(0,4.5f,-.2f),new Color(.72f,.70f,.63f));
            Box("HeaderBoard",new Vector3(10.5f,1.8f,.15f),new Vector3(0,7,.08f),new Color(.22f,.28f,.30f));
            Box("CafeBoard",new Vector3(6.6f,2.5f,.15f),new Vector3(-2,4,.08f),new Color(.32f,.41f,.35f));
            Box("OpenBoard",new Vector3(2.3f,2.5f,.15f),new Vector3(3.8f,4,.08f),new Color(.35f,.25f,.20f));
            var cam = new EditorCamera { Position = new Vector3(.7f,4.7f,20), Projection = Camera3D.ProjectionType.Orthogonal, Size = 13.5f, Near = .01f }; AddChild(cam);
            var ed = new Editor(); ed.Setup("TextPropsReview_"+System.Environment.ProcessId,this,cam); AddChild(ed);
            var objects = new EditorObjects(ed,this,cam); ed.AddChild(objects); ed.Objects = objects;
            var ui = new CanvasLayer { Layer = 50 }; ed.AddChild(ui); ui.AddChild(new EditorObjectBrowser(objects));
            var title = new Label { Text = "LEVEL  |  Text prop editor workbench", Position = new Vector2(12,10) }; title.AddThemeFontSizeOverride("font_size",24); ui.AddChild(title);
            var a = objects.CreateText(new TextPropStyle { Text="Hardware",Height=1.05f,Depth=.18f,Align=1,Anchor=2,Color="eee8cf" },new Transform3D(Basis.Identity,new Vector3(0,7,.18f)));
            var b = objects.CreateText(new TextPropStyle { Text="Northside\nDoughnuts",Height=.65f,Depth=.10f,Tracking=.02f,LineSpacing=1.5f,Align=1,Anchor=2,Color="f0e9d3" },new Transform3D(Basis.Identity,new Vector3(-2,4,.18f)));
            var c = objects.CreateText(new TextPropStyle { Text="24/7\nOPEN",Height=.38f,Depth=.08f,Tracking=.1f,LineSpacing=1.6f,Align=1,Anchor=2,Color="edc58b" },new Transform3D(Basis.Identity,new Vector3(3.8f,4,.18f)));
            var d = objects.CreateText(new TextPropStyle { Text="SERVICE & REPAIRS",Height=.34f,WidthScale=1.1f,Depth=.04f,Tracking=.07f,Align=1,Anchor=2,Color="414b52" },new Transform3D(Basis.Identity,new Vector3(0,1.65f,.02f)));
            objects.SelectText(b); objects.TextTool.Open(b);
            if (string.IsNullOrEmpty(_out)) return; // interactive workbench unless capture explicitly requested
            Directory.CreateDirectory(_out);
            await Shot("Text_Prop_Editor_Panel");
            objects.TextTool.Close(); objects.ClearTransientVisuals(); ui.Visible = false;
            cam.SetPose(new Vector3(5,5.8f,20),14,-3);
            await Shot("Text_Prop_Placed_Examples");
            // Exercise the normal authored-data path as part of this visual session; no test suite is invoked.
            ed.Save(); var saved = TextPropStore.Read(ed.MapName);
            var old = GetChildren().OfType<ModeledTextProp>().ToArray(); objects.RemovePlaced(old);
            foreach (var record in saved) objects.PutText(record);
            Log.Print($"[text-review] reloaded {saved.Count} editable text props from {TextPropStore.PathFor(ed.MapName)}");
            await Shot("Text_Prop_After_Save_Reload");
            var first = objects.FindText(saved[0].Id); var change = first.Style.Copy(); change.Text="Hardware 24/7";
            objects.EditText(first.Id,change); ed.Undo(); ed.Redo(); ed.Undo();
            Log.Print($"[text-review] edit → undo → redo → undo: '{objects.FindText(saved[0].Id).Style.Text}'");
            GetTree().Quit();
        }
        void Box(string name,Vector3 size,Vector3 position,Color color)
        {
            var body = new StaticBody3D { Name=name,Position=position,CollisionLayer=1,CollisionMask=0 }; AddChild(body);
            body.AddChild(new MeshInstance3D { Mesh=new BoxMesh { Size=size },MaterialOverride=new StandardMaterial3D { AlbedoColor=color,Roughness=1 } });
            body.AddChild(new CollisionShape3D { Shape=new BoxShape3D { Size=size } });
        }
        async System.Threading.Tasks.Task Shot(string name)
        {
            await ToSignal(GetTree().CreateTimer(1.2),SceneTreeTimer.SignalName.Timeout);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(_out,name+".png")); Log.Print("[text-review] captured "+name);
        }
    }
}