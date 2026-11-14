using System;
using Slg.Networking;
using UnityEngine;

namespace Slg
{
    public sealed class LoginScene : MonoBehaviour
    {
        [SerializeField] private Font displayFont;
        [SerializeField] private Font interfaceFont;

        private string gateway = GatewayHealth.DefaultGatewayUrl;
        private string account = "demo";
        private string password = "";
        private string status = "";
        private bool busy;
        private bool failed;
        private GatewayConnection connection;

        private static readonly Color Background = new(0.047f, 0.085f, 0.095f);
        private static readonly Color Paper = new(0.95f, 0.92f, 0.83f);
        private static readonly Color Muted = new(0.64f, 0.72f, 0.71f);
        private static readonly Color Gold = new(0.83f, 0.68f, 0.40f);

        private GUIStyle eyebrow;
        private GUIStyle title;
        private GUIStyle body;
        private GUIStyle label;
        private GUIStyle input;
        private GUIStyle button;
        private GUIStyle message;

        private void Awake()
        {
            gateway = Environment.GetEnvironmentVariable("SLG_CLIENT_GATEWAY_URL") ?? gateway;
            account = Environment.GetEnvironmentVariable("SLG_DEMO_ACCOUNT") ?? account;
        }

        private void OnDestroy() => connection?.Dispose();

        private async void SignIn()
        {
            if (busy || connection != null || string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            {
                return;
            }
            busy = true;
            failed = false;
            status = "Signing in and opening TCP session…";
            try
            {
                var opened = await GatewayConnection.LoginAsync(gateway.Trim(), account.Trim(), password);
                if (!this)
                {
                    opened.Dispose();
                    return;
                }
                connection = opened;
                password = "";
                status = "TCP authentication accepted";
                WatchConnection(opened);
            }
            catch (Exception error)
            {
                status = error.Message;
                failed = true;
            }
            finally
            {
                busy = false;
            }
        }

        private async void WatchConnection(GatewayConnection opened)
        {
            string reason;
            try
            {
                await opened.WaitForDisconnectAsync();
                reason = "Gateway closed the connection";
            }
            catch (Exception error)
            {
                reason = error.Message;
            }
            if (this && ReferenceEquals(connection, opened))
            {
                Disconnect();
                status = reason;
            }
        }

        private void Disconnect()
        {
            var opened = connection;
            connection = null;
            opened.Dispose();
            status = "Connection closed";
        }

        private void OnGUI()
        {
            if (title == null)
            {
                CreateStyles();
            }
            float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            var original = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            Fill(new Rect(0, 0, width * 0.42f, height), Background);
            Fill(new Rect(width * 0.42f, 0, 1, height), new Color(0.41f, 0.39f, 0.25f));
            const float x = 58;
            float y = (height - 720) * 0.5f;
            GUI.Label(new Rect(x, y + 54, 400, 24), "SLG  /  CONNECTION DEMO", eyebrow);
            GUI.Label(new Rect(x - 3, y + 94, 450, 79), "Frontier", title);
            GUI.Label(new Rect(x, y + 188, 408, 51),
                "Sign in to the game gateway and open a TCP session.", body);
            Fill(new Rect(x, y + 248, 410, 1), new Color(0.23f, 0.30f, 0.30f));

            if (connection == null)
            {
                bool wasEnabled = GUI.enabled;
                GUI.enabled = !busy;
                gateway = DrawField(x, y + 269, "Gateway", gateway);
                account = DrawField(x, y + 347, "Account", account);
                password = DrawField(x, y + 425, "Password", password, secret: true);
                GUI.enabled = !busy && !string.IsNullOrWhiteSpace(account) && !string.IsNullOrEmpty(password);
                if (DrawButton(new Rect(x, y + 518, 410, 48), busy ? "Connecting…" : "Sign in"))
                {
                    SignIn();
                }
                GUI.enabled = wasEnabled;
            }
            else
            {
                GUI.Label(new Rect(x, y + 283, 400, 32), "Session authenticated", body);
                GUI.Label(new Rect(x, y + 343, 410, 26), $"ACCOUNT     {connection.Session.Account}", label);
                GUI.Label(new Rect(x, y + 386, 410, 26), $"SESSION     {connection.Session.SessionId}", label);
                if (DrawButton(new Rect(x, y + 518, 410, 48), "Disconnect"))
                {
                    Disconnect();
                }
            }

            message.normal.textColor = failed ? new Color(1f, 0.61f, 0.52f) : Muted;
            GUI.Label(new Rect(x, y + 580, 410, 69), status, message);
            GUI.Label(new Rect(width * 0.42f + 36, height - 61, 400, 25), "WORLD MAP PREVIEW", eyebrow);
            GUI.matrix = original;
        }

        private string DrawField(float x, float y, string caption, string value, bool secret = false)
        {
            GUI.Label(new Rect(x, y, 410, 23), caption, label);
            var border = new Rect(x, y + 27, 410, 39);
            Fill(border, new Color(0.23f, 0.30f, 0.30f));
            Fill(new Rect(x + 1, y + 28, 408, 37), new Color(0.075f, 0.12f, 0.13f));
            var field = new Rect(x + 1, y + 28, 408, 37);
            return secret ? GUI.PasswordField(field, value, '•', 128, input) : GUI.TextField(field, value, 256, input);
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
            input = Text(interfaceFont, 16, Paper, alignment: TextAnchor.MiddleLeft);
            input.padding = new RectOffset(12, 12, 0, 0);
            button = Text(interfaceFont, 16, Background, FontStyle.Bold, TextAnchor.MiddleCenter);
            message = Text(interfaceFont, 15, Muted);
            message.wordWrap = true;
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
