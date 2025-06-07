package club.kron.pumpin;

import javax.swing.*;
import java.awt.*;
import java.io.BufferedReader;
import java.io.File;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.PrintWriter;
import java.net.*;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.List;

public class Main {

    private static final String PLAYERS_DIR = "Players";
    private static final String PROP_FILE = "server.properties";

    private static final String DEFAULT_IP = "0.0.0.0";
    private static final int DEFAULT_PORT = 19299;

    private static final java.util.Set<String> pausedClients =
            java.util.Collections.synchronizedSet(new java.util.HashSet<>());

    private static final java.util.Map<String, String> cameraPositions =
            new java.util.concurrent.ConcurrentHashMap<>();

    private static String bindIp = DEFAULT_IP;
    private static int bindPort = DEFAULT_PORT;

    private static final java.util.Map<String, java.util.Queue<String>> inputEvents =
            new java.util.concurrent.ConcurrentHashMap<>();

    public static void addInputEvent(String steamID, String key) {
        if (key == null || key.isEmpty()) return;
        inputEvents
                .computeIfAbsent(steamID, k -> new java.util.concurrent.ConcurrentLinkedQueue<>())
                .add(key);
    }

    public static java.util.List<String> pollInputs(String steamID) {
        java.util.Queue<String> q = inputEvents.get(steamID);
        if (q == null || q.isEmpty()) return java.util.Collections.emptyList();

        java.util.List<String> list = new java.util.ArrayList<>();
        for (String k; (k = q.poll()) != null; ) list.add(k);
        return list;
    }

    private static final java.util.Map<String, Long> clientLastSeen =
            new java.util.concurrent.ConcurrentHashMap<>();
    private static final java.util.Map<String, String> playerPositions =
            new java.util.concurrent.ConcurrentHashMap<>();
    private static final java.util.Map<String, String> playerRotations =
            new java.util.concurrent.ConcurrentHashMap<>();
    private static final long TIMEOUT_MS = 10_000;

    private static final java.util.Map<String, String> playerObjects =
            new java.util.concurrent.ConcurrentHashMap<>();

    private static boolean guiMode;

    private static final java.util.Set<String> runningClients =
            java.util.Collections.synchronizedSet(new java.util.HashSet<>());

    private static final java.util.Map<String, java.util.Queue<String>> pendingCommands =
            new java.util.concurrent.ConcurrentHashMap<>();

    private static final java.util.Set<String> activeClients =
            java.util.Collections.synchronizedSet(new java.util.HashSet<>());

    private static JTextArea logArea;
    private static JFrame frame;

    private static ServerSocket serverSocket;

    public static void main(String[] args) {

        guiMode = !GraphicsEnvironment.isHeadless();

        for (String arg : args) {
            if ("nogui".equalsIgnoreCase(arg.trim())) {
                guiMode = false;
            } else if (arg.startsWith("--port=")) {
                try {
                    int p = Integer.parseInt(arg.substring("--port=".length()));
                    if (p < 1 || p > 65535) throw new NumberFormatException();
                    bindPort = p;
                } catch (NumberFormatException ex) {
                    System.err.println("[WARN] Invalid port '" +
                            arg.substring("--port=".length()) + "'. Using default " + DEFAULT_PORT + '.');
                    bindPort = DEFAULT_PORT;
                }
            }
        }

        if (guiMode)
            javax.swing.SwingUtilities.invokeLater(Main::createAndShowGui);

        handleServerProperties();
        log("[INFO] Server starting …");
        log("[INFO] Binding to IP: " + bindIp + ", port: " + bindPort);
        handlePlayersFolder();

        ExtensionManager.loadAll();

        startListeningThread();
        startTimeoutMonitor();

        if (!guiMode) {
            log("[INFO] Console mode – type 'stop' to exit.");
            consoleCommandLoop();
        } else {
            log("[INFO] GUI mode – press 'Stop Server' to exit.");
        }
    }

    public static void enqueueCommand(String steamID, String cmdJson) {
        pendingCommands
                .computeIfAbsent(steamID, k -> new java.util.concurrent.ConcurrentLinkedQueue<>())
                .add(cmdJson);
    }

    private static String dequeueCommandsJson(String steamID) {
        java.util.Queue<String> q = pendingCommands.get(steamID);
        if (q == null || q.isEmpty()) return "[]";

        StringBuilder sb = new StringBuilder("[");
        boolean first = true;
        for (String cmd; (cmd = q.poll()) != null; ) {
            if (!first) sb.append(',');
            first = false;

            String escaped = cmd
                    .replace("\\", "\\\\")
                    .replace("\"", "\\\"");

            sb.append('"').append(escaped).append('"');
        }
        sb.append(']');
        return sb.toString();
    }

    private static void respondJson(PrintWriter out, String body) {
        out.print("HTTP/1.1 200 OK\r\n");
        out.print("Content-Type: application/json; charset=UTF-8\r\n");
        out.print("Content-Length: " + body.getBytes().length + "\r\n");
        out.print("Connection: close\r\n\r\n");
        out.print(body);
        out.flush();
    }

    private static void handleServerProperties() {
        File propFile = new File(PROP_FILE);

        boolean needsRewrite = false;
        String fileIp = null;
        Integer filePort = null;

        if (propFile.exists()) {
            try {
                List<String> lines = Files.readAllLines(Paths.get(PROP_FILE));
                for (String line : lines) {
                    line = line.trim();
                    if (line.startsWith("server-ip=")) {
                        fileIp = line.substring("server-ip=".length()).trim();
                    } else if (line.startsWith("server-port=")) {
                        try {
                            filePort = Integer.parseInt(
                                    line.substring("server-port=".length()).trim()
                            );
                        } catch (NumberFormatException ignored) {
                        }
                    }
                }

                if (fileIp == null || fileIp.isEmpty()) {
                    needsRewrite = true;
                    fileIp = DEFAULT_IP;
                }
                if (filePort == null || filePort < 1 || filePort > 65535) {
                    needsRewrite = true;
                    filePort = DEFAULT_PORT;
                }

            } catch (IOException e) {
                log("[WARN] Failed to read existing " + PROP_FILE
                        + ": " + e.getMessage() + ". Recreating file.");
                needsRewrite = true;
                fileIp = DEFAULT_IP;
                filePort = DEFAULT_PORT;
            }
        } else {
            needsRewrite = true;
            fileIp = DEFAULT_IP;
            filePort = DEFAULT_PORT;
        }

        if (filePort != null && filePort != bindPort) {
            filePort = bindPort;
            needsRewrite = true;
        }

        bindIp = fileIp;
        bindPort = filePort;

        if (needsRewrite) {
            try (PrintWriter pw = new PrintWriter(propFile)) {
                pw.println("server-ip=" + bindIp);
                pw.println("server-port=" + bindPort);
                log("[INFO] Wrote default " + PROP_FILE
                        + " with server-ip=" + bindIp
                        + " and server-port=" + bindPort);
            } catch (IOException e) {
                log("[ERROR] Could not write " + PROP_FILE + ": " + e.getMessage());
            }
        } else {
            log("[INFO] Found existing " + PROP_FILE
                    + " with server-ip=" + bindIp
                    + " and server-port=" + bindPort
                    + " (no rewrite needed)");
        }
    }

    private static void handlePlayersFolder() {
        ensureFolder("player-data");
        ensureFolder("config");
        ensureFolder("extensions");
        ensureFolder("world");
        log("[INFO] Server folder setup complete at " + nowTimestamp());
    }

    private static void ensureFolder(String folderName) {
        File dir = new File(folderName);
        if (!dir.exists()) {
            try {
                Files.createDirectory(dir.toPath());
                log("[INFO] '" + folderName + "' folder created");
            } catch (IOException e) {
                log("[ERROR] Could not create '" + folderName + "' folder: " + e.getMessage());
            }
        } else {
            log("[INFO] '" + folderName + "' folder already exists (skipping creation)");
        }
    }

    private static void startListeningThread() {
        Thread t = new Thread(() -> {
            try {
                try {
                    serverSocket = new ServerSocket(bindPort, 50, InetAddress.getByName(bindIp));
                } catch (IOException e) {
                    log("[WARN] Could not bind to " + bindIp + ":" + bindPort +
                            " (" + e.getMessage() + "). Falling back to 0.0.0.0");
                    serverSocket = new ServerSocket(bindPort);
                }
                log("[INFO] HTTP server listening on " +
                        serverSocket.getInetAddress().getHostAddress() + ":" + bindPort);

                while (!serverSocket.isClosed()) {
                    Socket client = serverSocket.accept();
                    String clientIp = client.getInetAddress().getHostAddress();

                    try (BufferedReader in = new BufferedReader(
                            new InputStreamReader(client.getInputStream(), "UTF-8"));
                         PrintWriter out = new PrintWriter(client.getOutputStream())) {

                        String reqLine = in.readLine();
                        if (reqLine == null) {
                            client.close();
                            continue;
                        }

                        String[] p = reqLine.split(" ");
                        String method = p[0];
                        String path   = p.length > 1 ? p[1] : "/";
                        boolean isPost = "POST".equalsIgnoreCase(method);

                        if (!isPost && path.startsWith("/cmd?steamID=")) {
                            while (in.readLine() != null && !in.readLine().isEmpty()) {}
                            String sid = URLDecoder.decode(
                                    path.substring("/cmd?steamID=".length()), "UTF-8");
                            String json = "{\"commands\":" + dequeueCommandsJson(sid) + "}";
                            respondJson(out, json);
                            continue;
                        }

                        int contentLen = 0;
                        for (String h; (h = in.readLine()) != null && !h.isEmpty(); ) {
                            if (h.regionMatches(true, 0, "content-length:", 0, 15)) {
                                try { contentLen = Integer.parseInt(h.substring(15).trim()); }
                                catch (NumberFormatException ignore) {}
                            }
                        }

                        String body = "";
                        if (isPost && contentLen > 0) {
                            char[] buf = new char[contentLen];
                            int read = 0;
                            while (read < contentLen) {
                                int n = in.read(buf, read, contentLen - read);
                                if (n == -1) break;
                                read += n;
                            }
                            body = new String(buf, 0, read);
                        }

                        if (isPost && !body.isEmpty()) {
                            String evt        = extractJson(body, "event").toLowerCase();
                            String playerName = extractJson(body, "playerName");
                            String steamID    = extractJson(body, "steamID");

                            if (playerName.isEmpty()) playerName = "Ghost";
                            if (steamID.isEmpty())    steamID    = "Unknown";

                            String key = clientIp + "|" + steamID + "|" + playerName;
                            if (!"disconnect".equals(evt))
                                clientLastSeen.put(key, System.currentTimeMillis());

                            switch (evt) {

                                case "axis": {
                                    String axis = extractJson(body, "axis");
                                    String val  = extractJson(body, "val");
                                    addInputEvent(steamID, "AXIS:" + axis + ':' + val);
                                    break;
                                }

                                case "input": {
                                    String keyName = extractJson(body, "key");
                                    addInputEvent(steamID, keyName);
                                    break;
                                }

                                case "pos": {
                                    try {
                                        double x  = Double.parseDouble(extractJson(body, "x"));
                                        double y  = Double.parseDouble(extractJson(body, "y"));
                                        double z  = Double.parseDouble(extractJson(body, "z"));
                                        double rx = Double.parseDouble(extractJson(body, "rx"));
                                        double ry = Double.parseDouble(extractJson(body, "ry"));
                                        double rz = Double.parseDouble(extractJson(body, "rz"));

                                        String pos = x + "," + y + "," + z;
                                        String rot = rx + "," + ry + "," + rz;
                                        playerPositions.put(steamID, pos);
                                        playerRotations.put(steamID, rot);

                                        String camX = extractJson(body, "camx");
                                        if (!camX.isEmpty()) {
                                            double cx = Double.parseDouble(camX);
                                            double cy = Double.parseDouble(extractJson(body, "camy"));
                                            double cz = Double.parseDouble(extractJson(body, "camz"));
                                            cameraPositions.put(steamID, cx + "," + cy + "," + cz);
                                        }
                                    } catch (NumberFormatException ignore) {}
                                    break;
                                }

                                case "ack": {
                                    String cmdType = extractJson(body, "cmd");
                                    String label   = extractJson(body, "label");
                                    if (!SUPPRESS_ACK_LABELS.contains(label))
                                        log("[INFO] Confirmed " + cmdType +
                                                " → '" + label + "' for SteamID=" + steamID);
                                    break;
                                }

                                case "objects": {
                                    String data = extractJson(body, "data");
                                    playerObjects.put(steamID, data);

                                    if (!runningClients.contains(steamID) &&
                                            data.contains("\"name\":\"Player_Human\"")) {
                                        runningClients.add(steamID);
                                        log("[INFO] Running state      from " + clientIp +
                                                " | Name=\"" + playerName + "\", SteamID=" + steamID);
                                    }
                                    writePlayerData(steamID, playerName, clientIp,
                                            playerPositions.get(steamID),
                                            playerRotations.get(steamID));
                                    break;
                                }

                                case "disconnect": {
                                    log("[INFO] Disconnect        from " + clientIp +
                                            " | Name=\"" + playerName + "\", SteamID=" + steamID);
                                    clientLastSeen.remove(key);
                                    playerPositions.remove(steamID);
                                    playerRotations.remove(steamID);
                                    playerObjects.remove(steamID);
                                    cameraPositions.remove(steamID);
                                    runningClients.remove(steamID);
                                    pausedClients.remove(steamID);
                                    activeClients.remove(key);
                                    break;
                                }

                                case "pause": {
                                    String state = extractJson(body, "state").toLowerCase();
                                    boolean on = "on".equals(state) || "true".equals(state) || "1".equals(state);
                                    if (on) {
                                        if (pausedClients.add(steamID))
                                            log("[INFO] Pause state        from " + clientIp +
                                                    " | Name=\"" + playerName + "\", SteamID=" + steamID);
                                    } else {
                                        if (pausedClients.remove(steamID))
                                            log("[INFO] Resume             from " + clientIp +
                                                    " | Name=\"" + playerName + "\", SteamID=" + steamID);
                                    }
                                    break;
                                }

                                default: break;
                            }

                            if (!activeClients.contains(key) && !"disconnect".equals(evt)) {
                                activeClients.add(key);
                                log("[INFO] Connect           from " + clientIp +
                                        " | Name=\"" + playerName + "\", SteamID=" + steamID);
                            }
                            respond(out, "OK");
                        } else {
                            respond(out, "Hello from Custom Server Stub – " + nowTimestamp());
                        }

                    } catch (IOException ex) {
                        log("[WARN] Error handling client " + clientIp + ": " + ex.getMessage());
                    } finally {
                        try { client.close(); } catch (IOException ignore) {}
                    }
                }
            } catch (IOException e) {
                log("[ERROR] ServerSocket error: " + e.getMessage());
            }
        });
        t.setDaemon(true);
        t.start();
    }

    private static void respondForbidden(PrintWriter out) {
        String body = "Forbidden";
        out.print("HTTP/1.1 403 Forbidden\r\n");
        out.print("Content-Type: text/plain; charset=UTF-8\r\n");
        out.print("Content-Length: " + body.getBytes().length + "\r\n");
        out.print("Connection: close\r\n\r\n");
        out.print(body);
        out.flush();
    }

    private static void handleTeleport(String rawLine) {
        String[] tok = rawLine.split("\\s+");
        if (tok.length != 5) {
            log("[WARN] Usage: tp <steamID> <x> <y> <z>");
            return;
        }
        String sid = tok[1];
        double x, y, z;
        try {
            x = Double.parseDouble(tok[2]);
            y = Double.parseDouble(tok[3]);
            z = Double.parseDouble(tok[4]);
        } catch (NumberFormatException ex) {
            log("[WARN] Coordinates must be numbers.");
            return;
        }

        double dx = 0, dy = 0, dz = 0;
        String p = playerPositions.get(sid), c = cameraPositions.get(sid);
        if (p != null && c != null) {
            try {
                String[] pp = p.split(","), cc = c.split(",");
                dx = Double.parseDouble(cc[0]) - Double.parseDouble(pp[0]);
                dy = Double.parseDouble(cc[1]) - Double.parseDouble(pp[1]);
                dz = Double.parseDouble(cc[2]) - Double.parseDouble(pp[2]);
            } catch (Exception ignore) {
            }
        }
        double cx = x + dx, cy = y + dy, cz = z + dz;

        String ply = "{\"cmd\":\"edit\",\"target\":\"Player_Human\",\"x\":" + x +
                ",\"y\":" + y + ",\"z\":" + z + ",\"vx\":0,\"vy\":0,\"vz\":0}";

        enqueueCommand(sid, ply);

        log("[INFO] Teleported SteamID=" + sid + " to (" + x + "," + y + "," + z +
                ").");
    }

    private static void writePlayerData(String steamID, String name,
                                        String ip, String pos, String rot) {
        java.io.File dir = new java.io.File("player-data");
        if (!dir.exists()) dir.mkdirs();

        java.io.File tmp = new java.io.File(dir, steamID + ".tmp");
        java.io.File real = new java.io.File(dir, steamID + ".dat");
        try (java.io.PrintWriter pw = new java.io.PrintWriter(tmp)) {
            pw.println("playerName=" + name);
            pw.println("lastIp=" + ip);
            if (pos != null) pw.println("lastPos=" + pos);
            if (rot != null) pw.println("lastRot=" + rot);
            String objs = playerObjects.get(steamID);
            if (objs != null) pw.println("objects=" + objs);
            pw.println("updated=" + nowTimestamp());
        } catch (IOException e) {
            log("[WARN] Could not write " + real.getPath() + ": " + e.getMessage());
            return;
        }
        if (!tmp.renameTo(real)) tmp.renameTo(real);
    }

    private static void startTimeoutMonitor() {
        Thread m = new Thread(() -> {
            try {
                while (true) {
                    long cutoff = System.currentTimeMillis() - TIMEOUT_MS;

                    for (java.util.Iterator<java.util.Map.Entry<String, Long>> it =
                         clientLastSeen.entrySet().iterator(); it.hasNext(); ) {
                        java.util.Map.Entry<String, Long> e = it.next();
                        if (e.getValue() < cutoff) {
                            String[] parts = e.getKey().split("\\|", 3);
                            String ip = parts[0];
                            String sid = parts[1];
                            String name = parts[2];

                            log("[INFO] Disconnect (timeout) from " + ip
                                    + " | Name=\"" + name + "\", SteamID=" + sid);
                            it.remove();
                            runningClients.remove(sid);
                        }
                    }
                    Thread.sleep(2000);
                }
            } catch (InterruptedException ignore) {
            }
        });
        m.setDaemon(true);
        m.start();
    }

    private static String extractJson(String json, String key) {
        String k = "\"" + key + "\"";
        int i = json.indexOf(k);
        if (i == -1) return "";

        int colon = json.indexOf(':', i);
        if (colon == -1) return "";

        int start = colon + 1;
        while (start < json.length() && Character.isWhitespace(json.charAt(start)))
            start++;

        if (start >= json.length()) return "";

        char c = json.charAt(start);

        if (c == '"') {
            int end = start + 1;
            boolean esc = false;
            for (; end < json.length(); end++) {
                char ch = json.charAt(end);
                if (esc) {
                    esc = false;
                    continue;
                }
                if (ch == '\\') {
                    esc = true;
                    continue;
                }
                if (ch == '"') break;
            }
            if (end >= json.length()) return "";
            return json.substring(start + 1, end);
        }

        if (c == '[') {
            int depth = 1;
            int end = start + 1;
            for (; end < json.length() && depth > 0; end++) {
                char ch = json.charAt(end);
                if (ch == '[') depth++;
                else if (ch == ']') depth--;
                else if (ch == '"') {
                    end++;
                    while (end < json.length() && json.charAt(end) != '"') {
                        if (json.charAt(end) == '\\') end++;
                        end++;
                    }
                }
            }
            if (depth != 0) return "";
            return json.substring(start, end);
        }

        if (c == '{') {
            int depth = 1;
            int end = start + 1;
            for (; end < json.length() && depth > 0; end++) {
                char ch = json.charAt(end);
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
                else if (ch == '"') {
                    end++;
                    while (end < json.length() && json.charAt(end) != '"') {
                        if (json.charAt(end) == '\\') end++;
                        end++;
                    }
                }
            }
            if (depth != 0) return "";
            return json.substring(start, end);
        }

        int end = start;
        while (end < json.length()) {
            char ch = json.charAt(end);
            if (ch == '-' || ch == '+' || ch == '.' || Character.isDigit(ch))
                end++;
            else break;
        }
        return json.substring(start, end);
    }

    private static final java.util.Set<String> SUPPRESS_ACK_LABELS =
            java.util.Collections.synchronizedSet(new java.util.HashSet<>());

    public static void suppressAckLabel(String label) {
        if (label != null) SUPPRESS_ACK_LABELS.add(label);
    }

    private static void respond(PrintWriter out, String body) {
        out.print("HTTP/1.1 200 OK\r\n");
        out.print("Content-Type: text/plain; charset=UTF-8\r\n");
        out.print("Content-Length: " + body.getBytes().length + "\r\n");
        out.print("Connection: close\r\n");
        out.print("\r\n");
        out.print(body);
        out.flush();
    }

    private static void consoleCommandLoop() {
        try (BufferedReader in = new BufferedReader(new InputStreamReader(System.in))) {

            for (String line; (line = in.readLine()) != null; ) {
                line = line.trim();
                if (line.isEmpty()) continue;

                if (ExtensionManager.forwardConsoleInput(line)) continue;

                if (ExtensionManager.handleConsole(line)) continue;

                if (line.equalsIgnoreCase("help") || line.equals("?")) {
                    log("[INFO] Commands:");
                    log("[INFO]   stop");
                    log("[INFO]   tp <steamID> <x> <y> <z>");
                    log("[INFO]   location <steamID>");
                    log("[INFO]   clientsideobject <steamID>");
                    log("[INFO]   create <steamID> <src> x y z rx ry rz [ … ]");
                    log("[INFO]   edit   <steamID> <targetName> [ … ]");
                    log("[INFO]   ext <sub> …   (see ‘ext help’)");
                    continue;
                }

                if (line.equalsIgnoreCase("stop")) {
                    log("[INFO] Stopping server …");
                    shutdownAndExit();
                    return;
                }

                if (line.toLowerCase().startsWith("tp ")) {
                    handleTeleport(line);
                    continue;
                }
                if (line.toLowerCase().startsWith("location ")) {
                    handleLocation(line);
                    continue;
                }
                if (line.toLowerCase().startsWith("clientsideobject ")) {
                    handleObjects(line);
                    continue;
                }

                if (line.toLowerCase().startsWith("create ")) {
                    String[] tok = line.split("\\s+");
                    if (tok.length < 9) {
                        log("[WARN] See ‘help’ for full syntax. Not enough arguments.");
                        continue;
                    }
                    try {
                        int idx = 1;
                        String sid = tok[idx++];
                        String src = tok[idx++];
                        double x = Double.parseDouble(tok[idx++]);
                        double y = Double.parseDouble(tok[idx++]);
                        double z = Double.parseDouble(tok[idx++]);
                        double rx = Double.parseDouble(tok[idx++]);
                        double ry = Double.parseDouble(tok[idx++]);
                        double rz = Double.parseDouble(tok[idx++]);

                        String color = "none";
                        String rename = "";
                        double sx = 1, sy = 1, sz = 1;
                        java.util.Map<String, Boolean> comps = new java.util.LinkedHashMap<>();

                        while (idx < tok.length) {
                            String t = tok[idx++];
                            if ("color".equalsIgnoreCase(t) && idx < tok.length) color = tok[idx++];
                            else if ("rename".equalsIgnoreCase(t) && idx < tok.length) rename = tok[idx++];
                            else if ("scale".equalsIgnoreCase(t) && idx + 2 < tok.length) {
                                sx = Double.parseDouble(tok[idx++]);
                                sy = Double.parseDouble(tok[idx++]);
                                sz = Double.parseDouble(tok[idx++]);
                            } else if (t.contains(":")) {
                                String[] kv = t.split(":", 2);
                                comps.put(kv[0], !"off".equalsIgnoreCase(kv[1]));
                            } else log("[WARN] Unknown token '" + t + "' – ignored.");
                        }

                        StringBuilder j = new StringBuilder()
                                .append("{\"cmd\":\"create\"")
                                .append(",\"src\":\"").append(src).append("\"")
                                .append(",\"x\":").append(x).append(",\"y\":").append(y).append(",\"z\":").append(z)
                                .append(",\"rx\":").append(rx).append(",\"ry\":").append(ry).append(",\"rz\":").append(rz);

                        if (!"none".equalsIgnoreCase(color)) j.append(",\"color\":\"").append(color).append("\"");
                        if (!rename.isEmpty()) j.append(",\"rename\":\"").append(rename).append("\"");
                        if (sx != 1 || sy != 1 || sz != 1)
                            j.append(",\"sx\":").append(sx).append(",\"sy\":").append(sy).append(",\"sz\":").append(sz);
                        if (!comps.isEmpty()) {
                            j.append(",\"components\":{");
                            boolean first = true;
                            for (java.util.Map.Entry<String, Boolean> e : comps.entrySet()) {
                                if (!first) j.append(',');
                                first = false;
                                j.append('"').append(e.getKey()).append("\":").append(e.getValue());
                            }
                            j.append('}');
                        }
                        j.append('}');

                        enqueueCommand(sid, j.toString());
                    } catch (Exception ex) {
                        log("[WARN] Bad arguments: " + ex.getMessage());
                    }
                    continue;
                }

                if (line.toLowerCase().startsWith("edit ")) {
                    String[] tok = line.split("\\s+");
                    if (tok.length < 3) {
                        log("[WARN] Usage: edit <steamID> <targetName> …");
                        continue;
                    }

                    int idx = 1;
                    String sid = tok[idx++];
                    String target = tok[idx++];

                    boolean delete = false;
                    Double x = null, y = null, z = null;
                    Double rx = null, ry = null, rz = null;
                    double sx = 1, sy = 1, sz = 1;
                    boolean scaleGiven = false;
                    String color = null;
                    String copyTex = null;
                    String rename = "";
                    java.util.Map<String, Boolean> comps = new java.util.LinkedHashMap<>();

                    while (idx < tok.length) {
                        String t = tok[idx++];
                        if ("delete".equalsIgnoreCase(t)) {
                            delete = true;
                        } else if ("pos".equalsIgnoreCase(t) && idx + 2 < tok.length) {
                            x = Double.parseDouble(tok[idx++]);
                            y = Double.parseDouble(tok[idx++]);
                            z = Double.parseDouble(tok[idx++]);
                        } else if ("rot".equalsIgnoreCase(t) && idx + 2 < tok.length) {
                            rx = Double.parseDouble(tok[idx++]);
                            ry = Double.parseDouble(tok[idx++]);
                            rz = Double.parseDouble(tok[idx++]);
                        } else if ("scale".equalsIgnoreCase(t) && idx + 2 < tok.length) {
                            sx = Double.parseDouble(tok[idx++]);
                            sy = Double.parseDouble(tok[idx++]);
                            sz = Double.parseDouble(tok[idx++]);
                            scaleGiven = true;
                        } else if ("color".equalsIgnoreCase(t) && idx < tok.length) color = tok[idx++];
                        else if ("copytex".equalsIgnoreCase(t) && idx < tok.length) copyTex = tok[idx++];
                        else if ("rename".equalsIgnoreCase(t) && idx < tok.length) rename = tok[idx++];
                        else if (t.contains(":")) {
                            String[] kv = t.split(":", 2);
                            comps.put(kv[0], !"off".equalsIgnoreCase(kv[1]));
                        } else log("[WARN] Unknown token '" + t + "' – ignored.");
                    }

                    StringBuilder j = new StringBuilder()
                            .append("{\"cmd\":\"edit\"")
                            .append(",\"target\":\"").append(target).append("\"");
                    if (delete) j.append(",\"delete\":true");
                    if (x != null)
                        j.append(",\"x\":").append(x).append(",\"y\":").append(y).append(",\"z\":").append(z);
                    if (rx != null)
                        j.append(",\"rx\":").append(rx).append(",\"ry\":").append(ry).append(",\"rz\":").append(rz);
                    if (scaleGiven)
                        j.append(",\"sx\":").append(sx).append(",\"sy\":").append(sy).append(",\"sz\":").append(sz);
                    if (color != null) j.append(",\"color\":\"").append(color).append("\"");
                    if (copyTex != null) j.append(",\"copytex\":\"").append(copyTex).append("\"");
                    if (!rename.isEmpty()) j.append(",\"rename\":\"").append(rename).append("\"");
                    if (!comps.isEmpty()) {
                        j.append(",\"components\":{");
                        boolean first = true;
                        for (java.util.Map.Entry<String, Boolean> e : comps.entrySet()) {
                            if (!first) j.append(',');
                            first = false;
                            j.append('"').append(e.getKey()).append("\":").append(e.getValue());
                        }
                        j.append('}');
                    }
                    j.append('}');

                    enqueueCommand(sid, j.toString());
                    continue;
                }

                log("[WARN] Unknown command. Type 'help' for a list.");
            }
        } catch (Exception e) {
            log("[ERROR] Console input error: " + e.getMessage());
        }
    }

    private static void handleLocation(String rawLine) {
        String sid = rawLine.substring(9).trim();
        if (sid.isEmpty()) {
            log("[WARN] Usage: location <steamID>");
            return;
        }

        String pos = playerPositions.get(sid);
        String rot = playerRotations.get(sid);

        if (pos == null || rot == null) {
            java.io.File f = new java.io.File("player-data", sid + ".dat");
            if (f.exists()) {
                try {
                    for (String l : java.nio.file.Files.readAllLines(f.toPath())) {
                        if (l.startsWith("lastPos=")) pos = l.substring(8);
                        if (l.startsWith("lastRot=")) rot = l.substring(8);
                    }
                } catch (IOException ignore) {
                }
            }
        }

        if (pos == null || rot == null)
            log("[INFO] No data cached for SteamID=" + sid);
        else
            log("[INFO] " + sid + " → Pos(" + pos + ") Rot(" + rot + ")");
    }

    private static void handleObjects(String rawLine) {
        String sid = rawLine.substring("clientsideobject ".length()).trim();
        if (sid.isEmpty()) {
            log("[WARN] Usage: clientsideobject <steamID>");
            return;
        }

        String objs = playerObjects.get(sid);
        if (objs == null) {
            java.io.File f = new java.io.File("player-data", sid + ".dat");
            if (f.exists()) {
                try {
                    for (String l : java.nio.file.Files.readAllLines(f.toPath())) {
                        if (l.startsWith("objects=")) {
                            objs = l.substring(8);
                            break;
                        }
                    }
                } catch (IOException ignore) {
                }
            }
        }

        if (objs == null)
            log("[INFO] No object snapshot cached for SteamID=" + sid);
        else
            log("[INFO] Objects for " + sid + " → " + objs);
    }

    private static void shutdownAndExit() {

        ExtensionManager.disableAll();

        if (serverSocket != null && !serverSocket.isClosed()) {
            try {
                serverSocket.close();
                log("[INFO] ServerSocket closed.");
            } catch (java.io.IOException e) {
                log("[WARN] Error closing ServerSocket: " + e.getMessage());
            }
        }
        System.exit(0);
    }

    public static boolean isPaused(String sid) {
        return pausedClients.contains(sid);
    }

    public static boolean isRunning(String sid) {
        return runningClients.contains(sid);
    }

    public static java.util.Set<String> getActiveClients() {
        return activeClients;
    }

    public static void teleportFromApi(String sid, double x, double y, double z) {
        handleTeleport("tp " + sid + ' ' + x + ' ' + y + ' ' + z);
    }

    private static void createAndShowGui() {
        frame = new JFrame("Custom Server Stub (Java 21)");
        frame.setDefaultCloseOperation(JFrame.EXIT_ON_CLOSE);
        frame.setSize(700, 450);

        logArea = new JTextArea();
        logArea.setEditable(false);
        logArea.setFont(new Font("Monospaced", Font.PLAIN, 12));
        JScrollPane scrollPane = new JScrollPane(logArea);
        frame.getContentPane().add(scrollPane, BorderLayout.CENTER);

        JButton stopButton = new JButton("Stop Server");
        stopButton.addActionListener(e -> {
            log("[INFO] Stopping server as requested...");
            shutdownAndExit();
        });
        JPanel bottom = new JPanel(new FlowLayout(FlowLayout.RIGHT));
        bottom.add(stopButton);
        frame.getContentPane().add(bottom, BorderLayout.SOUTH);

        frame.setLocationRelativeTo(null);
        frame.setVisible(true);
    }

    public static void log(String message) {
        String ts = "[" + nowTimestamp() + "] " + message;
        System.out.println(ts);

        if (guiMode && logArea != null) {
            javax.swing.SwingUtilities.invokeLater(() -> {
                logArea.append(ts + '\n');
                logArea.setCaretPosition(logArea.getDocument().getLength());
            });
        }

        ExtensionManager.handleConsole(ts);
    }

    private static String nowTimestamp() {
        LocalDateTime now = LocalDateTime.now();
        DateTimeFormatter fmt = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss");
        return now.format(fmt);
    }

    public static String getObjectsSnapshot(String steamID) {
        return playerObjects.get(steamID);
    }

}
