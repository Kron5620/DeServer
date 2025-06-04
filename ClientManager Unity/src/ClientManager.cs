using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using ModTool.Interface;

public class ClientManager : ModBehaviour
{
    [Header("Server Settings")]
    public string serverIp        = "127.0.0.1";
    public int    serverPort      = 1;
    public int    timeoutSec      = 2;
    [Tooltip("Seconds between full scene-object snapshots")]
    public float  objectsInterval = 1f;

    private bool lastPaused;

    private Identification id;
    private bool           connected;
    private GameObject     playerGO;

    [System.Serializable] private class CommandList { public string[] commands; }

    [System.Serializable] private class CreateCmd
    {
        public string  cmd;
        public string  src;
        public float   x,  y,  z;
        public float   rx, ry, rz;
        public string  color;
        public string  rename;
        public float   sx = 1, sy = 1, sz = 1;
        public Dictionary<string,bool> components;
    }

    private void Start()
    {
        id = FetchId();
        if (id == null)
        {
            id = new Identification { playerName = "Ghost", steamID = "Unknown" };
            Debug.LogWarning("[ClientManager] Steam info missing – defaulting to Ghost/Unknown.");
        }

        StartCoroutine(NetLoop());
        StartCoroutine(CommandLoop());
        StartCoroutine(PauseLoop());
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
        string body = "{\"event\":\"pause\""
                + ",\"state\":\"" + (paused ? "on" : "off") + "\""
                + ",\"playerName\":\"" + id.playerName + "\""
                + ",\"steamID\":\"" + id.steamID + "\"}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string url   = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    private void OnApplicationQuit() { PostBlocking("disconnect", null, null); }

    private IEnumerator NetLoop()
    {
        WaitForSeconds retry    = new WaitForSeconds(1f);
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

    private UnityWebRequest Build(string evt,
                                  Vector3? pos,
                                  Vector3? rot,
                                  Vector3? camPos)
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
        string url  = "http://" + serverIp + ":" + serverPort + "/";
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(body);
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

    [System.Serializable] private class EditCmd
    {
        public string  cmd;
        public string  target;
        public bool    delete;
        public float   x,  y,  z;
        public float   rx, ry, rz;
        public float   sx = 1, sy = 1, sz = 1;
        public string  color;
        public string  copytex;
        public string  rename;
        public float   vx, vy, vz;
        public Dictionary<string,bool> components;
    }

    private IEnumerator CommandLoop()
    {
        WaitForSeconds wait = new WaitForSeconds(0.5f);
        string url = "http://" + serverIp + ":" + serverPort + "/cmd?steamID="
                + UnityWebRequest.EscapeURL(id.steamID);

        while (true)
        {
            UnityWebRequest req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
            yield return req.SendWebRequest();

            if (!req.isNetworkError && !req.isHttpError && !string.IsNullOrEmpty(req.downloadHandler.text))
            {
                CommandList cl = JsonUtility.FromJson<CommandList>(req.downloadHandler.text);
                if (cl != null && cl.commands != null)
                {
                    foreach (string raw in cl.commands) ApplyCommand(raw);
                }
            }
            yield return wait;
        }
    }

    private void ApplyCommand(string json)
    {
        if (string.IsNullOrEmpty(json))
            return;

        if (json.IndexOf("\"cmd\":\"create\"") != -1)
        {
            CreateCmd c = JsonUtility.FromJson<CreateCmd>(json);
            if (c != null && c.components == null)
                c.components = ParseComponents(json);
            if (c != null) ApplyCreate(c);
            return;
        }

        if (json.IndexOf("\"cmd\":\"mesh\"") != -1)
        {
            MeshCmd m = JsonUtility.FromJson<MeshCmd>(json);
            if (m != null) ApplyMesh(m);
            return;
        }

        if (json.IndexOf("\"cmd\":\"edit\"") != -1)
        {
            EditCmd e = JsonUtility.FromJson<EditCmd>(json);
            if (e != null && e.components == null)
                e.components = ParseComponents(json);
            if (e != null) ApplyEdit(e);
            return;
        }

        if (json.IndexOf("\"cmd\":\"tween\"") != -1)
        {
            TweenCmd t = JsonUtility.FromJson<TweenCmd>(json);
            if (t != null) ApplyTween(t);
            return;
        }

        if (json.IndexOf("\"cmd\":\"turn\"") != -1)
        {
            TurnCmd old = JsonUtility.FromJson<TurnCmd>(json);
            if (old != null) ApplyTurn(old);
            return;
        }
    }

    private void ApplyTween(TweenCmd t)
    {
        GameObject go = GameObject.Find(t.target);
        if (go == null)
        {
            Debug.LogWarning("[ClientManager] Tween: target '" + t.target + "' not found.");
            return;
        }

        Vector3 startPos   = go.transform.position;
        Vector3 endPos     = startPos + new Vector3(t.dx,  t.dy,  t.dz);

        Vector3 startEuler = go.transform.eulerAngles;
        Vector3 endEuler   = startEuler + new Vector3(t.drx, t.dry, t.drz);

        Vector3 startScale = go.transform.localScale;
        Vector3 endScale   = startScale + new Vector3(t.dsx, t.dsy, t.dsz);

        bool move = t.dx != 0f || t.dy != 0f || t.dz != 0f;
        bool turn = t.drx!= 0f || t.dry!= 0f || t.drz!= 0f;
        bool size = t.dsx!= 0f || t.dsy!= 0f || t.dsz!= 0f;

        if (!move && !turn && !size)
        {
            SendAck("tween", go.name);
            return;
        }

        if (t.duration <= 0f)
        {
            if (move) go.transform.position   = endPos;
            if (turn) go.transform.eulerAngles = endEuler;
            if (size) go.transform.localScale = endScale;
            SendAck("tween", go.name);
            return;
        }

        StartCoroutine(TweenRoutine(go,
                                    move ? (Vector3?)endPos   : null,
                                    turn ? (Vector3?)endEuler : null,
                                    size ? (Vector3?)endScale : null,
                                    t.duration,
                                    go.name));
    }

    private IEnumerator TweenRoutine(GameObject go,
                                     Vector3?   finalPos,
                                     Vector3?   finalEuler,
                                     Vector3?   finalScale,
                                     float      duration,
                                     string     label)
    {
        Vector3 pStart = go.transform.position;
        Vector3 eStart = go.transform.eulerAngles;
        Vector3 sStart = go.transform.localScale;

        float elapsed = 0f;

        while (elapsed < duration && go != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            if (finalPos   != null)
                go.transform.position   = Vector3.Lerp(pStart, finalPos.Value,   t);

            if (finalEuler != null)
                go.transform.eulerAngles = Vector3.Lerp(eStart, finalEuler.Value, t);

            if (finalScale != null)
                go.transform.localScale = Vector3.Lerp(sStart, finalScale.Value, t);

            yield return null;
        }

        if (go != null)
        {
            if (finalPos   != null) go.transform.position   = finalPos.Value;
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
        Quaternion end   = start * Quaternion.Euler(deltaEuler);
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

        bool isPlayer  = go.name == "Player_Human";
        bool needsMove = isPlayer || e.x != 0f || e.y != 0f || e.z != 0f;

        if (needsMove)
        {
            Vector3 newPos = new Vector3(e.x, e.y, e.z);

            CharacterController cc = go.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            go.transform.position = newPos;

            if (cc != null) cc.enabled = true;

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        if (e.rx != 0f || e.ry != 0f || e.rz != 0f)
            go.transform.rotation = Quaternion.Euler(e.rx, e.ry, e.rz);

        if (e.sx != 1f || e.sy != 1f || e.sz != 1f)
            go.transform.localScale = new Vector3(e.sx, e.sy, e.sz);

        if (!string.IsNullOrEmpty(e.rename))
            go.name = e.rename;

        if (!string.IsNullOrEmpty(e.color) && !"none".Equals(e.color))
        {
            string cs = e.color.Length == 6 && !e.color.StartsWith("#") ? "#" + e.color : e.color;
            Color col;
            if (ColorUtility.TryParseHtmlString(cs, out col))
            {
                Renderer r = go.GetComponent<Renderer>();
                if (r != null)
                {
                    if (r.material == null)
                        r.material = new Material(Shader.Find("Standard"));
                    r.material.color = col;
                }
            }
        }

        if (!string.IsNullOrEmpty(e.copytex))
        {
            GameObject src = GameObject.Find(e.copytex);
            if (src != null)
            {
                Renderer sr = src.GetComponent<Renderer>();
                Renderer tr = go.GetComponent<Renderer>();
                if (sr != null && tr != null && sr.material != null)
                    tr.material = sr.material;
            }
        }

        if (e.components != null)
        {
            foreach (KeyValuePair<string, bool> kv in e.components)
            {
                string compName = kv.Key;
                bool   turnOn   = kv.Value;

                System.Type t = FindType(compName);
                if (t == null)
                {
                    Debug.LogWarning("[ClientManager] Unknown component '" + compName + "'");
                    continue;
                }

                Component comp = go.GetComponent(t);
                if (comp == null) comp = AddComponent(t, go);
                if (comp == null) continue;

                Collider col = comp as Collider;
                if (col != null) { col.enabled = turnOn; continue; }

                Rigidbody rb = comp as Rigidbody;
                if (rb != null)
                {
                    rb.isKinematic      = !turnOn;
                    rb.useGravity       = turnOn;
                    rb.detectCollisions = turnOn;
                    continue;
                }

                Renderer rendComp = comp as Renderer;
                if (rendComp != null) { rendComp.enabled = turnOn; continue; }

                Behaviour behComp = comp as Behaviour;
                if (behComp != null) { behComp.enabled = turnOn; continue; }
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
        public float  drx, dry, drz;
        public float  duration = 1f;
    }

    [System.Serializable]
    private class TweenCmd
    {
        public string cmd;
        public string target;
        public float  dx,  dy,  dz;
        public float  drx, dry, drz;
        public float  dsx, dsy, dsz;
        public float  duration = 1f;
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
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    [System.Serializable] private class MeshCmd
    {
        public string cmd;
        public string src;
        public string data;
        public float  x,  y,  z;
        public float  rx, ry, rz;
        public float  sx = 1, sy = 1, sz = 1;
        public string color;
    }

    [System.Serializable] private class MeshDTO
    {
        public float[] v;
        public int[]   t;
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

        go.transform.position   = new Vector3(m.x,  m.y,  m.z);
        go.transform.rotation   = Quaternion.Euler(m.rx, m.ry, m.rz);
        go.transform.localScale = new Vector3(m.sx, m.sy, m.sz);

        byte[] raw  = System.Convert.FromBase64String(m.data);
        string json = System.Text.Encoding.UTF8.GetString(raw);
        MeshDTO dto = JsonUtility.FromJson<MeshDTO>(json);
        if (dto == null || dto.v == null || dto.t == null)
        {
            Debug.LogWarning("[ClientManager] Bad mesh data.");
            Destroy(go);
            return;
        }

        int vCount = dto.v.Length / 3;
        Vector3[] verts = new Vector3[vCount];
        for (int i = 0, v = 0; v < vCount; v++, i += 3)
            verts[v] = new Vector3(dto.v[i], dto.v[i + 1], dto.v[i + 2]);

        Mesh mesh = new Mesh();
        mesh.vertices  = verts;
        mesh.triangles = dto.t;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf == null)
            mf = AddComponent(typeof(MeshFilter), go) as MeshFilter;
        mf.sharedMesh = mesh;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr == null)
            mr = AddComponent(typeof(MeshRenderer), go) as MeshRenderer;
        mr.enabled = true;

        if (!string.IsNullOrEmpty(m.color) && !"none".Equals(m.color))
        {
            string cs = m.color.Length == 6 && !m.color.StartsWith("#") ? "#" + m.color : m.color;
            Color col;
            if (ColorUtility.TryParseHtmlString(cs, out col))
            {
                if (mr.material == null)
                    mr.material = new Material(Shader.Find("Standard"));
                mr.material.color = col;
            }
        }

        Debug.Log("[ClientManager] Mesh spawned – verts: " + vCount + ", tris: "
                + dto.t.Length / 3);
    }

    private void ApplyCreate(CreateCmd c)
    {
        GameObject prototype = GameObject.Find(c.src);
        if (prototype == null)
        {
            Debug.LogWarning("[ClientManager] Prototype '" + c.src + "' not found – command ignored.");
            return;
        }

        GameObject go = Instantiate(prototype);

        go.transform.position   = new Vector3(c.x,  c.y,  c.z);
        go.transform.rotation   = Quaternion.Euler(c.rx, c.ry, c.rz);
        go.transform.localScale = new Vector3(c.sx, c.sy, c.sz);

        if (!string.IsNullOrEmpty(c.rename)) go.name = c.rename;

        if (!string.IsNullOrEmpty(c.color) && !"none".Equals(c.color))
        {
            string cs = c.color.Length == 6 && !c.color.StartsWith("#") ? "#" + c.color : c.color;

            Color col;
            if (ColorUtility.TryParseHtmlString(cs, out col))
            {
                Renderer r = go.GetComponent<Renderer>();
                if (r != null)
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
                bool   turnOn   = kv.Value;

                System.Type t = FindType(compName);
                if (t == null)
                {
                    Debug.LogWarning("[ClientManager] Unknown component '" + compName + "'");
                    continue;
                }

                Component comp = go.GetComponent(t);
                if (comp == null) comp = AddComponent(t, go);
                if (comp == null) continue;

                Collider col = comp as Collider;
                if (col != null) { if (turnOn) col.enabled = true; else Destroy(col); continue; }

                Rigidbody rb = comp as Rigidbody;
                if (rb != null) {
                    if (turnOn) { rb.isKinematic=false; rb.useGravity=true; rb.detectCollisions=true; }
                    else        { rb.isKinematic=true;  rb.useGravity=false; rb.detectCollisions=false; }
                    continue;
                }

                Renderer rend = comp as Renderer;
                if (rend != null) { rend.enabled = turnOn; continue; }

                Behaviour beh = comp as Behaviour;
                if (beh != null) { beh.enabled = turnOn; continue; }
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
                val = true;  idx += 4;
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
            case "SphereCollider":      return typeof(SphereCollider);
            case "BoxCollider":         return typeof(BoxCollider);
            case "CapsuleCollider":     return typeof(CapsuleCollider);
            case "MeshCollider":        return typeof(MeshCollider);
            case "MeshRenderer":        return typeof(MeshRenderer);
            case "SkinnedMeshRenderer": return typeof(SkinnedMeshRenderer);
            case "TrailRenderer":       return typeof(TrailRenderer);
            case "Rigidbody":           return typeof(Rigidbody);
            case "AudioSource":         return typeof(AudioSource);
            case "Light":               return typeof(Light);
            case "Camera":              return typeof(Camera);
            case "Animator":            return typeof(Animator);
            case "ParticleSystem":      return typeof(ParticleSystem);
            default:                    return null;
        }
    }

    private UnityWebRequest BuildObjects(string data)
    {
        string url  = "http://" + serverIp + ":" + serverPort + "/";
        string body = "{\"event\":\"objects\",\"playerName\":\"" + id.playerName
                + "\",\"steamID\":\"" + id.steamID + "\",\"data\":" + data + "}";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSec;
        return req;
    }

    private string GatherSceneObjects()
    {
        StringBuilder sb = new StringBuilder(8192).Append('[');
        bool first = true;
        Stack<GameObject> stack = new Stack<GameObject>();

        GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++) stack.Push(roots[i]);

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
            int    parentId = parent ? parent.gameObject.GetInstanceID() : 0;
            string parentNm = parent ? parent.gameObject.name : "";

            sb.Append("{\"id\":").Append(go.GetInstanceID())
              .Append(",\"parentId\":").Append(parentId)
              .Append(",\"parentName\":\"").Append(parentNm.Replace("\"","\\\"")).Append('"')
              .Append(",\"name\":\"").Append(go.name.Replace("\"","\\\"")).Append('"')
              .Append(",\"x\":").Append(p.x).Append(",\"y\":").Append(p.y).Append(",\"z\":").Append(p.z)
              .Append(",\"rx\":").Append(r.x).Append(",\"ry\":").Append(r.y).Append(",\"rz\":").Append(r.z)
              .Append(",\"components\":[");

            Component[] comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                string cn = "null";
                if (comps[i] != null)
                {
                    cn = comps[i].ToString();
                    int paren = cn.IndexOf('(');
                    if (paren > 0) cn = cn.Substring(0, paren).Trim();
                }
                sb.Append('"').Append(cn).Append('"');
            }
            sb.Append("]}");

            for (int i = 0; i < t.childCount; i++)
                stack.Push(t.GetChild(i).gameObject);
        }
        sb.Append(']');
        return sb.ToString();
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
}
