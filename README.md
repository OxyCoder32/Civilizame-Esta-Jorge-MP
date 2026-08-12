# CivilizameMP — Multiplayer Mod para Civilización

Mod multijugador en red para **Civilización** (Unity Mono) usando **Photon PUN 2** y **BepInEx 5**.

---

## 📋 Requisitos
 **BepInEx:**  5.x (x64 o x86) | **Mono** (no IL2CPP). Descarga la versión que coincida con tu ejecutable del juego. 
 
 **Photon**  SDK gratuito | Requiere cuenta en [Photon Engine](https://www.photonengine.com/). 


---

## 🚀 Instalación

### 1. Instalar BepInEx 5

1. Descarga **BepInEx 5** desde la [página oficial](https://github.com/BepInEx/BepInEx/releases).
   - Si tu juego es **x64**, descarga `BepInEx_x64_5.x.x.x.zip`
   - Si tu juego es **x86**, descarga `BepInEx_x86_5.x.x.x.zip`
   - **IMPORTANTE:** Selecciona la versión **Mono**, no IL2CPP.
2. Extrae el contenido en la carpeta raíz del juego (donde está el `.exe`).
3. Ejecuta el juego **una vez** para que BepInEx genere las carpetas necesarias.
4. Cierra el juego.

### 2. Configurar Photon PUN 2

1. Ve a [Photon Engine](https://www.photonengine.com/) y **crea una cuenta gratuita**.
2. En el Dashboard, crea una nueva app de tipo **PUN**.
3. Copia el **App ID** que te proporciona Photon.

### 3. Instalar el mod

1. Compila el proyecto o descarga la última release.
2. Copia `CivilizameMP.dll` a:
   <Juego>\BepInEx\plugins\
3. Crea el archivo de configuración de Photon:
   - Ve a `<Juego>\BepInEx\config\`
   - Crea un archivo `civilizame.photon` (o edita según tu implementación) y pega tu **App ID** de Photon.
   - **⚠️ TODOS los jugadores deben usar el MISMO App ID** para conectarse entre sí.

### 4. Verificar instalación

1. Inicia el juego.
2. En el menú principal debe aparecer el panel de **Multiplayer** (botón "Multijugador").
3. Si no aparece, revisa `<Juego>\BepInEx\LogOutput.log` para errores.

---

## 🎮 Cómo jugar

### Crear partida (Host)

1. En el menú principal, haz clic en **"Host"**.
2. Configura el mapa, dificultad, número de jugadores, etc.
3. Haz clic en **"Generar Mundo"**.
4. Espera a que el mundo se genere (tú como host debes pasar por la generación normal del juego).
5. Cuando el mundo esté listo, el mod enviará automáticamente el estado a los clientes.

### Unirse a partida (Cliente)

1. En el menú principal, haz clic en **"Join"**.
2. Introduce el **código de sala** o selecciona de la lista de salas públicas.
3. Espera a que el host genere el mundo.
4. Recibirás el mapa y la configuración automáticamente.
5. ¡Listo para jugar!

---

## 🔄 Cómo funciona el sistema

### Arquitectura general

El mod usa una arquitectura **Host-Authoritative** con sincronización de estado completo:

### Flujo de turnos

1. **Host** siempre tiene la copia maestra del estado.
2. Cuando un **cliente** termina su turno:
   - Guarda su estado local (`GuardadoSeguridad.jue`).
   - Lo envía al host vía Photon.
3. El **host** recibe el estado:
   - Verifica el hash de integridad.
   - Carga el estado recibido.
   - Si el turno sigue siendo del cliente remoto, ejecuta `NextTurn()`.
   - Procesa turnos de IA automáticamente (bucle).
   - Cuando llega el turno del host o de otro cliente, guarda y envía el nuevo estado a todos.
4. Los **clientes** reciben el estado actualizado y lo cargan.

### Seguridad y validación

- **Hash SHA-256** de cada estado enviado para detectar corrupción.
- **Secuencia numérica** para descartar estados desordenados o duplicados.
- **Debounce de 0.3s** en el host para evitar spam de estados.


---

## 🐛 Solución de problemas

| Problema | Causa probable | Solución |
|----------|---------------|----------|
| No aparece el menú multiplayer | BepInEx mal instalado o versión IL2CPP | Reinstala BepInEx **Mono** x64/x86 correcto. |
| No se conecta a Photon | App ID incorrecto o vacío | Verifica el archivo de config con el App ID. |
| Los clientes no reciben el mapa | Firewall o App ID diferente | Asegúrate que **todos** usen el **mismo App ID**. |
| Desync entre jugadores | Cliente modificó archivos del juego | Todos deben tener la misma versión del juego base. |
| Panel de espera tapa el menú ESC | Blocker activo | Normal en turnos de IA; en turnos humanos debería permitir ESC. |

---

## 📝 Notas importantes

- **Todos los jugadores deben usar el mismo App ID de Photon.** Sin esto, no se verán en la red.
- El host debe tener un PC decente: procesa las IA de todos los turnos.
- La partida guarda en `GuardadoSeguridad.jue` en cada sincronización. No borres este archivo durante la partida.
- El mod es **experimental**: reporta bugs en Issues.
