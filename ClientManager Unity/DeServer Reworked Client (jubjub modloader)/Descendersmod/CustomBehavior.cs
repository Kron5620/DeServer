using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System.CodeDom.Compiler;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System;
using UnityEngine.UI;


public class CustomBehaviour : MonoBehaviour
{
    [Header("Server Settings")]
    public string serverIp = "127.0.0.1";
    public int serverPort = 1;
    public int timeoutSec = 2;
    public float objectsInterval = 1f;

    private bool lastPaused;

    private Identification id;
    private bool connected;
    private GameObject playerGO;

    [System.Serializable] private class CommandList { public string[] commands; }

    [System.Serializable]
    private class CreateCmd
    {
        public string cmd;
        public string src;
        public float x, y, z;
        public float rx, ry, rz;
        public string color;
        public string rename;
        public float sx = 1, sy = 1, sz = 1;
        public Dictionary<string, bool> components;
    }

    private void Start()
    {
        DetectServerInfo();

        id = FetchId();
        if (id == null)
        {
            id = new Identification { playerName = "Ghost", steamID = "Unknown" };
            Debug.LogWarning("[ClientManager] Steam info missing – defaulting to Ghost/Unknown.");
        }

        PostBlocking("connect", null, null);
        connected = true;
        Debug.Log("[ClientManager] Connected (initial handshake sent).");

        StartCoroutine(NetLoop());
        StartCoroutine(CommandLoop());
        StartCoroutine(PauseLoop());
        StartCoroutine(InputLoop());

        StartCoroutine(ModsLoader());
    }

    private void DetectServerInfo()
    {
        const string prefix = "ServerInfo:";

        foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null) continue;

            string name = go.name;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = name.Split(':');
            if (parts.Length < 3) continue;

            serverIp = parts[1];

            if (int.TryParse(parts[2], out int port)
                && port > 0 && port < 65536)
                serverPort = port;

            Debug.Log($"[ClientManager] ServerInfo detected → {serverIp}:{serverPort}");
            return;
        }

        Debug.LogWarning("[ClientManager] No ServerInfo:<ip>:<port> object found; using inspector defaults.");
    }


    private IEnumerator ModsLoader()
    {
        // wait until the handshake succeeded
        while (!connected) yield return null;

        string urlList = $"http://{serverIp}:{serverPort}/mods";
        UnityWebRequest listReq = UnityWebRequest.Get(urlList);
        listReq.timeout = timeoutSec;
        yield return listReq.SendWebRequest();

        if (listReq.isNetworkError || listReq.isHttpError)
        {
            Debug.LogWarning("[ClientManager] Could not fetch mod list: " + listReq.error);
            yield break;
        }

        List<string> names = new List<string>();
        const string key = "\"mods\":[";
        int i = listReq.downloadHandler.text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            i += key.Length;
            int j = listReq.downloadHandler.text.IndexOf(']', i);
            if (j > i)
                foreach (string n in listReq.downloadHandler.text
                           .Substring(i, j - i).Replace("\"", "").Split(','))
                    if (!string.IsNullOrEmpty(n)) names.Add(n.Trim());
        }

        foreach (string file in names)
        {
            if (!file.ToLowerInvariant().EndsWith(".dll"))
            {
                Debug.LogWarning("[ClientManager] Skipping " + file +
                                 " – only DLL mods are supported on .NET 3.5.");
                continue;
            }

            string urlFile = $"http://{serverIp}:{serverPort}/mods/{UnityWebRequest.EscapeURL(file)}";
            UnityWebRequest fileReq = UnityWebRequest.Get(urlFile);
            fileReq.timeout = timeoutSec;
            yield return fileReq.SendWebRequest();

            if (fileReq.isNetworkError || fileReq.isHttpError)
            {
                Debug.LogWarning("[ClientManager] Download failed for " + file + ": " + fileReq.error);
                continue;
            }

            LoadAssemblyAndAttach(file, fileReq.downloadHandler.data);
        }

        Debug.Log("[ClientManager] ModsLoader finished (DLL workflow).");
    }



    private void LoadAssemblyAndAttach(string fileName, byte[] bytes)
    {
        try
        {
            var asm = Assembly.Load(bytes);
            int count = 0;

            foreach (Type t in asm.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                GameObject host = GameObject.Find(t.Name) ?? new GameObject(t.Name);
                host.AddComponent(t);
                count++;
            }

            Debug.Log($"[ClientManager] Loaded {fileName} – attached {count} MonoBehaviour(s).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ClientManager] Could not load mod DLL {fileName}: {ex}");
        }
    }




    private void CompileAndAttach(string sourceCode)
    {
        Type providerType = Type.GetType(
                "Microsoft.CSharp.CSharpCodeProvider, Microsoft.CSharp", false);

        if (providerType == null)
        {
            Debug.LogWarning("[ClientManager] Keine CodeDom-Runtime gefunden – " +
                             "Mods bitte als vor­kompilierte DLL ausliefern.");
            return;
        }

        Assembly compiledAssembly = null;

        try
        {
            using (var provider =
                   (CodeDomProvider)Activator.CreateInstance(providerType))
            {
                var options = new CompilerParameters
                {
                    GenerateExecutable = false,
                    GenerateInMemory = true,
                    TreatWarningsAsErrors = false
                };

                
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { options.ReferencedAssemblies.Add(asm.Location); }
                    catch {}
                }

                CompilerResults res = provider.CompileAssemblyFromSource(
                                            options, new[] { sourceCode });

                if (res.Errors.HasErrors)
                {
                    foreach (CompilerError err in res.Errors)
                        Debug.LogError("[ClientManager] Mod-Compiler-Fehler: " + err);
                    return;
                }

                compiledAssembly = res.CompiledAssembly;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ClientManager] Laufzeit­kompilierung fehlgeschlagen: " + ex.Message);
            return;
        }

        foreach (Type t in compiledAssembly.GetTypes())
        {
            if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;

            GameObject host = GameObject.Find(t.Name) ?? new GameObject(t.Name);
            host.AddComponent(t);
            Debug.Log($"[ClientManager] Mod-Behaviour {t.FullName} an \"{host.name}\" angehängt");
        }
    }









    private IEnumerator InputLoop()
    {
        string[] axes =
        {
            "Horizontal", "Vertical",
            "Mouse X", "Mouse Y", "Mouse ScrollWheel",
            "JoystickAxis1", "JoystickAxis2", "JoystickAxis3",
            "JoystickAxis4", "JoystickAxis5", "JoystickAxis6",
            "JoystickAxis7", "JoystickAxis8", "JoystickAxis9",
            "JoystickAxis10"
        };

        Dictionary<string, float> lastAxis = new Dictionary<string, float>(axes.Length);

        while (true)
        {
            if (connected && Input.anyKeyDown)
            {
                foreach (KeyCode code in System.Enum.GetValues(typeof(KeyCode)))
                {
                    if (!Input.GetKeyDown(code)) continue;
                    UnityWebRequest req = BuildInput(code.ToString());
                    yield return req.SendWebRequest();
                }
            }

            if (connected)
            {
                foreach (string a in axes)
                {
                    float v = Input.GetAxisRaw(a);
                    float prev;
                    lastAxis.TryGetValue(a, out prev);
                    if (Mathf.Abs(v - prev) > 0.01f)
                    {
                        UnityWebRequest req = BuildAxis(a, v);
                        yield return req.SendWebRequest();
                        lastAxis[a] = v;
                    }
                }
            }

            yield return null;
        }
    }

    private UnityWebRequest BuildAxis(string axisName, float value)
    {
        string body = "{\"event\":\"axis\"" +
                      ",\"axis\":\"" + axisName + "\"" +
                      ",\"val\":" + value.ToString("F4") +
                      ",\"playerName\":\"" + id.playerName + "\"" +
                      ",\"steamID\":\"" + id.steamID + "\"}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string url = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    private UnityWebRequest BuildInput(string key)
    {
        string body = "{\"event\":\"input\"" +
                      ",\"key\":\"" + key + "\"" +
                      ",\"playerName\":\"" + id.playerName + "\"" +
                      ",\"steamID\":\"" + id.steamID + "\"}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string url = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    [System.Serializable]
    private class TimelineEntry
    {
        public float offset;
        public string json;
    }

    [System.Serializable]
    private class TimelineCmd
    {
        public string cmd;
        public string label;
        public TimelineEntry[] entries;
    }

    private bool isInPauseMenu()
    {
        string[] menus =
        {
            "UI_Pause",
            "UI_Options",
            "UI_OptionsGameplay",
            "UI_OptionsVideo",
            "UI_OptionsAudio",
            "UI_OptionsKeyBindings",
            "UI_OptionsLanguages"
        };

        foreach (string name in menus)
            if (GameObject.Find(name) != null)
                return true;

        return false;
    }

    private IEnumerator PauseLoop()
    {
        WaitForSeconds wait = new WaitForSeconds(0.25f);
        lastPaused = isInPauseMenu();

        UnityWebRequest init = BuildPause(lastPaused);
        yield return init.SendWebRequest();

        while (true)
        {
            bool nowPaused = isInPauseMenu();
            if (nowPaused != lastPaused)
            {
                UnityWebRequest req = BuildPause(nowPaused);
                yield return req.SendWebRequest();
                lastPaused = nowPaused;
            }
            yield return wait;
        }
    }

    private UnityWebRequest BuildPause(bool paused)
    {
        string body = "{\"event\":\"pause\"" +
                      ",\"state\":\"" + (paused ? "on" : "off") + "\"" +
                      ",\"playerName\":\"" + id.playerName + "\"" +
                      ",\"steamID\":\"" + id.steamID + "\"}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string url = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    private void OnApplicationQuit() { PostBlocking("disconnect", null, null); }

    private IEnumerator NetLoop()
    {
        WaitForSeconds retry = new WaitForSeconds(1f);
        WaitForSeconds interval = new WaitForSeconds(objectsInterval);

        while (true)
        {
            while (!connected)
            {
                UnityWebRequest req = Build("connect", null, null, null);
                yield return req.SendWebRequest();

                if (req.isNetworkError || req.isHttpError)
                    yield return retry;
                else
                {
                    connected = true;
                    Debug.Log("[ClientManager] Connected.");
                }
            }

            if (playerGO == null)
                playerGO = GameObject.Find("Player_Human");
            Camera cam = Camera.main;

            Vector3? p = null, r = null, c = null;
            if (playerGO != null)
            {
                p = playerGO.transform.position;
                r = playerGO.transform.eulerAngles;
            }
            if (cam != null)
                c = cam.transform.position;

            UnityWebRequest posReq = Build("pos", p, r, c);
            yield return posReq.SendWebRequest();
            if (posReq.isNetworkError || posReq.isHttpError)
            {
                connected = false;
                yield return retry;
                continue;
            }

            string objJson = GatherSceneObjects();
            UnityWebRequest objReq = BuildObjects(objJson);
            yield return objReq.SendWebRequest();
            if (objReq.isNetworkError || objReq.isHttpError)
            {
                connected = false;
                yield return retry;
                continue;
            }

            yield return interval;
        }
    }

    private UnityWebRequest Build(string evt, Vector3? pos, Vector3? rot)
    {
        return Build(evt, pos, rot, null);
    }

    private UnityWebRequest Build(string evt, Vector3? pos, Vector3? rot, Vector3? camPos)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"event\":\"").Append(evt)
          .Append("\",\"playerName\":\"").Append(id.playerName)
          .Append("\",\"steamID\":\"").Append(id.steamID).Append("\"");

        if ("pos".Equals(evt) && pos.HasValue && rot.HasValue)
        {
            Vector3 p = pos.Value;
            Vector3 r = rot.Value;
            sb.Append(",\"x\":").Append(p.x)
              .Append(",\"y\":").Append(p.y)
              .Append(",\"z\":").Append(p.z)
              .Append(",\"rx\":").Append(r.x)
              .Append(",\"ry\":").Append(r.y)
              .Append(",\"rz\":").Append(r.z);

            if (camPos.HasValue)
            {
                Vector3 c = camPos.Value;
                sb.Append(",\"camx\":").Append(c.x)
                  .Append(",\"camy\":").Append(c.y)
                  .Append(",\"camz\":").Append(c.z);
            }
        }

        sb.Append("}");
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        string url = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    private void PostBlocking(string evt, Vector3? p, Vector3? r)
    {
        UnityWebRequest req = Build(evt, p, r);
        var op = req.Send();
        float end = Time.realtimeSinceStartup + timeoutSec;
        while (!op.isDone && Time.realtimeSinceStartup < end) { }
    }

    [System.Serializable]
    private class EditCmd
    {
        public string cmd;
        public string target;
        public bool delete;
        public float x, y, z;
        public float rx, ry, rz;
        public float sx = 1, sy = 1, sz = 1;
        public string color;
        public string copytex;
        public string rename;
        public string text;
        public float vx, vy, vz;
        public Dictionary<string, bool> components;
    }

    private IEnumerator CommandLoop()
    {
        WaitForSeconds wait = new WaitForSeconds(0.5f);
        string url = "http://" + serverIp + ":" + serverPort +
                     "/cmd?steamID=" + UnityWebRequest.EscapeURL(id.steamID);

        while (true)
        {
            UnityWebRequest req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
            yield return req.SendWebRequest();

            if (!req.isNetworkError && !req.isHttpError)
            {
                string txt = req.downloadHandler.text;
                if (!string.IsNullOrEmpty(txt))
                {
                    string[] cmds = ParseCommandArray(txt);
                    if (cmds != null)
                        for (int i = 0; i < cmds.Length; i++)
                            ApplyCommand(cmds[i]);
                }
            }
            yield return wait;
        }
    }

    private static string[] ParseCommandArray(string json)
    {
        const string key = "\"commands\":[";
        int start = json.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += key.Length;
        int end = json.IndexOf(']', start);
        if (end < 0) return null;

        string contents = json.Substring(start, end - start);
        if (string.IsNullOrEmpty(contents)) return new string[0];

        List<string> list = new List<string>();
        int idx = 0;

        while (idx < contents.Length)
        {
            while (idx < contents.Length && contents[idx] != '"') idx++;
            if (++idx >= contents.Length) break;

            int strStart = idx;
            bool escape = false;

            while (idx < contents.Length)
            {
                char c = contents[idx++];
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') break;
            }
            int strEnd = idx - 1;
            string cmd = contents.Substring(strStart, strEnd - strStart)
                                 .Replace("\\\"", "\"")
                                 .Replace("\\\\", "\\");
            list.Add(cmd);

            while (idx < contents.Length && contents[idx] != '"') idx++;
        }

        return list.ToArray();
    }

    private void ApplyCommand(string json)
    {
        if (string.IsNullOrEmpty(json))
            return;

        const string cmdKey = "\"cmd\":\"";
        int p = json.IndexOf(cmdKey, System.StringComparison.OrdinalIgnoreCase);
        if (p < 0) return;

        p += cmdKey.Length;
        int q = json.IndexOf('"', p);
        if (q < 0) return;

        string cmd = json.Substring(p, q - p).ToLowerInvariant();

        switch (cmd)
        {
            case "timeline": ApplyTimeline(ParseTimeline(json)); break;
            case "create": ApplyCreate(ParseCreate(json)); break;
            case "mesh": ApplyMesh(ParseMesh(json)); break;
            case "edit": ApplyEdit(ParseEdit(json)); break;
            case "tween": ApplyTween(ParseTween(json)); break;
            case "turn": ApplyTurn(ParseTurn(json)); break;
            case "modload": ApplyModLoad(ParseModLoad(json)); break;   // NEW
            default:
                Debug.LogWarning("[ClientManager] Unknown cmd \"" + cmd + '\"');
                break;
        }
    }

    private ModLoadCmd ParseModLoad(string src)
    {
        ModLoadCmd m = new ModLoadCmd();
        m.cmd = "modload";
        m.file = ReadString(src, "\"file\":\"", "");
        return m;
    }


    private void ApplyModLoad(ModLoadCmd m)
    {
        if (string.IsNullOrEmpty(m.file))
        {
            Debug.LogWarning("[ClientManager] modload: no file specified.");
            return;
        }
        StartCoroutine(DownloadAndLoadMod(m.file));
    }

    private IEnumerator DownloadAndLoadMod(string fileName)
    {
        string url = $"http://{serverIp}:{serverPort}/mods/{UnityWebRequest.EscapeURL(fileName)}";
        UnityWebRequest req = UnityWebRequest.Get(url);
        req.timeout = timeoutSec;
        yield return req.SendWebRequest();

        if (req.isNetworkError || req.isHttpError)
        {
            Debug.LogWarning("[ClientManager] modload download failed for " +
                             fileName + ": " + req.error);
            yield break;
        }

        byte[] data = req.downloadHandler.data;
        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            LoadAssemblyAndAttach(fileName, data);
            SendAck("modload", fileName);
        }
        else
        {
            Debug.LogWarning("[ClientManager] modload ignored “" + fileName +
                             "” (only DLLs supported on .NET 3.5).");
        }
    }




    [System.Serializable]
    private class ModLoadCmd
    {
        public string cmd;
        public string file;
    }




    private TimelineCmd ParseTimeline(string src)
    {
        TimelineCmd tl = new TimelineCmd();
        tl.cmd = "timeline";
        tl.label = ReadString(src, "\"label\":\"", null);

        List<TimelineEntry> list = new List<TimelineEntry>();

        int arrStart = src.IndexOf("\"entries\":[", System.StringComparison.OrdinalIgnoreCase);
        if (arrStart >= 0)
        {
            arrStart += 11;
            int arrEnd = src.IndexOf(']', arrStart);
            if (arrEnd > arrStart)
            {
                string arr = src.Substring(arrStart, arrEnd - arrStart);
                string[] pieces = arr.Split(new[] { "},{" }, System.StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < pieces.Length; i++)
                {
                    string p = pieces[i];
                    float off = ReadFloat(p, "\"offset\":", 0f);
                    string js = ReadString(p, "\"json\":\"", "{}");
                    if (!js.StartsWith("{")) js = "{" + js;
                    if (!js.EndsWith("}")) js += "}";
                    list.Add(new TimelineEntry { offset = off, json = js });
                }
            }
        }
        tl.entries = list.ToArray();
        return tl;
    }

    private CreateCmd ParseCreate(string src)
    {
        CreateCmd c = new CreateCmd();
        c.cmd = "create";
        c.src = ReadString(src, "\"src\":\"", "");
        c.x = ReadFloat(src, "\"x\":", 0f);
        c.y = ReadFloat(src, "\"y\":", 0f);
        c.z = ReadFloat(src, "\"z\":", 0f);
        c.rx = ReadFloat(src, "\"rx\":", 0f);
        c.ry = ReadFloat(src, "\"ry\":", 0f);
        c.rz = ReadFloat(src, "\"rz\":", 0f);
        c.sx = ReadFloat(src, "\"sx\":", 1f);
        c.sy = ReadFloat(src, "\"sy\":", 1f);
        c.sz = ReadFloat(src, "\"sz\":", 1f);
        c.color = ReadString(src, "\"color\":\"", null);
        c.rename = ReadString(src, "\"rename\":\"", null);
        c.components = ParseComponents(src);
        return c;
    }

    private MeshCmd ParseMesh(string src)
    {
        MeshCmd m = new MeshCmd();
        m.cmd = "mesh";
        m.src = ReadString(src, "\"src\":\"", "");
        m.data = ReadString(src, "\"data\":\"", "");
        m.x = ReadFloat(src, "\"x\":", 0f);
        m.y = ReadFloat(src, "\"y\":", 0f);
        m.z = ReadFloat(src, "\"z\":", 0f);
        m.rx = ReadFloat(src, "\"rx\":", 0f);
        m.ry = ReadFloat(src, "\"ry\":", 0f);
        m.rz = ReadFloat(src, "\"rz\":", 0f);
        m.sx = ReadFloat(src, "\"sx\":", 1f);
        m.sy = ReadFloat(src, "\"sy\":", 1f);
        m.sz = ReadFloat(src, "\"sz\":", 1f);
        m.color = ReadString(src, "\"color\":\"", null);
        return m;
    }

    private EditCmd ParseEdit(string src)
    {
        EditCmd e = new EditCmd();
        e.cmd = "edit";
        e.target = ReadString(src, "\"target\":\"", "");
        e.delete = ReadBool(src, "\"delete\":", false);
        e.x = ReadFloat(src, "\"x\":", 0f);
        e.y = ReadFloat(src, "\"y\":", 0f);
        e.z = ReadFloat(src, "\"z\":", 0f);
        e.rx = ReadFloat(src, "\"rx\":", 0f);
        e.ry = ReadFloat(src, "\"ry\":", 0f);
        e.rz = ReadFloat(src, "\"rz\":", 0f);
        e.sx = ReadFloat(src, "\"sx\":", 1f);
        e.sy = ReadFloat(src, "\"sy\":", 1f);
        e.sz = ReadFloat(src, "\"sz\":", 1f);
        e.color = ReadString(src, "\"color\":\"", null);
        e.copytex = ReadString(src, "\"copytex\":\"", null);
        e.rename = ReadString(src, "\"rename\":\"", null);
        e.text = ReadString(src, "\"text\":\"", null);
        e.vx = ReadFloat(src, "\"vx\":", 0f);
        e.vy = ReadFloat(src, "\"vy\":", 0f);
        e.vz = ReadFloat(src, "\"vz\":", 0f);
        e.components = ParseComponents(src);
        return e;
    }

    private TweenCmd ParseTween(string src)
    {
        TweenCmd t = new TweenCmd();
        t.cmd = "tween";
        t.target = ReadString(src, "\"target\":\"", "");
        t.dx = ReadFloat(src, "\"dx\":", 0f);
        t.dy = ReadFloat(src, "\"dy\":", 0f);
        t.dz = ReadFloat(src, "\"dz\":", 0f);
        t.drx = ReadFloat(src, "\"drx\":", 0f);
        t.dry = ReadFloat(src, "\"dry\":", 0f);
        t.drz = ReadFloat(src, "\"drz\":", 0f);
        t.dsx = ReadFloat(src, "\"dsx\":", 0f);
        t.dsy = ReadFloat(src, "\"dsy\":", 0f);
        t.dsz = ReadFloat(src, "\"dsz\":", 0f);
        t.duration = ReadFloat(src, "\"duration\":", 1f);
        return t;
    }

    private TurnCmd ParseTurn(string src)
    {
        TurnCmd trn = new TurnCmd();
        trn.cmd = "turn";
        trn.target = ReadString(src, "\"target\":\"", "");
        trn.drx = ReadFloat(src, "\"drx\":", 0f);
        trn.dry = ReadFloat(src, "\"dry\":", 0f);
        trn.drz = ReadFloat(src, "\"drz\":", 0f);
        trn.duration = ReadFloat(src, "\"duration\":", 1f);
        return trn;
    }

    private static string ReadString(string json, string key, string def)
    {
        int idx = json.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return def;
        idx += key.Length;
        int end = json.IndexOf('"', idx);
        if (end < 0) return def;
        return json.Substring(idx, end - idx)
                   .Replace("\\\"", "\"")
                   .Replace("\\\\", "\\");
    }

    private static float ReadFloat(string json, string key, float def)
    {
        int idx = json.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return def;
        idx += key.Length;
        int end = idx;
        while (end < json.Length && "0123456789+-.eE".IndexOf(json[end]) != -1)
            end++;
        float val;
        return float.TryParse(json.Substring(idx, end - idx),
                              System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out val) ? val : def;
    }

    private static bool ReadBool(string json, string key, bool def)
    {
        int idx = json.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return def;
        idx += key.Length;
        if (json.Length >= idx + 4 &&
            json.Substring(idx, 4).Equals("true", System.StringComparison.OrdinalIgnoreCase))
            return true;
        if (json.Length >= idx + 5 &&
            json.Substring(idx, 5).Equals("false", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return def;
    }

    private void ApplyTimeline(TimelineCmd tl)
    {
        System.Array.Sort(tl.entries, (a, b) => a.offset.CompareTo(b.offset));
        StartCoroutine(TimelineRoutine(tl));
    }

    private IEnumerator TimelineRoutine(TimelineCmd tl)
    {
        float startTime = Time.realtimeSinceStartup;
        foreach (TimelineEntry entry in tl.entries)
        {
            float targetTime = startTime + Mathf.Max(0f, entry.offset);
            float wait = targetTime - Time.realtimeSinceStartup;
            if (wait > 0f) yield return new WaitForSecondsRealtime(wait);
            ApplyCommand(entry.json);
        }
        SendAck("timeline", tl.label ?? "timeline");
    }

    private void ApplyTween(TweenCmd t)
    {
        GameObject go = GameObject.Find(t.target);
        if (go == null)
        {
            Debug.LogWarning("[ClientManager] Tween: target '" + t.target + "' not found.");
            return;
        }

        Vector3 startPos = go.transform.position;
        Vector3 endPos = startPos + new Vector3(t.dx, t.dy, t.dz);

        Vector3 startEuler = go.transform.eulerAngles;
        Vector3 endEuler = startEuler + new Vector3(t.drx, t.dry, t.drz);

        Vector3 startScale = go.transform.localScale;
        Vector3 endScale = startScale + new Vector3(t.dsx, t.dsy, t.dsz);

        bool move = t.dx != 0f || t.dy != 0f || t.dz != 0f;
        bool turn = t.drx != 0f || t.dry != 0f || t.drz != 0f;
        bool size = t.dsx != 0f || t.dsy != 0f || t.dsz != 0f;

        if (!move && !turn && !size)
        {
            SendAck("tween", go.name);
            return;
        }

        if (t.duration <= 0f)
        {
            if (move) go.transform.position = endPos;
            if (turn) go.transform.eulerAngles = endEuler;
            if (size) go.transform.localScale = endScale;
            SendAck("tween", go.name);
            return;
        }

        StartCoroutine(TweenRoutine(go,
                                    move ? (Vector3?)endPos : null,
                                    turn ? (Vector3?)endEuler : null,
                                    size ? (Vector3?)endScale : null,
                                    t.duration,
                                    go.name));
    }

    private IEnumerator TweenRoutine(GameObject go,
                                     Vector3? finalPos,
                                     Vector3? finalEuler,
                                     Vector3? finalScale,
                                     float duration,
                                     string label)
    {
        Vector3 pStart = go.transform.position;
        Vector3 eStart = go.transform.eulerAngles;
        Vector3 sStart = go.transform.localScale;

        float elapsed = 0f;

        while (elapsed < duration && go != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            if (finalPos != null)
                go.transform.position = Vector3.Lerp(pStart, finalPos.Value, t);

            if (finalEuler != null)
                go.transform.eulerAngles = Vector3.Lerp(eStart, finalEuler.Value, t);

            if (finalScale != null)
                go.transform.localScale = Vector3.Lerp(sStart, finalScale.Value, t);

            yield return null;
        }

        if (go != null)
        {
            if (finalPos != null) go.transform.position = finalPos.Value;
            if (finalEuler != null) go.transform.eulerAngles = finalEuler.Value;
            if (finalScale != null) go.transform.localScale = finalScale.Value;
        }
        SendAck("tween", label);
    }

    private void ApplyTurn(TurnCmd t)
    {
        GameObject go = GameObject.Find(t.target);
        if (go == null)
        {
            Debug.LogWarning("[ClientManager] Turn: target '" + t.target + "' not found.");
            return;
        }

        Vector3 deltaEuler = new Vector3(t.drx, t.dry, t.drz);

        if (deltaEuler == Vector3.zero || t.duration <= 0f)
        {
            go.transform.rotation *= Quaternion.Euler(deltaEuler);
            SendAck("turn", go.name);
            return;
        }

        Quaternion start = go.transform.rotation;
        Quaternion end = start * Quaternion.Euler(deltaEuler);
        StartCoroutine(TurnRoutine(go, start, end, t.duration, go.name));
    }

    private IEnumerator TurnRoutine(GameObject go,
                                    Quaternion startRot,
                                    Quaternion endRot,
                                    float duration,
                                    string label)
    {
        float elapsed = 0f;

        while (elapsed < duration && go != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            go.transform.rotation = Quaternion.Slerp(startRot, endRot, t);
            yield return null;
        }

        if (go != null)
            go.transform.rotation = endRot;

        SendAck("turn", label);
    }

    private void ApplyEdit(EditCmd e)
    {
        GameObject go = GameObject.Find(e.target);
        if (go == null && e.target == "Player_Human")
            go = GameObject.Find("Player_Human");

        if (go == null)
        {
            foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
                if (g.name == e.target) { go = g; break; }

            if (go == null)
            {
                Debug.LogWarning("[ClientManager] Edit: target '" + e.target + "' not found.");
                return;
            }
        }

        if (e.delete)
        {
            Destroy(go);
            Debug.Log("[ClientManager] Deleted object '" + e.target + "'.");
            SendAck("delete", e.target);
            return;
        }

        bool isPlayer = (go.name == "Player_Human");
        bool hasPosValues = (e.x != 0f || e.y != 0f || e.z != 0f);
        bool colorGiven = (!string.IsNullOrEmpty(e.color) && !"none".Equals(e.color));

        bool needsMove = hasPosValues || (isPlayer && !colorGiven);
        if (needsMove)
        {
            Vector3 newPos = new Vector3(e.x, e.y, e.z);

            CharacterController cc = go.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            go.transform.position = newPos;

            if (cc != null) cc.enabled = true;

            Rigidbody rbVel = go.GetComponent<Rigidbody>();
            if (rbVel != null)
            {
                rbVel.velocity = Vector3.zero;
                rbVel.angularVelocity = Vector3.zero;
            }
        }

        if (e.rx != 0f || e.ry != 0f || e.rz != 0f)
            go.transform.rotation = Quaternion.Euler(e.rx, e.ry, e.rz);

        if (e.sx != 1f || e.sy != 1f || e.sz != 1f)
            go.transform.localScale = new Vector3(e.sx, e.sy, e.sz);

        if (!string.IsNullOrEmpty(e.rename))
            go.name = e.rename;

        if (colorGiven)
        {
            string cs = e.color.Length == 6 && !e.color.StartsWith("#") ? "#" + e.color : e.color;
            Color parsedColor;
            if (ColorUtility.TryParseHtmlString(cs, out parsedColor))
            {
                foreach (Renderer ren in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (ren.material == null)
                        ren.material = new Material(Shader.Find("Standard"));
                    ren.material.color = parsedColor;
                }
            }
        }

        if (!string.IsNullOrEmpty(e.copytex))
        {
            GameObject src = GameObject.Find(e.copytex);
            if (src != null)
            {
                Renderer[] sr = src.GetComponentsInChildren<Renderer>(true);
                Renderer[] tr = go.GetComponentsInChildren<Renderer>(true);
                if (sr.Length > 0 && tr.Length > 0 && sr[0].material != null)
                    foreach (Renderer r in tr) r.material = sr[0].material;
            }
        }

        if (!string.IsNullOrEmpty(e.text))
        {
            foreach (TextMesh tm in go.GetComponentsInChildren<TextMesh>(true))
                tm.text = e.text;

            foreach (UnityEngine.UI.Text uiTxt in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                uiTxt.text = e.text;

            foreach (UnityEngine.UI.InputField ifield in go.GetComponentsInChildren<UnityEngine.UI.InputField>(true))
                ifield.text = e.text;
        }

        if (e.components != null)
        {
            foreach (KeyValuePair<string, bool> kv in e.components)
            {
                string compName = kv.Key;
                bool turnOn = kv.Value;

                System.Type t = FindType(compName);
                if (t == null)
                {
                    Debug.LogWarning("[ClientManager] Unknown component '" + compName + "'");
                    continue;
                }

                Component[] comps = go.GetComponentsInChildren(t, true);
                bool found = comps != null && comps.Length > 0;

                if (!found && turnOn)
                {
                    Component added = AddComponent(t, go);
                    if (added != null)
                        comps = new Component[] { added };
                    found = comps.Length > 0;
                }

                if (!found)
                {
                    Debug.LogWarning("[ClientManager] Component '" + compName +
                                     "' not found on '" + go.name + "' or its children.");
                    continue;
                }

                foreach (Component comp in comps)
                {
                    Collider col = comp as Collider;
                    if (col != null) { col.enabled = turnOn; continue; }

                    Rigidbody rb = comp as Rigidbody;
                    if (rb != null)
                    {
                        rb.isKinematic = !turnOn;
                        rb.useGravity = turnOn;
                        rb.detectCollisions = turnOn;
                        continue;
                    }

                    Renderer rendComp = comp as Renderer;
                    if (rendComp != null) { rendComp.enabled = turnOn; continue; }

                    Behaviour beh = comp as Behaviour;
                    if (beh != null) { beh.enabled = turnOn; continue; }
                }
            }
        }

        Debug.Log("[ClientManager] Edited object → " + go.name);
        SendAck("edit", go.name);
    }

    [System.Serializable]
    private class TurnCmd
    {
        public string cmd;
        public string target;
        public float drx, dry, drz;
        public float duration = 1f;
    }

    [System.Serializable]
    private class TweenCmd
    {
        public string cmd;
        public string target;
        public float dx, dy, dz;
        public float drx, dry, drz;
        public float dsx, dsy, dsz;
        public float duration = 1f;
    }

    private void SendAck(string cmd, string label) { StartCoroutine(AckRoutine(cmd, label)); }

    private IEnumerator AckRoutine(string cmd, string label)
    {
        UnityWebRequest req = BuildAck(cmd, label);
        yield return req.SendWebRequest();
    }

    private UnityWebRequest BuildAck(string cmd, string label)
    {
        string safeLabel = label.Replace("\"", "\\\"");
        string body = "{\"event\":\"ack\",\"playerName\":\"" + id.playerName +
                      "\",\"steamID\":\"" + id.steamID +
                      "\",\"cmd\":\"" + cmd +
                      "\",\"label\":\"" + safeLabel + "\"}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string url = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    [System.Serializable]
    private class MeshCmd
    {
        public string cmd;
        public string src;
        public string data;
        public float x, y, z;
        public float rx, ry, rz;
        public float sx = 1, sy = 1, sz = 1;
        public string color;
    }

    [System.Serializable]
    private class MeshDTO
    {
        public float[] v;
        public int[] t;
    }

    private void ApplyMesh(MeshCmd m)
    {
        GameObject proto = GameObject.Find(m.src);
        if (proto == null)
        {
            Debug.LogWarning("[ClientManager] MeshPrototype '" + m.src + "' not found.");
            return;
        }

        GameObject go = Instantiate(proto);
        go.transform.position = new Vector3(m.x, m.y, m.z);
        go.transform.rotation = Quaternion.Euler(m.rx, m.ry, m.rz);
        go.transform.localScale = new Vector3(m.sx, m.sy, m.sz);

        byte[] raw;
        try { raw = System.Convert.FromBase64String(m.data ?? ""); }
        catch { Debug.LogWarning("[ClientManager] Mesh data is not valid base-64."); Destroy(go); return; }

        string j = System.Text.Encoding.UTF8.GetString(raw);
        const string KV = "\"v\":[";
        const string KT = "\"t\":[";
        int v0 = j.IndexOf(KV, System.StringComparison.OrdinalIgnoreCase);
        int t0 = j.IndexOf(KT, System.StringComparison.OrdinalIgnoreCase);
        if (v0 < 0 || t0 < 0) { Debug.LogWarning("[ClientManager] Mesh JSON missing arrays."); Destroy(go); return; }

        v0 += KV.Length;
        int v1 = j.IndexOf(']', v0);
        string[] vParts = j.Substring(v0, v1 - v0).Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);

        if (vParts.Length % 3 != 0) { Debug.LogWarning("[ClientManager] Vert count not divisible by 3."); Destroy(go); return; }
        int vCount = vParts.Length / 3;
        Vector3[] verts = new Vector3[vCount];

        for (int i = 0, v = 0; v < vCount; v++, i += 3)
        {
            float x = float.Parse(vParts[i], System.Globalization.CultureInfo.InvariantCulture);
            float y = float.Parse(vParts[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            float z = float.Parse(vParts[i + 2], System.Globalization.CultureInfo.InvariantCulture);
            verts[v] = new Vector3(x, y, z);
        }

        t0 += KT.Length;
        int t1 = j.IndexOf(']', t0);
        string[] tParts = j.Substring(t0, t1 - t0).Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
        int[] tris = new int[tParts.Length];
        for (int i = 0; i < tParts.Length; i++)
            tris[i] = int.Parse(tParts[i], System.Globalization.CultureInfo.InvariantCulture);

        Mesh mesh = new Mesh();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        MeshFilter mf = go.GetComponent<MeshFilter>() ?? AddComponent(typeof(MeshFilter), go) as MeshFilter;
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.GetComponent<MeshRenderer>() ?? AddComponent(typeof(MeshRenderer), go) as MeshRenderer;
        mr.enabled = true;

        if (!string.IsNullOrEmpty(m.color) && !"none".Equals(m.color))
        {
            string cs = m.color.Length == 6 && !m.color.StartsWith("#") ? "#" + m.color : m.color;
            Color col;
            if (ColorUtility.TryParseHtmlString(cs, out col))
            {
                mr.material = mr.material ?? new Material(Shader.Find("Standard"));
                mr.material.color = col;
            }
        }

        Debug.Log("[ClientManager] Mesh spawned – verts: " + verts.Length + ", tris: " + (tris.Length / 3));
    }

    private void ApplyCreate(CreateCmd c)
    {
        GameObject prototype = GameObject.Find(c.src);
        if (prototype == null)
        {
            foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
                if (g.name == c.src) { prototype = g; break; }
        }

        if (prototype == null)
        {
            Debug.LogWarning("[ClientManager] Create: prototype '" + c.src + "' not found.");
            SendAck("create", c.src);
            return;
        }

        GameObject go = Instantiate(prototype);

        go.transform.position = new Vector3(c.x, c.y, c.z);
        go.transform.rotation = Quaternion.Euler(c.rx, c.ry, c.rz);
        go.transform.localScale = new Vector3(c.sx, c.sy, c.sz);

        if (!string.IsNullOrEmpty(c.rename)) go.name = c.rename;

        if (!string.IsNullOrEmpty(c.color) && !"none".Equals(c.color))
        {
            string cs = c.color.Length == 6 && !c.color.StartsWith("#") ? "#" + c.color : c.color;
            Color col;
            if (ColorUtility.TryParseHtmlString(cs, out col))
            {
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.material == null)
                        r.material = new Material(Shader.Find("Standard"));
                    r.material.color = col;
                }
            }
        }

        if (c.components != null)
        {
            foreach (KeyValuePair<string, bool> kv in c.components)
            {
                string compName = kv.Key;
                bool turnOn = kv.Value;

                System.Type t = FindType(compName);
                if (t == null)
                {
                    Debug.LogWarning("[ClientManager] Unknown component '" + compName + "'");
                    continue;
                }

                Component[] comps = go.GetComponentsInChildren(t, true);
                bool found = comps.Length > 0;

                if (!found && turnOn)
                {
                    AddComponent(t, go);
                    comps = go.GetComponentsInChildren(t, true);
                }

                foreach (Component comp in comps)
                {
                    Collider colComp = comp as Collider;
                    if (colComp != null) { colComp.enabled = turnOn; continue; }

                    Rigidbody rb = comp as Rigidbody;
                    if (rb != null)
                    {
                        rb.isKinematic = !turnOn;
                        rb.useGravity = turnOn;
                        rb.detectCollisions = turnOn;
                        continue;
                    }

                    Renderer rend = comp as Renderer;
                    if (rend != null) { rend.enabled = turnOn; continue; }

                    Behaviour beh = comp as Behaviour;
                    if (beh != null) { beh.enabled = turnOn; continue; }
                }
            }
        }

        Debug.Log("[ClientManager] Spawned object via command → " + go.name);
        SendAck("create", go.name);
    }

    private static Dictionary<string, bool> ParseComponents(string json)
    {
        Dictionary<string, bool> map = new Dictionary<string, bool>();
        const string key = "\"components\":{";
        int idx = json.IndexOf(key);
        if (idx == -1) return map;

        idx += key.Length;
        int depth = 1;

        while (idx < json.Length && depth > 0)
        {
            while (idx < json.Length && json[idx] != '"') { if (json[idx] == '}') return map; idx++; }
            if (++idx >= json.Length) break;
            int start = idx;
            while (idx < json.Length && json[idx] != '"')
            {
                if (json[idx] == '\\') idx++;
                idx++;
            }
            if (idx >= json.Length) break;
            string name = json.Substring(start, idx - start);
            idx++;
            while (idx < json.Length && json[idx] != ':') idx++;
            if (++idx >= json.Length) break;
            while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;

            bool val;
            if (json.Substring(idx).StartsWith("true"))
            {
                val = true; idx += 4;
            }
            else if (json.Substring(idx).StartsWith("false"))
            {
                val = false; idx += 5;
            }
            else
            {
                break;
            }
            map[name] = val;
            while (idx < json.Length && json[idx] != ',' && json[idx] != '}') idx++;
            if (idx < json.Length && json[idx] == ',') idx++;
            if (idx < json.Length && json[idx] == '}') depth--;
        }
        return map;
    }

    private static System.Type FindType(string shortName)
    {
        switch (shortName)
        {
            case "SphereCollider": return typeof(SphereCollider);
            case "BoxCollider": return typeof(BoxCollider);
            case "CapsuleCollider": return typeof(CapsuleCollider);
            case "MeshCollider": return typeof(MeshCollider);
            case "MeshRenderer": return typeof(MeshRenderer);
            case "SkinnedMeshRenderer": return typeof(SkinnedMeshRenderer);
            case "TrailRenderer": return typeof(TrailRenderer);
            case "Rigidbody": return typeof(Rigidbody);
            case "AudioSource": return typeof(AudioSource);
            case "Light": return typeof(Light);
            case "Camera": return typeof(Camera);
            case "Animator": return typeof(Animator);
            case "ParticleSystem": return typeof(ParticleSystem);
            default: return null;
        }
    }

    private UnityWebRequest BuildObjects(string data)
    {
        string url = "http://" + serverIp + ":" + serverPort + "/";
        string body = "{\"event\":\"objects\",\"playerName\":\"" + id.playerName
                + "\",\"steamID\":\"" + id.steamID + "\",\"data\":" + data + "}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    private string GatherSceneObjects()
    {
        StringBuilder sb = new StringBuilder(16384).Append('[');
        bool first = true;

        Stack<GameObject> stack = new Stack<GameObject>();
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            stack.Push(root);

        while (stack.Count > 0)
        {
            GameObject go = stack.Pop();
            if (go == null || !go.activeInHierarchy) continue;

            if (!first) sb.Append(',');
            first = false;

            Transform t = go.transform;
            Vector3 p = t.position;
            Vector3 r = t.eulerAngles;

            Transform parent = t.parent;
            int parentId = parent ? parent.gameObject.GetInstanceID() : 0;
            string parentNm = parent ? parent.gameObject.name : "";

            Component[] comps = go.GetComponents<Component>();
            string txt = null;
            List<string> compNames = new List<string>(comps.Length);

            foreach (Component c in comps)
            {
                string cn = "null";
                if (c != null)
                {
                    if (txt == null)
                    {
                        TextMesh tm = c as TextMesh;
                        if (tm != null) txt = tm.text;

                        Text uiTxt = c as Text;
                        if (uiTxt != null) txt = uiTxt.text;

                        InputField ifld = c as InputField;
                        if (ifld != null) txt = ifld.text;
                    }

                    cn = c.ToString();
                    int paren = cn.IndexOf('(');
                    if (paren > 0) cn = cn.Substring(0, paren).Trim();
                }
                compNames.Add(cn);
            }

            sb.Append("{\"id\":").Append(go.GetInstanceID())
              .Append(",\"parentId\":").Append(parentId)
              .Append(",\"parentName\":\"").Append(parentNm.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"')
              .Append(",\"name\":\"").Append(go.name.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"')
              .Append(",\"x\":").Append(p.x).Append(",\"y\":").Append(p.y).Append(",\"z\":").Append(p.z)
              .Append(",\"rx\":").Append(r.x).Append(",\"ry\":").Append(r.y).Append(",\"rz\":").Append(r.z)
              .Append(",\"components\":[");

            for (int i = 0; i < compNames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(compNames[i]).Append('"');
            }
            sb.Append(']');

            if (txt != null)
                sb.Append(",\"text\":\"").Append(txt.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');

            sb.Append('}');

            for (int i = 0; i < t.childCount; i++)
                stack.Push(t.GetChild(i).gameObject);
        }
        sb.Append(']');
        return sb.ToString();
    }

    [System.Serializable]
    public class Identification
    {
        public string playerName = "Ghost";
        public string steamID = "Unknown";
    }

    private static Identification FetchId()
    {
        GameObject g = GameObject.Find("PlayerInfo_Human");
        if (g == null) return null;
        Component c = g.GetComponent("PlayerInfoImpact");
        if (c == null) return null;

        string raw = JsonUtility.ToJson(c)
                .Replace("a^sXf\u0083Y", "playerName")
                .Replace("r~x\u007Fs{n", "steamID");
        return JsonUtility.FromJson<Identification>(raw);
    }

    private static Component AddComponent(System.Type type, GameObject host)
    {
        if (type == null || host == null)
            return null;

        Component existing = host.GetComponent(type);
        if (existing != null)
        {
            MonoBehaviour mb = existing as MonoBehaviour;
            if (mb != null) mb.enabled = true;
            return existing;
        }

        try
        {
            return host.AddComponent(type);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ClientManager] Failed to add '{type}' to '{host.name}': {ex.Message}");
            return null;
        }
    }
}
