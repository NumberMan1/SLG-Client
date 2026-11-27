using Slg.Protocol;
using UnityEngine;

namespace Slg
{
    internal sealed partial class WorldView
    {
        private const float ExchangeAnimationSeconds = 0.74f;
        // FrontierBattleUnits.png 的透明裁切区域，顺序为民兵、枪兵、弓兵、骑兵。
        private static readonly Rect[] UnitPortraits =
        {
            new(0.60612f, 0.00977f, 0.25521f, 0.46875f),
            new(0.10938f, 0.51563f, 0.36263f, 0.45996f),
            new(0.64974f, 0.50684f, 0.23438f, 0.49023f),
            new(0.01497f, 0.00586f, 0.44076f, 0.49023f)
        };
        private readonly Texture2D battlefields;
        private readonly Texture2D battleUnits;
        private WorldBattleReport sceneResult;
        private bool showBattleMap;
        private ulong sceneBattleId;
        private uint sceneRound;
        private double exchangeAt;

        private int SelectedBattleIndex()
        {
            int index = activeBattles.FindIndex(battle => battle.TargetTileId == selectedBattleTile);
            return Mathf.Max(0, index);
        }

        private void ToggleBattleMap(uint target)
        {
            bool toMap = !showBattleMap;
            Locate(target, Tab.Battle);
            showBattleMap = toMap;
        }

        private void DrawBattleScene(Rect area, double serverNow)
        {
            WorldBattle battle = sceneResult == null && activeBattles.Count > 0 ? activeBattles[SelectedBattleIndex()] : null;
            uint siteId = sceneResult?.TargetTileId ?? battle?.TargetTileId ?? selectedTile;
            BattlefieldTerrain terrain = battle?.Terrain ?? tiles[siteId].Terrain;
            DrawFrame(area);
            string title = sceneResult != null ? "交战结束" : battle == null ? "战地勘察" : "战地近景";
            GUI.Label(new Rect(area.x + 16, area.y + 9, area.width - 126, 34), $"{title} · {SiteName(siteId)}", heading);
            var back = new Rect(area.xMax - 105, area.y + 10, 92, 32);
            if (DrawButton(back, "返回地图", false, true)) ToggleBattleMap(siteId);

            var viewport = new Rect(area.x + 9, area.y + PanelHeader, area.width - 18, area.height - PanelHeader - 46);
            GUI.BeginGroup(viewport);
            if (Event.current.type == EventType.Repaint)
            {
                var field = new Rect(0, 0, viewport.width, viewport.height);
                DrawGround(field, terrain);
                if (battle != null) DrawArmies(field, battle, serverNow);
                if (sceneResult != null) DrawRemainingArmies(field, sceneResult);
            }
            GUI.EndGroup();

            string effect = battle != null ? BattleEffect(battle) :
                sceneResult == null || sceneResult.RuleVersion == 5 ? TerrainEffect(terrain) : "按兵力结算";
            GUI.Label(new Rect(area.x + 16, area.yMax - 34, area.width - 32, 24),
                $"{TerrainName(terrain)} · {effect}", muted);
        }

        private void DrawGround(Rect field, BattlefieldTerrain terrain)
        {
            Fill(field, Inset);
            int quadrant = terrain switch
            {
                BattlefieldTerrain.Highland => 1,
                BattlefieldTerrain.Woods => 2,
                BattlefieldTerrain.Ford => 3,
                _ => 0
            };
            if (battlefields != null)
            {
                var uv = AtlasCell(quadrant);
                float imageAspect = (float)battlefields.width / battlefields.height;
                float viewAspect = field.width / field.height;
                float width = uv.width * Mathf.Min(1, viewAspect / imageAspect);
                float height = uv.height * Mathf.Min(1, imageAspect / viewAspect);
                DrawAtlas(field, battlefields, new Rect(uv.center - new Vector2(width, height) * 0.5f, new Vector2(width, height)));
            }
            // 场景地标用轻量标注，地貌以服务端的据点类型为准。
            string landmark = terrain switch
            {
                BattlefieldTerrain.Ford => "浅滩渡道",
                BattlefieldTerrain.Highland => "山口栈道",
                BattlefieldTerrain.Woods => "林间土道",
                _ => "开阔原野"
            };
            string camp = terrain switch
            {
                BattlefieldTerrain.Ford => "河岸木栅",
                BattlefieldTerrain.Highland => "坡顶哨台",
                BattlefieldTerrain.Woods => "林边鹿砦",
                _ => "野地营帐"
            };
            float landmarkHeight = terrain == BattlefieldTerrain.Ford ? 0.35f : 0.19f;
            DrawLandmarkLabel(new Vector2(field.width * 0.50f, field.height * landmarkHeight), landmark);
            DrawLandmarkLabel(new Vector2(field.width * 0.81f, field.height * 0.24f), camp);
        }

        private void DrawLandmarkLabel(Vector2 point, string label)
        {
            var area = new Rect(point.x - 40, point.y - 11, 80, 22);
            Fill(area, WithAlpha(Inset, 0.8f));
            Fill(new Rect(area.x, area.yMax - 1, area.width, 1), WithAlpha(Gold, 0.76f));
            GUI.Label(area, label, badge);
        }

        private void DrawArmies(Rect field, WorldBattle battle, double serverNow)
        {
            var (west, east, scale) = Deployment(field);
            Color westColor = TeamColor(battle.Attacker), eastColor = TeamColor(battle.Defender);
            float elapsed = ObserveExchange(battle);
            float phase = Mathf.Clamp01(elapsed / ExchangeAnimationSeconds);
            float advance = Mathf.Sin(phase * Mathf.PI);

            // 两侧分开绘制，每侧的队列按纵深从远到近叠放。兵力由快照驱动。
            DrawRanks(west, 1, battle.AttackerTroops, battle.AttackerType, westColor, scale,
                battle.LastDefenderLosses == 0 ? 0 : advance);
            DrawRanks(east, -1, battle.DefenderTroops, battle.DefenderType, eastColor, scale,
                battle.LastAttackerLosses == 0 ? 0 : advance);
            DrawArmyPlaque(west - new Vector2(0, 151 * scale), "攻方", battle.AttackerType, battle.AttackerTroops, westColor);
            DrawArmyPlaque(east - new Vector2(0, 151 * scale), "守方", battle.DefenderType, battle.DefenderTroops, eastColor);
            if (phase < 1)
            {
                if (battle.LastDefenderLosses != 0)
                    DrawExchange(west, east, battle.AttackerType, battle.LastDefenderLosses, phase, scale, westColor);
                if (battle.LastAttackerLosses != 0)
                    DrawExchange(east, west, battle.DefenderType, battle.LastAttackerLosses, phase, scale, eastColor);
            }
            DrawIncomingReinforcements(field, battle, serverNow, scale);
        }

        private void DrawRemainingArmies(Rect field, WorldBattleReport result)
        {
            var (west, east, scale) = Deployment(field);
            Color westColor = TeamColor(result.Attacker), eastColor = TeamColor(result.Defender);
            DrawRanks(west, 1, result.AttackerSurvivors, result.AttackerType, westColor, scale, 0);
            DrawRanks(east, -1, result.DefenderSurvivors, result.DefenderType, eastColor, scale, 0);
            var labelOffset = new Vector2(0, 151 * scale);
            DrawArmyPlaque(west - labelOffset, "攻方存活", result.AttackerType, result.AttackerSurvivors, westColor);
            DrawArmyPlaque(east - labelOffset, "守方存活", result.DefenderType, result.DefenderSurvivors, eastColor);
        }

        private static (Vector2 West, Vector2 East, float Scale) Deployment(Rect field) =>
            (new Vector2(field.width * 0.29f, field.height * 0.68f), new Vector2(field.width * 0.71f, field.height * 0.68f),
                Mathf.Min(field.width / 842, field.height / 440));

        private float ObserveExchange(WorldBattle battle)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (sceneBattleId != battle.BattleId || battle.Round < sceneRound)
            {
                sceneBattleId = battle.BattleId;
                sceneRound = battle.Round;
                exchangeAt = double.NegativeInfinity;
            }
            else if (battle.Round > sceneRound)
            {
                // 只有游戏服推进交锋回合时才播放冲锋、飞矢和本次战损。
                sceneRound = battle.Round;
                exchangeAt = now;
            }
            return (float)(now - exchangeAt);
        }

        private void DrawRanks(Vector2 anchor, int facing, uint troops, TroopType type, Color color, float scale, float advance)
        {
            if (troops == 0) return;
            int groups = Mathf.Min(12, Mathf.CeilToInt(troops / 8f));
            int rows = Mathf.CeilToInt(groups / 4f);
            float movement = type == TroopType.Cavalry ? 38 : type == TroopType.Bow ? 7 : 20;
            float idleTime = (float)Time.realtimeSinceStartupAsDouble % 100;
            DrawStandard(anchor + new Vector2(-facing * 105, -36) * scale, facing, color, scale);
            for (int row = 0; row < rows; row++)
            {
                int columns = Mathf.Min(4, groups - row * 4);
                for (int column = 0; column < columns; column++)
                {
                    float x = ((column - (columns - 1) * 0.5f) * 49 + (row % 2) * 12) * facing;
                    float y = (row - (rows - 1) * 0.5f) * 43;
                    float step = Mathf.Sin(idleTime * 5 + row * 2 + column) * (0.5f + advance * 1.1f);
                    var feet = anchor + new Vector2(x + facing * movement * advance, y + step) * scale;
                    DrawTrooper(feet, facing, type, color, scale * (1 + row * 0.045f));
                }
            }
        }

        private void DrawTrooper(Vector2 feet, int facing, TroopType type, Color color, float scale)
        {
            float baseWidth = (type == TroopType.Cavalry ? 48 : 29) * scale;
            Paint(new Rect(feet.x - baseWidth * 0.56f, feet.y - 3 * scale, baseWidth * 1.12f, 10 * scale),
                circle, WithAlpha(Inset, 0.6f));
            Paint(new Rect(feet.x - baseWidth / 2, feet.y - 2 * scale, baseWidth, 6 * scale), circle, WithAlpha(color, 0.84f));

            if (battleUnits == null)
            {
                DrawFormation(feet + new Vector2(0, -11 * scale), type, scale * 1.4f, color);
                return;
            }
            var uv = UnitPortraits[type is >= TroopType.Spear and <= TroopType.Cavalry ? (int)type : 0];
            float height = (type == TroopType.Cavalry ? 84 : 76) * scale;
            float width = uv.width * battleUnits.width / (uv.height * battleUnits.height) * height;
            if (facing < 0) uv = new Rect(uv.xMax, uv.y, -uv.width, uv.height);
            DrawAtlas(new Rect(feet.x - width / 2, feet.y - height, width, height), battleUnits, uv);
        }

        private void DrawStandard(Vector2 foot, int facing, Color color, float scale)
        {
            Fill(new Rect(foot.x - scale, foot.y - 69 * scale, 2 * scale, 70 * scale), new Color32(236, 212, 163, 255));
            float left = facing > 0 ? foot.x : foot.x - 25 * scale;
            Fill(new Rect(left, foot.y - 68 * scale, 25 * scale, 16 * scale), color);
            Fill(new Rect(left, foot.y - 54 * scale, 25 * scale, 2 * scale), WithAlpha(Inset, 0.65f));
        }

        private void DrawArmyPlaque(Vector2 point, string role, TroopType type, uint troops, Color color)
        {
            var label = new Rect(point.x - 69, point.y - 12, 138, 25);
            Fill(label, WithAlpha(Inset, 0.9f));
            Fill(new Rect(label.x, label.y, label.width, 2), color);
            GUI.Label(label, $"{role} · {TroopName(type)} {troops}", badge);
        }

        private void DrawExchange(Vector2 from, Vector2 to, TroopType type, uint losses, float phase, float scale, Color team)
        {
            int direction = to.x > from.x ? 1 : -1;
            if (type == TroopType.Bow && phase < 0.72f)
            {
                // 飞矢作为回合反馈，弧线长度随战场视图缩放。
                float progress = phase / 0.72f;
                for (int arrow = 0; arrow < 3; arrow++)
                {
                    float z = (arrow - 1) * 18 * scale;
                    var head = Vector2.Lerp(from, to, progress) + new Vector2(0, z - (38 + Mathf.Sin(progress * Mathf.PI) * 46) * scale);
                    var tail = head + new Vector2(-direction * 13, progress > 0.5f ? -4 : 4) * scale;
                    DrawSegment(tail, head, 1.8f * scale, Color.Lerp(team, Paper, 0.64f));
                }
            }
            else if (phase is > 0.2f and < 0.63f)
            {
                var contact = Vector2.Lerp(from, to, 0.56f) - new Vector2(0, 32 * scale);
                float fade = Mathf.Sin((phase - 0.2f) / 0.43f * Mathf.PI);
                DrawDisc(contact, 10 * scale * fade, WithAlpha(Gold, fade * 0.85f));
            }
            if (phase < 0.24f) return;
            float rise = Mathf.InverseLerp(0.24f, 1, phase);
            var damage = new Vector2(to.x + direction * 25 * scale, to.y - (100 + rise * 29) * scale);
            var area = new Rect(damage.x - 27, damage.y - 11, 54, 22);
            Fill(area, WithAlpha(Inset, Mathf.Min(0.85f, 1 - rise)));
            Color original = GUI.contentColor;
            GUI.contentColor = WithAlpha(new Color(1, 0.8f, 0.72f), 1 - rise);
            GUI.Label(area, $"-{losses}", centered);
            GUI.contentColor = original;
        }

        private void DrawIncomingReinforcements(Rect field, WorldBattle battle, double serverNow, float scale)
        {
            foreach (var march in world.Marches)
            {
                if (march.ReinforceBattleId != battle.BattleId) continue;
                bool attacker = march.Player == battle.Attacker;
                int facing = attacker ? 1 : -1;
                float progress = Mathf.Clamp01(1 - (float)((march.ArrivesAtMs - serverNow) / (MarchSeconds * 1000)));
                float x = Mathf.Lerp(0.05f, 0.25f, progress);
                var entry = new Vector2(field.width * (attacker ? x : 1 - x), field.height * 0.89f);
                Color team = TeamColor(march.Player);
                DrawTrooper(entry, facing, march.TroopType, team, scale * 0.76f);
                DrawTrooper(entry - new Vector2(facing * 29, 13) * scale, facing, march.TroopType, team, scale * 0.68f);
                string eta = Countdown(march.ArrivesAtMs, serverNow, "正在汇合");
                // 标签收在视图边缘以内，保持援军刚入场时的倒计时可读。
                float labelX = Mathf.Clamp(entry.x, 68, field.width - 68);
                DrawBadge(new Vector2(labelX, entry.y + 14 * scale), $"援军 {march.Troops} · {eta}", 128, team, true);
            }
        }

        private static Rect AtlasCell(int quadrant)
        {
            const float inset = 0.002f;
            return new Rect(quadrant % 2 * 0.5f + inset, (1 - quadrant / 2) * 0.5f + inset, 0.5f - 2 * inset, 0.5f - 2 * inset);
        }

        private static void DrawAtlas(Rect area, Texture2D atlas, Rect uv, Color? tint = null)
        {
            Color original = GUI.color;
            GUI.color = tint ?? Color.white;
            GUI.DrawTextureWithTexCoords(area, atlas, uv);
            GUI.color = original;
        }
    }
}
