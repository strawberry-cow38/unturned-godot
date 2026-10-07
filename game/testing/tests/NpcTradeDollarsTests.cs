using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // NPC VENDORS TRADE IN DOLLARS (strawberry 2026-10-07: "make npc vendors trade in $").
    //
    // A real vendor out of content/npcs.json (Leonard's Fresh Food Market), through the singleplayer loopback so the
    // SERVER owns the wallet and the goods: open the shop, read the prices, buy, sell one, sell the rest. Every number
    // is read twice -- off the server's bag, and off the window the player is looking at after the echo lands.
    public sealed class NpcTradeDollarsTests : GameTest
    {
        public override string Name => "npc.trade_in_dollars";
        public override double TimeoutSimSeconds => 40;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            NpcCatalog.Load();
            var vend = NpcCatalog.Vendor("438b0afe75ff4a94822a9ff02e7cdc6f");
            T.Check($"the market is in the catalog ({TradeRules.PlainText(vend?.Name)})", vend != null && vend.Selling.Length > 0 && vend.Buying.Length > 0);
            if (vend == null) yield break;
            var buyLine = vend.Selling[0];   // what we buy from them
            var sellLine = vend.Buying[0];   // what we sell to them
            T.Check($"both lines are real items ({buyLine.Item} at {buyLine.Cost}, {sellLine.Item} at {sellLine.Cost})",
                Assets.find(buyLine.Item) != null && Assets.find(sellLine.Item) != null && buyLine.Cost > 0 && sellLine.Cost > 0);

            Rigs.Ground(World);
            var driver = new SimDriver(); World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Wait(() => loop.Client.State == NetSessionState.Connected && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;
            loop.Server.Inventories.TryGet(pid, out var sinv);
            T.Check("the bag is the server's", sinv != null && player.NetNpcTrade != null);
            if (sinv == null) yield break;
            var bag = sinv.Inventory;
            for (byte p = 0; p < PlayerInventory.OWNPAGES; p++) bag.items[p]?.clear();

            // a wallet that covers ONE of what we buy and not two, and three of what they buy
            int wallet0 = buyLine.Cost + buyLine.Cost / 2;
            T.Check($"fixture: ${wallet0} in the wallet", Currency.Pay(bag, wallet0));
            for (int i = 0; i < 3; i++) T.Check($"fixture: sell item {i + 1} in the bag", bag.tryAddItem(new Item(sellLine.Item)));
            loop.Server.Inventories.ServerMarkDirty(pid);
            yield return Wait(() => player.Inventory.getItemCount(Currency.StackId) == wallet0 && player.Inventory.getItemCount(sellLine.Item) == 3, 3);

            // ---- the window: wallet, and prices in dollars
            player.OpenTrade(vend);
            yield return Ticks(3);
            var w = player.TradeWindow;
            T.Check($"the shop opens with the wallet on it ({w?.DebugWallet})", w != null && w.IsOpen && w.DebugWallet == $"Your wallet: ${wallet0}");
            if (w == null) yield break;
            var buyPrices = w.DebugRowPrices(false);
            var sellPrices = w.DebugRowPrices(true);
            T.Check($"what they sell is priced in $ ({string.Join(" ", buyPrices)})", buyPrices.Count > 0 && buyPrices[0] == $"${buyLine.Cost}");
            T.Check($"what they buy says what they pay ({string.Join(" ", sellPrices)})", sellPrices.Count > 0 && sellPrices[0] == $"${sellLine.Cost} ea");

            // ---- BUY one: the server takes the money and hands over the goods
            w.DebugPickSelling(0);
            T.Check($"affordable: \"{w.DebugStatus}\"", w.DebugCanBuy);
            w.DebugPressBuy();
            yield return Wait(() => bag.getItemCount(buyLine.Item) == 1, 3);
            T.Check($"the SERVER sold it: {bag.getItemCount(buyLine.Item)} bought, wallet ${bag.getItemCount(Currency.StackId)} (want ${wallet0 - buyLine.Cost})",
                bag.getItemCount(buyLine.Item) == 1 && bag.getItemCount(Currency.StackId) == wallet0 - buyLine.Cost);
            yield return Wait(() => w.DebugWallet == $"Your wallet: ${wallet0 - buyLine.Cost}", 3);
            T.Check($"...and the window shows the new wallet ({w.DebugWallet})", w.DebugWallet == $"Your wallet: ${wallet0 - buyLine.Cost}");

            // ---- the second one is too much: the button greys and says how short
            T.Check($"a second is out of reach: \"{w.DebugStatus}\"", !w.DebugCanBuy && w.DebugStatus.Contains("short"));
            w.DebugPressBuy();
            yield return Ticks(30);
            T.Check("...and pressing anyway buys nothing", bag.getItemCount(buyLine.Item) == 1);

            // ---- SELL one, then the rest
            int before = bag.getItemCount(Currency.StackId);
            w.DebugPickBuying(0);
            T.Check($"sellable: \"{w.DebugStatus}\"", w.DebugCanSell);
            w.DebugPressSellOne();
            yield return Wait(() => bag.getItemCount(sellLine.Item) == 2, 3);
            T.Check($"sold one: {bag.getItemCount(sellLine.Item)} left, wallet ${bag.getItemCount(Currency.StackId)} (want ${before + sellLine.Cost})",
                bag.getItemCount(sellLine.Item) == 2 && bag.getItemCount(Currency.StackId) == before + sellLine.Cost);
            yield return Wait(() => player.Inventory.getItemCount(sellLine.Item) == 2, 3);
            w.DebugPressSellAll();
            yield return Wait(() => bag.getItemCount(sellLine.Item) == 0, 3);
            T.Check($"sold the rest: wallet ${bag.getItemCount(Currency.StackId)} (want ${before + 3 * sellLine.Cost})",
                bag.getItemCount(sellLine.Item) == 0 && bag.getItemCount(Currency.StackId) == before + 3 * sellLine.Cost);
            yield return Wait(() => w.DebugWallet == $"Your wallet: ${before + 3 * sellLine.Cost}", 3);
            T.Check($"...and the window agrees ({w.DebugWallet}), with nothing left to sell", w.DebugWallet == $"Your wallet: ${before + 3 * sellLine.Cost}" && !w.DebugCanSell);

            // ---- what they do NOT buy is not on their list at all -- the gun you are carrying earns nothing here
            bool gunListed = false;
            foreach (var l in vend.Buying) if (l.Item == 4) gunListed = true;
            T.Check($"they don't buy a rifle, and it is not offered ({sellPrices.Count} lines, all theirs)",
                !gunListed && sellPrices.Count == System.Linq.Enumerable.Count(vend.Buying, l => l.Item != 0 && !Currency.IsCurrency(l.Item)));
            w.Close();
        }
    }
}
