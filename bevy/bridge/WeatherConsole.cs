namespace UnturnedBevy;

public sealed partial class BridgeHost
{
    bool WeatherConsole(string command)
    {
        var parts=command.Trim().Split(' ',2,StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0||!parts[0].Equals("weather",StringComparison.OrdinalIgnoreCase))return false;
        string arg=parts.Length>1?parts[1].Trim():"",text;
        if(!_server.Transactions.AllowCheats)text="console commands are disabled on this server";
        else if(arg.Length==0)
        {
            var sim=_environment.Sim;var active=sim.Active;
            text=active==null?$"clear -- next weather in {sim.ForecastTimer:0}s"
                :$"{active.Value.Name} -- stage {sim.Stage}, blend {sim.BlendAlpha:0.00}, {sim.ActiveTimer:0}s left";
        }
        else if(_environment.ApplyCommand(arg,()=>Emit("weatherStrike",new{})))text=$"weather -> {arg}";
        else text="weather: clear | rain | heavy | drizzle | squall | downpour | tempest | lightning";
        Emit("console",new{text});return true;
    }
}
