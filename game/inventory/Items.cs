using System;
using System.Collections.Generic;

// 1:1 port of SDG.Unturned.Items: ONE inventory page -- a width x height grid of cells (the `slots` occupancy
// array) plus the list of ItemJars occupying it. Pages with index < PlayerInventory.SLOTS are single-item "slots"
// (the two hand slots) where checkSpaceEmpty just tests items.Count==0; pages >= SLOTS are real grids. All the
// packing math (tryFindSpace's row-major scan preferring unrotated then rotated, checkSpaceEmpty, checkSpaceDrag,
// fillSlot) is a direct port so placement matches the game cell-for-cell.
namespace SDG.Unturned
{
    public class Items
    {
        byte _page;
        byte _width;
        byte _height;
        bool[,] slots;

        public byte page => _page;
        public byte width => _width;
        public byte height => _height;
        public List<ItemJar> items { get; private set; }

        // UI hooks (mirror the source events)
        public event Action<byte, byte, ItemJar> onItemAdded;      // page, index, jar
        public event Action<byte, byte, ItemJar> onItemRemoved;    // page, index, jar
        public event Action<byte, byte, byte> onItemsResized;      // page, newWidth, newHeight
        public event Action onStateUpdated;

        public Items(byte newPage)
        {
            _page = newPage;
            items = new List<ItemJar>();
        }

        public byte getItemCount() => (byte)items.Count;

        public ItemJar getItem(byte index) => (index < items.Count) ? items[index] : null;

        // the item occupying grid cell (x,y), or null -- used by the UI for click/drag hit-testing
        public ItemJar getItem(byte pos_x, byte pos_y)
        {
            for (int i = 0; i < items.Count; i++)
            {
                ItemJar jar = items[i];
                byte b = jar.size_x, b2 = jar.size_y;
                if (jar.rot % 2 == 1) { b = jar.size_y; b2 = jar.size_x; }
                if (pos_x >= jar.x && pos_x < jar.x + b && pos_y >= jar.y && pos_y < jar.y + b2)
                    return jar;
            }
            return null;
        }

        public byte getIndex(byte pos_x, byte pos_y)
        {
            for (byte i = 0; i < items.Count; i++)
            {
                ItemJar jar = items[i];
                byte b = jar.size_x, b2 = jar.size_y;
                if (jar.rot % 2 == 1) { b = jar.size_y; b2 = jar.size_x; }
                if (pos_x >= jar.x && pos_x < jar.x + b && pos_y >= jar.y && pos_y < jar.y + b2)
                    return i;
            }
            return byte.MaxValue;
        }

        public void addItem(byte x, byte y, byte rot, Item item)
        {
            ItemJar itemJar = new ItemJar(x, y, rot, item);
            fillSlot(itemJar, isOccupied: true);
            items.Add(itemJar);
            onItemAdded?.Invoke(page, (byte)(items.Count - 1), itemJar);
            onStateUpdated?.Invoke();
        }

        public bool tryAddItem(Item item)
        {
            if (getItemCount() >= 200) return false;
            ItemJar itemJar = new ItemJar(item);
            if (!tryFindSpace(itemJar.size_x, itemJar.size_y, out var x, out var y, out var rot)) return false;
            itemJar.x = x; itemJar.y = y; itemJar.rot = rot;
            fillSlot(itemJar, isOccupied: true);
            items.Add(itemJar);
            onItemAdded?.Invoke(page, (byte)(items.Count - 1), itemJar);
            onStateUpdated?.Invoke();
            return true;
        }

        public void removeItem(byte index)
        {
            if (index >= 0 && index < items.Count)
            {
                fillSlot(items[index], isOccupied: false);
                onItemRemoved?.Invoke(page, index, items[index]);
                items.RemoveAt(index);
                onStateUpdated?.Invoke();
            }
        }

        public void clear() => items.Clear();

        // rebuild the occupancy grid at a new size and re-seat existing items, discarding any that no longer fit a
        // real grid page (source: page >= SLOTS && x+w > width || y+h > height) -- e.g. shrinking a bag page
        public void loadSize(byte newWidth, byte newHeight)
        {
            _width = newWidth;
            _height = newHeight;
            slots = new bool[width, height];
            for (byte b = 0; b < width; b++)
                for (byte b2 = 0; b2 < height; b2++)
                    slots[b, b2] = false;

            List<ItemJar> list = new List<ItemJar>();
            if (items != null)
            {
                for (byte b3 = 0; b3 < items.Count; b3++)
                {
                    ItemJar itemJar = items[b3];
                    byte b4 = itemJar.size_x, b5 = itemJar.size_y;
                    if (itemJar.rot % 2 == 1) { b4 = itemJar.size_y; b5 = itemJar.size_x; }
                    if (width == 0 || height == 0 || (page >= PlayerInventory.SLOTS && (itemJar.x + b4 > width || itemJar.y + b5 > height)))
                    {
                        onStateUpdated?.Invoke();
                    }
                    else
                    {
                        fillSlot(itemJar, isOccupied: true);
                        list.Add(itemJar);
                    }
                }
            }
            items = list;
        }

        public void resize(byte newWidth, byte newHeight)
        {
            loadSize(newWidth, newHeight);
            onItemsResized?.Invoke(page, newWidth, newHeight);
            onStateUpdated?.Invoke();
        }

        // is the size_x by size_y (rot-aware) block at (pos_x,pos_y) entirely free and in-bounds?
        public bool checkSpaceEmpty(byte pos_x, byte pos_y, byte size_x, byte size_y, byte rot)
        {
            if (page < PlayerInventory.SLOTS) return items.Count == 0;
            if (rot % 2 == 1) { byte n = size_x; size_x = size_y; size_y = n; }
            for (byte b = pos_x; b < pos_x + size_x; b++)
                for (byte b2 = pos_y; b2 < pos_y + size_y; b2++)
                {
                    if (b >= width || b2 >= height) return false;
                    if (slots[b, b2]) return false;
                }
            return true;
        }

        // drag validity: like checkSpaceEmpty but the item's OWN old footprint doesn't count as blocking when
        // checkSame (a move within the same page), so you can nudge an item onto cells it already covers
        public bool checkSpaceDrag(byte old_x, byte old_y, byte oldRot, byte new_x, byte new_y, byte newRot, byte size_x, byte size_y, bool checkSame)
        {
            if (page < PlayerInventory.SLOTS) return items.Count == 0 || checkSame;
            byte b = size_x, b2 = size_y;
            if (oldRot % 2 == 1) { b = size_y; b2 = size_x; }
            byte b3 = size_x, b4 = size_y;
            if (newRot % 2 == 1) { b3 = size_y; b4 = size_x; }
            for (byte b5 = new_x; b5 < new_x + b3; b5++)
                for (byte b6 = new_y; b6 < new_y + b4; b6++)
                {
                    if (b5 >= width || b6 >= height) return false;
                    if (slots[b5, b6])
                    {
                        int num = b5 - old_x, num2 = b6 - old_y;
                        if (!checkSame || num < 0 || num2 < 0 || num >= b || num2 >= b2) return false;
                    }
                }
            return true;
        }

        // scan the grid row-major for the first free size_x by size_y block (rot 0), else the rotated fit (rot 1)
        public bool tryFindSpace(byte size_x, byte size_y, out byte x, out byte y, out byte rot)
        {
            x = byte.MaxValue; y = byte.MaxValue; rot = 0;
            if (page < PlayerInventory.SLOTS)
            {
                x = 0; y = 0; rot = 0;
                return items.Count == 0;
            }
            for (byte b = 0; b < height - size_y + 1; b++)
                for (byte b2 = 0; b2 < width - size_x + 1; b2++)
                {
                    bool flag = false;
                    byte b3 = 0;
                    while (b3 < size_y && !flag)
                    {
                        for (byte b4 = 0; b4 < size_x; b4++)
                        {
                            if (slots[b2 + b4, b + b3]) { flag = true; break; }
                            if (b4 == size_x - 1 && b3 == size_y - 1) { x = b2; y = b; rot = 0; return true; }
                        }
                        b3++;
                    }
                }
            for (byte b5 = 0; b5 < height - size_x + 1; b5++)
                for (byte b6 = 0; b6 < width - size_y + 1; b6++)
                {
                    bool flag2 = false;
                    byte b7 = 0;
                    while (b7 < size_x && !flag2)
                    {
                        for (byte b8 = 0; b8 < size_y; b8++)
                        {
                            if (slots[b6 + b8, b5 + b7]) { flag2 = true; break; }
                            if (b8 == size_y - 1 && b7 == size_x - 1) { x = b6; y = b5; rot = 1; return true; }
                        }
                        b7++;
                    }
                }
            return false;
        }

        void fillSlot(ItemJar jar, bool isOccupied)
        {
            byte b = jar.size_x, b2 = jar.size_y;
            if (jar.rot % 2 == 1) { b = jar.size_y; b2 = jar.size_x; }
            for (byte b3 = 0; b3 < b; b3++)
                for (byte b4 = 0; b4 < b2; b4++)
                    if (jar.x + b3 < width && jar.y + b4 < height)
                        slots[jar.x + b3, jar.y + b4] = isOccupied;
        }
    }
}
