package club.kron.pumpin;

import java.io.File;
import java.util.Set;

public final class ServerAPI {

    public void log(String msg) { Main.log("[EXT] " + msg); }

    public java.util.List<String> pollInputs(String steamID) {
        return Main.pollInputs(steamID);
    }

    public boolean isPaused (String sid) { return Main.isPaused (sid); }
    public boolean isRunning(String sid) { return Main.isRunning(sid); }

    public Set<String> getActiveClients() { return Main.getActiveClients(); }

    public void enqueueCommand(String sid, String json) {
        Main.enqueueCommand(sid, json);
    }

    public void loadMod(String steamID, String fileName) {
        if (steamID == null || fileName == null) return;
        Main.enqueueCommand(steamID,
                "{\"cmd\":\"modload\",\"file\":\"" +
                        fileName.replace("\\", "\\\\").replace("\"", "\\\"") + "\"}");
    }


    public void suppressAckLog(String label) { Main.suppressAckLabel(label); }

    public void teleport(String sid, double x, double y, double z) {
        Main.teleportFromApi(sid, x, y, z);
    }

    public File getExtensionsRoot() { return new File("extensions"); }

    public String getObjectsJson(String steamID) {
        return Main.getObjectsSnapshot(steamID);
    }
}
