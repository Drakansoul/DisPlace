using Dalamud.Game.Command;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.GeneratedSheets;
using DisPlacePlugin.Objects;
using DisPlacePlugin.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using static DisPlacePlugin.Memory;
using HousingFurniture = Lumina.Excel.GeneratedSheets.HousingFurniture;
using Lumina.Excel;

namespace DisPlacePlugin
{
    public sealed class DisPlacePlugin : IDalamudPlugin
    {
        public string Name => "DisPlace Plugin";
        public PluginUi Gui { get; private set; }
        public Configuration Config { get; private set; }

        public static List<HousingItem> ItemsToPlace = new List<HousingItem>();

        private delegate bool UpdateLayoutDelegate(IntPtr a1);
        private HookWrapper<UpdateLayoutDelegate> IsSaveLayoutHook;


        // Function for selecting an item, usually used when clicking on one in game.        
        public delegate void SelectItemDelegate(IntPtr housingStruct, IntPtr item);
        private static HookWrapper<SelectItemDelegate> SelectItemHook;
        

        public delegate void ClickItemDelegate(IntPtr housingStruct, IntPtr item);
        private static HookWrapper<ClickItemDelegate> ClickItemHook;

        public static bool CurrentlyPlacingItems = false;

        public static bool ApplyChange = false;

        public static SaveLayoutManager LayoutManager;

        public static bool logHousingDetour = false;

        internal static Location PlotLocation = new Location();

        public Layout Layout = new Layout();
        public List<HousingItem> InteriorItemList = new List<HousingItem>();
        public List<HousingItem> ExteriorItemList = new List<HousingItem>();
        public List<HousingItem> UnusedItemList = new List<HousingItem>();

        public void Dispose()
        {

            HookManager.Dispose();
            try {
                Memory.Instance.SetPlaceAnywhere(false);
            }
            catch (Exception ex)
            {
                DalamudApi.PluginLog.Error(ex, "Error while calling PluginMemory.Dispose()");
            }

            DalamudApi.ClientState.TerritoryChanged -= TerritoryChanged;
            DalamudApi.CommandManager.RemoveHandler("/displace");
            Gui?.Dispose();

        }

        public DisPlacePlugin(IDalamudPluginInterface pi)
        {
            DalamudApi.Initialize(pi);

            Config = DalamudApi.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Config.Save();

            Initialize();

            DalamudApi.CommandManager.AddHandler("/displace", new CommandInfo(CommandHandler)
            {
                HelpMessage = "load config window."
            });
            Gui = new PluginUi(this);
            DalamudApi.ClientState.TerritoryChanged += TerritoryChanged;


            HousingData.Init(this);
            Memory.Init();
            Memory.Instance.SetPlaceAnywhere(true);
            LayoutManager = new SaveLayoutManager(this, Config);

            DalamudApi.PluginLog.Info("DisPlace Plugin v3.7.0 initialized");
        }
        public void Initialize()
        {

            IsSaveLayoutHook = HookManager.Hook<UpdateLayoutDelegate>("40 53 48 83 ec 20 48 8b d9 48 8b 0d ?? ?? ?? ?? e8 ?? ?? ?? ?? 33 d2 48 8b c8 e8 ?? ?? ?? ?? 84 c0 75 ?? 38 83 ?? 01 00 00", IsSaveLayoutDetour);

            SelectItemHook = HookManager.Hook<SelectItemDelegate>("48 85 D2 0F 84 49 09 00 00 53 41 56 48 83 EC 48 48 89 6C 24 60 48 8B DA 48 89 74 24 70 4C 8B F1", SelectItemDetour);

            ClickItemHook = HookManager.Hook<ClickItemDelegate>("48 89 5C 24 10 48 89 74  24 18 57 48 83 EC 20 4c 8B 41 18 33 FF 0F B6 F2", ClickItemDetour);

            UpdateYardObjHook = HookManager.Hook<UpdateYardDelegate>("48 89 74 24 18 57 48 83 ec 20 b8 dc 02 00 00 0f b7 f2 ??", UpdateYardObj);

            GetGameObjectHook = HookManager.Hook<GetObjectDelegate>("48 89 5c 24 08 48 89 74 24 10 57 48 83 ec 20 0f b7 f2 33 db 0f 1f 40 00 0f 1f 84 00 00 00 00 00", GetGameObject);

            GetObjectFromIndexHook = HookManager.Hook<GetActiveObjectDelegate>("81 fa 90 01 00 00 75 08 48 8b 81 88 0c 00 00 c3 0f b7 81 90 0c 00 00 3b d0 72 03 33 c0 c3", GetObjectFromIndex);

            GetYardIndexHook = HookManager.Hook<GetIndexDelegate>("48 89 6c 24 18 56 48 83 ec 20 0f b6 ?? 0f b6 ?? ?? ?? ?? ?? ?? ?? ??", GetYardIndex);

            MaybePlaceh = HookManager.Hook<MaybePlaced>("40 55 56 57 48 8D AC 24 70 FF FF FF 48 81 EC 90", MaybePlace); // MaybePlace0
            ResetItemPlacementh = HookManager.Hook<ResetItemPlacementd>("48 89 5C 24 08 57 48 83  EC 20 48 83 79 18 00 0F", Hc1); // reset item to previous position on failed placement
            FinalizeHousingh = HookManager.Hook<FinalizeHousingd>("40 55 56 41 56 48 83 EC 20 48 63 EA 48 8B F1 8B", FinalizeHousing); // finalize housing placement on interface close
            PlaceCallh = HookManager.Hook<PlaceCalld>("40 53 48 83 Ec 20 48 8B 51 18 48 8B D9 48 85 D2 0F 84 B1 00 00 00", PlaceCall); // branch b place

        }

        

        internal delegate ushort GetIndexDelegate(byte type, byte objStruct);
        internal static HookWrapper<GetIndexDelegate> GetYardIndexHook;
        internal static ushort GetYardIndex(byte plotNumber, byte inventoryIndex)
        {
            var result = GetYardIndexHook.Original(plotNumber, inventoryIndex);
            return result;
        }

        internal delegate IntPtr GetActiveObjectDelegate(IntPtr ObjList, uint index);

        internal static IntPtr GetObjectFromIndex(IntPtr ObjList, uint index)
        {
            var result = GetObjectFromIndexHook.Original(ObjList, index);
            return result;
        }

        internal delegate IntPtr GetObjectDelegate(IntPtr ObjList, ushort index);
        internal static HookWrapper<GetObjectDelegate> GetGameObjectHook;
        internal static HookWrapper<GetActiveObjectDelegate> GetObjectFromIndexHook;

        internal static IntPtr GetGameObject(IntPtr ObjList, ushort index)
        {
            return GetGameObjectHook.Original(ObjList, index);
        }

        public delegate void UpdateYardDelegate(IntPtr housingStruct, ushort index);
        private static HookWrapper<UpdateYardDelegate> UpdateYardObjHook;


        private void UpdateYardObj(IntPtr objectList, ushort index)
        {
            UpdateYardObjHook.Original(objectList, index);
        }
        
        unsafe static public void SelectItemDetour(IntPtr housing, IntPtr item)
        {
            DalamudApi.PluginLog.Debug(string.Format("selecting item {0}",item.ToString()));
            SelectItemHook.Original(housing, item);
        }


        unsafe static public void SelectItem(IntPtr item)
        {
            SelectItemDetour((IntPtr)Memory.Instance.HousingStructure, item);
        }


        internal delegate void MaybePlaced(IntPtr housingPtr,IntPtr itemPtr, Int64 a); // @@@@@
        internal static HookWrapper<MaybePlaced> MaybePlaceh;
        unsafe static public void MaybePlacedt(IntPtr housingPtr,IntPtr itemPtr, Int64 a)
        {
            DalamudApi.PluginLog.Verbose(string.Format("maybe place {0} {1} {2}",(housingPtr+24).ToString(),itemPtr.ToString(),a.ToString()));
            MaybePlaceh.Original(housingPtr,itemPtr,a);
        }
        unsafe static public void MaybePlace(IntPtr housingPtr,IntPtr itemPtr, Int64 a) // ####
        {
            MaybePlacedt(housingPtr,itemPtr,a);
        }

        internal delegate void ResetItemPlacementd(IntPtr housingPtr,Int64 a); // @@@@@
        internal static HookWrapper<ResetItemPlacementd> ResetItemPlacementh;
        unsafe static public void ResetItemPlacementdt(IntPtr housingPtr, Int64 a)
        {
            DalamudApi.PluginLog.Debug(string.Format("Return item to previous location if placemnt is canceled {0}",housingPtr+24.ToString()));
            ResetItemPlacementh.Original(housingPtr,a);
        }
        unsafe static public void Hc1(IntPtr housingPtr,Int64 a) // ####
        {
            ResetItemPlacementdt(housingPtr,a);
        }
        
        internal delegate void FinalizeHousingd(IntPtr housingPtr,Int64 a,IntPtr b); // @@@@@
        internal static HookWrapper<FinalizeHousingd> FinalizeHousingh;
        unsafe static public void FinalizeHousingdt(IntPtr housingPtr, Int64 a, IntPtr b)
        {
            DalamudApi.PluginLog.Verbose(string.Format("Finalize housing {0} {1} {2}",(housingPtr+24).ToString(),a.ToString(),b.ToString()));
            FinalizeHousingh.Original(housingPtr,a,b);
        }
        unsafe static public void FinalizeHousing(IntPtr housingPtr,Int64 a,IntPtr b) // ####
        {
            FinalizeHousingdt(housingPtr,a,b);
        }
        
        internal delegate void PlaceCalld(IntPtr housingPtr,Int64 a,IntPtr b); // @@@@@
        internal static HookWrapper<PlaceCalld> PlaceCallh;
        unsafe static public void PlaceCalldt(IntPtr housingPtr, Int64 a, IntPtr b)
        {
            DalamudApi.PluginLog.Verbose(string.Format("placeCall item {0} {1} {2}",(housingPtr+24).ToString(),a.ToString(),b.ToString()));
            PlaceCallh.Original(housingPtr,a,b);
        }
        unsafe static public void PlaceCall(IntPtr housingPtr,Int64 a,IntPtr b) // ####
        {
            PlaceCalldt(housingPtr,a,b);
        }

        unsafe static public void ClickItemDetour(IntPtr housing, IntPtr item)
        {
            /*
            The call made by the XIV client has some strange behaviour.
            It can either place the item pointer passed to it or it retrieves the activeItem from the housing object.
            I tried passing the active item but I believe that doing so led to more crashes.
            As such I've just defaulted to the easier path of just passing in a zero pointer so that the call populates itself form the housing object.
            */
            DalamudApi.PluginLog.Verbose(string.Format("attempting to place item {0}",(housing+24).ToString()));
            ClickItemHook.Original(housing, item);
        }


        unsafe static public void ClickItem(IntPtr item)
        {
            ClickItemDetour((IntPtr)Memory.Instance.HousingStructure, item);
        }
        

        public unsafe void PlaceItems()
        {

            if (!Memory.Instance.CanEditItem() || ItemsToPlace.Count == 0)
            {
                return;
            }

            try
            {

                if (Memory.Instance.GetCurrentTerritory() == Memory.HousingArea.Outdoors)
                {
                    GetPlotLocation();
                }

                while (ItemsToPlace.Count > 0)
                {
                    var item = ItemsToPlace.First();
                    ItemsToPlace.RemoveAt(0);

                    if (item.ItemStruct == IntPtr.Zero) continue;

                    if (item.CorrectLocation && item.CorrectRotation)
                    {
                        Log($"{item.Name} is already correctly placed");
                        continue;
                    }
                    
                    SetItemPosition(item);

                    if (Config.LoadInterval > 0)
                    {
                        Thread.Sleep(Config.LoadInterval);
                    }

                }

                if (ItemsToPlace.Count == 0)
                {
                    Log("Finished applying layout");
                }

            }
            catch (Exception e)
            {
                LogError($"Error: {e.Message}", e.StackTrace);
            }

            CurrentlyPlacingItems = false;
        }

        unsafe public static void SetItemPosition(HousingItem rowItem)
        {

            if (!Memory.Instance.CanEditItem())
            {
                LogError("Unable to set position outside of Rotate Layout mode");
                return;
            }

            if (rowItem.ItemStruct == IntPtr.Zero) return;

            var MemInstance = Memory.Instance;

            logHousingDetour = true;
            ApplyChange = true;

            SelectItem(rowItem.ItemStruct);

            var thisItem = MemInstance.HousingStructure->ActiveItem;
            if (thisItem == null) {
                    DalamudApi.PluginLog.Error("Error occured while writing position! Item Was null");
                    return;
            }

            Log("Placing " + rowItem.Name);

            Vector3 position = new Vector3(rowItem.X, rowItem.Y, rowItem.Z);
            Vector3 rotation = new Vector3();

            rotation.Y = (float)(rowItem.Rotate * 180 / Math.PI);

            if (MemInstance.GetCurrentTerritory() == Memory.HousingArea.Outdoors)
            {
                var rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -PlotLocation.rotation);
                position = Vector3.Transform(position, rotateVector) + PlotLocation.ToVector();
                rotation.Y = (float)((rowItem.Rotate - PlotLocation.rotation) * 180 / Math.PI);
            }
            MemInstance.WritePosition(position);
            MemInstance.WriteRotation(rotation);

            ClickItem(nint.Zero);

            rowItem.CorrectLocation = true;
            rowItem.CorrectRotation = true;

        }

        public void ApplyLayout()
        {
            if (CurrentlyPlacingItems)
            {
                LogError($"Already placing items");
                return;
            }

            CurrentlyPlacingItems = true;
            Log($"Applying layout with interval of {Config.LoadInterval}ms");

            ItemsToPlace.Clear();

            List<HousingItem> placedLast = new List<HousingItem>();

            List<HousingItem> toBePlaced;

            if (Memory.Instance.GetCurrentTerritory() == Memory.HousingArea.Indoors)
            {
                toBePlaced = new List<HousingItem>();
                foreach (var houseItem in InteriorItemList)
                {
                    if (IsSelectedFloor(houseItem.Y))
                    {
                        toBePlaced.Add(houseItem);
                    }
                }
            }
            else
            {
                toBePlaced = new List<HousingItem>(ExteriorItemList);
            }

            foreach (var item in toBePlaced)
            {
                if (item.IsTableOrWallMounted)
                {
                    placedLast.Add(item);
                }
                else
                {
                    ItemsToPlace.Add(item);
                }
            }

            ItemsToPlace.AddRange(placedLast);


            var thread = new Thread(PlaceItems);
            thread.Start();
        }

        public bool MatchItem(HousingItem item, uint itemKey)
        {
            if (item.ItemStruct != IntPtr.Zero) return false;       // this item is already matched. We can skip

            return item.ItemKey == itemKey && IsSelectedFloor(item.Y);
        }

        public unsafe bool MatchExactItem(HousingItem item, uint itemKey, HousingGameObject obj)
        {
            if (!MatchItem(item, itemKey)) return false;

            if (item.Stain != obj.color) return false;

            var matNumber = obj.Item->MaterialManager->MaterialSlot1;

            if (item.MaterialItemKey == 0 && matNumber == 0) return true;
            else if (item.MaterialItemKey != 0 && matNumber == 0) return false;

            var matItemKey = HousingData.Instance.GetMaterialItemKey(item.ItemKey, matNumber);
            if (matItemKey == 0) return true;

            return matItemKey == item.MaterialItemKey;

        }

        public unsafe void MatchLayout()
        {

            List<HousingGameObject> allObjects = null;
            Memory Mem = Memory.Instance;

            Quaternion rotateVector = new();
            var currentTerritory = Mem.GetCurrentTerritory();

            switch (currentTerritory)
            {
                case HousingArea.Indoors:
                    Mem.TryGetNameSortedHousingGameObjectList(out allObjects);
                    InteriorItemList.ForEach(item =>
                    {
                        item.ItemStruct = IntPtr.Zero;
                    });
                    break;

                case HousingArea.Outdoors:
                    GetPlotLocation();
                    //Mem.GetExteriorPlacedObjects(out allObjects);
                    Mem.TryGetNameSortedHousingGameObjectList(out allObjects);
                    ExteriorItemList.ForEach(item =>
                    {
                        item.ItemStruct = IntPtr.Zero;
                    });
                    rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlotLocation.rotation);
                    break;
                case HousingArea.Island:
                    Mem.TryGetIslandGameObjectList(out allObjects);
                    ExteriorItemList.ForEach(item =>
                    {
                        item.ItemStruct = IntPtr.Zero;
                    });
                    break;
            }

            List<HousingGameObject> unmatched = new List<HousingGameObject>();

            // first we find perfect match
            foreach (var gameObject in allObjects)
            {
                if (!IsSelectedFloor(gameObject.Y)) continue;

                uint furnitureKey = gameObject.housingRowId;
                HousingItem houseItem = null;

                Vector3 localPosition = new Vector3(gameObject.X, gameObject.Y, gameObject.Z);
                float localRotation = gameObject.rotation;

                if (currentTerritory == HousingArea.Indoors)
                {
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                    var itemKey = furniture.Item.Value.RowId;
                    houseItem = Utils.GetNearestHousingItem(
                        InteriorItemList.Where(item => MatchExactItem(item, itemKey, gameObject)),
                        localPosition
                    );
                }
                else
                {
                    if (currentTerritory == HousingArea.Outdoors)
                    {
                        localPosition = Vector3.Transform(localPosition - PlotLocation.ToVector(), rotateVector);
                        localRotation += PlotLocation.rotation;
                    }
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                    var itemKey = furniture.Item.Value.RowId;
                    houseItem = Utils.GetNearestHousingItem(
                        ExteriorItemList.Where(item => MatchExactItem(item, itemKey, gameObject)),
                        localPosition
                    );

                }

                if (houseItem == null)
                {
                    unmatched.Add(gameObject);
                    continue;
                }

                // check if it's already correctly placed & rotated
                var locationError = houseItem.GetLocation() - localPosition;
                houseItem.CorrectLocation = locationError.Length() < 0.00001;
                houseItem.CorrectRotation = localRotation - houseItem.Rotate < 0.001;

                houseItem.ItemStruct = (IntPtr)gameObject.Item;
            }

            UnusedItemList.Clear();

            // then we match even if the dye doesn't fit
            foreach (var gameObject in unmatched)
            {

                uint furnitureKey = gameObject.housingRowId;
                HousingItem houseItem = null;

                Item item;
                Vector3 localPosition = new Vector3(gameObject.X, gameObject.Y, gameObject.Z);
                float localRotation = gameObject.rotation;

                if (currentTerritory == HousingArea.Indoors)
                {
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                    item = furniture.Item.Value;
                    houseItem = Utils.GetNearestHousingItem(
                        InteriorItemList.Where(hItem => MatchItem(hItem, item.RowId)),
                        new Vector3(gameObject.X, gameObject.Y, gameObject.Z)
                    );
                }
                else
                {
                    if (currentTerritory == HousingArea.Outdoors)
                    {
                        localPosition = Vector3.Transform(localPosition - PlotLocation.ToVector(), rotateVector);
                        localRotation += PlotLocation.rotation;
                    }
                    var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                    item = furniture.Item.Value;
                    houseItem = Utils.GetNearestHousingItem(
                        ExteriorItemList.Where(hItem => MatchItem(hItem, item.RowId)),
                        localPosition
                    );
                }
                if (houseItem == null)
                {
                    var unmatchedItem = new HousingItem(
                    item,
                    gameObject.color,
                    gameObject.X,
                    gameObject.Y,
                    gameObject.Z,
                    gameObject.rotation);
                    UnusedItemList.Add(unmatchedItem);
                    continue;
                }

                // check if it's already correctly placed & rotated
                var locationError = houseItem.GetLocation() - localPosition;
                houseItem.CorrectLocation = locationError.LengthSquared() < 0.0001;
                houseItem.CorrectRotation = localRotation - houseItem.Rotate < 0.001;

                houseItem.DyeMatch = false;

                houseItem.ItemStruct = (IntPtr)gameObject.Item;

            }

        }

        public unsafe void GetPlotLocation()
        {
            var mgr = Memory.Instance.HousingModule->outdoorTerritory;
            var plotNumber = mgr->Plot + 1;
            if (plotNumber == 256) {
                LogError("Not inside a valid Plot");
                PlotLocation = new Location();
                return;
            } else {
                
            DalamudApi.PluginLog.Debug($"Housing plot: {plotNumber}");
            }
            var territoryId = Memory.Instance.GetTerritoryTypeId();
            TerritoryType row = null;
            try {
                row = DalamudApi.DataManager.GetExcelSheet<TerritoryType>().GetRow(territoryId);
            } catch (Exception e) {
                LogError($"Error: {e.Message}", e.StackTrace);
            }
            if (row == null)
            {
                LogError("Plugin Cannot identify territory");
                return;
            }

            var placeName = row.Name.ToString();
            
            DalamudApi.PluginLog.Info($"Loading Plot Number: {plotNumber}");
            PlotLocation = Plots.Map[placeName][plotNumber];
        }


        public unsafe void LoadExterior()
        {
            SaveLayoutManager.LoadExteriorFixtures();

            List<HousingGameObject> objects;
            var playerPos = DalamudApi.ClientState.LocalPlayer.Position;
            Memory.Instance.GetExteriorPlacedObjects(out objects, playerPos);
            ExteriorItemList.Clear();
            GetPlotLocation();
            DalamudApi.PluginLog.Info($"Plot size: {PlotLocation.size}");
            DalamudApi.PluginLog.Info($"Plot x: {PlotLocation.x}");
            DalamudApi.PluginLog.Info($"Plot y: {PlotLocation.y}");
            DalamudApi.PluginLog.Info($"Plot z: {PlotLocation.z}");
            DalamudApi.PluginLog.Info($"Plot rot: {PlotLocation.rotation}");
            DalamudApi.PluginLog.Info($"Plot enter: {PlotLocation.entranceLayout}");
            
            DalamudApi.PluginLog.Debug($"Player location within Plot: ({playerPos.X-PlotLocation.x},{playerPos.Y-PlotLocation.y},{playerPos.Z-PlotLocation.z})");

            foreach (var gameObject in objects)
            {
                uint furnitureKey = gameObject.housingRowId;
                var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                Item item = furniture?.Item?.Value;
                if (item == null) continue;
                if (item.RowId == 0) continue;

                // I could probably do this better if I was fully rested and wanted to do vector math properly but I'm just going to do it the easy way. Drakansoul is very tired.
                var xMax = 0;
                var yMax = 7;
                var zMax = 0;
                switch (PlotLocation.size){ 
                    case "l":
                    xMax = 29;
                    zMax = 29;
                    break;
                    case "m":
                    xMax = 16;
                    zMax = 16;
                    break;
                    case "s":
                    xMax = 12;
                    zMax = 12;
                    break;
                    default:
                    yMax = 0;
                    break;
                }


                var housingItem = new HousingItem(item, gameObject);
                housingItem.ItemStruct = (IntPtr)gameObject.Item;
                
                var location = new Vector3(housingItem.X, housingItem.Y, housingItem.Z);
                var rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlotLocation.rotation);
                var newLocation = Vector3.Transform(location - PlotLocation.ToVector(), rotateVector);

                housingItem.X = newLocation.X;
                housingItem.Y = newLocation.Y;
                housingItem.Z = newLocation.Z;
                housingItem.Rotate += PlotLocation.rotation;

                if (!(Math.Abs(housingItem.X) > xMax|| Math.Abs(housingItem.Y) > yMax|| Math.Abs(housingItem.Z) > zMax)) { 
                    ExteriorItemList.Add(housingItem);
                } else {
                    DalamudApi.PluginLog.Verbose($"Discarding item: {housingItem.Name} at ({housingItem.X},{housingItem.Y},{housingItem.Z})");
                }
            }

            Config.Save();
        }

        public bool IsSelectedFloor(float y)
        {
            if (Memory.Instance.GetCurrentTerritory() != Memory.HousingArea.Indoors || Memory.Instance.GetIndoorHouseSize().Equals("Apartment")) return true;

            if (y < -0.001) return Config.Basement;
            if (y >= -0.001 && y < 6.999) return Config.GroundFloor;

            if (y >= 6.999)
            {
                if (Memory.Instance.HasUpperFloor()) return Config.UpperFloor;
                else return Config.GroundFloor;
            }

            return false;
        }


        public unsafe void LoadInterior()
        {
            SaveLayoutManager.LoadInteriorFixtures();

            List<HousingGameObject> dObjects;
            Memory.Instance.TryGetNameSortedHousingGameObjectList(out dObjects);

            InteriorItemList.Clear();

            foreach (var gameObject in dObjects)
            {
                uint furnitureKey = gameObject.housingRowId;

                var furniture = DalamudApi.DataManager.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                Item item = furniture?.Item?.Value;

                if (item == null) continue;
                if (item.RowId == 0) continue;

                if (!IsSelectedFloor(gameObject.Y)) continue;

                var housingItem = new HousingItem(item, gameObject);
                housingItem.ItemStruct = (IntPtr)gameObject.Item;

                if (gameObject.Item != null && gameObject.Item->MaterialManager != null)
                {
                    ushort material = gameObject.Item->MaterialManager->MaterialSlot1;
                    housingItem.MaterialItemKey = HousingData.Instance.GetMaterialItemKey(item.RowId, material);
                }

                InteriorItemList.Add(housingItem);
            }

            Config.Save();

        }


        public unsafe void LoadIsland()
        {
            SaveLayoutManager.LoadIslandFixtures();

            List<HousingGameObject> objects;
            Memory.Instance.TryGetIslandGameObjectList(out objects);
            ExteriorItemList.Clear();

            foreach (var gameObject in objects)
            {
                uint furnitureKey = gameObject.housingRowId;
                var furniture = DalamudApi.DataManager.GetExcelSheet<HousingYardObject>().GetRow(furnitureKey);
                Item item = furniture?.Item?.Value;

                if (item == null) continue;
                if (item.RowId == 0) continue;

                var housingItem = new HousingItem(item, gameObject);
                housingItem.ItemStruct = (IntPtr)gameObject.Item;

                ExteriorItemList.Add(housingItem);
            }

            Config.Save();
        }

        public void GetGameLayout()
        {
            Memory Mem = Memory.Instance;
            var currentTerritory = Mem.GetCurrentTerritory();
            DalamudApi.PluginLog.Debug($"Ter ID: {currentTerritory}");

            var itemList = currentTerritory == HousingArea.Indoors ? InteriorItemList : ExteriorItemList;
            itemList.Clear();

            switch (currentTerritory)
            {
                case HousingArea.Outdoors:
                    LoadExterior();
                    break;

                case HousingArea.Indoors:
                    LoadInterior();
                    break;

                case HousingArea.Island:
                    LoadIsland();
                    break;
            }

            Log(String.Format("Loaded {0} furniture items", itemList.Count));

            Config.HiddenScreenItemHistory = new List<int>();
            Config.Save();
        }


        public bool IsSaveLayoutDetour(IntPtr housingStruct)
        {
            var result = IsSaveLayoutHook.Original(housingStruct);

            if (ApplyChange)
            {
                ApplyChange = false;
                return true;
            }

            return result;
        }


        private void TerritoryChanged(ushort e)
        {
            Config.DrawScreen = false;
            Config.Save();
        }

        public unsafe void CommandHandler(string command, string arguments)
        {
            var args = arguments.Trim().Replace("\"", string.Empty);

            try
            {
                if (string.IsNullOrEmpty(args) || args.Equals("config", StringComparison.OrdinalIgnoreCase))
                {
                    Gui.ConfigWindow.Visible = !Gui.ConfigWindow.Visible;
                }
            }
            catch (Exception e)
            {
                LogError(e.Message, e.StackTrace);
            }
        }

        public static void Log(string message, string detail_message = "")
        {
            var msg = $"{message}";
            DalamudApi.PluginLog.Info(detail_message == "" ? msg : detail_message);
            DalamudApi.ChatGui.Print(msg);
        }
        public static void LogError(string message, string detail_message = "")
        {
            var msg = $"{message}";
            DalamudApi.PluginLog.Error(msg);

            if (detail_message.Length > 0) DalamudApi.PluginLog.Error(detail_message);

            DalamudApi.ChatGui.PrintError(msg);
        }

    }

}
