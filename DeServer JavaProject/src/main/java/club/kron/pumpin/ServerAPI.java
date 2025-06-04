package club.kron.pumpin;

import java.io.File;
import java.util.Set;

public final class ServerAPI {

    public void log(String msg) {
        Main.log("[EXT] " + msg);
    }

    public boolean isPaused(String sid) {
        return Main.isPaused(sid);
    }

    public boolean isRunning(String sid) {
        return Main.isRunning(sid);
    }

    public Set<String> getActiveClients() {
        return Main.getActiveClients();
    }

    public void enqueueCommand(String sid, String json) {
        Main.enqueueCommand(sid, json);
    }

    public void suppressAckLog(String label) {
        Main.suppressAckLabel(label);
    }

    public void teleport(String sid, double x, double y, double z) {
        Main.teleportFromApi(sid, x, y, z);
    }

    public File getExtensionsRoot() {
        return new File("extensions");
    }
}
