using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The launcher hands off with only `--path game`, so --newzombies is unreachable from a normal Play click --
    // the singleplayer screen's Zombies option (Normal / No Zombies / New Zombies) is the only route to the rewrite,
    // which makes "is that option present and actually wired" worth asserting rather than eyeballing once.
    //
    // The menu rewrite (ec5f88b1) turned the old three map-buttons into a retail-style singleplayer panel: a map list
    // plus a Zombies value cycler ( [<] Normal/No Zombies/New Zombies [>] ) and one PLAY button. So this now drives
    // that UI -- cycle Zombies to "New Zombies", press PLAY, and assert OnDriveNewZombies fires -- instead of hunting
    // for a button literally labelled "New Zombies".
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
            yield return Ticks(5);   // _Ready builds the singleplayer panel (hidden, but present in the tree)

            // the PEI map entry survived the rewrite (its row Text is "  Prince Edward Island")
            var buttons = new List<Button>();
            Collect(menu, buttons);
            var labels = new List<string>();
            foreach (var b in buttons) labels.Add(b.Text.Trim());
            T.Check($"the Prince Edward Island map entry is still there (saw: {string.Join(" | ", labels)})",
                labels.Contains("Prince Edward Island"));

            // find the Zombies value cycler: an HBox row [ Label "Zombies" ] [ Button "<" ] [ Label value ] [ Button ">" ]
            var rows = new List<HBoxContainer>();
            CollectRows(menu, rows);
            HBoxContainer zombiesRow = null;
            foreach (var row in rows)
                foreach (var c in row.GetChildren())
                    if (c is Label l && l.Text == "Zombies") { zombiesRow = row; break; }
            T.Check("the singleplayer panel has a Zombies option row", zombiesRow != null);

            if (zombiesRow != null)
            {
                Button next = null; Label val = null;
                foreach (var c in zombiesRow.GetChildren())
                {
                    if (c is Button b && b.Text == ">") next = b;
                    else if (c is Label l && l.Text != "Zombies") val = l;
                }
                // cycle forward until it reads "New Zombies" (robust to whatever the default index is)
                for (int i = 0; i < 3 && next != null && val != null && val.Text != "New Zombies"; i++)
                    next.EmitSignal(Button.SignalName.Pressed);
                yield return Ticks(1);
                T.Check($"the Zombies cycler can reach 'New Zombies' (shows: {val?.Text})", val != null && val.Text == "New Zombies");
            }
            else T.Fail("the Zombies cycler can reach 'New Zombies'");

            // PLAY with New Zombies selected launches the rewrite
            Button play = null;
            foreach (var b in buttons) if (b.Text == "PLAY") play = b;
            T.Check("a PLAY button exists", play != null);
            if (play != null)
            {
                play.EmitSignal(Button.SignalName.Pressed);
                yield return Ticks(1);
                T.Check("pressing PLAY on New Zombies invokes OnDriveNewZombies", fired);
            }
            else T.Fail("pressing PLAY on New Zombies invokes OnDriveNewZombies");

            ZombieDirector.Enabled = wasEnabled;   // a static flag: leave the suite as we found it
        }

        static void Collect(Node n, List<Button> into)
        {
            if (n is Button b) into.Add(b);
            foreach (var c in n.GetChildren()) Collect(c, into);
        }
        static void CollectRows(Node n, List<HBoxContainer> into)
        {
            if (n is HBoxContainer h) into.Add(h);
            foreach (var c in n.GetChildren()) CollectRows(c, into);
        }
    }
}
