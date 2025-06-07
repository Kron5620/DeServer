package club.kron.pumpin;

import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.*;
import java.util.concurrent.ConcurrentHashMap;

final class ExtensionManager {

    private static final class ExtHolder {
        final String id;
        final File jar;
        final URLClassLoader cl;
        final Extension instance;
        ExtHolder(String id, File jar, URLClassLoader cl, Extension instance) {
            this.id = id; this.jar = jar; this.cl = cl; this.instance = instance;
        }
    }

    private static final Map<String, ExtHolder> LOADED = new ConcurrentHashMap<>();
    private static final ServerAPI API = new ServerAPI();

    static boolean forwardConsoleInput(String line) {
        boolean handled = false;
        for (ExtHolder h : LOADED.values()) {
            try {
                if (h.instance.onConsoleInput(line))
                    handled = true;
            } catch (Throwable t) {
                Main.log("[EXT] " + h.id + ".onConsoleInput error: " + t.getMessage());
            }
        }
        return handled;
    }

    static void loadAll() {
        File dir = API.getExtensionsRoot();
        if (!dir.exists()) dir.mkdirs();

        File[] jars = dir.listFiles(f -> f.isFile() && f.getName().toLowerCase().endsWith(".jar"));
        if (jars == null || jars.length == 0) {
            Main.log("[EXT] No extensions found.");
            return;
        }
        for (File jar : jars) loadJar(jar);
        Main.log("[EXT] Total enabled: " + LOADED.size());
    }

    static boolean loadJar(File jar) {
        if (jar == null || !jar.isFile()) {
            Main.log("[EXT] load: file not found – " + jar);
            return false;
        }
        try {
            URLClassLoader cl = new URLClassLoader(new URL[]{jar.toURI().toURL()}, Main.class.getClassLoader());
            ServiceLoader<Extension> sl = ServiceLoader.load(Extension.class, cl);
            Iterator<Extension> it = sl.iterator();
            if (!it.hasNext()) {
                Main.log("[EXT] " + jar.getName() + " contains no Extension implementation");
                cl.close();
                return false;
            }
            while (it.hasNext()) {
                Extension ext = it.next();
                String id = ext.getClass().getSimpleName();
                if (LOADED.containsKey(id)) {
                    Main.log("[EXT] " + id + " already loaded – unload first.");
                    continue;
                }
                File dataDir = new File(API.getExtensionsRoot(), id);
                dataDir.mkdirs();
                ext.onEnable(API, dataDir);
                LOADED.put(id, new ExtHolder(id, jar, cl, ext));
                Main.log("[EXT] Enabled " + id + " (" + jar.getName() + ')');
            }
            return true;
        } catch (Throwable t) {
            Main.log("[EXT] Error loading " + jar.getName() + ": " + t.getMessage());
            return false;
        }
    }

    static boolean unload(String arg) {
        ExtHolder h = LOADED.remove(arg);
        if (h == null) {
            for (Iterator<Map.Entry<String, ExtHolder>> it = LOADED.entrySet().iterator(); it.hasNext(); ) {
                Map.Entry<String, ExtHolder> e = it.next();
                if (e.getValue().jar.getName().equalsIgnoreCase(arg)) {
                    h = e.getValue();
                    it.remove();
                    break;
                }
            }
        }
        if (h == null) {
            Main.log("[EXT] unload: '" + arg + "' not loaded.");
            return false;
        }
        try { h.instance.onDisable(); } catch (Exception ignore) {}
        try { h.cl.close(); } catch (Exception ignore) {}
        Main.log("[EXT] Unloaded " + h.id);
        return true;
    }

    static boolean reload(String arg) {
        ExtHolder h = LOADED.get(arg);
        if (h == null) {
            for (ExtHolder eh : LOADED.values()) {
                if (eh.jar.getName().equalsIgnoreCase(arg)) {
                    h = eh;
                    break;
                }
            }
        }
        if (h == null) {
            Main.log("[EXT] reload: '" + arg + "' not loaded.");
            return false;
        }
        File jar = h.jar;
        unload(h.id);
        return loadJar(jar);
    }

    static void disableAll() {
        for (String id : new ArrayList<>(LOADED.keySet())) unload(id);
        Main.log("[EXT] All extensions disabled");
    }

    static boolean handleConsole(String line) {
        String[] tok = line.trim().split("\\s+");
        if (tok.length == 0 || !"ext".equalsIgnoreCase(tok[0])) return false;

        if (tok.length == 1 || "help".equalsIgnoreCase(tok[1])) {
            Main.log("[EXT] Commands: ext list | load <jar> | unload <id> | reload <id>");
            return true;
        }
        switch (tok[1].toLowerCase()) {
            case "list":
                if (LOADED.isEmpty()) Main.log("[EXT] No extensions loaded.");
                else for (ExtHolder h : LOADED.values())
                    Main.log("[EXT] " + h.id + "  (" + h.jar.getName() + ')');
                return true;
            case "load":
                if (tok.length < 3) { Main.log("[EXT] load <jarFileName>"); return true; }
                File jar = new File(API.getExtensionsRoot(), tok[2]);
                loadJar(jar);
                return true;
            case "unload":
                if (tok.length < 3) { Main.log("[EXT] unload <id>"); return true; }
                unload(tok[2]);
                return true;
            case "reload":
                if (tok.length < 3) { Main.log("[EXT] reload <id>"); return true; }
                reload(tok[2]);
                return true;
            default:
                Main.log("[EXT] Unknown sub-command. Try: ext help");
                return true;
        }
    }
}
