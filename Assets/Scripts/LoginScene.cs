using System;
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
        private (MarchOrder Order, string Account, string Instance)? pendingMarch;
        private long serverTimeMs;
        private double serverTimeReceivedAt;

        private static readonly Color Background = new(0.047f, 0.085f, 0.095f);
        private static readonly Color Paper = new(0.95f, 0.92f, 0.83f);
        private static readonly Color Muted = new(0.64f, 0.72f, 0.71f);
        private static readonly Color Gold = new(0.83f, 0.68f, 0.40f);
        private static readonly Color PlayerOne = new(0.15f, 0.40f, 0.45f);
        private static readonly Color PlayerTwo = new(0.51f, 0.27f, 0.21f);

        private GUIStyle eyebrow;
        private GUIStyle title;
        private GUIStyle body;
        private GUIStyle label;
        private GUIStyle sessionLabel;
        private GUIStyle input;
        private GUIStyle button;
        private GUIStyle secondaryButton;
        private GUIStyle message;
        private GUIStyle tileLabel;

        private void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // 网页构建从当前页面取得网关地址，以便资源和协议请求共享同一来源。
            gateway = new Uri(Application.absoluteURL).GetLeftPart(UriPartial.Authority);
#else
            gateway = Environment.GetEnvironmentVariable("SLG_CLIENT_GATEWAY_URL") ?? gateway;
#endif
            account = Environment.GetEnvironmentVariable("SLG_DEMO_ACCOUNT") ?? account;
        }

        private void OnDestroy() => connection?.Dispose();

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
            try
            {
                while (this && ReferenceEquals(connection, opened))
                {
                    if (Time.realtimeSinceStartupAsDouble >= nextSnapshotAt)
                    {
                        // 重连先读取快照，再决定是否用原 ID 重试该世界中的行军。
                        var order = world != null && pendingMarch is { } march && march.Instance == world.Instance
                            ? march.Order : null;
                        var reply = await opened.UpdateWorldAsync(new WorldRequest
                        {
                            KnownInstance = world?.Instance ?? "",
                            KnownRevision = world?.Revision ?? 0,
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
            if (reply.Result == WorldResult.WorldChanged ||
                (reply.Snapshot != null && pendingMarch is { } march && march.Instance != reply.Snapshot.Instance))
            {
                world = reply.Snapshot;
                pendingMarch = null;
                failed = false;
                status = "世界实例已变更，请重新选择地块。";
                return;
            }
            if (reply.Result == WorldResult.Full) world = null;
            else if (reply.Snapshot != null) world = reply.Snapshot;
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
                    WorldResult.Ok => order == null ? "已连接" : "已派出行军",
                    WorldResult.Full => "世界已满，正在等待名额…",
                    WorldResult.InvalidTarget => "请选择与己方领地相邻的中立地块。",
                    WorldResult.AlreadyMarching => "请等待当前行军抵达。",
                    WorldResult.Reserved => "已有玩家正在前往该地块。",
                    _ => "行军指令被拒绝。"
                };
            }
            if (order != null) pendingMarch = null;
        }

        private void Disconnect(bool keepPendingMarch = false)
        {
            var opened = connection;
            connection = null;
            opened.Dispose();
            world = null;
            if (!keepPendingMarch) pendingMarch = null;
            failed = false;
            status = "连接已关闭";
        }

        private void OnGUI()
        {
            if (title == null)
            {
                CreateStyles();
            }
            // 以 1280×720 为布局基准，等比缩放并垂直居中。
            float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            var original = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

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
            if (world == null)
            {
                GUI.Label(new Rect(width * 0.42f + 36, height - 61, 400, 25), "世界地图预览", eyebrow);
            }
            else
            {
                DrawWorld(width, height);
            }
            GUI.matrix = original;
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

        private void DrawWorld(float width, float height)
        {
            float left = width * 0.42f;
            float center = (left + width) * 0.5f;
            float top = (height - 720) * 0.5f;
            Fill(new Rect(left + 1, 0, width - left - 1, height), Background);
            GUI.Label(new Rect(left + 36, top + 54, 400, 24), "共享世界", eyebrow);
            GUI.Label(new Rect(left + 36, top + 88, width - left - 72, 32),
                $"玩家 {world.YourPlayer} · 请选择领地旁的中立地块", body);

            foreach (var tile in world.Tiles)
            {
                // 轴坐标中的相邻行横向错开半格。
                var rect = new Rect(center + (tile.Q + tile.R * 0.5f) * 111 - 49, top + 339 + tile.R * 74, 98, 62);
                var color = tile.Owner switch
                {
                    1 => PlayerOne,
                    2 => PlayerTwo,
                    _ => new Color(0.18f, 0.23f, 0.24f)
                };
                bool marching = false;
                foreach (var march in world.Marches)
                {
                    if (march.TargetTileId == tile.Id) marching = true;
                }
                if (marching) color = new Color(0.42f, 0.32f, 0.17f);
                Fill(rect, color);
                string caption = tile.Home != 0 ? $"出生地 {tile.Home}" : marching ? "行军中" :
                    tile.Owner == 0 ? $"地块 {tile.Id}" : $"玩家 {tile.Owner}";
                if (GUI.Button(rect, caption, tileLabel) && tile.Owner == 0 && tile.Home == 0 &&
                    !marching && pendingMarch == null)
                {
                    var order = new MarchOrder { CommandId = Guid.NewGuid().ToString("N"), TargetTileId = tile.Id };
                    pendingMarch = (order, connection.Session.Account, world.Instance);
                }
            }

            double now = serverTimeMs + (Time.realtimeSinceStartupAsDouble - serverTimeReceivedAt) * 1000;
            float line = top + 590;
            foreach (var march in world.Marches)
            {
                double seconds = Math.Max(0, (march.ArrivesAtMs - now) / 1000);
                GUI.Label(new Rect(left + 36, line, width - left - 72, 28),
                    $"玩家 {march.Player} · {march.SourceTileId} → {march.TargetTileId} · {seconds:0.0} 秒", body);
                line += 30;
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
            tileLabel = Text(interfaceFont, 15, Paper, FontStyle.Bold, TextAnchor.MiddleCenter);
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
