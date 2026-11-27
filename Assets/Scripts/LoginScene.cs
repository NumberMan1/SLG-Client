using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Slg.Networking;
using Slg.Protocol;
using UnityEngine;

namespace Slg
{
    /// <summary>绘制登录和注册界面，并在场景内管理网关会话。</summary>
    public sealed class LoginScene : MonoBehaviour
    {
        [SerializeField] private Font displayFont;
        [SerializeField] private Font interfaceFont;
        [SerializeField] private Texture2D terrainAtlas;
        [SerializeField] private Texture2D campaignMap;
        [SerializeField] private Texture2D battlefields;
        [SerializeField] private Texture2D battleUnits;
        [SerializeField] private Camera loginCamera;

        private string gateway = GatewayHealth.DefaultGatewayUrl;
        private string account = "demo";
        private string password = "";
        private string confirmedPassword = "";
        private string status = "";
        private bool busy;
        private bool failed;
        private bool registering;
        private GatewayConnection connection;
        private WorldSnapshot world;
        private WorldView worldView;
        private int loginPreviewLayers;
        private const int BattleReportLimit = 8;
        private readonly List<WorldBattleReport> battleReports = new(BattleReportLimit);
        private int battleReportIndex;
        private (MarchOrder Order, string Account, string Instance)? pendingMarch;
        private long serverTimeMs;
        private double serverTimeReceivedAt;

        private static readonly Color Background = new(0.047f, 0.085f, 0.095f);
        private static readonly Color Paper = new(0.95f, 0.92f, 0.83f);
        private static readonly Color Muted = new(0.64f, 0.72f, 0.71f);
        private static readonly Color Gold = new(0.83f, 0.68f, 0.40f);

        private GUIStyle eyebrow;
        private GUIStyle title;
        private GUIStyle body;
        private GUIStyle label;
        private GUIStyle sessionLabel;
        private GUIStyle input;
        private GUIStyle button;
        private GUIStyle secondaryButton;
        private GUIStyle message;
        private GUIStyle worldTitle;

        private void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // 网页构建从当前页面取得网关地址，以便资源和协议请求共享同一来源。
            gateway = new Uri(Application.absoluteURL).GetLeftPart(UriPartial.Authority);
#else
            gateway = Environment.GetEnvironmentVariable("SLG_CLIENT_GATEWAY_URL") ?? gateway;
#endif
            account = Environment.GetEnvironmentVariable("SLG_DEMO_ACCOUNT") ?? account;
            loginPreviewLayers = loginCamera.cullingMask;
            worldView = new WorldView(interfaceFont, terrainAtlas, campaignMap, battlefields, battleUnits);
        }

        private void OnDestroy()
        {
            connection?.Dispose();
            worldView?.Dispose();
        }

        private void ShowRegistration(bool show)
        {
            registering = show;
            if (show && account == "demo") account = "";
            password = "";
            confirmedPassword = "";
            status = "";
            failed = false;
        }

        private async void Register()
        {
            var registrationAccount = account.Trim();
            if (!HasRegistrationLength(registrationAccount) || !HasRegistrationLength(password))
            {
                status = GatewayConnection.RegistrationLengthMessage;
                failed = true;
                return;
            }
            if (password != confirmedPassword)
            {
                status = "两次输入的密码不一致。";
                failed = true;
                return;
            }
            busy = true;
            failed = false;
            status = "正在创建账号…";
            try
            {
                await GatewayConnection.RegisterAsync(gateway.Trim(), registrationAccount, password);
                if (!this) return;
                ShowRegistration(false);
                status = "账号已创建，请使用新密码登录。";
            }
            catch (Exception error)
            {
                if (!this) return;
                status = error is GatewayException ? error.Message : "注册失败，请检查网络或稍后重试。";
                failed = true;
            }
            finally
            {
                busy = false;
            }
        }

        private static bool HasRegistrationLength(string value)
        {
            int count = 0;
            for (int index = 0; index < value.Length; index++)
            {
                // Unity 文本使用 UTF-16，代理对按一个 Unicode 字符计数，与服务端一致。
                if (index + 1 < value.Length && char.IsSurrogatePair(value, index)) index++;
                if (++count > 8) return false;
            }
            return count > 0;
        }

        private async void SignIn()
        {
            if (busy || connection != null || string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            {
                return;
            }
            busy = true;
            failed = false;
            status = "正在登录…";
            try
            {
                var opened = await GatewayConnection.LoginAsync(gateway.Trim(), account.Trim(), password);
                // 等待登录时场景可能已被销毁，此时由回调关闭刚建立的连接。
                if (!this)
                {
                    opened.Dispose();
                    return;
                }
                connection = opened;
                if (pendingMarch is { } march && march.Account != opened.Session.Account)
                {
                    pendingMarch = null;
                }
                password = "";
                status = "已连接";
                WatchWorld(opened);
            }
            catch (Exception error)
            {
                status = error is GatewayException ? error.Message : "登录失败，请检查网络或稍后重试。";
                failed = true;
            }
            finally
            {
                busy = false;
            }
        }

        private async void WatchWorld(GatewayConnection opened)
        {
            double nextSnapshotAt = 0;
            bool waitingForSeat = false;
            string lastAttempt = null;
            try
            {
                while (this && ReferenceEquals(connection, opened))
                {
                    // 重连后沿用待确认指令的原实例，即使当前地图实例已经变化也能查询回执。
                    var order = pendingMarch?.Order;
                    var knownInstance = pendingMarch?.Instance ?? world?.Instance ?? "";
                    if (Time.realtimeSinceStartupAsDouble >= nextSnapshotAt || (order != null && order.CommandId != lastAttempt))
                    {
                        lastAttempt = order?.CommandId;
                        var reply = await opened.UpdateWorldAsync(new WorldRequest
                        {
                            KnownInstance = knownInstance,
                            KnownRevision = world?.Instance == knownInstance ? world.Revision : 0,
                            KnownTileRevision = world?.Instance == knownInstance && world.Tiles.Count > 0 ? world.TileRevision : 0,
                            KnownBattleId = battleReports.Count == 0 ? 0 : battleReports[^1].BattleId,
                            KnownReportId = battleReports.Count == 0 ? 0 : ReportCursor(battleReports[^1]),
                            Order = order
                        });
                        if (!this || !ReferenceEquals(connection, opened)) return;
                        ApplyWorldReply(reply, order);
                        if (reply.Result == WorldResult.Full) waitingForSeat = true;
                        else if (reply.Snapshot != null) waitingForSeat = false;
                        // 满员时错开复查时刻，间隔保持在网关的 15 秒读超时内。
                        double delay = waitingForSeat ? UnityEngine.Random.Range(5f, 8f) : 0.5;
                        nextSnapshotAt = Time.realtimeSinceStartupAsDouble + delay;
                    }
                    // WebGL 与原生端共用轮询，在同一连接上串行读取快照和发送指令。
                    await Task.Yield();
                }
            }
            catch (Exception error)
            {
                if (this && ReferenceEquals(connection, opened))
                {
                    Disconnect(keepPendingMarch: true);
                    status = pendingMarch.HasValue ? "连接已断开，请重新登录以确认行军。" :
                        error is GatewayException ? error.Message : "游戏连接已断开，请重新登录。";
                    failed = true;
                }
            }
        }

        private void ApplyWorldReply(WorldReply reply, MarchOrder order)
        {
            if (reply.ServerTimeMs != 0)
            {
                serverTimeMs = reply.ServerTimeMs;
                serverTimeReceivedAt = Time.realtimeSinceStartupAsDouble;
            }
            // 回执和地图实例独立判断。新快照也可以随旧指令的成功回执一起返回。
            bool changed = reply.Result == WorldResult.WorldChanged;
            bool sameWorld = world != null && (reply.Snapshot == null || world.Instance == reply.Snapshot.Instance);
            var snapshot = reply.Snapshot;
            if (sameWorld && snapshot != null && snapshot.Tiles.Count == 0 && snapshot.HasTileRevision &&
                world.HasTileRevision && snapshot.TileRevision == world.TileRevision)
            {
                snapshot.Tiles.Add(world.Tiles);
            }
            // 世界实例变化时清空战报缓存，并从服务端重新读取历史。
            if (!sameWorld || reply.Result == WorldResult.Full)
            {
                battleReports.Clear();
                battleReportIndex = 0;
            }
            if (changed || reply.Result == WorldResult.Full || snapshot != null) SetWorld(snapshot);
            if (world != null)
            {
                if (reply.HasAvailableTroops) world.AvailableTroops = reply.AvailableTroops;
                ApplyBattleReports(reply, sameWorld);
            }
            if (changed)
            {
                pendingMarch = null;
                failed = false;
                status = "世界实例已变更，请重新选择地块。";
                return;
            }
            if (reply.Result == WorldResult.Unavailable)
            {
                // 上游响应可能丢失，重试时沿用原指令 ID。
                status = "暂时无法连接游戏世界，正在重试…";
                failed = true;
                return;
            }
            if (order != null || failed || world == null)
            {
                failed = reply.Result != WorldResult.Ok;
                status = reply.Result switch
                {
                    WorldResult.Ok => order == null ? "已连接" : order.RetreatBattleId != 0 ? "部队已撤出战斗" :
                        order.ReinforceBattleId != 0 ? "已派出援军" : "已派出行军",
                    WorldResult.Full => "世界已满，正在等待名额…",
                    WorldResult.InvalidTarget => "请选择领地旁的中立或敌方地块。",
                    WorldResult.AlreadyMarching => "请等待当前行军结束。",
                    WorldResult.Reserved => "已有玩家正在前往该地块。",
                    WorldResult.InsufficientTroops => "预备兵力不足，请等待补充。",
                    WorldResult.ReinforcementClosed => "这场战斗已停止接收援军。",
                    WorldResult.BattleCapacity => "该方参战兵力已达上限。",
                    WorldResult.BattleClosed => "这场战斗已结束。",
                    _ => "行军指令被拒绝。"
                };
            }
            if (order != null) pendingMarch = null;
        }

        private void ApplyBattleReports(WorldReply reply, bool showBattleEffects)
        {
            bool followsLatest = battleReportIndex >= battleReports.Count - 1;
            var after = battleReports.Count == 0 ? 0 : ReportCursor(battleReports[^1]);
            foreach (var report in reply.BattleReports)
            {
                if (ReportCursor(report) <= after) continue;
                battleReports.Add(report);
                if (showBattleEffects) worldView.ShowBattle(report);
            }
            // 兼容只在地图快照内返回最近一份战报的服务端。
            if (reply.BattleReports.Count == 0 && reply.Snapshot?.LastBattle is { } last && ReportCursor(last) > after)
            {
                battleReports.Add(last);
                if (showBattleEffects) worldView.ShowBattle(last);
            }

            int excess = battleReports.Count - BattleReportLimit;
            if (excess > 0)
            {
                battleReports.RemoveRange(0, excess);
                battleReportIndex = Math.Max(0, battleReportIndex - excess);
            }
            if (followsLatest) battleReportIndex = Math.Max(0, battleReports.Count - 1);
        }

        private static ulong ReportCursor(WorldBattleReport report) => report.ReportId == 0 ? report.BattleId : report.ReportId;

        private void Disconnect(bool keepPendingMarch = false)
        {
            var opened = connection;
            connection = null;
            opened.Dispose();
            SetWorld(null);
            battleReports.Clear();
            battleReportIndex = 0;
            if (!keepPendingMarch) pendingMarch = null;
            failed = false;
            status = "连接已关闭";
        }

        private void SetWorld(WorldSnapshot snapshot)
        {
            if ((world == null) != (snapshot == null))
            {
                // 玩法界面覆盖整个视口，只保留显示所需的相机，跳过登录地形和阴影的绘制。
                loginCamera.cullingMask = snapshot == null ? loginPreviewLayers : 0;
            }
            world = snapshot;
            worldView.ApplySnapshot(snapshot);
        }

        private void OnGUI()
        {
            if (title == null)
            {
                CreateStyles();
            }
            bool compact = world != null && Screen.width < Screen.height * 1.2f;
            // 窄窗口将面板放到地图下方，地图和面板共用缩放比例。
            float scale = compact ? Mathf.Min(Screen.width / 560f, Screen.height / 1040f) :
                Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            var original = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            if (world != null) DrawWorld(width, height, compact);
            else DrawLogin(width, height);
            GUI.matrix = original;
        }

        private void DrawLogin(float width, float height)
        {
            Fill(new Rect(0, 0, width * 0.42f, height), Background);
            Fill(new Rect(width * 0.42f, 0, 1, height), new Color(0.41f, 0.39f, 0.25f));
            const float x = 58;
            float y = (height - 720) * 0.5f;
            GUI.Label(new Rect(x, y + 54, 400, 24), "策略游戏试玩", eyebrow);
            GUI.Label(new Rect(x - 3, y + 94, 450, 79), "边境", title);
            GUI.Label(new Rect(x, y + 188, 408, 51),
                registering ? GatewayConnection.RegistrationLengthMessage : "登录后探索边境世界。", body);
            Fill(new Rect(x, y + 248, 410, 1), new Color(0.23f, 0.30f, 0.30f));

            if (connection == null)
            {
                DrawCredentialsForm(x, y);
            }
            else
            {
                GUI.Label(new Rect(x, y + 283, 400, 32), "身份认证成功", body);
                GUI.Label(new Rect(x, y + 343, 410, 26), $"账号     {connection.Session.Account}", sessionLabel);
                GUI.Label(new Rect(x, y + 386, 410, 26), $"会话编号     {connection.Session.SessionId}", label);
                if (DrawButton(new Rect(x, y + 518, 410, 48), "断开连接"))
                {
                    Disconnect();
                }
            }

            message.normal.textColor = failed ? new Color(1f, 0.61f, 0.52f) : Muted;
            GUI.Label(new Rect(x, y + (registering ? 611 : 580), 410, 69), status, message);
            GUI.Label(new Rect(width * 0.42f + 36, height - 61, 400, 25), "世界地图预览", eyebrow);
        }

        private void DrawCredentialsForm(float x, float y)
        {
            bool wasEnabled = GUI.enabled;
            bool showingRegistration = registering;
            GUI.enabled = wasEnabled && !busy;
            float row = showingRegistration ? 68 : 78;
            gateway = DrawField(x, y + 269, "网关地址", gateway);
            account = DrawField(x, y + 269 + row, "账号", account);
            password = DrawField(x, y + 269 + 2 * row, "密码", password, secret: true);
            if (showingRegistration)
            {
                confirmedPassword = DrawField(x, y + 269 + 3 * row, "确认密码", confirmedPassword, secret: true);
            }
            GUI.enabled = wasEnabled && !busy && !string.IsNullOrWhiteSpace(gateway) &&
                !string.IsNullOrWhiteSpace(account) && !string.IsNullOrEmpty(password) &&
                (!showingRegistration || !string.IsNullOrEmpty(confirmedPassword));
            float buttonY = y + (showingRegistration ? 550 : 518);
            string primaryLabel = showingRegistration ? (busy ? "正在注册…" : "注册账号") : (busy ? "正在连接…" : "登录");
            if (DrawButton(new Rect(x, buttonY, 242, 48), primaryLabel))
            {
                if (showingRegistration) Register();
                else SignIn();
            }
            GUI.enabled = wasEnabled && !busy;
            var secondaryArea = new Rect(x + 254, buttonY, 156, 48);
            Fill(secondaryArea, new Color(0.23f, 0.30f, 0.30f));
            Fill(new Rect(secondaryArea.x + 1, secondaryArea.y + 1, 154, 46), Background);
            if (GUI.Button(secondaryArea, showingRegistration ? "返回登录" : "注册账号", secondaryButton))
            {
                GUI.FocusControl(null);
                ShowRegistration(!showingRegistration);
            }
            GUI.enabled = wasEnabled;
        }

        private void DrawWorld(float width, float height, bool compact)
        {
            Fill(new Rect(0, 0, width, height), new Color32(13, 18, 18, 255));
            float headerHeight = compact ? 105 : 74;
            Fill(new Rect(0, 0, width, headerHeight), new Color32(25, 33, 31, 255));
            Fill(new Rect(0, 0, width, 3), Gold);
            Fill(new Rect(0, headerHeight - 1, width, 1), new Color32(58, 68, 61, 255));
            GUI.Label(new Rect(20, 15, 88, 50), "边境", worldTitle);
            GUI.Label(new Rect(112, 17, 120, 22), $"玩家 {world.YourPlayer}", eyebrow);
            GUI.Label(new Rect(112, 39, 250, 24),
                $"预备兵力 {world.AvailableTroops} · 补兵上限 {world.TroopCapacity}", label);
            message.normal.textColor = failed ? new Color(1f, 0.61f, 0.52f) : Muted;
            var statusArea = compact ? new Rect(20, 72, width - 40, 28) : new Rect(384, 25, width - 530, 40);
            GUI.Label(statusArea, status, message);
            var disconnect = new Rect(width - 126, 19, 106, 36);
            Fill(disconnect, new Color32(58, 68, 61, 255));
            if (GUI.Button(disconnect, "断开连接", secondaryButton))
            {
                Disconnect();
                return;
            }

            double now = serverTimeMs + (Time.realtimeSinceStartupAsDouble - serverTimeReceivedAt) * 1000;
            var bounds = new Rect(16, headerHeight + 14, width - 32, height - headerHeight - 30);
            var dispatch = worldView.Draw(bounds, compact, pendingMarch.HasValue, now, battleReports, ref battleReportIndex);
            if (dispatch is { } chosen)
            {
                var order = new MarchOrder
                {
                    CommandId = Guid.NewGuid().ToString("N"), TargetTileId = chosen.Target, Troops = chosen.Troops,
                    TroopType = chosen.Type,
                    ContinuousBattle = chosen.BattleId == 0,
                    ReinforceBattleId = chosen.Retreat ? 0 : chosen.BattleId,
                    RetreatBattleId = chosen.Retreat ? chosen.BattleId : 0
                };
                pendingMarch = (order, connection.Session.Account, world.Instance);
                status = chosen.Retreat ? "正在提交撤退指令…" : "正在提交行军…";
                failed = false;
            }
        }

        private string DrawField(float x, float y, string caption, string value, bool secret = false)
        {
            GUI.Label(new Rect(x, y, 410, 23), caption, label);
            var border = new Rect(x, y + 27, 410, 39);
            Fill(border, new Color(0.23f, 0.30f, 0.30f));
            Fill(new Rect(x + 1, y + 28, 408, 37), new Color(0.075f, 0.12f, 0.13f));
            var field = new Rect(x + 1, y + 28, 408, 37);
            return secret ? GUI.PasswordField(field, value, '•', input) : GUI.TextField(field, value, input);
        }

        private bool DrawButton(Rect area, string caption)
        {
            Fill(area, GUI.enabled ? Gold : new Color(0.35f, 0.37f, 0.32f));
            return GUI.Button(area, caption, button);
        }

        private void CreateStyles()
        {
            eyebrow = Text(interfaceFont, 13, Gold, FontStyle.Bold);
            title = Text(displayFont, 76, Paper, FontStyle.Bold);
            body = Text(interfaceFont, 19, Muted);
            body.wordWrap = true;
            label = Text(interfaceFont, 15, Paper, FontStyle.Bold);
            sessionLabel = new GUIStyle(label) { clipping = TextClipping.Clip };
            input = Text(interfaceFont, 16, Paper, alignment: TextAnchor.MiddleLeft);
            input.padding = new RectOffset(12, 12, 0, 0);
            button = Text(interfaceFont, 16, Background, FontStyle.Bold, TextAnchor.MiddleCenter);
            secondaryButton = Text(interfaceFont, 15, Paper, FontStyle.Bold, TextAnchor.MiddleCenter);
            message = Text(interfaceFont, 15, Muted);
            message.wordWrap = true;
            worldTitle = Text(interfaceFont, 32, Paper, FontStyle.Bold);
        }

        private static GUIStyle Text(Font font, int size, Color color,
            FontStyle weight = FontStyle.Normal, TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var style = new GUIStyle { font = font, fontSize = size, fontStyle = weight, alignment = alignment };
            style.normal.textColor = color;
            style.focused.textColor = color;
            style.hover.textColor = color;
            style.active.textColor = color;
            return style;
        }

        private static void Fill(Rect rect, Color color)
        {
            Color original = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = original;
        }
    }
}
