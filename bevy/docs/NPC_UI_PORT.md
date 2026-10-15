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
  Wallet is the original dollar stack1056. Vehicle purchases remain unavailable.
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
numeric IDs are not unique: Chef and Sous Chef share756; placement/key identity
must remain independent of that field. Authored NPC positions are already in the
engine frame and do not receive another Z conversion.

The original server batching path also discarded a response's one-shot vendor
before constructing the deferred event. `ServerNpcs.Publish` now clears it after
the event callback; direct and nested-batch calls both publish it exactly once.
No packet layout changed.

## Validation and remaining source gaps

The new batched-vendor core regression fails before the fix; all17 `ServerNpcs`
tests pass afterward. Four actual managed adapter tests cover real UDP filtered
choices/refusal, two-page navigation, event ACK retention/session reset, the real
Pilot vendor response, quest objectives/auto-tracking, dollar purchase, and placed
retail/custom character serialization. All53 managed integration tests and all121
JSONL bridge regressions pass against immutable `bevy/.local/npc-view-53`.

Current content has40 characters,142 dialogues,8 vendors and30 quests but no
`content/spawns/editor_*_npcs.txt` files. Ordinary maps therefore have no invented
NPC placements. The original explicit `npc` console seam is suitable for visual
verification. NPC flag/quest/progress persistence is absent from the original
WorldSave, tracked kill/tree/object progress has no current gameplay callers,
and vehicle vendor delivery is explicitly unfinished in the source. The native
Information/map dashboard, NPC editor and NpcHeli remain separate milestones.
