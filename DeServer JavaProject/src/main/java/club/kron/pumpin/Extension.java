package club.kron.pumpin;

import java.io.File;

public interface Extension {

    void onEnable(ServerAPI api, File dataFolder) throws Exception;

    void onDisable() throws Exception;

    default boolean onConsoleInput(String line) { return false; }

    default void onLog(String message) {}
}
