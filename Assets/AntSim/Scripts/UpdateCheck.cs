using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace AntSim
{
    /// <summary>
    /// Asks GitHub (once per run) whether a newer release of the game is out, so the start screen can say so and
    /// link to the download page. Nothing is downloaded or replaced automatically. Off with Settings ▸ Check for updates.
    /// </summary>
    public class UpdateCheck : MonoBehaviour
    {
        public const string Repo = "EthanDolphinP/DolphinAntSim";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

        /// <summary>The newer version's tag (e.g. "v1.2"), or null if none is known.</summary>
        public static string NewerVersion { get; private set; }
        public static string DownloadUrl { get; private set; } = ReleasesPage;

        static bool started;

        [System.Serializable]
        class Release { public string tag_name; public string html_url; public bool draft; public bool prerelease; }

        /// <summary>Starts the check if it hasn't run yet this session (and the setting allows it).</summary>
        public static void Begin()
        {
            if (started || !GameSettings.CheckUpdates || Application.isEditor) return;
            started = true;
            var go = new GameObject("UpdateCheck");
            DontDestroyOnLoad(go);
            go.AddComponent<UpdateCheck>().StartCoroutine(go.GetComponent<UpdateCheck>().Run());
        }

        IEnumerator Run()
        {
            using (var req = UnityWebRequest.Get("https://api.github.com/repos/" + Repo + "/releases/latest"))
            {
                req.SetRequestHeader("User-Agent", "DolphinAntSimulator");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var r = JsonUtility.FromJson<Release>(req.downloadHandler.text);
                        if (r != null && !r.draft && !r.prerelease && IsNewer(r.tag_name, Application.version))
                        {
                            NewerVersion = r.tag_name;
                            if (!string.IsNullOrEmpty(r.html_url)) DownloadUrl = r.html_url;
                        }
                    }
                    catch { /* a bad reply just means no notice */ }
                }
            }
            Destroy(gameObject);
        }

        /// <summary>Is tag (e.g. "v1.2" or "1.2.0") a higher version than current (e.g. "1.1")?</summary>
        public static bool IsNewer(string tag, string current)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            return System.Version.TryParse(Normalise(tag), out var a)
                   && System.Version.TryParse(Normalise(current), out var b) && a > b;
        }

        static string Normalise(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            int dash = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (dash >= 0) v = v.Substring(0, dash);
            return v.Contains(".") ? v : v + ".0";
        }
    }
}
