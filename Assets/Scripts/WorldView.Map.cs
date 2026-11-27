using System.Collections.Generic;
using Slg.Protocol;
using UnityEngine;

namespace Slg
{
    internal sealed partial class WorldView
    {
        private const float ColumnSpacing = 1.94f;
        private const float MaxZoom = 4;
        private const double MarchSeconds = 3;
        private readonly Texture2D campaignMap;
        private readonly Texture2D terrainAtlas;
        private readonly Texture2D circle = CreateCircle();
        private readonly Texture2D landmarks = CreateLandmarks();
        private Vector2? mapFocus;
        private float mapZoom = 1;
        private Vector2 pressPosition;
        private bool dragging;

        private void DrawMap(Rect area, double serverNow)
        {
            DrawFrame(area);
            GUI.Label(new Rect(area.x + 16, area.y + 9, area.width - 260, 34), "边境战区", heading);
            float right = area.xMax - 12;
            if (DrawButton(new Rect(right - 213, area.y + 10, 72, 32), "定位主城", false, home != 0)) Locate(home, Tab.Tile);
            if (DrawButton(new Rect(right - 135, area.y + 10, 32, 32), "-", false, mapZoom > 1)) SetZoom(mapZoom / 1.25f);
            if (DrawButton(new Rect(right - 97, area.y + 10, 32, 32), "+", false, mapZoom < MaxZoom)) SetZoom(mapZoom * 1.25f);
            if (DrawButton(new Rect(right - 59, area.y + 10, 59, 32), "全图", false, tiles.Count > 0)) ResetMap();

            var viewport = new Rect(area.x + 9, area.y + PanelHeader, area.width - 18, area.height - PanelHeader - 46);
            GUI.BeginGroup(viewport);
            if (tiles.Count > 0) DrawCampaign(new Rect(0, 0, viewport.width, viewport.height), serverNow);
            GUI.EndGroup();

            float legendX = area.x + 16, legendY = area.yMax - 34;
            DrawLegend(legendX, legendY, Friendly, "己方据点");
            DrawLegend(legendX + 93, legendY, Enemy, "敌方据点");
            DrawLegend(legendX + 186, legendY, Gold, "相邻目标");
            DrawLegend(legendX + 279, legendY, Paper, "交战", battle: true);
            GUI.Label(new Rect(area.xMax - 146, legendY, 135, 24), "拖动地图 · 滚轮缩放", muted);
        }

        private void DrawCampaign(Rect field, double serverNow)
        {
            Vector2 span = mapMax - mapMin + new Vector2(2.1f, 1.55f);
            float fitRadius = Mathf.Min((field.width - 16) / span.x, (field.height - 16) / span.y);
            HandleMapInput(field, fitRadius);
            if (Event.current.type != EventType.Repaint) return;

            float radius = fitRadius * mapZoom;
            Vector2 mapCenter = (mapMin + mapMax) * 0.5f;
            Vector2 offset = (mapCenter - FocusPoint(field, radius)) * radius;
            DrawLandscape(field, offset);
            Vector2 origin = field.center + offset - mapCenter * radius;

            // 归属先绘制为据点光晕。路线、驻军和交战状态覆盖在连续地貌上。
            var visible = new Rect(-65, -65, field.width + 130, field.height + 130);
            foreach (var tile in world.Tiles)
            {
                Vector2 point = origin + GridPoint(tile) * radius;
                if (visible.Contains(point) && tile.Owner != 0)
                    DrawDisc(point, Mathf.Min(radius * 0.76f, 67), WithAlpha(TeamColor(tile.Owner), 0.28f));
            }
            if (sources.TryGetValue(selectedTile, out var source))
                DrawRoute(origin + GridPoint(source) * radius, origin + GridPoint(tiles[selectedTile]) * radius, Gold, 3, true);
            foreach (var march in world.Marches) DrawMarch(march, origin, radius, serverNow);

            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var tile in world.Tiles)
            {
                Vector2 point = origin + GridPoint(tile) * radius;
                if (visible.Contains(point)) DrawSite(tile, point, radius, now);
            }
        }

        private void DrawLandscape(Rect field, Vector2 offset)
        {
            Fill(field, Inset);
            if (campaignMap == null) return;
            var bounds = new Rect(field.center + offset - field.size * (mapZoom * 0.5f), field.size * mapZoom);
            Paint(bounds, campaignMap, new Color(0.77f, 0.78f, 0.74f));
            Fill(new Rect(0, 0, field.width, 1), Line);
        }

        private Vector2 FocusPoint(Rect field, float radius)
        {
            Vector2 center = (mapMin + mapMax) * 0.5f;
            Vector2 span = field.size * ((mapZoom - 1) / (2 * radius));
            Vector2 min = Vector2.Max(mapMin, center - span), max = Vector2.Min(mapMax, center + span);
            return Vector2.Max(min, Vector2.Min(max, mapFocus ?? center));
        }

        private void HandleMapInput(Rect field, float fitRadius)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            Event input = Event.current;
            Vector2 mouse = input.mousePosition;
            float radius = fitRadius * mapZoom;
            Vector2 focus = FocusPoint(field, radius);
            switch (input.GetTypeForControl(control))
            {
                case EventType.ScrollWheel when field.Contains(mouse):
                    Vector2 gridPoint = focus + (mouse - field.center) / radius;
                    SetZoom(mapZoom * Mathf.Pow(1.15f, -input.delta.y));
                    mapFocus = gridPoint - (mouse - field.center) / (fitRadius * mapZoom);
                    input.Use();
                    break;
                case EventType.MouseDown when input.button == 0 && field.Contains(mouse):
                    GUIUtility.hotControl = control;
                    pressPosition = mouse;
                    dragging = false;
                    input.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == control:
                    dragging |= (mouse - pressPosition).sqrMagnitude > 16;
                    if (dragging) mapFocus = focus - input.delta / radius;
                    input.Use();
                    break;
                case EventType.MouseUp when input.button == 0 && GUIUtility.hotControl == control:
                    GUIUtility.hotControl = 0;
                    if (!dragging && field.Contains(mouse))
                    {
                        Vector2 origin = field.center - focus * radius;
                        SelectSiteAt(mouse, origin, radius);
                    }
                    input.Use();
                    break;
            }
        }

        private void SelectSiteAt(Vector2 mouse, Vector2 origin, float radius)
        {
            WorldTile closest = null;
            WorldTile visible = null;
            float closestDistance = float.PositiveInfinity;
            float hitWidth = Mathf.Clamp(radius * 0.80f, 10, 42);
            foreach (var tile in world.Tiles)
            {
                Vector2 point = origin + GridPoint(tile) * radius;
                bool battle = battles.ContainsKey(tile.Id);
                bool showLabel = ShowSiteLabel(tile, radius, battle);
                float size = SiteSize(tile, battle, showLabel);
                float distance = (point - mouse).sqrMagnitude;
                // 与绘制顺序一致，重叠时优先选取最后画出的可见图标或名称。
                if (distance <= size * size || (showLabel && SiteNameBadge(tile, point, size).Contains(mouse)))
                    visible = tile;
                var hit = new Rect(point.x - hitWidth, point.y - 20, hitWidth * 2, 40);
                if (!hit.Contains(mouse) || distance >= closestDistance) continue;
                closest = tile;
                closestDistance = distance;
            }
            var target = visible ?? closest;
            if (target != null) SelectTile(target.Id, battles.ContainsKey(target.Id) ? Tab.Battle : Tab.Tile);
        }

        private static bool IsKeySite(WorldTile tile, bool battle) => tile.Home != 0 || battle || tile.Id == 10;

        private bool ShowSiteLabel(WorldTile tile, float radius, bool battle) =>
            radius >= 34 || tile.Id == selectedTile || IsKeySite(tile, battle);

        private static float SiteSize(WorldTile tile, bool battle, bool showLabel) =>
            tile.Home != 0 ? 19 : battle ? 16 : showLabel ? 13 : 8;

        private static Rect SiteNameBadge(WorldTile tile, Vector2 point, float size) =>
            BadgeBounds(point + new Vector2(0, size + 11), tile.Home != 0 || tile.Id == 10 ? 79 : 74);

        private void SetZoom(float value) => mapZoom = Mathf.Clamp(value, 1, MaxZoom);

        private void ResetMap()
        {
            mapFocus = null;
            mapZoom = 1;
        }

        private void DrawSite(WorldTile tile, Vector2 point, float radius, double now)
        {
            bool selected = tile.Id == selectedTile;
            bool battle = battles.ContainsKey(tile.Id);
            bool hasNotice = notices.TryGetValue(tile.Id, out var notice) && now - notice.Start < NoticeSeconds;
            bool showLabels = ShowSiteLabel(tile, radius, battle);
            Color team = TeamColor(tile.Owner);
            Color edge = selected || battle || sources.ContainsKey(tile.Id) ? Gold : tile.Owner != 0 ? team : Muted;
            if ((selected && now < locateUntil) || hasNotice)
                edge = Color.Lerp(Gold, Paper, 0.5f + 0.5f * Mathf.Sin((float)now * 9));

            float size = SiteSize(tile, battle, showLabels);
            if (selected)
                DrawDisc(point, size + 7, WithAlpha(Gold, 0.36f));
            DrawDisc(point + new Vector2(0, 3), size + 1.5f, WithAlpha(Inset, 0.87f));
            DrawDisc(point, size, edge);
            DrawDisc(point, size - (selected ? 2.5f : 1.4f), Inset);
            if (tile.Home != 0) DrawCitadel(point, 1.1f, tile.Owner == 0 ? Paper : team);
            else if (battle) DrawClash(point, 1, Paper);
            else if (tile.Owner != 0) DrawFormation(point, tile.GarrisonType, size / 15, team);
            else DrawLandmark(point, tile.Terrain, size / 13, edge);

            if (!showLabels) return;
            var name = SiteNameBadge(tile, point, size);
            DrawBadge(name, TileName(tile), edge, selected || IsKeySite(tile, battle));
            string detail = hasNotice ? notice.Label : battle ? "交战中" : inbound.GetValueOrDefault(tile.Id) > 1 ? "双方争夺" :
                tile.Garrison > 0 ? $"{TroopName(tile.GarrisonType)} {tile.Garrison}" : "";
            if (detail.Length > 0 && (radius >= 38 || selected || battle))
                DrawBadge(point + new Vector2(0, size + 28), detail, name.width, Muted, false);
        }

        private void DrawMarch(WorldMarch march, Vector2 origin, float radius, double serverNow)
        {
            if (!tiles.TryGetValue(march.SourceTileId, out var from) || !tiles.TryGetValue(march.TargetTileId, out var to)) return;
            Vector2 start = origin + GridPoint(from) * radius;
            Vector2 end = origin + GridPoint(to) * radius;
            Vector2 direction = (end - start).normalized;
            Vector2 offset = new Vector2(-direction.y, direction.x) * Mathf.Min(10, radius * 0.22f);
            start += offset;
            end += offset;
            Color team = TeamColor(march.Player);
            DrawRoute(start, end, team, 4, false);
            double remaining = System.Math.Max(0, (march.ArrivesAtMs - serverNow) / 1000);
            float progress = Mathf.Clamp01(1 - (float)(remaining / MarchSeconds));
            Vector2 marker = Vector2.Lerp(start, end, 0.26f + progress * 0.48f);
            DrawDisc(marker, 12, team);
            DrawDisc(marker, 10.5f, Inset);
            DrawFormation(marker, march.TroopType, 0.69f, team);
            if (radius < 36 && selectedTile != to.Id) return;
            string time = Countdown(march.ArrivesAtMs, serverNow, "正在抵达");
            float side = march.Player == 1 ? -1 : 1;
            DrawBadge(marker + new Vector2(0, side * 21), $"{TroopName(march.TroopType)} {march.Troops} · {time}", 137, team, true);
        }

        private static Rect BadgeBounds(Vector2 center, float width) => new(center.x - width / 2, center.y - 9, width, 18);

        private void DrawBadge(Vector2 center, string label, float width, Color color, bool underline) =>
            DrawBadge(BadgeBounds(center, width), label, color, underline);

        private void DrawBadge(Rect box, string label, Color color, bool underline)
        {
            Fill(box, WithAlpha(Inset, 0.93f));
            if (underline) Fill(new Rect(box.x, box.yMax - 1, box.width, 1), color);
            GUI.Label(box, label, badge);
        }

        private static void DrawRoute(Vector2 start, Vector2 end, Color color, float size, bool preview)
        {
            int dots = preview ? 10 : 14;
            for (int index = 0; index < dots; index++)
            {
                Vector2 point = Vector2.Lerp(start, end, 0.19f + index * 0.60f / (dots - 1));
                Fill(new Rect(point.x - size / 2 - 1, point.y - size / 2 - 1, size + 2, size + 2), Inset);
                Fill(new Rect(point.x - size / 2, point.y - size / 2, size, size), color);
            }
            Vector2 direction = (end - start).normalized;
            Vector2 tip = Vector2.Lerp(start, end, 0.86f);
            Vector2 perpendicular = new(-direction.y, direction.x);
            DrawSegment(tip - direction * 9 + perpendicular * 5, tip, size, color);
            DrawSegment(tip - direction * 9 - perpendicular * 5, tip, size, color);
        }

        private static void DrawCitadel(Vector2 point, float scale, Color color)
        {
            float x = point.x - 11 * scale, y = point.y - 8 * scale;
            Fill(new Rect(x, y + 5 * scale, 22 * scale, 11 * scale), color);
            for (int tower = 0; tower < 3; tower++)
                Fill(new Rect(x + tower * 8.5f * scale, y, 5 * scale, 7 * scale), color);
            Fill(new Rect(point.x - 2 * scale, y + 10 * scale, 4 * scale, 6 * scale), Inset);
        }

        private void DrawLandmark(Vector2 point, BattlefieldTerrain terrain, float scale, Color color)
        {
            float half = 12 * scale;
            var area = new Rect(point.x - half, point.y - half, half * 2, half * 2);
            DrawAtlas(area, landmarks, new Rect((int)terrain * 0.25f, 0, 0.25f, 1), color);
        }

        private static Texture2D CreateLandmarks()
        {
            const int size = 24;
            // 一次生成四种图标。逐帧每处地标只需绘制图集的一格。
            var strokes = new (Vector2 Start, Vector2 End)[][]
            {
                new[] { (new Vector2(-7, 4), new Vector2(0, -5)), (new Vector2(0, -5), new Vector2(7, 4)) },
                new[] { (new Vector2(-7, 4), new Vector2(0, -5)), (new Vector2(0, -5), new Vector2(7, 4)),
                    (new Vector2(-5, 6), new Vector2(5, 6)) },
                new[] { (new Vector2(-4, -4), new Vector2(-4, 5)), (new Vector2(4, -6), new Vector2(4, 5)) },
                new[] { (new Vector2(-6, -3), new Vector2(6, -3)), (new Vector2(-7, 3), new Vector2(7, 3)) }
            };
            int width = size * strokes.Length;
            var pixels = new Color32[width * size];
            for (int icon = 0; icon < strokes.Length; icon++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 point = new(x + 0.5f - size / 2f, size / 2f - y - 0.5f);
                float coverage = 0;
                foreach (var (start, end) in strokes[icon])
                {
                    Vector2 line = end - start;
                    float progress = Mathf.Clamp01(Vector2.Dot(point - start, line) / line.sqrMagnitude);
                    float distance = Vector2.Distance(point, start + line * progress);
                    coverage = Mathf.Max(coverage, Mathf.Clamp01(1.5f - distance));
                }
                pixels[y * width + icon * size + x] = new Color32(255, 255, 255, (byte)(coverage * 255));
            }
            var texture = new Texture2D(width, size, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static void DrawFormation(Vector2 point, TroopType type, float scale, Color color)
        {
            if (type == TroopType.Cavalry)
            {
                DrawSegment(point + new Vector2(-7, 3) * scale, point + new Vector2(0, -4) * scale, 3 * scale, color);
                DrawSegment(point + new Vector2(0, -4) * scale, point + new Vector2(7, 3) * scale, 3 * scale, color);
                Fill(new Rect(point.x - 5 * scale, point.y + 5 * scale, 10 * scale, 2 * scale), color);
                return;
            }
            for (int i = -1; i <= 1; i++)
            {
                float x = point.x + i * 5 * scale;
                Fill(new Rect(x - scale, point.y + 1 * scale, 3 * scale, 5 * scale), color);
                if (type == TroopType.Spear)
                    Fill(new Rect(x + scale, point.y - 6 * scale, 1.5f * scale, 9 * scale), color);
                else if (type == TroopType.Bow)
                    DrawSegment(new Vector2(x - scale, point.y - 4 * scale), new Vector2(x + 2 * scale, point.y), 1.5f * scale, color);
                else
                    Fill(new Rect(x - scale, point.y - 3 * scale, 2 * scale, 2 * scale), color);
            }
        }

        private static void DrawClash(Vector2 point, float scale, Color color)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                DrawSegment(point + new Vector2(-8 * side, -8) * scale, point + new Vector2(6 * side, 6) * scale, 3 * scale, color);
                DrawSegment(point + new Vector2(2 * side, 6) * scale, point + new Vector2(6 * side, 2) * scale, 2 * scale, color);
            }
        }

        private static void DrawSegment(Vector2 start, Vector2 end, float width, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(start, end) / (width * 0.6f)));
            for (int step = 0; step <= steps; step++)
            {
                Vector2 point = Vector2.Lerp(start, end, (float)step / steps);
                Fill(new Rect(point.x - width / 2, point.y - width / 2, width, width), color);
            }
        }

        private void DrawLegend(float x, float y, Color color, string label, bool battle = false)
        {
            if (battle) DrawClash(new Vector2(x + 6, y + 11), 0.65f, color);
            else DrawDisc(new Vector2(x + 6, y + 11), 5, color);
            GUI.Label(new Rect(x + 18, y, 71, 23), label, muted);
        }

        private void DrawDisc(Vector2 point, float radius, Color color) =>
            Paint(new Rect(point.x - radius, point.y - radius, radius * 2, radius * 2), circle, color);

        private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

        private static Vector2 GridPoint(WorldTile tile)
        {
            // 据点沿用服务端坐标的邻接关系，并在约束范围内错开图标。
            int variation = (tile.Id.GetHashCode() * 17 % 7) - 3;
            float shift = tile.Home != 0 || tile.Id == 10 ? 0 : variation * 0.055f;
            return new Vector2(ColumnSpacing * (tile.Q + tile.R * 0.5f) + shift, tile.R * 1.55f + shift * 0.7f);
        }

        private static Texture2D CreateCircle()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 offset = new(x + 0.5f - size / 2f, y + 0.5f - size / 2f);
                float coverage = Mathf.Clamp01(size / 2f - offset.magnitude - 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * coverage));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }
}
