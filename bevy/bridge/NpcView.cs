using System.Text.Json;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Explicit property DTOs survive MapServices' default serializer. Character definitions use
/// fields, and serializing them directly there previously produced an empty object.</summary>
public static class NpcMetadata
{
    public static object Character(NpcCharacterDef c) => new {
        id = c.Id, key = c.Key, name = c.Name, displayName = ProfileRules.SanitizeName(c.Name),
        shirt = c.Shirt, pants = c.Pants, hat = c.Hat, vest = c.Vest, mask = c.Mask,
        glasses = c.Glasses, backpack = c.Backpack, face = c.Face, skin = c.Skin,
        hair = c.Hair, dialogue = c.Dialogue, shop = c.Shop,
    };
}

public sealed partial class BridgeHost
{
    NpcReadState _npcView = new();

    object ReadNpcView(JsonElement request)
    {
        var view = new NpcReadWorld(_client, _npcView);
        var dialogue = NpcCatalog.Dialogue(_npcView.Accepted.OpenDialogue);
        var speaker = NpcCatalog.CharacterByKey(Text(request, "speakerKey"));
        var direct = string.IsNullOrEmpty(speaker?.Shop) ? null : NpcCatalog.VendorByKey(speaker.Shop);
        string selected = Text(request, "vendor");
        var vendor = NpcCatalog.Vendor(selected) ?? NpcCatalog.VendorByKey(selected);
        int wallet = view.CountItem(Currency.StackId), pinned = Int(request, "pinnedQuest");
        object Quest(NpcQuestDef q) => q == null ? null : new {
            id = q.Id, name = TradeRules.PlainText(q.Name), description = TradeRules.PlainText(q.Description),
            status = QuestRules.StatusOf(q, view).ToString().ToLowerInvariant(),
            objectives = QuestRules.Objectives(q, view).Select(o => new { text = o.Text, done = o.Done }).ToArray(),
        };
        object Line(NpcTradeLine row, int index, bool sell) {
            bool vehicle = row.Type == "Vehicle" || row.Item == 0;
            int held = view.CountItem(row.Item);
            return new { index, itemId = row.Item, name = Assets.find(row.Item)?.itemName ?? (vehicle ? "Vehicle" : $"Item {row.Item}"),
                icon = Assets.find(row.Item)?.iconPath, cost = row.Cost, held, vehicle,
                enabled = sell ? held > 0 : !vehicle && TradeRules.CanBuy(row, wallet),
                maxCount = sell ? Math.Min(held, ServerNpcs.MaxTradeCount) : TradeRules.Affordable(row, wallet, ServerNpcs.MaxTradeCount) };
        }
        var all = NpcCatalog.Quests.ToArray();
        var touched = all.Where(q => QuestRules.StatusOf(q, view) != ENpcQuestStatus.None).ToArray();
        return new {
            revision = _npcView.Revision, openDialogue = _npcView.Accepted.OpenDialogue,
            vendorRevision = _npcView.VendorRevision, vendorOpened = _npcView.LastVendor,
            reputation = view.Reputation, wallet,
            dialogue = dialogue == null ? null : new {
                id = dialogue.Id,
                pages = DialogueRules.MessageFor(dialogue, view)?.Pages ?? System.Array.Empty<string>(),
                responses = DialogueRules.AvailableResponses(dialogue, view).Select(index => new {
                    index, text = dialogue.Responses[index].Text, endsConversation = dialogue.Responses[index].EndsConversation,
                }).ToArray(),
            },
            directVendor = direct == null ? null : new { guid = direct.Guid, name = TradeRules.PlainText(direct.Name) },
            trade = vendor == null ? null : new {
                guid = vendor.Guid, key = vendor.Key, name = TradeRules.PlainText(vendor.Name), description = TradeRules.PlainText(vendor.Description),
                selling = vendor.Selling.Select((row, index) => Line(row, index, false)).ToArray(),
                buying = vendor.Buying.Select((row, index) => (row, index)).Where(x => x.row.Item != 0 && x.row.Item != Currency.StackId)
                    .Select(x => Line(x.row, x.index, true)).ToArray(),
            },
            quests = touched.OrderBy(q => QuestRules.StatusOf(q, view) switch { ENpcQuestStatus.Ready => 0, ENpcQuestStatus.Active => 1, _ => 2 }).Select(Quest).ToArray(),
            unknownQuests = all.Length - touched.Length,
            tracker = Quest(QuestRules.AutoTrack(all, view, pinned)),
        };
    }

    sealed class NpcReadState
    {
        public NpcStateEvent Accepted;
        public ulong Revision, VendorRevision;
        public string LastVendor = "";
        public void Adopt(NpcStateEvent e)
        {
            Accepted = e; Revision++;
            if (!string.IsNullOrEmpty(e.Vendor)) { LastVendor = e.Vendor; VendorRevision++; }
        }
        public object Snapshot() => new { revision = Revision, openDialogue = Accepted.OpenDialogue,
            vendorRevision = VendorRevision, vendorOpened = LastVendor };
    }

    /// <summary>The same source rule interface as PlayerController.NpcWorldView, backed solely by
    /// accepted owner events and inventory/skill replicas. Mutating methods are deliberately unavailable.</summary>
    sealed class NpcReadWorld(NetWorldClient client, NpcReadState state) : INpcWorld
    {
        public short GetFlag(ushort id) => state.Accepted.Flags?.FirstOrDefault(x => x.Id == id).Value ?? 0;
        public ENpcQuestStatus GetQuestStatus(ushort id)
        {
            var raw = (ENpcQuestStatus)(state.Accepted.Quests?.FirstOrDefault(x => x.Id == id).Status ?? 0);
            var quest = NpcCatalog.Quest(id);
            return raw == ENpcQuestStatus.Active && quest != null && QuestRules.CanTurnIn(quest, this) ? ENpcQuestStatus.Ready : raw;
        }
        public int QuestProgress(ushort questId, int index) => state.Accepted.Progress?.FirstOrDefault(x => x.Quest == questId && x.Index == index).Value ?? 0;
        public string ActiveHoliday => client.ServerHoliday;
        public int Reputation => state.Accepted.Reputation;
        public uint Experience => client.Skills.TryGet(client.PlayerId, out var e) ? e.Skills.experience : 0;
        public int CountItem(ushort itemId) => client.Inventories.TryGet(client.PlayerId, out var e) ? e.Inventory.getItemCount(itemId) : 0;
        static void ReadOnly() => throw new InvalidOperationException("NPC presentation cannot mutate authoritative state");
        public void SetFlag(ushort id, short value) => ReadOnly();
        public void SetQuestStatus(ushort id, ENpcQuestStatus status) => ReadOnly();
        public void AddQuestProgress(ushort questId, int index, int by) => ReadOnly();
        public void GiveItem(ushort itemId, short amount) => ReadOnly();
        public void TakeItem(ushort itemId, int amount) => ReadOnly();
        public void AddExperience(int amount) => ReadOnly();
        public void AddReputation(int amount) => ReadOnly();
    }
}
