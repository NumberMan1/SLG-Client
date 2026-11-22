using System;
using Slg.Networking;
using UnityEditor;
using UnityEngine;

namespace Slg.EditorTools
{
    /// <summary>使用联调账号验证 HTTP 登录和 TCP 票据认证。</summary>
    public static class LoginSmoke
    {
        /// <summary>从编辑器菜单运行检查，将结果写入控制台。</summary>
        [MenuItem("SLG/验证登录和 TCP 连接")]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        /// <summary>从批处理入口运行检查，以退出码返回结果。</summary>
        public static void RunBatch() => Run(exitWhenDone: true);

        private static async void Run(bool exitWhenDone)
        {
            var exitCode = 1;
            try
            {
                var url = Environment.GetEnvironmentVariable("SLG_CLIENT_GATEWAY_URL");
                var account = Environment.GetEnvironmentVariable("SLG_DEMO_ACCOUNT");
                var password = Environment.GetEnvironmentVariable("SLG_DEMO_PASSWORD");
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("请在启动 Unity 前设置 SLG_DEMO_PASSWORD。");
                }
                using var connection = await GatewayConnection.LoginAsync(
                    string.IsNullOrWhiteSpace(url) ? GatewayHealth.DefaultGatewayUrl : url,
                    string.IsNullOrEmpty(account) ? "demo" : account,
                    password);
                Debug.Log($"登录和 TCP 验证通过，会话编号为 {connection.Session.SessionId}。");
                exitCode = 0;
            }
            catch (Exception error)
            {
                Debug.LogError($"登录和 TCP 验证失败。{error.Message}");
            }
            finally
            {
                if (exitWhenDone)
                {
                    EditorApplication.Exit(exitCode);
                }
            }
        }
    }
}
