# NPC dialogue, trade and quest presentation

The managed adapter uses the original `NpcCatalog`, `DialogueRules`, `QuestRules`,
`TradeRules`, `Currency`, and server NPC commands. It does not run a second NPC
simulation. Native character and interface checkpoints are tracked separately.

## Managed read model

`npcView {speakerKey?,vendor?,pinnedQuest?}` reads the last accepted owner
`NpcStateEvent` plus current inventory, skills and accepted holiday replicas.
The cached state survives ordinary event acknowledgements and resets with the
client session. It returns:

- `revision,openDialogue`, selected `dialogue {id,pages,responses}`. Each response
  retains its original `index`, text and `endsConversation` flag. Filtering never
  renumbers the command's response index.
- `vendorRevision,vendorOpened`, identifying the latest accepted one-shot vendor
  opening independently of later owner-state changes.
- `directVendor {guid,name}` from the current speaker's original `Shop` key;
  this is a presentation shortcut inside a conversation, not a dialogue command.
- `trade {guid,key,name,description,selling,buying}`, original row indices, item
  names/icons/prices, accepted held counts and advisory enabled/max-count values.
  Wallet is the original dollar stack 1056. Vehicle purchases remain unavailable.
- Touched `quests`, ready/active/completed ordering, original objective sentences,
  `unknownQuests`, and `tracker` from `QuestRules.AutoTrack(pinnedQuest)`.

Every mutator on the read-model `INpcWorld` throws. The UI sends unchanged
`npcTalk {dialogue}`, `npcChoose {dialogue,response}`, `npcClose {}` and
`npcTrade {vendor,sell,index,count}` requests; the server still validates and
applies them. A request itself does not navigate the conversation or change money.

`npcCatalog.characters` and `mapServices`/`mapBindings` character rows now share
one explicit property DTO. It retains id/key/name, all seven worn IDs, face,
skin/hair, dialogue and shop, plus source-sanitized `displayName`. The previous
map serializer discarded public definition fields and emitted `{}`. Character
numeric IDs are not unique: Chef and Sous Chef share 756; placement/key identity
must remain independent of that field. Authored NPC positions are already in the
engine frame and do not receive another Z conversion.

The original server batching path also discarded a response's one-shot vendor
before constructing the deferred event. `ServerNpcs.Publish` now clears it after
the event callback; direct and nested-batch calls both publish it exactly once.
No packet layout changed.

## Native interface

`ui::npc` renders the accepted dialogue pages, filtered responses, dollar vendor,
touched quest log and tracker. Space/click advances message pages before choices
become available; numbered choices retain their original response indices. A
choice sends an intent and waits for the accepted dialogue ID. Closing a shop
returns to the conversation; the accepted vendor revision opens it once without
replaying on later owner-state updates. Epoch/disconnect resets retire all panels.

Trade rows show the original item icons, dollar prices and current held counts.
Buy, sell one and sell all submit the existing transaction; the accepted inventory
and dollar stack drive redraws. Scroll offsets survive those redraws. Vehicle
rows retain the source unavailable state. The original `trade`/`tradepick` debug
presentation is available; it does not grant stock or money. The quest log is
currently reached through Inventory's Quests button while the original Information
map dashboard remains pending. Pinning changes the local tracker selection only.

Six native tests exercise real keyboard page/choice gating, original filtered
indices, accepted-only vendor transitions, capped trade intents, actual UI tree
construction/tracker visibility, epoch/disconnect cleanup and the original vendor
console through a real bridge. All six pass against the frozen managed candidate.

The live PEI Vulkan journey was driven with actual keyboard/mouse input and every
resulting image was opened. Original Chef focus opens 59; the available supply
response maps to original index 1 and accepted 60, whose two pages wrap correctly.
Reopening 59 reveals the original flag-gated trade response. The original vendor
console opens FreshMarket: a $100 note becomes the accepted dollar stack; buying
Maple Syrup 1159 for $85 leaves $15, selling MRE 81 for $50 leaves $65, and buying Dough 1162
for $65 removes the final dollar stack. Disabled controls and held counts redraw
from those accepted results. The selling column retains its scroll offset across
the last purchase. Medic 74 grants defined quest 124 and accepted dialogue 125; after closing,
Research Recovery appears in the tracker and log with the source 0/4 Research
Capsules objective. Clicking its row visibly pins it. The session exits through
the real Pause/Quit control with exit 0.

Reviewed artifacts in `bevy/artifacts/` (PNG plus accepted-state JSON):

- `bevy-npc-dialogue-first`, `bevy-npc-dialogue-page1`, `bevy-npc-dialogue-page2`,
  `bevy-npc-dialogue-unlocked`.
- `bevy-npc-trade-open`, `bevy-npc-trade-purchased`, `bevy-npc-trade-sold`,
  `bevy-npc-trade-scrolled`, `bevy-npc-trade-scroll-retained`.
- `bevy-npc-medic-accepted`, `bevy-npc-quest-tracker-defined`,
  `bevy-npc-quest-log-defined`, `bevy-npc-quest-pinned`.

World-agent code review found no blocker in filtered indices, accepted-only
mutation, vendor revisions, panel lifecycle or modal input; the quest log was
also independently opened and checked for clipping. This is functional UI parity,
not a pixel-perfect font/layout match to the original engine.

## Validation and remaining source gaps

The new batched-vendor core regression fails before the fix; all 17 `ServerNpcs`
tests pass afterward. Four actual managed adapter tests cover real UDP filtered
choices/refusal, two-page navigation, event ACK retention/session reset, the real
Pilot vendor response, quest objectives/auto-tracking, dollar purchase, and placed
retail/custom character serialization. All 53 managed integration tests and all 121
JSONL bridge regressions pass against immutable `bevy/.local/npc-view-53`.

The raw content file has 40 character rows, 142 dialogue rows, 8 vendors and 30 quests
(some numeric IDs repeat; the effective dictionaries retain the source overwrite
semantics), but no
`content/spawns/editor_*_npcs.txt` files. Ordinary maps therefore have no invented
NPC placements. The original explicit `npc` console seam is suitable for visual
verification. NPC flag/quest/progress persistence is absent from the original
WorldSave. The live Janitor test also verified that his response grants quest 9,
but no definition 9 is shipped: the original catalog-driven log cannot display it.
Tracked kill/tree/object progress has no current gameplay callers,
and vehicle vendor delivery is explicitly unfinished in the source. The native
Information/map dashboard, NPC editor and NpcHeli remain separate milestones.

Original Information/map dashboard navigation, dialogue/editor authoring, and the
legacy `menu`, `say`, `track`, `flag`, `quest` and `tradestock` debug verbs remain
separate work. In particular the old flag/quest/stock shortcuts directly mutate
local state and must not be copied into a remote native client. No missing quest
objectives, rewards, vehicle delivery or map placements are fabricated here.
