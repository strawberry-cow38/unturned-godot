using System;
using System.Collections.Generic;

namespace SDG.Unturned
{
    // A zombie KIND is a data record, not a subclass. Phase 0 carries only the fields the sim core
    // itself needs to exist (identity + the capsule the analytic hit test will use in phase 2); the
    // behaviour side -- composable modules like Pursue/Flank/Charge -- is designed and added in phase 2
    // where something actually consumes it. Inventing that surface now would be scaffolding with no
    // caller, which is the thing this rewrite exists to avoid.
    //
    // The rule this type enforces: adding a zombie kind is adding a ROW. Never a subclass, never a new
    // arm on a switch (plan section 7).
    public sealed class ZombieKind
    {
        public string Name = "normal";

        public float Health = 100f;
        public float MoveSpeed = 1.6f;       // m/s shambling
        public float SprintSpeed = 5.4f;     // m/s when alerted (sprinter kinds raise this)

        public float SightRange = 48f;
        public float SightHalfAngleDeg = 60f;
        public float HearingRange = 32f;

        public float AttackDamage = 20f;
        public float AttackRange = 1.75f;

        // The capsule the sim treats this kind as, for analytic hit resolution. This is a SIM number,
        // deliberately not a collider property -- nothing about being shootable routes through physics.
        public float Radius = 0.4f;
        public float Height = 1.9f;
    }

    // Registry of kinds. Ids are ushort so a sim row costs 2 bytes for its kind reference.
    public sealed class ZombieKindTable
    {
        readonly List<ZombieKind> _kinds = new List<ZombieKind>();

        public int Count => _kinds.Count;
        public float MaxRadius { get; private set; }

        public ushort Register(ZombieKind kind)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            if (_kinds.Count >= ushort.MaxValue) throw new InvalidOperationException("zombie kind ids exhausted");
            if (kind.Radius > MaxRadius) MaxRadius = kind.Radius;
            _kinds.Add(kind);
            return (ushort)(_kinds.Count - 1);
        }

        public ZombieKind this[ushort id]
        {
            get
            {
                if (id >= _kinds.Count) throw new ArgumentOutOfRangeException(nameof(id), $"no zombie kind {id} (have {_kinds.Count})");
                return _kinds[id];
            }
        }

        public bool IsValid(ushort id) => id < _kinds.Count;

        // The stand-in set the sim boots with when no content is loaded. Real kinds come from data.
        public static ZombieKindTable Default()
        {
            var t = new ZombieKindTable();
            t.Register(new ZombieKind { Name = "normal" });
            return t;
        }
    }
}
