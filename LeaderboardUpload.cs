// =====================================================================
//  Leaderboard Upload for Streamer.bot
//  Reads the stats saved by LoyaltyBot.cs and uploads them as
//  leaderboard.json to your free GitHub Pages site.
//
//  Put this in its OWN action ("Leaderboard Upload") with a
//  Timed Action trigger (every 5 minutes, only while live is fine).
//
//  Needs a persisted GLOBAL variable named  github_token
//  (Variables tab > Global > Persisted). NEVER show it on stream.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

public class CPHInline
{
    // ---------------- SETTINGS (edit these) ----------------
    const string GITHUB_USER  = "YOUR_GITHUB_NAME";  // your GitHub username
    const string REPO         = "leaderboard";       // repo name
    const string BRANCH       = "main";
    const string FILE_PATH    = "leaderboard.json";
    const string CHANNEL_NAME = "YourChannel";       // shown at the top of the website
    const int    MAX_USERS    = 500;                 // how many viewers to put on the site

    static readonly string[] IGNORED =
    {
        "streamelements", "nightbot", "streamlabs", "moobot", "fossabot",
        "wizebot", "soundalerts", "sery_bot", "falkorie", "kofistreambot"
    };
    // --------------------------------------------------------

    static bool Ignored(string login)
    {
        foreach (var n in IGNORED)
            if (string.Equals(n, login, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool Execute()
    {
        string token = CPH.GetGlobalVar<string>("github_token", true);
        if (string.IsNullOrEmpty(token))
        {
            CPH.LogWarn("[Leaderboard] Missing persisted global variable 'github_token'");
            return false;
        }

        var xp    = Longs("loyalty_xp");
        var watch = Longs("loyalty_watchMin");
        var msgs  = Longs("loyalty_messages");
        var st    = Longs("loyalty_streak");
        var best  = Longs("loyalty_bestStreak");
        var names = Strings("loyalty_display");

        // Everyone who has any stat
        var logins = new List<string>();
        var seen = new Dictionary<string, bool>();
        foreach (var d in new[] { xp, watch, msgs })
            foreach (var k in d.Keys)
                if (!seen.ContainsKey(k) && !Ignored(k)) { seen[k] = true; logins.Add(k); }
        logins.Sort((a, b) => V(xp, b).CompareTo(V(xp, a)));
        if (logins.Count > MAX_USERS) logins.RemoveRange(MAX_USERS, logins.Count - MAX_USERS);

        var sb = new StringBuilder();
        sb.Append("{\"channel\":\"").Append(Esc(CHANNEL_NAME)).Append("\",");
        sb.Append("\"updated\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"users\":[");
        for (int i = 0; i < logins.Count; i++)
        {
            string l = logins[i];
            long x = V(xp, l);
            int lvl = LevelFromXp(x);
            long lo = XpForLevel(lvl), hi = XpForLevel(lvl + 1);
            int pct = hi > lo ? (int)((x - lo) * 100 / (hi - lo)) : 0;
            string name = names.ContainsKey(l) && !string.IsNullOrEmpty(names[l]) ? names[l] : l;

            if (i > 0) sb.Append(',');
            sb.Append("{\"n\":\"").Append(Esc(name)).Append("\",\"l\":\"").Append(Esc(l)).Append('"')
              .Append(",\"xp\":").Append(x)
              .Append(",\"lv\":").Append(lvl)
              .Append(",\"pct\":").Append(pct)
              .Append(",\"wt\":").Append(V(watch, l))
              .Append(",\"st\":").Append(V(st, l))
              .Append(",\"best\":").Append(V(best, l))
              .Append(",\"msg\":").Append(V(msgs, l))
              .Append('}');
        }
        sb.Append("]}");
        string json = sb.ToString();

        // Skip the upload if nothing changed since last time (ignore the timestamp)
        string fingerprint = Regex.Replace(json, "\"updated\":\"[^\"]*\"", "");
        if (CPH.GetGlobalVar<string>("leaderboard_lastUpload", false) == fingerprint) return true;

        if (Upload(token, json))
            CPH.SetGlobalVar("leaderboard_lastUpload", fingerprint, false);
        return true;
    }

    WebClient NewClient(string token)
    {
        var wc = new WebClient();
        wc.Encoding = Encoding.UTF8;
        wc.Headers[HttpRequestHeader.UserAgent] = "StreamerBot-Leaderboard";
        wc.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
        wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
        return wc;
    }

    bool Upload(string token, string json)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        string url = "https://api.github.com/repos/" + GITHUB_USER + "/" + REPO + "/contents/" + FILE_PATH;

        // Need the current file's sha to overwrite it (404 = file doesn't exist yet, that's fine)
        string sha = null;
        try
        {
            using (var wc = NewClient(token))
            {
                var m = Regex.Match(wc.DownloadString(url + "?ref=" + BRANCH), "\"sha\"\\s*:\\s*\"([0-9a-f]{40})\"");
                if (m.Success) sha = m.Groups[1].Value;
            }
        }
        catch (WebException) { }

        try
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            string body = "{\"message\":\"Update leaderboard\",\"branch\":\"" + BRANCH + "\",\"content\":\"" + b64 + "\""
                        + (sha != null ? ",\"sha\":\"" + sha + "\"" : "") + "}";
            using (var wc = NewClient(token))
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                wc.UploadString(url, "PUT", body);
            }
            CPH.LogInfo("[Leaderboard] Uploaded leaderboard.json");
            return true;
        }
        catch (WebException e)
        {
            string detail = "";
            try
            {
                if (e.Response != null)
                    using (var r = new System.IO.StreamReader(e.Response.GetResponseStream())) detail = r.ReadToEnd();
            }
            catch { }
            CPH.LogWarn("[Leaderboard] Upload failed: " + e.Message + " " + detail);
            return false;
        }
    }

    // Same level curve as LoyaltyBot.cs (Falkorie match: 20*L^2 - 18*L)
    static long XpForLevel(int level)
    {
        if (level <= 0) return 0;
        return 20L * level * level - 18L * level;
    }
    static int LevelFromXp(long xp)
    {
        int lvl = 0;
        while (XpForLevel(lvl + 1) <= xp) lvl++;
        return lvl;
    }

    Dictionary<string, long> Longs(string varName)
    {
        var d = new Dictionary<string, long>();
        var raw = CPH.GetTwitchUsersVar<long>(varName, true);
        if (raw == null) return d;
        foreach (var u in raw)
        {
            if (u == null || string.IsNullOrEmpty(u.UserLogin)) continue;
            string k = u.UserLogin.ToLower();
            if (!d.ContainsKey(k) || u.Value > d[k]) d[k] = u.Value;
        }
        return d;
    }
    Dictionary<string, string> Strings(string varName)
    {
        var d = new Dictionary<string, string>();
        var raw = CPH.GetTwitchUsersVar<string>(varName, true);
        if (raw == null) return d;
        foreach (var u in raw)
        {
            if (u == null || string.IsNullOrEmpty(u.UserLogin)) continue;
            string k = u.UserLogin.ToLower();
            if (!d.ContainsKey(k)) d[k] = u.Value;
        }
        return d;
    }
    static long V(Dictionary<string, long> d, string k) { long v; return d.TryGetValue(k, out v) ? v : 0; }

    static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
