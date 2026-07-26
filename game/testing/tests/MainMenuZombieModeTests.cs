using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The launcher hands off with only `--path game`, so --newzombies was unreachable from a normal
    // Play click -- the rewrite could only be run from a command line nobody uses. The menu entry is
    // therefore the ONLY route most people have to it, which makes "does that button exist and is it
    // actually wired to anything" worth asserting rather than eyeballing once.
    public class MainMenuOffersTheZombieRewrite : GameTest
    {
        public override string Name => "menu.new_zombies_entry";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            bool wasEnabled = ZombieDirector.Enabled;
            ZombieDirector.Enabled = false;

            var menu = new MainMenu();
            bool fired = false;
            menu.OnDriveNewZombies = () => fired = true;
            World.AddChild(menu);
            yield return Ticks(5);   // _Ready builds the singleplayer panel

            var buttons = new List<Button>();
            Collect(menu, buttons);
            var labels = new List<string>();
            foreach (var b in buttons) labels.Add(b.Text);

            Button target = null;
            foreach (var b in buttons)
                if (b.Text.Contains("New Zombies")) target = b;

            T.Check($"a 'New Zombies' entry exists (saw: {string.Join(" | ", labels)})", target != null);
            T.Check("the two original PEI entries are still there",
                labels.Contains("Prince Edward Island") && labels.Contains("Prince Edward Island — No Zombies"));

            if (target != null)
            {
                target.EmitSignal(Button.SignalName.Pressed);
                yield return Ticks(1);
                T.Check("pressing it invokes OnDriveNewZombies", fired);
            }
            else T.Fail("pressing it invokes OnDriveNewZombies");

            ZombieDirector.Enabled = wasEnabled;   // a static flag: leave the suite as we found it
        }

        static void Collect(Node n, List<Button> into)
        {
            if (n is Button b) into.Add(b);
            foreach (var c in n.GetChildren()) Collect(c, into);
        }
    }
}
