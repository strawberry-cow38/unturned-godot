// 1:1 port of SDG.Unturned.ItemJar: an item placed in a page at grid cell (x,y) with a rotation (rot%2==1 means
// turned 90 degrees, swapping width/height). size_x/size_y are cached from the asset at construction, exactly as
// the source does, so packing math never re-reads the asset.
namespace SDG.Unturned
{
    public class ItemJar
    {
        public byte x;
        public byte y;
        public byte rot;
        public byte size_x;
        public byte size_y;
        public Item item;

        public ItemAsset GetAsset() => item?.GetAsset();

        public ItemJar(Item newItem)
        {
            item = newItem;
            ItemAsset asset = item?.GetAsset();
            if (asset != null) { size_x = asset.size_x; size_y = asset.size_y; }
        }

        public ItemJar(byte new_x, byte new_y, byte newRot, Item newItem)
        {
            x = new_x;
            y = new_y;
            rot = newRot;
            item = newItem;
            ItemAsset asset = item?.GetAsset();
            if (asset != null) { size_x = asset.size_x; size_y = asset.size_y; }
        }
    }
}
