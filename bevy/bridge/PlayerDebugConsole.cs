using System.Globalization;

namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    // DevConsole.kill/sethp use the ordinary damage sink. The local console
    // is trusted host input, but still obeys the server's existing cheats gate.
    bool PlayerDebugConsole(string command)
    {
        var words = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        string verb = words[0].ToLowerInvariant();
        if (verb is not ("kill" or "sethp")) return false;
        string message;
        if (!_server.Transactions.AllowCheats) message = "console commands are disabled on this server";
        else if (!_server.CombatState.TryGet(_client.PlayerId, out var player)) message = $"{verb}: no player";
        else if (!player.Alive) message = $"{verb}: you're already dead";
        else if (verb == "kill")
        {
            _server.Combat.DamagePlayerExternal(_client.PlayerId, 9999f);
            message = "kill: lethal damage queued";
        }
        else if (words.Length < 2 || !float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float hp) || !float.IsFinite(hp))
            message = "sethp: not a finite number";
        else
        {
            float damage = MathF.Max(0f, player.HealthExact - Math.Clamp(hp, 0f, 100f));
            if (damage > 0f) _server.Combat.DamagePlayerExternal(_client.PlayerId, damage);
            message = $"sethp: queued {damage:0} damage";
        }
        Emit("console", new { text = message });
        return true;
    }
}
