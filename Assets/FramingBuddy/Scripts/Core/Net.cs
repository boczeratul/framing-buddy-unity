using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FramingBuddy
{
    /// <summary>設定：Google 桌面用金鑰（不可有 HTTP referrer 限制）。存在使用者的偏好設定中。</summary>
    public static class Config
    {
        const string KeyPref = "framing-buddy.google-key";

        public static string GoogleKey
        {
            get
            {
                string k = PlayerPrefs.GetString(KeyPref, "");
                if (!string.IsNullOrEmpty(k)) return k;
                // 開發用：StreamingAssets/config.local.json {"googleKey": "..."}（不進版控）
                string path = Path.Combine(Application.streamingAssetsPath, "config.local.json");
                if (File.Exists(path))
                {
                    var c = JsonUtility.FromJson<LocalConfig>(File.ReadAllText(path));
                    if (c != null && !string.IsNullOrEmpty(c.googleKey)) return c.googleKey;
                }
                return "";
            }
            set
            {
                PlayerPrefs.SetString(KeyPref, value ?? "");
                PlayerPrefs.Save();
            }
        }

        public static bool HasGoogle => !string.IsNullOrEmpty(GoogleKey);

        [Serializable]
        class LocalConfig
        {
            public string googleKey;
        }
    }

    /// <summary>UnityWebRequest 的 async 包裝（主執行緒上呼叫）。</summary>
    public static class Net
    {
        public const string UserAgent = "FramingBuddy-Unity/1.0 (photography planning app)";

        public static Task<UnityWebRequest> Send(UnityWebRequest req, int timeoutSec = 30)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            req.timeout = timeoutSec;
            req.SetRequestHeader("User-Agent", UserAgent);
            var op = req.SendWebRequest();
            op.completed += _ => tcs.TrySetResult(req);
            return tcs.Task;
        }

        public static async Task<string> GetText(string url, Dictionary<string, string> headers = null, int timeoutSec = 30)
        {
            using var req = UnityWebRequest.Get(url);
            if (headers != null) foreach (var kv in headers) req.SetRequestHeader(kv.Key, kv.Value);
            await Send(req, timeoutSec);
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[net] {url.Split('?')[0]} → {req.responseCode} {req.error}");
                return null;
            }
            return req.downloadHandler.text;
        }

        public static async Task<byte[]> GetBytes(string url, Dictionary<string, string> headers = null, int timeoutSec = 30)
        {
            using var req = UnityWebRequest.Get(url);
            if (headers != null) foreach (var kv in headers) req.SetRequestHeader(kv.Key, kv.Value);
            await Send(req, timeoutSec);
            if (req.result != UnityWebRequest.Result.Success) return null;
            return req.downloadHandler.data;
        }

        public static async Task<string> PostJson(string url, string json, Dictionary<string, string> headers = null, int timeoutSec = 30)
        {
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (headers != null) foreach (var kv in headers) req.SetRequestHeader(kv.Key, kv.Value);
            await Send(req, timeoutSec);
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[net] POST {url.Split('?')[0]} → {req.responseCode} {req.error} {req.downloadHandler?.text}");
                return null;
            }
            return req.downloadHandler.text;
        }

        public static async Task<string> PostForm(string url, string body, int timeoutSec = 60)
        {
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            await Send(req, timeoutSec);
            return req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null;
        }

        /// <summary>簡單的磁碟快取（Application.temporaryCachePath），用於地形與向量圖磚</summary>
        public static async Task<byte[]> GetBytesCached(string url, string cacheKey, TimeSpan ttl)
        {
            string dir = Path.Combine(Application.temporaryCachePath, "fb-cache");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, cacheKey.Replace('/', '_').Replace(':', '_'));
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < ttl) return File.ReadAllBytes(file);
            var bytes = await GetBytes(url);
            if (bytes != null) File.WriteAllBytes(file, bytes);
            return bytes;
        }
    }
}
