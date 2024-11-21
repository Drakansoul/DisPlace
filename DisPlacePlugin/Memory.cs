using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel.Sheets;
using static DisPlacePlugin.DisPlacePlugin;

namespace DisPlacePlugin
{
    public unsafe class Memory
    {

        public static GetInventoryContainerDelegate GetInventoryContainer;
        public delegate InventoryContainer* GetInventoryContainerDelegate(IntPtr inventoryManager, InventoryType inventoryType);

        // Pointers to modify assembly to enable place anywhere.
        public IntPtr placeAnywhere;
        public IntPtr wallAnywhere;
        public IntPtr wallmountAnywhere;
        private Memory()
        {
            try
            {
                // Assembly address for asm rewrites.
                placeAnywhere = DalamudApi.SigScanner.ScanText("C6 ?? ?? ?? 00 00 00 8B FE 48 89") + 6;
                wallAnywhere = DalamudApi.SigScanner.ScanText("48 85 C0 74 ?? C6 87 ?? ?? 00 00 00") + 11;
                wallmountAnywhere = DalamudApi.SigScanner.ScanText("c6 87 83 01 00 00 00 48 83 c4 ??") + 6;

                housingModulePtr = DalamudApi.SigScanner.GetStaticAddressFromSig("48 8B 05 ?? ?? ?? ?? 8B 52");
                LayoutWorldPtr = DalamudApi.SigScanner.GetStaticAddressFromSig("48 8B D1 48 8B 0D ?? ?? ?? ?? 48 85 C9 74 0A", 3);


                var getInventoryContainerPtr = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 40 38 78 18");
                GetInventoryContainer = Marshal.GetDelegateForFunctionPointer<GetInventoryContainerDelegate>(getInventoryContainerPtr);

            }
            catch (Exception e)
            {
                DalamudApi.PluginLog.Error(e, "Could not load housing memory!!");
            }
        }

        public static Memory Instance { get; private set; }

        public IntPtr housingModulePtr { get; }
        public IntPtr LayoutWorldPtr { get; }

        public unsafe HousingModule* HousingModule => housingModulePtr != IntPtr.Zero ? (HousingModule*)Marshal.ReadIntPtr(housingModulePtr) : null;
        public unsafe LayoutWorld* LayoutWorld => LayoutWorldPtr != IntPtr.Zero ? (LayoutWorld*)Marshal.ReadIntPtr(LayoutWorldPtr) : null;

        public unsafe HousingObjectManager* CurrentManager => HousingModule->currentTerritory;
        public unsafe HousingStructure* HousingStructure => LayoutWorld->HousingStruct;


        public static void Init()
        {
            Instance = new Memory();
        }

        public static InventoryContainer* GetContainer(InventoryType inventoryType)
        {
            return InventoryManager.Instance()->GetInventoryContainer(inventoryType);
        }

        public uint GetTerritoryTypeId()
        {
            if (!GetActiveLayout(out var manager)) return 0;
            return manager.TerritoryTypeId;
        }

        public bool HasUpperFloor()
        {
            var houseSize = GetIndoorHouseSize();
            return houseSize.Equals("Medium") || houseSize.Equals("Large");
        }

        public string GetIndoorHouseSize()
        {
            var territoryId = Memory.Instance.GetTerritoryTypeId();
            var row = DalamudApi.DataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);

            if (row.Equals(null)) return null;

            var placeName = row.Name.ToString();
            var sizeName = placeName.Substring(1, 3);

            switch (sizeName)
            {
                case "1i1":
                    return "Small";

                case "1i2":
                    return "Medium";

                case "1i3":
                    return "Large";

                case "1i4":
                    return "Apartment";

                default:
                    return null;
            }
        }

        public float GetInteriorLightLevel()
        {

            if (GetCurrentTerritory() != HousingArea.Indoors) return 0f;
            if (!GetActiveLayout(out var manager)) return 0f;
            if (!manager.IndoorAreaData.HasValue) return 0f;
            return manager.IndoorAreaData.Value.LightLevel;
        }

        public CommonFixture[] GetInteriorCommonFixtures(int floorId)
        {
            if (GetCurrentTerritory() != HousingArea.Indoors) return new CommonFixture[0];
            if (!GetActiveLayout(out var manager)) return new CommonFixture[0];
            if (!manager.IndoorAreaData.HasValue) return new CommonFixture[0];
            var floor = manager.IndoorAreaData.Value.GetFloor(floorId);

            var ret = new CommonFixture[IndoorFloorData.PartsMax];
            for (var i = 0; i < IndoorFloorData.PartsMax; i++)
            {
                var key = floor.GetPart(i);
                if (!HousingData.Instance.TryGetItem(unchecked((uint)key), out var item))
                    HousingData.Instance.IsUnitedExteriorPart(unchecked((uint)key), out item);

                ret[i] = new CommonFixture(
                    false,
                    i,
                    key,
                    null,
                    item);
            }

            return ret;
        }

        public CommonFixture[] GetExteriorCommonFixtures(int plotId)
        {
            if (GetCurrentTerritory() != HousingArea.Outdoors) return new CommonFixture[0];
            if (!GetHousingController(out var controller)) return new CommonFixture[0];
            var home = controller.Houses(plotId);

            if (home.Size == -1) return new CommonFixture[0];
            if (home.GetPart(0).Category == -1) return new CommonFixture[0];

            var ret = new CommonFixture[HouseCustomize.PartsMax];
            for (var i = 0; i < HouseCustomize.PartsMax; i++)
            {
                var colorId = home.GetPart(i).Color;
                HousingData.Instance.TryGetStain(colorId, out var stain);
                HousingData.Instance.TryGetItem(home.GetPart(i).FixtureKey, out var item);

                ret[i] = new CommonFixture(
                    true,
                    home.GetPart(i).Category,
                    home.GetPart(i).FixtureKey,
                    stain,
                    item);
            }

            return ret;
        }
        public unsafe bool GetExteriorPlacedObjects(out List<HousingGameObject> objects, Vector3 playerPos) 
        /*
        this misses interactable objects such as mailboxes and the cold knights cookfire.
        I'll probably just leave it that way unless people really want the functionality since it's probably a hassle to actually track down where these items are stored.
        Additionally, HouseMate also does not tag these kinds of items so I presume that adding it also wasn;t a priority for them either.
        */
        {
            objects = null;
            if (HousingModule == null ||
                HousingModule->GetCurrentManager() == null ||
                HousingModule->GetCurrentManager()->Objects == null)
                return false;

            var tmpObjects = new List<(HousingGameObject gObject, float distance)>();
            objects = new List<HousingGameObject>();
            var curMgr = HousingModule->outdoorTerritory;
            for (var i = 0; i < 400; i++)
            {
                var oPtr = curMgr->Objects[i];
                if (oPtr == 0)
                    continue;
                var o = *(HousingGameObject*) oPtr;
                tmpObjects.Add((o, Utils.DistanceFromPlayer(o, playerPos)));
            }

            tmpObjects.Sort((obj1, obj2) => obj1.distance.CompareTo(obj2.distance));
            objects = tmpObjects.Select(obj => obj.gObject).ToList();
            return true;
        }

        public unsafe bool TryGetIslandGameObjectList(out List<HousingGameObject> objects)
        {
            objects = new List<HousingGameObject>();

            var manager = (MjiManagerExtended*)MJIManager.Instance();
            var objectManager = manager->ObjectManager;
            var furnManager = objectManager->FurnitureManager;

            for (int i = 0; i < 200; i++)
            {
                var objPtr = (HousingGameObject*)furnManager->Objects[i];
                if (objPtr == null) continue;
                objects.Add(*objPtr);
            }
            return true;
        }

        public unsafe bool TryGetNameSortedHousingGameObjectList(out List<HousingGameObject> objects)
        {
            objects = null;
            if (HousingModule == null ||
                HousingModule->GetCurrentManager() == null ||
                HousingModule->GetCurrentManager()->Objects == null)
                return false;

            objects = new List<HousingGameObject>();
            for (var i = 0; i < 400; i++)
            {
                var oPtr = HousingModule->GetCurrentManager()->Objects[i];
                if (oPtr == 0)
                    continue;
                var o = *(HousingGameObject*)oPtr;
                objects.Add(o);
            }

            objects.Sort(
                (obj1, obj2) =>
                {
                    string name1 = "", name2 = "";
                    if (HousingData.Instance.TryGetFurniture(obj1.housingRowId, out var furniture1))
                        name1 = furniture1.Item.Value.Name.ToString();
                    else if (HousingData.Instance.TryGetYardObject(obj1.housingRowId, out var yardObject1))
                        name1 = yardObject1.Item.Value.Name.ToString();
                    if (HousingData.Instance.TryGetFurniture(obj2.housingRowId, out var furniture2))
                        name2 = furniture2.Item.Value.Name.ToString();
                    else if (HousingData.Instance.TryGetYardObject(obj2.housingRowId, out var yardObject2))
                        name2 = yardObject2.Item.Value.Name.ToString();

                    return string.Compare(name1, name2, StringComparison.Ordinal);
                });
            return true;
        }


        public unsafe bool GetActiveLayout(out LayoutManager manager)
        {
            manager = new LayoutManager();
            if (LayoutWorld == null ||
                LayoutWorld->ActiveLayout == null)
                return false;
            manager = *LayoutWorld->ActiveLayout;
            return true;
        }

        public bool GetHousingController(out HousingController controller)
        {
            controller = new HousingController();
            if (!GetActiveLayout(out var manager) ||
                !manager.HousingController.HasValue)
                return false;

            controller = manager.HousingController.Value;
            return true;
        }

        public enum HousingArea
        {
            Indoors,
            Outdoors,
            Island,
            None
        }

        public unsafe HousingArea GetCurrentTerritory() // this gets called every window draw for some reason, should probably looke to refactor to only call this when something needs to be changed or might have been updated.
        {
            var territoryRow = DalamudApi.DataManager.GetExcelSheet<TerritoryType>().GetRow(GetTerritoryTypeId());
            if (territoryRow.Equals(null) || territoryRow.Name.ToString().Equals("r1i5")) // blacklist company workshop from editing since it's not actually a housing area
            {
                return HousingArea.None;
            }
            //DalamudApi.PluginLog.Debug(territoryRow.Name.ToString());

            if (territoryRow.Name.ToString().Equals("h1m2"))
            {
                return HousingArea.Island;
            }

            if (HousingModule == null) return HousingArea.None;

            if (HousingModule->IsOutdoors()) return HousingArea.Outdoors;
            else return HousingArea.Indoors;
        }

        public unsafe bool IsHousingMode()
        {
            if (HousingStructure == null)
                return false;

            return HousingStructure->Mode != HousingLayoutMode.None;
        }

        /// <summary>
        /// Checks if you can edit a housing item, specifically checks that rotate mode is active.
        /// </summary>
        /// <returns>Boolean state if housing menu is on or off.</returns>
        public unsafe bool CanEditItem()
        {
            if (HousingStructure == null)
                return false;

            // Rotate mode only.
            return HousingStructure->Mode == HousingLayoutMode.Rotate;
        }

        /// <summary>
        /// Writes the position vector to memory.
        /// </summary>
        /// <param name="newPosition">Position vector to write.</param>
        public unsafe void WritePosition(Vector3 newPosition)
        {
            // Don't write if housing mode isn't on.
            if (!CanEditItem())
                return;

            try
            {
                var item = HousingStructure->ActiveItem;
                if (item == null)
                {
                    return;
                }

                // Set the position.
                item->Position = newPosition;
            }
            catch (Exception ex)
            {
                DalamudApi.PluginLog.Error(ex, "Error occured while writing position!");
            }
        }

        public unsafe void WriteRotation(Vector3 newRotation)
        {
            // Don't write if housing mode isn't on.
            if (!CanEditItem())
                return;

            try
            {
                var item = HousingStructure->ActiveItem;
                if (item == null)
                    return;

                // Convert into a quaternion.
                item->Rotation = MoveUtil.ToQ(newRotation);
            }
            catch (Exception ex)
            {
                DalamudApi.PluginLog.Error(ex, "Error occured while writing rotation!");
            }
        }
        private static void WriteProtectedBytes(IntPtr addr, byte[] b)
        {
        if (addr == IntPtr.Zero) return;
        VirtualProtect(addr, 1, Protection.PAGE_EXECUTE_READWRITE, out var oldProtection);
        Marshal.Copy(b, 0, addr, b.Length);
        VirtualProtect(addr, 1, oldProtection, out _);
        }

        private static void WriteProtectedBytes(IntPtr addr, byte b)
        {
        if (addr == IntPtr.Zero) return;
        WriteProtectedBytes(addr, [b]);
        }

        /// <summary>
        /// Sets the flag for place anywhere in memory.
        /// </summary>
        /// <param name="state">Boolean state for if you can place anywhere.</param>
        public void SetPlaceAnywhere(bool state)
        {
            
        if (placeAnywhere == IntPtr.Zero || wallAnywhere == IntPtr.Zero || wallmountAnywhere == IntPtr.Zero){
            DalamudApi.PluginLog.Debug(string.Format("Cannot Set PlaceAnywhere",state.ToString()));
            return;
        }
        DalamudApi.PluginLog.Debug(string.Format("Setting PlaceAnywhere to {0}",state.ToString()));
        

        // The byte state from boolean.
        var bstate = (byte)(state ? 1 : 0);

        // Write the bytes for place anywhere.
        WriteProtectedBytes(placeAnywhere, bstate);
        WriteProtectedBytes(wallAnywhere, bstate);
        WriteProtectedBytes(wallmountAnywhere, bstate);

        // Which bytes to write.
        // byte[] showcaseBytes = state ? [0x90, 0x90, 0x90, 0x90, 0x90, 0x90] : [0x88, 0x87, 0x98, 0x02, 0x00, 0x00];

        // // Write bytes for showcase anywhere (nop or original bytes).
        // WriteProtectedBytes(showcaseAnywhereRotate, showcaseBytes);
        // WriteProtectedBytes(showcaseAnywherePlace, showcaseBytes);
        }
        #region Kernel32

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, Protection flNewProtect, out Protection lpflOldProtect);

    public enum Protection
    {
      PAGE_NOACCESS = 0x01,
      PAGE_READONLY = 0x02,
      PAGE_READWRITE = 0x04,
      PAGE_WRITECOPY = 0x08,
      PAGE_EXECUTE = 0x10,
      PAGE_EXECUTE_READ = 0x20,
      PAGE_EXECUTE_READWRITE = 0x40,
      PAGE_EXECUTE_WRITECOPY = 0x80,
      PAGE_GUARD = 0x100,
      PAGE_NOCACHE = 0x200,
      PAGE_WRITECOMBINE = 0x400
    }

    #endregion
    }
}