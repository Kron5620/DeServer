package club.kron.pumpin;

import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.Iterator;
import java.util.List;
import java.util.ServiceLoader;
import java.util.concurrent.CopyOnWriteArrayList;

final class ExtensionManager {

    private static final List<Extension> LOADED = new CopyOnWriteArrayList<>();
    private static final ServerAPI API = new ServerAPI();

    static void loadAll() {

        File dir = new File("extensions");
        if (!dir.exists()) {
            dir.mkdirs();
            Main.log("[EXT] Created 'extensions' directory");
        }

        File[] jars = dir.listFiles(f -> f.isFile() && f.getName().toLowerCase().endsWith(".jar"));
        if (jars == null || jars.length == 0) {
            Main.log("[EXT] No extensions found.");
            return;
        }

        for (File jar : jars) {
            try (URLClassLoader cl = new URLClassLoader(
                    new URL[]{jar.toURI().toURL()}, Main.class.getClassLoader())) {

                ServiceLoader<Extension> sl = ServiceLoader.load(Extension.class, cl);
                Iterator<Extension> it = sl.iterator();

                if (!it.hasNext()) {
                    Main.log("[EXT] " + jar.getName() + " contains no " +
                            Extension.class.getName() + " implementation");
                    continue;
                }

                while (it.hasNext()) {
                    Extension ext = it.next();
                    String id = ext.getClass().getSimpleName();

                    File dataDir = new File(dir, id);
                    dataDir.mkdirs();

                    try {
                        ext.onEnable(API, dataDir);
                        LOADED.add(ext);
                        Main.log("[EXT] Enabled " + id + " (" + jar.getName() + ')');
                    } catch (Exception ex) {
                        Main.log("[EXT] Failed enabling " + id + ": " + ex.getMessage());
                    }
                }
            } catch (Exception e) {
                Main.log("[EXT] Error loading " + jar.getName() + ": " + e.getMessage());
            }
        }
        Main.log("[EXT] Total enabled: " + LOADED.size());
    }

    static void disableAll() {
        for (Extension ext : LOADED) {
            try {
                ext.onDisable();
            } catch (Exception ignore) {
            }
        }
        LOADED.clear();
        Main.log("[EXT] All extensions disabled");
    }

    static boolean broadcastConsoleInput(String line) {
        boolean handled = false;

        for (Extension ext : LOADED) {
            try {
                if (ext.onConsoleInput(line))
                    handled = true;
            } catch (Exception ignore) {
            }
        }
        return handled;
    }

    static void broadcastLog(String msg) {
        for (Extension ext : LOADED)
            try {
                ext.onLog(msg);
            } catch (Exception ignore) {
            }
    }
}
