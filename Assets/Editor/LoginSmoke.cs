using System;
using Slg.Networking;
using UnityEditor;
using UnityEngine;

namespace Slg.EditorTools
{
    public static class LoginSmoke
    {
        [MenuItem("SLG/Verify Login and TCP Connection")]
        public static void RunFromMenu() => Run(exitWhenDone: false);

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
                    throw new InvalidOperationException("Set SLG_DEMO_PASSWORD before starting Unity.");
                }
                using var connection = await GatewayConnection.LoginAsync(
                    string.IsNullOrWhiteSpace(url) ? GatewayHealth.DefaultGatewayUrl : url,
                    string.IsNullOrEmpty(account) ? "demo" : account,
                    password);
                Debug.Log($"SLG login and TCP smoke passed (session {connection.Session.SessionId})");
                exitCode = 0;
            }
            catch (Exception error)
            {
                Debug.LogError($"SLG login and TCP smoke failed: {error.Message}");
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
