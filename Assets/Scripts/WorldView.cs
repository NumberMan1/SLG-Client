using System;
using System.Collections.Generic;
using Slg.Protocol;
using UnityEngine;

namespace Slg
{
    internal sealed partial class WorldView
    {
        private const double NoticeSeconds = 2.6;
        private const float PanelHeader = 52;
        private static readonly uint[] MarchChoices = { 20, 40, 60 };
        private static readonly TroopType[] Formations = { TroopType.Spear, TroopType.Bow, TroopType.Cavalry };
        private static readonly Vector2Int[] Neighbors = { new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1) };
        private static readonly string[] Tabs = { "地块", "军队", "战场", "战报" };
        private static readonly Color Panel = new Color32(25, 33, 31, 255);
        private static readonly Color Inset = new Color32(15, 22, 22, 255);
        private static readonly Color Line = new Color32(58, 68, 61, 255);
        private static readonly Color Paper = new Color32(247, 236, 212, 255);
        private static readonly Color Muted = new Color32(174, 186, 172, 255);
        private static readonly Color Gold = new Color32(223, 190, 125, 255);
        private static readonly Color Friendly = new Color32(128, 224, 188, 255);
        private static readonly Color Enemy = new Color32(255, 153, 127, 255);

        private readonly Dictionary<uint, WorldTile> tiles = new();
        private readonly Dictionary<Vector2Int, WorldTile> tilesByPosition = new();
        private readonly Dictionary<uint, WorldBattle> battles = new();
        private readonly Dictionary<uint, int> inbound = new();
        private readonly Dictionary<uint, WorldTile> sources = new();
        private readonly Dictionary<uint, (double Start, string Label)> notices = new();
        private readonly List<WorldTile> garrisons = new();
        private readonly List<WorldBattle> activeBattles = new();
        private readonly List<WorldMarch> ownMarches = new();
        private readonly GUIStyle heading;
        private readonly GUIStyle body;
        private readonly GUIStyle muted;
        private readonly GUIStyle note;
        private readonly GUIStyle centered;
        private readonly GUIStyle badge;
        private readonly GUIStyle primary;
        private readonly GUIStyle disabledButton;
        private readonly GUIStyle number;
        private WorldSnapshot world;
        private Vector2 mapMin, mapMax;
        private bool attacking;
        private uint home;
        private uint selectedTile;
        private uint selectedBattleTile;
        private double locateUntil;
        private uint marchTroops = 40;
        private TroopType marchType = TroopType.Spear;
        private int ownedTiles;
        private ulong garrisonTroops;
        private Tab tab;
        private Vector2 armyScroll;

        private enum Tab { Tile, Army, Battle, Reports }

        internal WorldView(Font font, Texture2D terrainAtlas, Texture2D campaignMap, Texture2D battlefields, Texture2D battleUnits)
        {
            this.terrainAtlas = terrainAtlas;
            this.campaignMap = campaignMap;
            this.battlefields = battlefields;
            this.battleUnits = battleUnits;
            heading = Text(font, 23, Paper, FontStyle.Bold);
            body = Text(font, 17, Paper);
            muted = Text(font, 15, Muted);
            note = new GUIStyle(muted) { wordWrap = true };
            centered = Text(font, 16, Paper, FontStyle.Bold, TextAnchor.MiddleCenter);
            badge = Text(font, 13, Paper, FontStyle.Bold, TextAnchor.MiddleCenter);
            primary = Text(font, 16, Inset, FontStyle.Bold, TextAnchor.MiddleCenter);
            disabledButton = Text(font, 16, Muted, FontStyle.Bold, TextAnchor.MiddleCenter);
            number = Text(font, 23, Gold, FontStyle.Bold);
        }

        internal void Dispose()
        {
            UnityEngine.Object.Destroy(circle);
            UnityEngine.Object.Destroy(landmarks);
        }

        internal void ApplySnapshot(WorldSnapshot snapshot)
        {
            if (ReferenceEquals(world, snapshot)) return;
            bool sameWorld = snapshot != null && world?.Instance == snapshot.Instance;
            bool sameTiles = sameWorld && snapshot.HasTileRevision && world.HasTileRevision &&
                snapshot.TileRevision == world.TileRevision && snapshot.Tiles.Count == tiles.Count;
            if (!sameWorld)
            {
                // 实例更换时旧选择、镜头位置和占领提示均失效。
                selectedTile = selectedBattleTile = 0;
                locateUntil = 0;
                notices.Clear();
                armyScroll = Vector2.zero;
                tab = Tab.Tile;
                ResetMap();
                showBattleMap = false;
                sceneBattleId = 0;
                sceneResult = null;
            }
            else if (!sameTiles)
            {
                foreach (var tile in snapshot.Tiles)
                {
                    if (tile.Home == 0 && tile.Owner != 0 && tiles.TryGetValue(tile.Id, out var previous) && previous.Owner == 0)
                        notices[tile.Id] = (Time.realtimeSinceStartupAsDouble, "已占领");
                }
            }

            bool wasInBattle = battles.ContainsKey(selectedTile);
            world = snapshot;
            if (!sameTiles) UpdateTiles();
            battles.Clear();
            activeBattles.Clear();
            inbound.Clear();
            sources.Clear();
            ownMarches.Clear();
            attacking = false;
            if (world == null) return;
            foreach (var battle in world.Battles)
            {
                battles[battle.TargetTileId] = battle;
                activeBattles.Add(battle);
                if (battle.Attacker == world.YourPlayer) attacking = true;
            }
            activeBattles.Sort((first, second) => first.TargetTileId.CompareTo(second.TargetTileId));
            foreach (var march in world.Marches)
            {
                inbound[march.TargetTileId] = inbound.GetValueOrDefault(march.TargetTileId) + 1;
                if (march.Player == world.YourPlayer) ownMarches.Add(march);
            }
            UpdateGarrisons();
            UpdateSources();
            if (!tiles.ContainsKey(selectedTile)) selectedTile = home;
            if (!wasInBattle && tab == Tab.Tile && battles.ContainsKey(selectedTile))
                SelectTile(selectedTile, Tab.Battle);
        }

        private void UpdateTiles()
        {
            tiles.Clear();
            tilesByPosition.Clear();
            home = 0;
            ownedTiles = 0;
            mapMin = Vector2.one * float.PositiveInfinity;
            mapMax = Vector2.one * float.NegativeInfinity;
            if (world == null) return;
            foreach (var tile in world.Tiles)
            {
                tiles[tile.Id] = tile;
                tilesByPosition[new Vector2Int(tile.Q, tile.R)] = tile;
                Vector2 point = GridPoint(tile);
                mapMin = Vector2.Min(mapMin, point);
                mapMax = Vector2.Max(mapMax, point);
                if (tile.Owner != world.YourPlayer) continue;
                ownedTiles++;
                if (tile.Home != 0) home = tile.Id;
            }
        }

        private void UpdateGarrisons()
        {
            garrisons.Clear();
            garrisonTroops = 0;
            foreach (var tile in world.Tiles)
            {
                // 交战地块保留开战时的驻军数，守军当前兵力由参战条目展示。
                if (tile.Owner != world.YourPlayer || tile.Garrison == 0 || battles.ContainsKey(tile.Id)) continue;
                garrisons.Add(tile);
                garrisonTroops += tile.Garrison;
            }
        }

        private void UpdateSources()
        {
            foreach (var tile in world.Tiles)
            {
                if (tile.Home != 0) continue;
                if (!battles.ContainsKey(tile.Id) && (tile.Owner == world.YourPlayer ||
                    (tile.Owner != 0 && inbound.ContainsKey(tile.Id)))) continue;

                // 和游戏服一致，在相邻己方领地中选地块编号最小的出发点。
                var position = new Vector2Int(tile.Q, tile.R);
                WorldTile first = null;
                foreach (var neighbor in Neighbors)
                {
                    if (tilesByPosition.TryGetValue(position + neighbor, out var source) && source.Owner == world.YourPlayer &&
                        (first == null || source.Id < first.Id)) first = source;
                }
                if (first != null) sources.Add(tile.Id, first);
            }
        }

        internal void ShowBattle(WorldBattleReport report)
        {
            notices[report.TargetTileId] = (Time.realtimeSinceStartupAsDouble, report.Captured ? "攻占" : "守住");
            // 保留正在观看的战斗结算，按战斗编号匹配，避免同一据点的新战斗混入。
            if (tab == Tab.Battle && !showBattleMap && sceneBattleId == report.BattleId)
                sceneResult = report;
        }

        internal (uint Target, uint Troops, ulong BattleId, bool Retreat, TroopType Type)? Draw(Rect bounds, bool compact, bool submitting, double serverNow,
            IReadOnlyList<WorldBattleReport> reports, ref int reportIndex)
        {
            const float gap = 14;
            const float sidebarWidth = 354;
            float mapHeight = compact ? bounds.height - 478 - gap : bounds.height;
            var map = new Rect(bounds.x, bounds.y, compact ? bounds.width : bounds.width - sidebarWidth - gap, mapHeight);
            var sidebar = compact ? new Rect(bounds.x, map.yMax + gap, bounds.width, bounds.yMax - map.yMax - gap) :
                new Rect(map.xMax + gap, bounds.y, sidebarWidth, bounds.height);
            if (tab == Tab.Battle && !showBattleMap && tiles.ContainsKey(selectedTile))
                DrawBattleScene(map, serverNow);
            else
                DrawMap(map, serverNow);
            DrawFrame(sidebar);
            float tabWidth = (sidebar.width - 20) / Tabs.Length;
            for (int index = 0; index < Tabs.Length; index++)
            {
                var button = new Rect(sidebar.x + 10 + index * tabWidth, sidebar.y + 8, tabWidth, 38);
                if (DrawButton(button, Tabs[index], tab == (Tab)index, true))
                {
                    if (tab != (Tab)index)
                    {
                        sceneResult = null;
                        sceneBattleId = 0;
                    }
                    tab = (Tab)index;
                    showBattleMap = false;
                }
            }
            Fill(new Rect(sidebar.x + 10, sidebar.y + PanelHeader, sidebar.width - 20, 1), Line);
            var content = new Rect(sidebar.x + 16, sidebar.y + PanelHeader + 13, sidebar.width - 32, sidebar.height - PanelHeader - 25);
            switch (tab)
            {
                case Tab.Army: DrawArmy(content, serverNow); break;
                case Tab.Battle when sceneResult != null: DrawBattleOutcome(content, reports, ref reportIndex); break;
                case Tab.Battle: return DrawBattlefront(content, serverNow, submitting);
                case Tab.Reports: DrawHistory(content, reports, ref reportIndex); break;
                default: return DrawOrders(content, submitting);
            }
            return null;
        }

        private (uint Target, uint Troops, ulong BattleId, bool Retreat, TroopType Type)? DrawOrders(Rect area, bool submitting)
        {
            if (!tiles.TryGetValue(selectedTile, out var target))
            {
                GUI.Label(area, "点击地图上的地块查看详情。", note);
                return null;
            }

            float x = area.x, y = area.y, width = area.width;
            battles.TryGetValue(selectedTile, out var battle);
            GUI.Label(new Rect(x, y, width, 34), TileName(target), heading);
            string owner = target.Owner == 0 ? "中立" : target.Owner == world.YourPlayer ? "己方领地" : "敌方领地";
            GUI.Label(new Rect(x, y + 37, width, 24), $"{owner} · {TerrainName(target.Terrain)} · 坐标 {target.Q}, {target.R}", muted);
            DrawTerrainBanner(new Rect(x, y + 69, width, 67), target.Terrain);
            string troops = target.Home != 0 ? "主城受保护" : target.Garrison == 0 ? "暂无驻军" :
                $"驻军 · {TroopName(target.GarrisonType)} {target.Garrison} 兵";
            if (battle != null) troops = "交战中";
            GUI.Label(new Rect(x + 12, y + 79, width - 24, 26), troops, body);
            bool hasSource = sources.TryGetValue(target.Id, out var source);
            if (hasSource) GUI.Label(new Rect(x + 12, y + 106, width - 24, 23), $"出发地 · {TileName(source)}", muted);
            if (battle != null && DrawButton(new Rect(area.xMax - 106, y + 83, 94, 36), "查看交战", false, true))
            {
                Locate(battle.TargetTileId, Tab.Battle);
            }
            if (battle == null && (target.Home != 0 || target.Owner == world.YourPlayer))
            {
                if (target.Owner == world.YourPlayer) DrawNearbyTargets(area, target);
                return null;
            }

            float choiceWidth = (width - 12) / MarchChoices.Length;
            if (battle == null)
            {
                GUI.Label(new Rect(x, y + 146, width, 24), "出征编成", body);
                for (int index = 0; index < Formations.Length; index++)
                {
                    var formation = Formations[index];
                    var option = new Rect(x + index * (choiceWidth + 6), y + 175, choiceWidth, 35);
                    if (DrawButton(option, TroopName(formation), formation == marchType, true)) marchType = formation;
                }
                GUI.Label(new Rect(x, y + 215, width, 24), $"{TroopName(marchType)}克制{TroopName(CounteredType(marchType))} · {TerrainEffect(target.Terrain)}", muted);
            }
            else
            {
                TroopType reinforcement = battle.Attacker == world.YourPlayer ? battle.AttackerType : battle.DefenderType;
                GUI.Label(new Rect(x, y + 146, width, 24), "增援编成", body);
                GUI.Label(new Rect(x, y + 177, width, 48), $"援军并入{TroopName(reinforcement)}部队，沿用该方军种。", note);
            }

            GUI.Label(new Rect(x, y + 244, width, 24), $"派遣兵力 · 可用 {world.AvailableTroops}", muted);
            for (int index = 0; index < MarchChoices.Length; index++)
            {
                uint count = MarchChoices[index];
                var choice = new Rect(x + index * (choiceWidth + 6), y + 270, choiceWidth, 34);
                if (DrawButton(choice, $"{count} 兵", count == marchTroops, true)) marchTroops = count;
            }

            string hint = DispatchBlockedReason(target, battle, submitting);
            string action = battle != null ? "派出援军" : target.Owner == 0 ? "出征占领" : "发起进攻";
            if (DrawButton(new Rect(x, y + 313, width, 38), action, true, hint.Length == 0))
                return (target.Id, marchTroops, battle?.BattleId ?? 0, false, battle == null ? marchType : TroopType.Unspecified);
            if (hint.Length == 0)
            {
                hint = battle != null ? (battle.Continuous ? "援军抵达后加入交战。" : "援军于下一回合参战。") :
                    inbound.ContainsKey(target.Id) ? "对方正在前往，先抵达者占领。" : $"向{TileName(target)}派兵。";
            }
            GUI.Label(new Rect(x, y + 359, width, 38), hint, note);
            return null;
        }

        private void DrawNearbyTargets(Rect area, WorldTile tile)
        {
            float y = area.y + 151;
            GUI.Label(new Rect(area.x, y, area.width, 24), "相邻目标", body);
            float buttonWidth = (area.width - 12) / 3;
            var position = new Vector2Int(tile.Q, tile.R);
            int index = 0;
            foreach (var neighbor in Neighbors)
            {
                if (!tilesByPosition.TryGetValue(position + neighbor, out var target) || !sources.ContainsKey(target.Id)) continue;
                bool inBattle = battles.TryGetValue(target.Id, out var battle);
                if (inBattle && !Participating(battle)) continue;
                var button = new Rect(area.x + index % 3 * (buttonWidth + 6), y + 34 + index / 3 * 43, buttonWidth, 37);
                if (DrawButton(button, TileName(target), false, true))
                    SelectTile(target.Id, inBattle ? Tab.Battle : Tab.Tile);
                index++;
            }
            if (index == 0)
                GUI.Label(new Rect(area.x, y + 34, area.width, 24), "暂无可出征的相邻地块。", muted);
        }

        private string DispatchBlockedReason(WorldTile target, WorldBattle battle, bool submitting)
        {
            if (submitting) return "等待指令确认。";
            if (battle != null)
            {
                if (!Participating(battle)) return "只有交战双方可以增援。";
                if (!battle.Continuous && battle.Round >= battle.Rounds) return "决胜回合已开始。";
            }
            else
            {
                if (target.Owner != 0 && inbound.ContainsKey(target.Id)) return "已有部队前往该地块。";
                if (attacking) return "当前进攻尚未结算。";
            }
            if (!sources.ContainsKey(target.Id)) return "请选择与己方领地相邻的地块。";
            if (ownMarches.Count > 0) return "请等待当前行军结束。";
            if (marchTroops > world.AvailableTroops) return "预备兵力不足，请等待补充。";
            return "";
        }

        private void DrawArmy(Rect area, double serverNow)
        {
            float x = area.x, y = area.y, width = area.width;
            GUI.Label(new Rect(x, y, width, 34), "军队总览", heading);
            float statWidth = (width - 12) / 3;
            DrawStat(new Rect(x, y + 43, statWidth, 69), "预备兵力", world.AvailableTroops.ToString());
            DrawStat(new Rect(x + statWidth + 6, y + 43, statWidth, 69), "各地驻军", garrisonTroops.ToString());
            DrawStat(new Rect(x + (statWidth + 6) * 2, y + 43, statWidth, 69), "己方领地", ownedTiles.ToString());
            string reserves = world.AvailableTroops < world.TroopCapacity ? "正在补充预备兵力" : "预备兵力已充足";
            GUI.Label(new Rect(x, y + 121, width, 24), $"{reserves} · 补兵上限 {world.TroopCapacity}", muted);

            // 每个可定位条目对应快照中的行军、参战部队或实际驻军。
            int participating = 0;
            foreach (var battle in activeBattles)
                if (Participating(battle)) participating++;
            int rows = ownMarches.Count + participating + garrisons.Count;
            var viewport = new Rect(x, y + 152, width, area.height - 152);
            if (rows == 0)
            {
                GUI.Label(viewport, "暂无行军、参战部队或驻军。选择相邻地块后可派出部队。", note);
                return;
            }
            const float rowHeight = 58;
            float innerWidth = width - (rows * rowHeight > viewport.height ? 18 : 0);
            armyScroll = GUI.BeginScrollView(viewport, armyScroll, new Rect(0, 0, innerWidth, rows * rowHeight));
            float row = 0;
            foreach (var march in ownMarches)
            {
                string label = march.ReinforceBattleId != 0 ? "增援" : "行军";
                DrawArmyRow(new Rect(0, row, innerWidth, rowHeight), $"{label} · {TroopName(march.TroopType)} {march.Troops} 兵",
                    $"{SiteName(march.TargetTileId)} · {Countdown(march.ArrivesAtMs, serverNow, "正在抵达")}",
                    march.TargetTileId, Tab.Army);
                row += rowHeight;
            }
            foreach (var battle in activeBattles)
            {
                if (!Participating(battle)) continue;
                bool attackingHere = battle.Attacker == world.YourPlayer;
                uint count = attackingHere ? battle.AttackerTroops : battle.DefenderTroops;
                uint waiting = attackingHere ? battle.AttackerWaiting : battle.DefenderWaiting;
                string role = attackingHere ? "进攻" : "防守";
                string detail = waiting > 0 ? $"{role} · 待加入 {waiting}" : role;
                TroopType type = attackingHere ? battle.AttackerType : battle.DefenderType;
                DrawArmyRow(new Rect(0, row, innerWidth, rowHeight), $"参战 · {TroopName(type)} {count} 兵",
                    $"{SiteName(battle.TargetTileId)} · {detail}", battle.TargetTileId, Tab.Battle);
                row += rowHeight;
            }
            foreach (var tile in garrisons)
            {
                DrawArmyRow(new Rect(0, row, innerWidth, rowHeight), $"驻军 · {TroopName(tile.GarrisonType)} {tile.Garrison} 兵",
                    TileName(tile), tile.Id, Tab.Army);
                row += rowHeight;
            }
            GUI.EndScrollView();
        }

        private void DrawStat(Rect area, string label, string value)
        {
            Fill(area, Inset);
            GUI.Label(new Rect(area.x + 9, area.y + 7, area.width - 18, 21), label, muted);
            GUI.Label(new Rect(area.x + 9, area.y + 29, area.width - 18, 31), value, number);
        }

        private void DrawArmyRow(Rect area, string label, string detail, uint tile, Tab destination)
        {
            Fill(new Rect(area.x, area.y, area.width, area.height - 5), Inset);
            GUI.Label(new Rect(area.x + 10, area.y + 4, area.width - 88, 24), label, body);
            GUI.Label(new Rect(area.x + 10, area.y + 27, area.width - 88, 23), detail, muted);
            if (DrawButton(new Rect(area.xMax - 69, area.y + 9, 60, 34), "定位", false, tiles.ContainsKey(tile)))
                Locate(tile, destination);
        }

        private (uint Target, uint Troops, ulong BattleId, bool Retreat, TroopType Type)? DrawBattlefront(
            Rect area, double serverNow, bool submitting)
        {
            float x = area.x, y = area.y, width = area.width;
            GUI.Label(new Rect(x, y, width, 34), "战场态势", heading);
            if (activeBattles.Count == 0)
            {
                if (tiles.TryGetValue(selectedTile, out var site))
                {
                    DrawTerrainBanner(new Rect(x, y + 42, width, 68), site.Terrain);
                    GUI.Label(new Rect(x + 10, y + 48, width - 20, 26), $"勘察 · {TileName(site)}", body);
                    GUI.Label(new Rect(x + 10, y + 78, width - 20, 22), TerrainEffect(site.Terrain), muted);
                }
                GUI.Label(new Rect(x, y + 127, width, 26), "战区暂无交战", muted);
                if (DrawButton(new Rect(x, y + 165, width, 37), "返回地图选择据点", false, true))
                {
                    tab = Tab.Tile;
                    showBattleMap = false;
                }
                return null;
            }

            int index = SelectedBattleIndex();
            var battle = activeBattles[index];
            selectedBattleTile = battle.TargetTileId;
            Fill(new Rect(x, y + 42, width, 39), Inset);
            GUI.Label(new Rect(x + 10, y + 49, width - 146, 26), $"{SiteName(battle.TargetTileId)} · {index + 1}/{activeBattles.Count}", body);
            if (DrawButton(new Rect(area.xMax - 133, y + 46, 58, 31), "上一处", false, index > 0))
                Locate(activeBattles[index - 1].TargetTileId, Tab.Battle);
            if (DrawButton(new Rect(area.xMax - 69, y + 46, 58, 31), "下一处", false, index < activeBattles.Count - 1))
                Locate(activeBattles[index + 1].TargetTileId, Tab.Battle);

            DrawTerrainBanner(new Rect(x, y + 89, width, 62), battle.Terrain);
            GUI.Label(new Rect(x + 10, y + 94, width - 20, 24), TerrainName(battle.Terrain), body);
            GUI.Label(new Rect(x + 10, y + 121, width - 20, 23), BattleEffect(battle), muted);
            string phase = battle.Continuous ? $"第 {battle.Round} 次交锋" : $"第 {battle.Round} / {battle.Rounds} 回合";
            GUI.Label(new Rect(x, y + 160, width, 24),
                $"{phase} · {Countdown(battle.RoundEndsAtMs, serverNow, "等待结算")}", muted);

            uint largest = Math.Max(1u, Math.Max(battle.AttackerTroops, battle.DefenderTroops));
            float flankWidth = (width - 28) * 0.5f;
            DrawBattleSide(new Rect(x, y + 187, flankWidth, 86), "攻方", battle.Attacker, battle.AttackerType,
                battle.AttackerTroops, battle.AttackerWaiting, largest);
            DrawClash(new Vector2(x + width * 0.5f, y + 229), 0.8f, Gold);
            DrawBattleSide(new Rect(area.xMax - flankWidth, y + 187, flankWidth, 86), "守方", battle.Defender,
                battle.DefenderType, battle.DefenderTroops, battle.DefenderWaiting, largest);
            string losses = battle.LastAttackerLosses != 0 || battle.LastDefenderLosses != 0
                ? $"上次交锋战损 · 攻方 {battle.LastAttackerLosses} / 守方 {battle.LastDefenderLosses}"
                : "尚无交锋战损";
            GUI.Label(new Rect(x, y + 280, width, 24), losses, muted);

            float buttonWidth = (width - 8) / 2;
            if (DrawButton(new Rect(x, y + 310, buttonWidth, 37), showBattleMap ? "进入战场" : "地图定位", false,
                tiles.ContainsKey(battle.TargetTileId)))
                ToggleBattleMap(battle.TargetTileId);
            bool participant = Participating(battle);
            if (DrawButton(new Rect(x + buttonWidth + 8, y + 310, buttonWidth, 37), "增援", true,
                participant && tiles.ContainsKey(battle.TargetTileId)))
                Locate(battle.TargetTileId, Tab.Tile);
            if (!battle.Continuous || !participant) return null;

            bool defending = battle.Defender == world.YourPlayer;
            GUI.Label(new Rect(x, y + 355, width - 120, 41), defending ? "守方撤出后，攻方接管据点。" : "撤出后，幸存部队返回预备兵力。", note);
            if (DrawButton(new Rect(area.xMax - 109, y + 355, 109, 37), "撤出战斗", false, !submitting))
                return (battle.TargetTileId, 0, battle.BattleId, true, TroopType.Unspecified);
            return null;
        }

        private void DrawBattleOutcome(Rect area, IReadOnlyList<WorldBattleReport> reports, ref int reportIndex)
        {
            var report = sceneResult;
            float x = area.x, y = area.y, width = area.width;
            GUI.Label(new Rect(x, y, width, 34), "战斗结算", heading);
            GUI.Label(new Rect(x, y + 42, width, 28), ReportOutcome(report), body);
            if (report.RetreatedPlayer != 0)
                GUI.Label(new Rect(x, y + 72, width, 24), $"玩家 {report.RetreatedPlayer} 撤出战斗", muted);

            uint largest = Math.Max(1u, Math.Max(report.AttackerSurvivors, report.DefenderSurvivors));
            float flankWidth = (width - 12) * 0.5f;
            DrawBattleSide(new Rect(x, y + 111, flankWidth, 86), "攻方存活", report.Attacker, report.AttackerType,
                report.AttackerSurvivors, 0, largest);
            DrawBattleSide(new Rect(area.xMax - flankWidth, y + 111, flankWidth, 86), "守方存活", report.Defender,
                report.DefenderType, report.DefenderSurvivors, 0, largest);
            if (!DrawButton(new Rect(x, y + 212, width, 38), "查看战报", true, true)) return;
            for (int index = 0; index < reports.Count; index++)
            {
                if (reports[index].BattleId != report.BattleId) continue;
                reportIndex = index;
                break;
            }
            sceneResult = null;
            tab = Tab.Reports;
        }

        private void DrawBattleSide(Rect area, string role, uint player, TroopType type, uint troops, uint waiting, uint largest)
        {
            Color color = TeamColor(player);
            string team = player == world.YourPlayer ? "己方" : "敌方";
            Fill(area, Inset);
            GUI.Label(new Rect(area.x + 8, area.y + 5, area.width - 16, 23), $"{role} · {team}", body);
            DrawFormation(new Vector2(area.x + 18, area.y + 39), type, 0.9f, color);
            GUI.Label(new Rect(area.x + 32, area.y + 29, area.width - 36, 23), $"{TroopName(type)} {troops} 兵", muted);
            if (waiting > 0) GUI.Label(new Rect(area.x + 8, area.y + 51, area.width - 16, 20), $"待加入 {waiting}", muted);
            var bar = new Rect(area.x + 8, area.y + 71, area.width - 16, 6);
            Fill(bar, Line);
            if (troops > 0) Fill(new Rect(bar.x, bar.y, bar.width * troops / largest, bar.height), color);
        }

        private void DrawHistory(Rect area, IReadOnlyList<WorldBattleReport> reports, ref int index)
        {
            float x = area.x, y = area.y, width = area.width;
            GUI.Label(new Rect(x, y, width, 34), "最近战报", heading);
            if (reports.Count == 0)
            {
                GUI.Label(new Rect(x, y + 45, width, 54), "攻打敌方领地后，双方都会收到战报。", note);
                return;
            }
            index = Mathf.Clamp(index, 0, reports.Count - 1);
            GUI.Label(new Rect(area.xMax - 56, y + 7, 54, 23), $"{index + 1} / {reports.Count}", muted);
            var report = reports[index];
            GUI.Label(new Rect(x, y + 43, width, 27), ReportOutcome(report), body);
            Fill(new Rect(x, y + 77, width, 77), Inset);
            GUI.Label(new Rect(x + 12, y + 87, width - 24, 25),
                $"攻方 {TroopName(report.AttackerType)} · {report.AttackerTroops} → {report.AttackerSurvivors} 兵", body);
            GUI.Label(new Rect(x + 12, y + 117, width - 24, 25),
                $"守方 {TroopName(report.DefenderType)} · {report.DefenderTroops} → {report.DefenderSurvivors} 兵", body);
            string survivors = report.RetreatedPlayer != 0 ? $"玩家 {report.RetreatedPlayer} 撤出，幸存部队返回。" :
                report.Captured ? "攻方幸存部队留守地块。" : report.AttackerSurvivors > 0 ?
                "守军继续驻扎，攻方幸存部队返回。" : "守军继续驻扎。";
            if (report.Rounds != 0) survivors += $"\n{report.Rounds} 回合";
            else if (report.Exchanges > 0) survivors += $"\n{TerrainName(report.Terrain)} · 交锋 {report.Exchanges} 次";
            if (report.AttackerReinforcements != 0 || report.DefenderReinforcements != 0)
                survivors += $"\n增援 攻 {report.AttackerReinforcements} / 守 {report.DefenderReinforcements}";
            GUI.Label(new Rect(x, y + 169, width, 72), survivors, note);
            if (DrawButton(new Rect(x, y + 248, 68, 38), "上一份", false, index > 0)) index--;
            if (DrawButton(new Rect(x + 74, y + 248, 68, 38), "下一份", false, index < reports.Count - 1)) index++;
            if (DrawButton(new Rect(x + 150, y + 248, width - 150, 38), "定位地块", true, tiles.ContainsKey(report.TargetTileId)))
                Locate(report.TargetTileId, Tab.Reports);
        }

        private string ReportOutcome(WorldBattleReport report) => report.Captured
            ? $"玩家 {report.Attacker} 攻占{SiteName(report.TargetTileId)}"
            : $"玩家 {report.Defender} 守住{SiteName(report.TargetTileId)}";

        private void Locate(uint tile, Tab destination)
        {
            if (!tiles.TryGetValue(tile, out var target)) return;
            SelectTile(tile, destination);
            mapFocus = GridPoint(target);
            locateUntil = Time.realtimeSinceStartupAsDouble + NoticeSeconds;
        }

        private void SelectTile(uint tile, Tab destination)
        {
            selectedTile = tile;
            if (battles.ContainsKey(tile)) selectedBattleTile = tile;
            tab = destination;
            showBattleMap = false;
            sceneResult = null;
            sceneBattleId = 0;
            locateUntil = 0;
        }

        private bool Participating(WorldBattle battle) => battle.Attacker == world.YourPlayer || battle.Defender == world.YourPlayer;

        private string SiteName(uint id) => tiles.TryGetValue(id, out var tile) ? TileName(tile) : $"据点 {id}";

        private static string TileName(WorldTile tile)
        {
            if (tile.Home != 0) return tile.Home == 1 ? "西境城" : "东境城";
            if (tile.Id == 10) return "白石渡";
            return tile.Terrain switch
            {
                BattlefieldTerrain.Highland => $"山口 {tile.Id}",
                BattlefieldTerrain.Woods => $"林营 {tile.Id}",
                BattlefieldTerrain.Ford => $"渡口 {tile.Id}",
                _ => $"营地 {tile.Id}"
            };
        }

        private static string TroopName(TroopType type) => type switch
        {
            TroopType.Spear => "枪兵",
            TroopType.Bow => "弓兵",
            TroopType.Cavalry => "骑兵",
            _ => "民兵"
        };

        private static TroopType CounteredType(TroopType type) => type switch
        {
            TroopType.Spear => TroopType.Cavalry,
            TroopType.Cavalry => TroopType.Bow,
            TroopType.Bow => TroopType.Spear,
            _ => TroopType.Unspecified
        };

        private static string TerrainName(BattlefieldTerrain terrain) => terrain switch
        {
            BattlefieldTerrain.Highland => "高地",
            BattlefieldTerrain.Woods => "林地",
            BattlefieldTerrain.Ford => "渡口",
            _ => "平原"
        };

        private static string TerrainEffect(BattlefieldTerrain terrain) => terrain switch
        {
            BattlefieldTerrain.Highland => "守方弓兵占优",
            BattlefieldTerrain.Woods => "攻方骑兵受限",
            BattlefieldTerrain.Ford => "守方占据地利",
            _ => "平原无地形修正"
        };

        private static string BattleEffect(WorldBattle battle)
        {
            if (battle.RuleVersion != 5) return "按兵力结算";
            if (battle.AttackerType != TroopType.Unspecified && CounteredType(battle.AttackerType) == battle.DefenderType)
                return $"攻方军种克制 · {TerrainEffect(battle.Terrain)}";
            if (battle.DefenderType != TroopType.Unspecified && CounteredType(battle.DefenderType) == battle.AttackerType)
                return $"守方军种克制 · {TerrainEffect(battle.Terrain)}";
            return TerrainEffect(battle.Terrain);
        }

        private void DrawTerrainBanner(Rect area, BattlefieldTerrain terrain)
        {
            Fill(area, Inset);
            if (terrainAtlas == null || Event.current.type != EventType.Repaint) return;
            Vector2 offset = terrain switch
            {
                BattlefieldTerrain.Woods => new Vector2(0.5f, 0.5f),
                BattlefieldTerrain.Highland => Vector2.zero,
                BattlefieldTerrain.Ford => new Vector2(0.5f, 0),
                _ => new Vector2(0, 0.5f)
            };
            var original = GUI.color;
            GUI.color = new Color(0.42f, 0.43f, 0.39f);
            GUI.DrawTextureWithTexCoords(area, terrainAtlas, new Rect(offset.x, offset.y + 0.15f, 0.5f, 0.2f));
            GUI.color = original;
            Fill(area, WithAlpha(Inset, 0.56f));
        }

        private Color TeamColor(uint player) => player == 0 ? Muted : player == world.YourPlayer ? Friendly : Enemy;

        private static string Countdown(long endsAt, double serverNow, string finished)
        {
            double seconds = (endsAt - serverNow) / 1000;
            return seconds > 0 ? $"{seconds:0.0} 秒" : finished;
        }

        private static void DrawFrame(Rect area)
        {
            Fill(area, Line);
            Fill(new Rect(area.x + 1, area.y + 1, area.width - 2, area.height - 2), Panel);
        }

        private bool DrawButton(Rect area, string text, bool accent, bool enabled)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            Color background = accent && enabled ? Gold : enabled ? Line : Inset;
            if (GUI.enabled && area.Contains(Event.current.mousePosition)) background = Color.Lerp(background, Paper, 0.16f);
            Fill(area, background);
            bool clicked = GUI.Button(area, text, GUI.enabled ? (accent ? primary : centered) : disabledButton);
            GUI.enabled = previous;
            return clicked;
        }

        private static GUIStyle Text(Font font, int size, Color color, FontStyle weight = FontStyle.Normal,
            TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var style = new GUIStyle { font = font, fontSize = size, fontStyle = weight, alignment = alignment, clipping = TextClipping.Clip };
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            return style;
        }

        private static void Fill(Rect rect, Color color) => Paint(rect, Texture2D.whiteTexture, color);

        private static void Paint(Rect rect, Texture texture, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color original = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, texture);
            GUI.color = original;
        }
    }
}
