using Godot;

namespace UnturnedGodot
{
    /// <summary>Every process-lifetime resource cache in the game, and the one call that drops all of them.
    ///
    /// WHY THIS EXISTS. Leaving the editor is `GetTree().ReloadCurrentScene()` (Main.cs). That frees the whole
    /// node tree -- but a `static` field is not in the tree, so every cache below SURVIVES the reload and keeps
    /// handing the next map resources built during the editor session. strawberry_cow reported it as "after
    /// using the map editor and going back onto a real map, every texture looks corrupted -- purple, black,
    /// white", and the decisive detail was that RELAUNCHING fixes it: that rules out anything on disk and
    /// leaves process-lifetime state as the only thing that could differ.
    ///
    /// CLEAR EVERYTHING, then RE-WARM -- not "clear the ones that look map-specific". Scoping the reset to the
    /// caches I believe can hold editor state requires me to be RIGHT about which those are, and if I am wrong
    /// about one the bug simply survives in it, with the same symptom and one fewer suspect. Clearing all of
    /// them cannot be wrong that way. The cost is that the launch warmup's preloaded meshes go too, which would
    /// bring back the first-use hitch (and cow tools' first-break rubble stutter) -- so the map-entry path
    /// re-runs Warmup rather than leaving the caches cold. Clear on the way out, warm on the way in.
    ///
    /// ADDING A CACHE: add its clear here. A static resource cache that is not in this list is a leak across
    /// the editor boundary by construction, and it will present as this bug again.
    ///
    /// NOT VISUALLY VERIFIED BY ME: this is a GPU-visible symptom and the test suite is headless, which
    /// discards exactly the data that would show it. The suite proves this change breaks nothing; it cannot
    /// prove it fixes the corruption. That confirmation has to come from someone looking at the screen.</summary>
    public static class ResourceCaches
    {
        /// <summary>Drop every cached mesh, texture, material and sound held in a static field.
        ///
        /// Safe to call at any time: every cache below is a pure memo of something reconstructible from disk.
        /// Call it BEFORE ReloadCurrentScene, while the old scene's nodes still exist -- clearing after the
        /// reload would drop whatever the NEW scene had already cached during its own startup.</summary>
        public static void ClearAll()
        {
            ObjMesh.ClearCaches();
            ImpactFx.ClearCaches();
            DoorDeploy.ClearCaches();
            EditorIcons.ClearCaches();
            CharacterModel.ClearCaches();
            AttachmentMenu.ClearCaches();
            RiggedCharacter.ClearCaches();
            TVDevice.ClearCaches();
            InventoryUI.ClearCaches();
            StoreShelf.ClearCaches();
            ConnectionPort.ClearCaches();
            LampLight.ClearCaches();
            PlayerController.ClearCaches();
            Viewmodel.ClearCaches();
            GlassPane.ClearCaches();
            RainSystem3D.ResetGlobals();   // rain_wetness/rain_intensity are process-wide + outlive the scene -> zero them so the next scene/menu isn't stuck wet (tinyclaw)
            GD.Print("[caches] cleared all static resource caches (editor/map transition)");
        }

        /// <summary>How many entries the keyed caches are holding, for the test that asserts ClearAll actually
        /// drops them. A test that only checks "ClearAll did not throw" would pass against an empty body.</summary>
        public static int TotalCached =>
            ObjMesh.CachedCount + ImpactFx.CachedCount + DoorDeploy.CachedCount + EditorIcons.CachedCount
            + AttachmentMenu.CachedCount + RiggedCharacter.CachedCount + TVDevice.CachedCount
            + InventoryUI.CachedCount + StoreShelf.CachedCount;
    }

    public static partial class ObjMesh
    {
        /// <summary>Drops the launch warmup's preloaded meshes too. That is deliberate -- see ResourceCaches --
        /// and the map-entry path is expected to re-warm.</summary>
        internal static void ClearCaches()
        {
            _cache.Clear();
            _lensCache.Clear();
            _multiCache.Clear();
            _headCache.Clear();
            _cutCache.Clear();
        }
    }

    public static partial class ImpactFx
    {
        internal static int CachedCount => _debris.Count + _decal.Count + _snd.Count;

        // The `_tried` flags MUST be reset alongside the fields they guard. They exist so a missing file is not
        // re-opened every impact; leaving one true while nulling its texture pins that texture at null for the
        // rest of the process -- a worse bug than the one being fixed, and a silent one.
        internal static void ClearCaches()
        {
            _debris.Clear(); _decal.Clear(); _snd.Clear();
            _blood = null; _bloodTried = false;
            _bloodSnd = null; _bloodSndTried = false;
        }
    }

    public static partial class DoorDeploy
    {
        internal static int CachedCount => _mats.Count;
        internal static void ClearCaches() => _mats.Clear();
    }

    public static partial class EditorIcons
    {
        internal static int CachedCount => Cache.Count;
        internal static void ClearCaches() => Cache.Clear();
    }

    public static partial class CharacterModel
    {
        internal static void ClearCaches() => _mesh = null;
    }

    public partial class AttachmentMenu
    {
        internal static int CachedCount => _itemIcons.Count;
        internal static void ClearCaches() => _itemIcons.Clear();
    }

    public partial class RiggedCharacter
    {
        internal static int CachedCount => _texCache.Count;
        internal static void ClearCaches() => _texCache.Clear();
    }

    public partial class TVDevice
    {
        internal static int CachedCount => _pngCache.Count + _solidCache.Count;
        internal static void ClearCaches()
        {
            _pngCache.Clear();
            _solidCache.Clear();
            _pattern = null;
        }
    }

    public partial class InventoryUI
    {
        internal static int CachedCount => _iconCache.Count;
        internal static void ClearCaches()
        {
            _iconCache.Clear();
            _snowTex = null;
        }
    }

    public partial class StoreShelf
    {
        internal static int CachedCount => _meshes.Count + _mats.Count;
        internal static void ClearCaches() { _meshes.Clear(); _mats.Clear(); }
    }

    public partial class ConnectionPort
    {
        internal static void ClearCaches() => _arrowTex = null;
    }

    public partial class LampLight
    {
        internal static void ClearCaches() => _glowTex = null;
    }

    public partial class PlayerController
    {
        // Both guard flags reset with their textures -- same trap as ImpactFx above.
        internal static void ClearCaches()
        {
            _npcFlashTex = null; _npcFlashTexTried = false;
            _tracerTex = null; _tracerTexTried = false;
        }
    }

    public partial class Viewmodel
    {
        internal static void ClearCaches() => _blankReticle = null;
    }

    public partial class GlassPane
    {
        internal static void ClearCaches() { _snd = null; _sndTried = false; }
    }
}
