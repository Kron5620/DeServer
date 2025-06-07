<!-- README.md -->

<div align="center">
  <h1><b>DeServer</b> <br><small>Real‑time command bridge for <em>Descenders</em></small></h1>

  <p>
    <a href="https://jdk.java.net/21/"><img src="https://img.shields.io/badge/Java-21-blue?logo=openjdk"/></a>
    <a href="https://maven.apache.org/"><img src="https://img.shields.io/badge/Maven-3.9%2B-C71A36?logo=apache"/></a>
    <a href="https://unity3d.com/get-unity/download/archive"><img src="https://img.shields.io/badge/Unity-2017.4.9f1-black?logo=unity"/></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green"/></a>
  </p>

<sub>Turn your local Java process into a fully‑scriptable, multiplayer‑ish host for <em>Descenders</em> mods.</sub> <br> <br>

</div>

---

## 🌟 Features

* **Plug‑and‑play HTTP Server** – Single shaded JAR, zero external deps.
* **ServiceLoader extensions** – Drop JARs into `/extensions` & they auto‑load.
* **Bi‑directional Comms** – Commands → Unity & snapshots ← Unity, over plain JSON.
* **GUI or CLI** – Swing log window when a display is present, or `nogui` for headless.
* **Hot telemetry** – Players, camera position, object snapshots every *N* seconds.
* **Scripting‑friendly** – Send `create`, `edit`, `mesh`, `tween`, `turn` … commands.

---

## ⚡ Quick Start

```bash
# 1. Build & run the server
mvn -q clean package && java -jar target/DeServer-*-shaded.jar --port=19299

# 2. Import ClientManager.cs into your Unity 2017.4.9f1 project
#    Attach to an empty GameObject and play — that's it!
```

> **Note** – No original *Descenders* game code is included here; this repo only ships a networking stub. You still need the official ModTool SDK from RageSquid/No More Robots.

---

## 🛠️ Prerequisites

| Tool               | Version              |
| ------------------ | -------------------- |
| OpenJDK            | **21**               |
| Maven              | **3.9+**             |
| Unity Editor       | **2017.4.9f1** (LTS) |
| Descenders ModTool | Latest               |

Install Java & Maven any way you like. On Windows, [`Scoop`](https://scoop.sh), on macOS, `brew install openjdk maven`, on Linux use your package manager.

---

## 🔨 Building & Running the Server

1. **Clone**

   ```bash
   git clone https://github.com/your‑org/DeServer.git
   cd DeServer
   ```
2. **Build**

   ```bash
   mvn clean package  # creates target/DeServer‑<ver>‑shaded.jar
   ```
3. **Run**

   ```bash
   java -jar target/DeServer-*-shaded.jar  # GUI if possible
   # or
   java -jar target/DeServer-*-shaded.jar nogui --port=28080
   ```

First launch produces:

```
server.properties     # IP & port
player-data/          # per‑player cache
world/                # saves, if you need them later
extensions/           # drop‑in jars
```

### CLI Commands (type in server console)

| Command                  | Purpose                                        |
| ------------------------ | ---------------------------------------------- |
| `stop`                   | graceful shutdown                              |
| `tp <sid> x y z`         | teleport player                                |
| `location <sid>`         | print cached pos / rot                         |
| `clientsideobject <sid>` | dump last object snapshot                      |
| `create` / `edit`        | low‑level spawn / mutate (see help in console) |

---

## 🎮 Unity Client Integration

> Tested against **Unity 2017.4.9f1** (matching the official Descenders ModTool baseline).

1. **Copy** `client/ClientManager.cs` into your Unity project (e.g. `Assets/Scripts`).

2. **Scene Setup**

   * Create `GameObject ▸ Create Empty` → rename **DeServerClient**.
   * Drag **ClientManager** onto it.
   * In the Inspector:

     | Field                | Recommended test value | What to put for a real connection                       |
     | -------------------- | ---------------------- | ------------------------------------------------------- |
     | **Server Ip**        | `127.0.0.1`            | **Your server’s LAN / public IP** (e.g. `192.168.1.42`) |
     | **Server Port**      | `19299`                | **The port your server is listening on**                |
     | **Timeout Sec**      | `2`                    | *(Leave as-is unless you need longer timeouts)*         |
     | **Objects Interval** | `1`                    | *(Leave as-is; sends object updates every second)*      |

3. **Build your Mod** with ModTool as usual. The client will attempt to connect on play and you should see `Connect from ...` in the Java window.

> **Tip** – To package the Unity side into a standalone DLL, simply place the script in an Assembly Definition and reference it from your mod. The server doesn’t care which way you ship it.

---

## 🔌 Writing Extensions

Extensions are **plain Java jars** exposing `club.kron.pumpin.Extension` via Java SPI.

### 1 ▪︎ Project Scaffold (Maven)

```xml
<project>
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.example</groupId>
  <artifactId>greeting-extension</artifactId>
  <version>1.0-SNAPSHOT</version>

  <dependencies>
    <dependency>
      <groupId>club.kron</groupId>
      <artifactId>DeServer</artifactId>
      <version>1.0-SNAPSHOT</version>
      <scope>provided</scope>
    </dependency>
  </dependencies>

  <build>
    <plugins>
      <plugin>
        <groupId>org.apache.maven.plugins</groupId>
        <artifactId>maven-shade-plugin</artifactId>
        <version>3.5.0</version>
        <executions>
          <execution>
            <phase>package</phase>
            <goals><goal>shade</goal></goals>
          </execution>
        </executions>
      </plugin>
    </plugins>
  </build>
</project>
```

### 2 ▪︎ Implementation

```java
package com.example;

import club.kron.pumpin.Extension;
import club.kron.pumpin.ServerAPI;
import java.io.File;

public final class GreetingExtension implements Extension {
    private ServerAPI api;

    @Override
    public void onEnable(ServerAPI api, File dataFolder) {
        this.api = api;
        api.log("👋 GreetingExtension online – clients: " + api.getActiveClients().size());
    }

    @Override
    public void onDisable() {
        api.log("👋 GreetingExtension offline.");
    }

    @Override
    public boolean onConsoleInput(String line) {
        if (line.equalsIgnoreCase("hello")) {
            api.log("Hello back atcha!");
            return true; // consumed
        }
        return false;
    }
}
```

### 3 ▪︎ Service Descriptor

Create `src/main/resources/META-INF/services/club.kron.pumpin.Extension` with:

```
com.example.GreetingExtension
```

### 4 ▪︎ Build & Deploy

```bash
mvn package
cp target/greeting-extension-*.jar ~/DeServer/extensions/
# restart the server → [EXT] Enabled GreetingExtension
```

---

## 📚 DeServer API Cheat Sheet

| Method                             | Description                                       |         |                             |
| ---------------------------------- | ------------------------------------------------- | ------- | --------------------------- |
| `log(msg)`                         | Write to console & broadcast to other extensions  |         |                             |
| `getActiveClients()`               | `Set<String>` of \`ip                             | steamID | name\` of connected clients |
| `enqueueCommand(sid,json)`         | Push raw JSON command string to a specific client |         |                             |
| `teleport(sid,x,y,z)`              | Instant player warp                               |         |                             |
| `isPaused(sid)` / `isRunning(sid)` | Query client state                                |         |                             |
| `suppressAckLog(label)`            | Hide certain ACK spam lines                       |         |                             |
| `getExtensionsRoot()`              | `File` pointing at `/extensions` dir              |         |                             |

Full docs live in `ServerAPI.java`.

---

## 🧐 Troubleshooting

| Problem                                     | Remedy                                                                           |
| ------------------------------------------- | -------------------------------------------------------------------------------- |
| **`BindException: Address already in use`** | Another process is on that port. Pass `--port=`.                                 |
| No connection messages                      | Check firewall / IP mismatch; confirm ModTool bundled the script.                |
| Extension not detected                      | Verify `META-INF/services` file and that you restarted the server.               |
| Unity `NetworkError`                        | Server unreachable (wrong IP) or JSON payload too big – try higher `timeoutSec`. |

---

## 🤝 Contributing

Pull requests are welcome! Please target the **code** branch, follow the existing code style, and add tests where it makes sense.

---

## 📄 License

This project is released under the **MIT License** — see [LICENSE](LICENSE) for details.

> **Disclaimer** – *Descenders* is © RageSquid & No More Robots. This open‑source project is not affiliated with, endorsed, or supported by them in any way.
