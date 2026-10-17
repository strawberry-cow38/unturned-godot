// Godot oracle for the NoteReader panel (bevy/docs/WORLD_DEVICES_PORT.md).
// Shows a real note through the actual C# NoteBody.Spawn + NoteReader.Show
// and saves the frame. To run it, copy this file to game/NoteReaderOracle.cs,
// register it for one run with an untracked game/override.cfg:
//   [autoload]
//   NoteReaderOracle="*res://NoteReaderOracle.cs"
// rebuild the game (dotnet build game/UnturnedGodot.sln), then
//   UG_NOTE_GUID=<guid> UG_NOTE_SHOT=/abs/out.png <godot> --path game
//   --rendering-driver vulkan -- --lamptest
// (xvfb + Vulkan, never --headless). Remove both files afterwards.
using Godot;

namespace UnturnedGodot
{
    public partial class NoteReaderOracle : Node
    {
        int _frames;
        NoteReader _reader;

        public override void _Ready()
        {
            _reader = new NoteReader();
            AddChild(_reader);
        }

        public override void _Process(double delta)
        {
            _frames++;
            if (_frames == 5)
            {
                string guid = System.Environment.GetEnvironmentVariable("UG_NOTE_GUID") ?? "53f724bd841b42b2839d476f07e1d3de";
                NoteTexts.TryGet(guid, out var name, out var lines);
                var note = NoteBody.Spawn(this, new BoxMesh(), Transform3D.Identity, name, lines, 10f, null);
                _reader.Show(note);
            }
            if (_frames == 40)
            {
                var img = GetViewport().GetTexture().GetImage();
                img.SavePng(System.Environment.GetEnvironmentVariable("UG_NOTE_SHOT") ?? "/tmp/godot-note.png");
                GetTree().Quit();
            }
        }
    }
}
