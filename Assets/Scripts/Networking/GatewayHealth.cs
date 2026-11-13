using System;
using UnityEngine;
using UnityEngine.Networking;

namespace Slg.Networking
{
    public static class GatewayHealth
    {
        public const string DefaultGatewayUrl = "http://127.0.0.1:8080";

        public static UnityWebRequest CreateRequest(string gatewayUrl)
        {
            var request = UnityWebRequest.Get(gatewayUrl.TrimEnd('/') + "/v1/game/health");
            request.timeout = 5;
            return request;
        }

        public static void ValidateResponse(UnityWebRequest request)
        {
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
            {
                throw new InvalidOperationException(
                    $"Gateway request failed (HTTP {request.responseCode}): {request.error}");
            }

            var health = JsonUtility.FromJson<HealthResponse>(request.downloadHandler.text);
            if (health?.service != "game-server" || health.status != "ok")
            {
                throw new InvalidOperationException("Gateway returned an unexpected game-server health response.");
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
