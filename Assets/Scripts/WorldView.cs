using System.Collections.Generic;
using Slg.Protocol;
using UnityEngine;

namespace Slg
{
    internal sealed class WorldView
    {
        private const float HexWidth = 1.7320508f;
        private const double MarchSeconds = 3;
        private const double NoticeSeconds = 2.6;
        private static readonly uint[] MarchChoices = { 20, 40, 60 };
        private static readonly Vector2 RouteOffset = new(0, -26);
        private static readonly Color Panel = new(0.071f, 0.124f, 0.135f);
        private static readonly Color Inset = new(0.052f, 0.091f, 0.099f);
        private static readonly Color Line = new(0.21f, 0.30f, 0.30f);
        private static readonly Color Paper = new(0.95f, 0.92f, 0.83f);
        private static readonly Color Muted = new(0.68f, 0.76f, 0.73f);
        private static readonly Color Gold = new(0.83f, 0.68f, 0.40f);
        private static readonly Color Friendly = new(0.39f, 0.74f, 0.69f);
        private static readonly Color Enemy = new(0.89f, 0.54f, 0.42f);

        private readonly Dictionary<uint, WorldTile> tiles = new();
        private readonly Dictionary<uint, WorldBattle> battles = new();
        private readonly Dictionary<uint, int> inbound = new();
        private readonly Dictionary<uint, WorldTile> sources = new();
        private readonly Dictionary<uint, (double Start, string Label, bool Battle)> notices = new();
        private readonly List<(Vector2 Center, string Text, Color Team)> contestedBadges = new(2);
        private readonly Texture2D hexMask;
        private readonly GUIStyle heading;
        private readonly GUIStyle body;
        private readonly GUIStyle muted;
        private readonly GUIStyle centered;
        private readonly GUIStyle badge;
        private readonly GUIStyle primary;
        private WorldSnapshot world;
        private bool hasMarch;
        private bool attacking;
        private uint selectedTile;
        private double locateUntil;
        private uint marchTroops = 40;

        internal WorldView(Font font)
        {
            hexMask = CreateHexMask();
            heading = Text(font, 23, Paper, FontStyle.Bold);
            body = Text(font, 17, Paper);
            muted = Text(font, 15, Muted);
            muted.wordWrap = true;
            centered = Text(font, 16, Paper, FontStyle.Bold, TextAnchor.MiddleCenter);
            badge = Text(font, 13, Paper, FontStyle.Bold, TextAnchor.MiddleCenter);
            primary = Text(font, 17, Inset, FontStyle.Bold, TextAnchor.MiddleCenter);
        }

        internal void Dispose() => Object.Destroy(hexMask);

        internal void ApplySnapshot(WorldSnapshot snapshot)
        {
            if (ReferenceEquals(world, snapshot)) return;

            if (snapshot == null || world?.Instance != snapshot.Instance)
            {
                // 重新加入时只显示当前地图，旧地块、定位和占领提示均随实例失效。
                selectedTile = 0;
                locateUntil = 0;
                notices.Clear();
            }
            else
            {
                foreach (var tile in snapshot.Tiles)
                {
                    if (tile.Home == 0 && tile.Owner != 0 && tiles.TryGetValue(tile.Id, out var previous) && previous.Owner == 0)
                        notices[tile.Id] = (Time.realtimeSinceStartupAsDouble, "已占领", false);
                }
            }

            world = snapshot;
            tiles.Clear();
            battles.Clear();
            inbound.Clear();
            sources.Clear();
            hasMarch = false;
            attacking = false;
            if (world == null) return;
            foreach (var battle in world.Battles)
            {
                battles[battle.TargetTileId] = battle;
                if (battle.Attacker == world.YourPlayer) attacking = true;
            }
            foreach (var march in world.Marches)
            {
                inbound[march.TargetTileId] = inbound.GetValueOrDefault(march.TargetTileId) + 1;
                if (march.Player == world.YourPlayer) hasMarch = true;
            }
            foreach (var tile in world.Tiles)
            {
                tiles[tile.Id] = tile;
                if (tile.Home != 0) continue;
                if (!battles.ContainsKey(tile.Id) && (tile.Owner == world.YourPlayer ||
                    (tile.Owner != 0 && inbound.ContainsKey(tile.Id)))) continue;

                // 按游戏服的轴坐标判定相邻关系，并预览同一遍历顺序选出的出发地块。
                foreach (var source in world.Tiles)
                {
                    if (source.Owner != world.YourPlayer) continue;
                    int dq = source.Q - tile.Q, dr = source.R - tile.R;
                    if (Mathf.Max(Mathf.Abs(dq), Mathf.Max(Mathf.Abs(dr), Mathf.Abs(dq + dr))) != 1) continue;
                    sources.Add(tile.Id, source);
                    break;
                }
            }
            if (!tiles.ContainsKey(selectedTile)) selectedTile = 0;
        }

        internal void ShowBattle(WorldBattleReport report)
        {
            notices[report.TargetTileId] = (Time.realtimeSinceStartupAsDouble, report.Captured ? "攻占" : "守住", true);
        }

        internal (uint Target, uint Troops, ulong BattleId, bool Retreat)? Draw(Rect bounds, bool compact, bool submitting, double serverNow,
            IReadOnlyList<WorldBattleReport> reports, ref int reportIndex)
        {
            const float gap = 16;
            const float panelHeight = 278;
            float panelWidth = compact ? (bounds.width - gap) / 2 : 332;
            var map = compact ? new Rect(bounds.x, bounds.y, bounds.width, bounds.height - panelHeight - gap) :
                new Rect(bounds.x, bounds.y, bounds.width - panelWidth - gap, bounds.height);
            var orders = new Rect(compact ? bounds.x : map.xMax + gap, compact ? map.yMax + gap : bounds.y,
                panelWidth, panelHeight);
            var history = compact ? new Rect(orders.xMax + gap, orders.y, panelWidth, panelHeight) :
                new Rect(orders.x, orders.yMax + gap, orders.width, bounds.yMax - orders.yMax - gap);
            DrawMap(map, serverNow);
            if (battles.TryGetValue(selectedTile, out var battle))
            {
                if (DrawBattle(history, battle, serverNow, submitting)) return (selectedTile, 0, battle.BattleId, true);
            }
            else DrawHistory(history, reports, ref reportIndex);
            return DrawOrders(orders, submitting);
        }

        private void DrawMap(Rect area, double serverNow)
        {
            DrawPanel(area, "领地地图");
            var field = new Rect(area.x + 16, area.y + 56, area.width - 32, area.height - 104);
            Fill(field, Inset);
            if (tiles.Count == 0) return;

            Vector2 min = Vector2.one * float.PositiveInfinity;
            Vector2 max = Vector2.one * float.NegativeInfinity;
            foreach (var tile in world.Tiles)
            {
                var point = GridPoint(tile);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            Vector2 span = max - min + new Vector2(HexWidth, 2);
            float radius = Mathf.Min(98, Mathf.Min((field.width - 28) / span.x, (field.height - 28) / span.y));
            Vector2 origin = field.center - (min + max) * (radius * 0.5f);
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var tile in world.Tiles)
            {
                Vector2 center = origin + GridPoint(tile) * radius;
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                    field.Contains(Event.current.mousePosition) && InHex(Event.current.mousePosition - center, radius - 2))
                {
                    selectedTile = tile.Id;
                    locateUntil = 0;
                    Event.current.Use();
                }
                DrawTile(tile, center, radius - 2, now);
            }

            if (sources.TryGetValue(selectedTile, out var source))
            {
                var start = origin + GridPoint(source) * radius + RouteOffset;
                var end = origin + GridPoint(tiles[selectedTile]) * radius + RouteOffset;
                FillRoute(start, end, new Color(Gold.r, Gold.g, Gold.b, 0.72f), true);
            }
            contestedBadges.Clear();
            foreach (var march in world.Marches)
            {
                if (!tiles.TryGetValue(march.SourceTileId, out var from) || !tiles.TryGetValue(march.TargetTileId, out var to)) continue;
                double seconds = System.Math.Max(0, (march.ArrivesAtMs - serverNow) / 1000);
                Vector2 start = origin + GridPoint(from) * radius;
                Vector2 end = origin + GridPoint(to) * radius;
                // 当前行军固定三秒。倒计时归零后仍保留等待结算提示，直到服务器更新地图。
                Vector2 flag = Vector2.Lerp(start, end, 0.21f + 0.58f * Mathf.Clamp01(1 - (float)(seconds / MarchSeconds)));
                Color troopColor = march.Player == world.YourPlayer ? Friendly : Enemy;
                FillRoute(start + RouteOffset, end + RouteOffset, troopColor, false);
                DrawFlag(flag + RouteOffset - new Vector2(0, 16), troopColor);
                string countdown = seconds > 0 ? $"{seconds:0.0} 秒" : march.Hostile ? "等待结算" : "正在抵达";
                string troops = march.ReinforceBattleId != 0 ? $"援军 {march.Troops}" : $"{march.Troops} 兵";
                if (inbound[march.TargetTileId] > 1)
                {
                    // 两个计时分列在目标格上下，给旁边地块的占领提示留出空间。
                    float badgeOffset = march.Player == 1 ? -31 : 33;
                    contestedBadges.Add((end + new Vector2(0, badgeOffset), $"{march.Troops} 兵 · {countdown}", troopColor));
                }
                else
                {
                    DrawBadge(flag + RouteOffset, troops, troopColor, march.ReinforceBattleId != 0 ? 68 : 55);
                    DrawBadge(end + new Vector2(0, 33), countdown, troopColor, 76);
                }
            }
            foreach (var contest in contestedBadges) DrawBadge(contest.Center, contest.Text, contest.Team, 92);
            foreach (var tile in world.Tiles)
            {
                if (inbound.ContainsKey(tile.Id) || !notices.TryGetValue(tile.Id, out var notice)) continue;
                double age = now - notice.Start;
                if (age >= NoticeSeconds) continue;
                Vector2 center = origin + GridPoint(tile) * radius;
                DrawBadge(center + new Vector2(0, 33), notice.Battle && age < 0.42 ? "交战" : notice.Label, Gold, 76);
            }

            float legend = area.yMax - 32;
            DrawLegend(area.x + 19, legend, Friendly, "己方领地");
            DrawLegend(area.x + 130, legend, Enemy, "敌方领地");
            DrawLegend(area.x + 241, legend, Gold, "相邻目标");
        }

        private void DrawTile(WorldTile tile, Vector2 center, float radius, double now)
        {
            bool friendly = tile.Owner == world.YourPlayer;
            bool selected = selectedTile == tile.Id;
            bool contested = tile.Owner == 0 && inbound.GetValueOrDefault(tile.Id) > 1;
            Color team = tile.Owner == 0 ? Line : friendly ? Friendly : Enemy;
            Color face = tile.Owner == 0 ? new Color(0.13f, 0.21f, 0.21f) : Color.Lerp(Inset, team, 0.31f);
            Color edge = selected || contested || sources.ContainsKey(tile.Id) ? Gold : Color.Lerp(Inset, team, 0.71f);
            if (selected && now < locateUntil) edge = Color.Lerp(Gold, Paper, 0.5f + 0.5f * Mathf.Sin((float)now * 9));
            if (notices.TryGetValue(tile.Id, out var notice) && now - notice.Start < NoticeSeconds)
            {
                float glow = 1 - (float)((now - notice.Start) / NoticeSeconds);
                edge = Color.Lerp(edge, Gold, glow);
                face = Color.Lerp(face, tile.Owner == 0 ? Gold : team, glow * 0.38f);
            }
            DrawHex(center + new Vector2(0, 4), radius, Inset);
            DrawHex(center, radius, edge);
            DrawHex(center, radius - (selected ? 3.5f : 1.5f), face);
            if (tile.Owner != 0) DrawFlag(center + new Vector2(0, -29), team);
            string title = tile.Home != 0 ? (friendly ? "己方出生地" : "敌方出生地") : $"地块 {tile.Id}";
            GUI.Label(new Rect(center.x - 52, center.y - 15, 104, 24), title, centered);
            string detail = battles.TryGetValue(tile.Id, out var battle) ? (battle.Continuous ? "交战中" : $"第 {battle.Round} 回合") :
                tile.Home != 0 ? "受保护" : contested ? "双方争夺" : tile.Owner == 0 ? "中立" : $"驻军 {tile.Garrison}";
            GUI.Label(new Rect(center.x - 50, center.y + 7, 100, 20), detail, badge);
        }

        private (uint Target, uint Troops, ulong BattleId, bool Retreat)? DrawOrders(Rect area, bool submitting)
        {
            float x = area.x + 18, width = area.width - 36;
            tiles.TryGetValue(selectedTile, out var target);
            battles.TryGetValue(selectedTile, out var battle);
            DrawPanel(area, battle == null ? "出征" : "增援");
            Fill(new Rect(x, area.y + 58, width, 67), Inset);
            GUI.Label(new Rect(x + 12, area.y + 66, width - 24, 22), target == null ? "选择目标地块" : $"地块 {target.Id}", body);
            if (target != null)
            {
                string owner = target.Owner == 0 ? "中立地块" : target.Owner == world.YourPlayer ? "己方领地" : "敌方领地";
                string detail = battle != null ? (battle.Continuous ? "持续交战" : $"第 {battle.Round} / {battle.Rounds} 回合") :
                    target.Home != 0 ? "出生地受保护" : $"{owner} · 驻军 {target.Garrison}";
                GUI.Label(new Rect(x + 12, area.y + 93, width - 24, 22), detail, muted);
            }

            GUI.Label(new Rect(x, area.y + 135, width, 21), battle == null ? "出征兵力" : "援军兵力", muted);
            float choiceWidth = (width - 12) / MarchChoices.Length;
            for (int index = 0; index < MarchChoices.Length; index++)
            {
                uint troops = MarchChoices[index];
                var choice = new Rect(x + index * (choiceWidth + 6), area.y + 158, choiceWidth, 34);
                if (DrawButton(choice, $"{troops}", troops == marchTroops, true)) marchTroops = troops;
            }

            string hint = DispatchBlockedReason(target, battle, submitting);
            string action = battle != null ? "派出援军" :
                target != null && target.Owner != 0 && target.Owner != world.YourPlayer ? "确认进攻" : "确认出征";
            if (DrawButton(new Rect(x, area.y + 204, width, 39), action, true, hint.Length == 0))
                return (target.Id, marchTroops, battle?.BattleId ?? 0, false);
            if (hint.Length == 0)
            {
                hint = battle != null ? (battle.Continuous ? "援军抵达后加入交战。" : "到达后从下一回合参战。") :
                    inbound.ContainsKey(target.Id) ? "对方正在前往，先抵达者占领。" : $"从地块 {sources[target.Id].Id} 出发";
            }
            GUI.Label(new Rect(x, area.y + 249, width, 22), hint, muted);
            return null;
        }

        private string DispatchBlockedReason(WorldTile target, WorldBattle battle, bool submitting)
        {
            if (submitting) return "等待行军指令确认。";
            if (target == null) return "点击地图上的地块查看驻军。";
            if (target.Home != 0) return "出生地受保护。";
            if (battle != null)
            {
                if (!battle.Continuous && battle.Round >= battle.Rounds) return "决胜回合已开始。";
            }
            else
            {
                if (target.Owner == world.YourPlayer) return "该地块已归己方。";
                if (target.Owner != 0 && inbound.ContainsKey(target.Id)) return "已有部队前往该地块。";
                if (attacking) return "当前进攻尚未结算。";
            }
            if (!sources.ContainsKey(target.Id)) return "请选择与己方领地相邻的地块。";
            if (hasMarch) return "请等待当前行军结束。";
            if (marchTroops > world.AvailableTroops) return "预备兵力不足，请等待补充。";
            return "";
        }

        private bool DrawBattle(Rect area, WorldBattle battle, double serverNow, bool submitting)
        {
            DrawPanel(area, $"地块 {battle.TargetTileId} 交战");
            float x = area.x + 18, width = area.width - 36;
            double remaining = System.Math.Max(0, (battle.RoundEndsAtMs - serverNow) / 1000);
            string countdown = remaining > 0 ? $"{remaining:0.0} 秒" : "等待结算";
            string phase = battle.Continuous ? "下次交锋" : $"第 {battle.Round} / {battle.Rounds} 回合";
            GUI.Label(new Rect(x, area.y + 60, width, 26), $"{phase} · {countdown}", body);
            Fill(new Rect(x, area.y + 94, width, 69), Inset);
            GUI.Label(new Rect(x + 11, area.y + 103, width - 22, 23),
                battle.Continuous ? $"攻方 {battle.AttackerTroops} 兵" : $"攻方 {battle.AttackerTroops} 兵 · 待加入 {battle.AttackerWaiting}", body);
            GUI.Label(new Rect(x + 11, area.y + 131, width - 22, 23),
                battle.Continuous ? $"守方 {battle.DefenderTroops} 兵" : $"守方 {battle.DefenderTroops} 兵 · 待加入 {battle.DefenderWaiting}", body);
            if (battle.Continuous)
            {
                bool defending = battle.Defender == world.YourPlayer;
                GUI.Label(new Rect(x, area.y + 174, width, 44), defending ?
                    "守方撤出后，攻方接管地块。" : "撤出后，存活部队返回预备兵力。", muted);
                return DrawButton(new Rect(x, area.y + 223, width, 34), "撤出战斗", false,
                    !submitting && (defending || battle.Attacker == world.YourPlayer));
            }
            GUI.Label(new Rect(x, area.y + 174, width, 44), battle.Round < battle.Rounds ?
                "本回合兵力已锁定，援军于下一回合加入。" : "决胜回合结束后更新地块归属。", muted);
            return false;
        }

        private void DrawHistory(Rect area, IReadOnlyList<WorldBattleReport> reports, ref int index)
        {
            DrawPanel(area, "战报");
            float x = area.x + 18, width = area.width - 36;
            if (reports.Count == 0)
            {
                GUI.Label(new Rect(x, area.y + 65, width, 48), "攻打敌方领地后，双方都会收到战报。", muted);
                return;
            }
            GUI.Label(new Rect(area.xMax - 75, area.y + 22, 58, 22), $"{index + 1} / {reports.Count}", muted);
            var report = reports[index];
            string outcome = report.Captured ? $"玩家 {report.Attacker} 攻占地块 {report.TargetTileId}" :
                $"玩家 {report.Defender} 守住地块 {report.TargetTileId}";
            GUI.Label(new Rect(x, area.y + 60, width, 24), outcome, body);
            Fill(new Rect(x, area.y + 94, width, 69), Inset);
            GUI.Label(new Rect(x + 11, area.y + 103, width - 22, 23), $"攻方 {report.AttackerTroops} → {report.AttackerSurvivors} 兵", body);
            GUI.Label(new Rect(x + 11, area.y + 132, width - 22, 23), $"守方 {report.DefenderTroops} → {report.DefenderSurvivors} 兵", body);
            string survivors = report.RetreatedPlayer != 0 ? $"玩家 {report.RetreatedPlayer} 撤出，幸存部队返回。" :
                report.Captured ? "攻方幸存部队留守地块。" : report.AttackerSurvivors > 0 ?
                "守军继续驻扎，攻方幸存部队返回。" : "守军继续驻扎。";
            if (report.Rounds != 0)
                survivors += $"\n{report.Rounds} 回合 · 增援 攻 {report.AttackerReinforcements} / 守 {report.DefenderReinforcements}";
            else if (report.AttackerReinforcements != 0 || report.DefenderReinforcements != 0)
                survivors += $"\n增援 攻 {report.AttackerReinforcements} / 守 {report.DefenderReinforcements}";
            GUI.Label(new Rect(x, area.y + 174, width, 43), survivors, muted);

            float y = area.y + 223;
            if (DrawButton(new Rect(x, y, 61, 34), "上一份", false, index > 0)) index--;
            if (DrawButton(new Rect(x + 67, y, 61, 34), "下一份", false, index < reports.Count - 1)) index++;
            if (DrawButton(new Rect(x + 134, y, width - 134, 34), "定位地块", true, tiles.ContainsKey(report.TargetTileId)))
            {
                selectedTile = report.TargetTileId;
                locateUntil = Time.realtimeSinceStartupAsDouble + NoticeSeconds;
            }
        }

        private static Vector2 GridPoint(WorldTile tile) => new(HexWidth * (tile.Q + tile.R * 0.5f), tile.R * 1.5f);

        private static bool InHex(Vector2 offset, float radius)
        {
            float x = Mathf.Abs(offset.x) / (radius * HexWidth * 0.5f);
            return x <= 1 && Mathf.Abs(offset.y) / radius <= 1 - x * 0.5f;
        }

        private static Texture2D CreateHexMask()
        {
            const int size = 96;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float horizontal = Mathf.Abs((x + 0.5f) * 2 / size - 1);
                float vertical = Mathf.Abs((y + 0.5f) * 2 / size - 1);
                float coverage = Mathf.Clamp01(Mathf.Min(1 - horizontal, 1 - vertical - horizontal * 0.5f) * (size * 0.5f));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * coverage));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void DrawHex(Vector2 center, float radius, Color color)
        {
            float width = radius * HexWidth;
            Paint(new Rect(center.x - width / 2, center.y - radius, width, radius * 2), hexMask, color);
        }

        private void DrawPanel(Rect area, string title)
        {
            Fill(area, Line);
            Fill(new Rect(area.x + 1, area.y + 1, area.width - 2, area.height - 2), Panel);
            GUI.Label(new Rect(area.x + 18, area.y + 15, area.width - 36, 33), title, heading);
            Fill(new Rect(area.x + 18, area.y + 49, area.width - 36, 1), Line);
        }

        private static void DrawFlag(Vector2 center, Color color)
        {
            Fill(new Rect(center.x - 6, center.y - 7, 2, 15), color);
            Fill(new Rect(center.x - 4, center.y - 7, 13, 8), color);
        }

        private void DrawBadge(Vector2 center, string text, Color color, float width)
        {
            var area = new Rect(center.x - width / 2, center.y - 10, width, 21);
            Fill(area, color);
            Fill(new Rect(area.x + 1, area.y + 1, area.width - 2, area.height - 2), Inset);
            GUI.Label(area, text, badge);
        }

        private static void FillRoute(Vector2 start, Vector2 end, Color color, bool preview)
        {
            int dots = preview ? 10 : 16;
            float size = preview ? 3 : 4;
            for (int point = 0; point < dots; point++)
            {
                Vector2 position = Vector2.Lerp(start, end, 0.24f + point * 0.52f / (dots - 1));
                Fill(new Rect(position.x - size / 2, position.y - size / 2, size, size), color);
            }
        }

        private void DrawLegend(float x, float y, Color color, string text)
        {
            Fill(new Rect(x, y + 5, 13, 13), color);
            GUI.Label(new Rect(x + 20, y, 86, 24), text, muted);
        }

        private bool DrawButton(Rect area, string text, bool accent, bool enabled)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            Color background = accent && enabled ? Gold : enabled ? Line : Inset;
            if (GUI.enabled && area.Contains(Event.current.mousePosition)) background = Color.Lerp(background, Paper, 0.16f);
            Fill(area, background);
            bool clicked = GUI.Button(area, text, accent && enabled ? primary : centered);
            GUI.enabled = previous;
            return clicked;
        }

        private static GUIStyle Text(Font font, int size, Color color, FontStyle weight = FontStyle.Normal,
            TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var style = new GUIStyle { font = font, fontSize = size, fontStyle = weight, alignment = alignment };
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
