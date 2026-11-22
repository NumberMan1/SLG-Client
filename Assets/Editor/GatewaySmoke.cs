using System;
using Slg.Networking;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Slg.EditorTools
{
    /// <summary>验证网关是否能通过 HTTP 路由到游戏服。</summary>
    public static class GatewaySmoke
    {
        /// <summary>从编辑器菜单运行检查，将结果写入控制台。</summary>
        [MenuItem("SLG/验证网关连接")]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        /// <summary>从批处理入口运行检查，以退出码返回结果。</summary>
        public static void RunBatch() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            UnityWebRequest request = null;
            try
            {
                var url = Environment.GetEnvironmentVariable("SLG_CLIENT_GATEWAY_URL");
                request = GatewayHealth.CreateRequest(
                    string.IsNullOrWhiteSpace(url) ? GatewayHealth.DefaultGatewayUrl : url);
                request.SendWebRequest().completed += _ => CheckResponse(request, exitWhenDone);
            }
            catch (Exception error)
            {
                request?.Dispose();
                Finish(error, exitWhenDone);
            }
        }

        private static void CheckResponse(UnityWebRequest request, bool exitWhenDone)
        {
            Exception failure = null;
            using (request)
            {
                try
                {
                    GatewayHealth.ValidateResponse(request);
                }
                catch (Exception error)
                {
                    failure = error;
                }
            }
            Finish(failure, exitWhenDone);
        }

        private static void Finish(Exception error, bool exitWhenDone)
        {
            if (error == null)
            {
                Debug.Log("网关连接验证通过，游戏服运行正常。");
            }
            else
            {
                Debug.LogError($"网关连接验证失败。{error.Message}");
            }

            if (exitWhenDone)
            {
                EditorApplication.Exit(error == null ? 0 : 1);
            }
        }
    }
}
