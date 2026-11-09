using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SuperWrap", "Shturman", "1.0.0")]
    [Description("Супер оберточная бумага: упаковывает до 20 предметов в подарок-кейс")]
    class SuperWrap : RustPlugin
    {
        #region Config

        private ConfigData config;

        private class ConfigData
        {
            [JsonProperty("Бумага: shortname предмета")]
            public string PaperShortname = "wrappingpaper";

            [JsonProperty("Бумага: имя")]
            public string PaperName = "Супер оберточная бумага";

            [JsonProperty("Бумага: skin id")]
            public ulong PaperSkin = 3414795863;

            [JsonProperty("Подарок: shortname предмета")]
            public string GiftShortname = "xmas.present.small";

            [JsonProperty("Подарок: имя")]
            public string GiftName = "кейс";

            [JsonProperty("Подарок: skin id")]
            public ulong GiftSkin = 3338711565;

            [JsonProperty("Вместимость (слотов)")]
            public int Slots = 20;

            [JsonProperty("Открывать упаковку по ЛКМ с бумагой в руках")]
            public bool OpenOnClick = true;
        }

        protected override void LoadDefaultConfig() => config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { config = Config.ReadObject<ConfigData>(); if (config == null) throw new Exception(); }
            catch { PrintWarning("Конфиг повреждён, создан новый."); LoadDefaultConfig(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Data

        private const string PermGive = "superwrap.give";
        private const string PermUse = "superwrap.use";
        private const string BoxPrefab = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";

        private Dictionary<string, List<ItemData>> gifts = new Dictionary<string, List<ItemData>>();

        private class ItemData
        {
            public string Short;
            public int Amount;
            public ulong Skin;
            public string Name;
            public string Text;
            public float Condition;
            public float MaxCondition;
            public int BlueprintTarget;
            public int Ammo;
            public string AmmoType;
            public List<ItemData> Contents;
        }

        private void LoadData()
        {
            try { gifts = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, List<ItemData>>>(Name); }
            catch { gifts = null; }
            if (gifts == null) gifts = new Dictionary<string, List<ItemData>>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, gifts);

        private class WrapRecord
        {
            public string GiftId;
            public string WrappedAt;
            public List<ItemData> Items = new List<ItemData>();
            public bool Opened;
            public string OpenedAt;
            public string OpenedBySteamId;
            public string OpenedByName;
        }

        private class PlayerLog
        {
            public string SteamId;
            public string Name;
            public List<WrapRecord> Records = new List<WrapRecord>();
        }

        private string LogPath(string steamId) => Name + "/" + steamId;

        private PlayerLog LoadLog(string steamId)
        {
            PlayerLog log = null;
            try { log = Interface.Oxide.DataFileSystem.ReadObject<PlayerLog>(LogPath(steamId)); }
            catch { log = null; }
            if (log == null) log = new PlayerLog();
            log.SteamId = steamId;
            if (log.Records == null) log.Records = new List<WrapRecord>();
            return log;
        }

        private void SaveLog(PlayerLog log) =>
            Interface.Oxide.DataFileSystem.WriteObject(LogPath(log.SteamId), log);

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        private static string OwnerFromGiftId(string giftId)
        {
            var parts = giftId.Split(':');
            return parts.Length == 3 ? parts[1] : null;
        }

        private void LogWrap(BasePlayer player, string giftId, List<ItemData> items)
        {
            var log = LoadLog(player.UserIDString);
            log.Name = player.displayName;
            log.Records.Add(new WrapRecord
            {
                GiftId = giftId,
                WrappedAt = Now(),
                Items = items
            });
            SaveLog(log);
        }

        private void LogOpen(string giftId, BasePlayer opener)
        {
            var owner = OwnerFromGiftId(giftId);
            if (owner == null) return;
            var log = LoadLog(owner);
            log.Records.RemoveAll(r => r.GiftId == giftId);

            if (log.Records.Count == 0)
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(LogPath(owner)))
                    Interface.Oxide.DataFileSystem.DeleteDataFile(LogPath(owner));
            }
            else SaveLog(log);
        }

        #endregion

        #region Oxide hooks

        private readonly Dictionary<ulong, StorageContainer> sessions = new Dictionary<ulong, StorageContainer>();
        private readonly Dictionary<ulong, float> clickCooldown = new Dictionary<ulong, float>();

        private void Init()
        {
            permission.RegisterPermission(PermGive, this);
            permission.RegisterPermission(PermUse, this);
            LoadData();
        }

        private void Unload()
        {
            foreach (var kv in sessions.ToList())
            {
                var player = BasePlayer.FindByID(kv.Key);
                if (player != null) player.EndLooting();
                if (kv.Value != null && !kv.Value.IsDestroyed)
                {
                    if (player != null) ReturnItems(player, kv.Value);
                    kv.Value.Kill();
                }
            }
            sessions.Clear();
            SaveData();
        }

        private void OnServerSave() => SaveData();

        private object OnItemAction(Item item, string action, BasePlayer player)
        {
            if (item == null || player == null || action != "unwrap") return null;
            string id = GetGiftId(item);
            if (id == null) return null;

            List<ItemData> list;
            if (!gifts.TryGetValue(id, out list))
            {
                return null;
            }

            gifts.Remove(id);
            item.Remove();
            foreach (var d in list)
            {
                var it = CreateItem(d);
                if (it != null) player.GiveItem(it);
            }
            SaveData();
            LogOpen(id, player);
            player.ChatMessage("<color=#7CFC00>Вы развернули подарок — предметы в инвентаре!</color>");
            return false;
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!config.OpenOnClick || player == null || input == null) return;
            if (!input.WasJustPressed(BUTTON.FIRE_PRIMARY)) return;
            var active = player.GetActiveItem();
            if (active == null || !IsPaper(active)) return;

            float last;
            if (clickCooldown.TryGetValue(player.userID, out last) && Time.realtimeSinceStartup - last < 1f) return;
            clickCooldown[player.userID] = Time.realtimeSinceStartup;

            StartWrapping(player);
        }

        private void OnLootEntityEnd(BasePlayer player, StorageContainer entity)
        {
            if (player == null || entity == null) return;
            StorageContainer box;
            if (!sessions.TryGetValue(player.userID, out box) || box != entity) return;

            sessions.Remove(player.userID);
            FinishWrapping(player, entity);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            StorageContainer box;
            if (player != null && sessions.TryGetValue(player.userID, out box))
            {
                sessions.Remove(player.userID);
                if (box != null && !box.IsDestroyed)
                {
                    ReturnItems(player, box);
                    box.Kill();
                }
            }
        }

        #endregion

        #region Wrapping

        private bool IsPaper(Item item) =>
            item != null && item.skin == config.PaperSkin && item.info.shortname == config.PaperShortname;

        private string GetGiftId(Item item)
        {
            if (item == null || item.skin != config.GiftSkin || string.IsNullOrEmpty(item.text)) return null;
            return item.text.StartsWith("SW:") ? item.text : null;
        }

        private Item FindPaper(BasePlayer player)
        {
            var active = player.GetActiveItem();
            if (IsPaper(active)) return active;
            var inv = player.inventory;
            var containers = new[] { inv.containerMain, inv.containerBelt, inv.containerWear };
            foreach (var c in containers)
            {
                if (c == null) continue;
                foreach (var it in c.itemList)
                    if (IsPaper(it)) return it;
            }
            return null;
        }

        private void StartWrapping(BasePlayer player)
        {
            if (sessions.ContainsKey(player.userID)) return;
            if (FindPaper(player) == null)
            {
                player.ChatMessage("У вас нет <color=#ffd34d>" + config.PaperName + "</color>.");
                return;
            }
            if (player.inventory.loot.IsLooting())
            {
                player.ChatMessage("Сначала закройте текущий контейнер.");
                return;
            }

            var ent = GameManager.server.CreateEntity(BoxPrefab, player.transform.position + Vector3.down * 0.5f) as StorageContainer;
            if (ent == null) return;
            ent.enableSaving = false;
            ent.Spawn();
            ent.inventory.capacity = config.Slots;
            ent.inventory.allowedContents = ItemContainer.ContentsType.Generic;
            ent.panelName = "generic";
            ent.SendNetworkUpdate();

            sessions[player.userID] = ent;

            NextTick(() =>
            {
                if (player == null || ent == null || ent.IsDestroyed || !player.IsConnected) return;
                player.inventory.loot.Clear();
                player.inventory.loot.PositionChecks = false;
                player.inventory.loot.entitySource = ent;
                player.inventory.loot.itemSource = null;
                player.inventory.loot.MarkDirty();
                player.inventory.loot.AddContainer(ent.inventory);
                player.inventory.loot.SendImmediate();
                player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), ent.panelName);
                player.SendNetworkUpdateImmediate();
            });
        }

        private void FinishWrapping(BasePlayer player, StorageContainer box)
        {
            var items = box.inventory.itemList.ToList();
            if (items.Count == 0)
            {
                box.Kill();
                player.ChatMessage("Вы ничего не положили — бумага не потрачена.");
                return;
            }

            var paper = FindPaper(player);
            if (paper == null)
            {
                ReturnItems(player, box);
                box.Kill();
                player.ChatMessage("Бумага пропала, предметы возвращены.");
                return;
            }

            var data = items.Select(SerializeItem).ToList();

            var def = ItemManager.FindItemDefinition(config.GiftShortname);
            if (def == null)
            {
                PrintError("Не найден предмет подарка: " + config.GiftShortname);
                ReturnItems(player, box);
                box.Kill();
                return;
            }

            var gift = ItemManager.Create(def, 1, config.GiftSkin);
            string id = "SW:" + player.UserIDString + ":" + Guid.NewGuid().ToString("N");
            gift.name = config.GiftName;
            gift.text = id;

            if (paper.amount > 1)
            {
                paper.UseItem(1);
                paper.MarkDirty();
            }
            else paper.Remove();

            foreach (var it in items) it.Remove();
            box.Kill();
            ItemManager.DoRemoves();

            gifts[id] = data;
            SaveData();
            LogWrap(player, id, data);

            player.GiveItem(gift);
            player.ChatMessage("<color=#7CFC00>Подарок упакован!</color> Предметов: " + data.Count);
        }

        private void ReturnItems(BasePlayer player, StorageContainer box)
        {
            foreach (var it in box.inventory.itemList.ToList())
                player.GiveItem(it);
        }

        #endregion

        #region Serialization

        private ItemData SerializeItem(Item item)
        {
            var d = new ItemData
            {
                Short = item.info.shortname,
                Amount = item.amount,
                Skin = item.skin,
                Name = item.name,
                Text = item.text,
                Condition = item.condition,
                MaxCondition = item.maxCondition,
                BlueprintTarget = item.blueprintTarget
            };

            var proj = item.GetHeldEntity() as BaseProjectile;
            if (proj != null && proj.primaryMagazine != null)
            {
                d.Ammo = proj.primaryMagazine.contents;
                d.AmmoType = proj.primaryMagazine.ammoType != null ? proj.primaryMagazine.ammoType.shortname : null;
            }

            if (item.contents != null && item.contents.itemList.Count > 0)
                d.Contents = item.contents.itemList.Select(SerializeItem).ToList();

            return d;
        }

        private Item CreateItem(ItemData d)
        {
            var def = ItemManager.FindItemDefinition(d.Short);
            if (def == null) return null;

            var item = ItemManager.Create(def, d.Amount, d.Skin);
            if (item == null) return null;

            if (!string.IsNullOrEmpty(d.Name)) item.name = d.Name;
            if (!string.IsNullOrEmpty(d.Text)) item.text = d.Text;
            if (d.MaxCondition > 0)
            {
                item.maxCondition = d.MaxCondition;
                item.condition = d.Condition;
            }
            if (d.BlueprintTarget != 0) item.blueprintTarget = d.BlueprintTarget;

            var proj = item.GetHeldEntity() as BaseProjectile;
            if (proj != null && proj.primaryMagazine != null)
            {
                proj.primaryMagazine.contents = d.Ammo;
                if (!string.IsNullOrEmpty(d.AmmoType))
                {
                    var ammo = ItemManager.FindItemDefinition(d.AmmoType);
                    if (ammo != null) proj.primaryMagazine.ammoType = ammo;
                }
            }

            if (d.Contents != null && item.contents != null)
            {
                foreach (var c in d.Contents)
                {
                    var ci = CreateItem(c);
                    if (ci != null && !ci.MoveToContainer(item.contents)) ci.Remove();
                }
            }

            item.MarkDirty();
            return item;
        }

        #endregion

        #region Commands

        [ChatCommand("wrap")]
        private void CmdWrap(BasePlayer player, string command, string[] args)
        {
            StartWrapping(player);
        }

        [ConsoleCommand("superwrap.give")]
        private void CmdGive(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                var caller = arg.Player();
                if (caller == null || (!caller.IsAdmin && !permission.UserHasPermission(caller.UserIDString, PermGive)))
                {
                    arg.ReplyWith("Нет прав.");
                    return;
                }
            }

            if (!arg.HasArgs())
            {
                arg.ReplyWith("Использование: superwrap.give <имя|steamid> [количество]");
                return;
            }

            var target = BasePlayer.Find(arg.GetString(0));
            if (target == null)
            {
                arg.ReplyWith("Игрок не найден.");
                return;
            }

            int amount = Math.Max(1, arg.GetInt(1, 1));
            if (!GivePaper(target, amount))
            {
                arg.ReplyWith("Ошибка: проверьте shortname бумаги в конфиге.");
                return;
            }

            arg.ReplyWith("Выдано " + amount + " x " + config.PaperName + " игроку " + target.displayName);
            target.ChatMessage("Вы получили <color=#ffd34d>" + config.PaperName + "</color> x" + amount);
        }

        private bool GivePaper(BasePlayer player, int amount)
        {
            var def = ItemManager.FindItemDefinition(config.PaperShortname);
            if (def == null) return false;
            var item = ItemManager.Create(def, amount, config.PaperSkin);
            if (item == null) return false;
            item.name = config.PaperName;
            player.GiveItem(item);
            return true;
        }

        #endregion
    }
}
