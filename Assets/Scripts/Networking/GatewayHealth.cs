using System;
using UnityEngine;
using UnityEngine.Networking;

namespace Slg.Networking
{
    /// <summary>检测网关至游戏服的 HTTP 路由是否连通。</summary>
    public static class GatewayHealth
    {
        /// <summary>本机联调使用的默认网关地址。</summary>
        public const string DefaultGatewayUrl = "http://127.0.0.1:8080";

        /// <summary>创建游戏服健康检查请求，由调用方发送并释放。</summary>
        public static UnityWebRequest CreateRequest(string gatewayUrl)
        {
            var request = UnityWebRequest.Get(gatewayUrl.TrimEnd('/') + "/v1/game/health");
            request.timeout = 5;
            return request;
        }

        /// <summary>检查已完成的请求，确认响应来自健康的游戏服。</summary>
        public static void ValidateResponse(UnityWebRequest request)
        {
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
            {
                throw new InvalidOperationException(
                    $"网关健康检查失败（HTTP {request.responseCode}）。");
            }

            var health = JsonUtility.FromJson<HealthResponse>(request.downloadHandler.text);
            if (health?.service != "game-server" || health.status != "ok")
            {
                throw new InvalidOperationException("网关返回的游戏服健康状态无效。");
            }
        }

        [Serializable]
        private sealed class HealthResponse
        {
            public string service = string.Empty;
            public string status = string.Empty;
        }
    }
}
