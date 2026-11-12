# 🌍 Civilízame Esta Jorge - Mod Multijugador (MP)

[![Versión](https://img.shields.io/badge/Versión-1.1%20Beta-blue)](https://github.com/OxyCoder32/Civilizame-Esta-Jorge-MP/releases)
[![Estado](https://img.shields.io/badge/Estado-En%20Desarrollo-yellow)](https://github.com/OxyCoder32/Civilizame-Esta-Jorge-MP)
[![Licencia](https://img.shields.io/badge/Licencia-MIT-green)](LICENSE)

Un mod que añade funcionalidad **multijugador** al juego de estrategia por turnos fan-made *[Civilízame esta Jorge](https://javierinatorxd.itch.io/civilzame-esta-jorge)*, inspirado en la saga Civilization.

> ⚠️ **Advertencia**: Este mod se encuentra actualmente en **fase Beta**. Algunas funciones, como la sincronización en tiempo real durante la partida o el soporte para inteligencia artificial (IA), están en desarrollo y pueden presentar errores.

---

## ✨ Características Principales

- 👥 **Soporte para múltiples jugadores**: Diseñado para soportar más de 2 jugadores en la misma partida.
- 🔄 **Sincronización de turnos**: Sistema de gestión de estados y control de turnos para mantener a todos los jugadores alineados.
- 🌐 **Integración con Photon**: Utiliza servicios de red (Photon) para la gestión de host, clientes y transferencia de estados (`StateHandoff`).
- 🛠️ **Arquitectura modular**: Código organizado en componentes claros para facilitar el mantenimiento y la contribución de la comunidad.

---

## 🚀 Instalación

1. Dirígete a la sección de **[Releases](https://github.com/OxyCoder32/Civilizame-Esta-Jorge-MP/releases)** y descarga la última versión disponible (ej. `V1.1`).
2. Extrae el contenido del archivo descargado.
3. Mueve los archivos extraidos a la ruta del Juego base (donde esta el .exe).
4. Inicia el juego y busca la nueva opción de "Multijugador" en el menú principal.

> 💡 **Nota**: Si encuentras problemas de sincronización, asegúrate de que todos los jugadores tengan exactamente la misma versión del mod y del juego base.

---

## 📂 Estructura del Proyecto

El código fuente está organizado de la siguiente manera para mantener un desarrollo ordenado:

| Carpeta | Descripción |
|---------|-------------|
| `Core` | Lógica principal del modo multijugador, configuración (`MPConfig`, `MPConstants`) y gestión de estados (`MPStateManager`, `MPMatchState`). |
| `Network` | Implementación de red, incluyendo `PhotonManager`, `ClientManager`, `HostManager` y controladores de turnos (`TurnController`). |
| `UI` | Adaptaciones y nuevos elementos de la interfaz de usuario para soportar salas, listas de jugadores y estados de conexión. |
| `Patches` | Parches y modificaciones al código original del juego para inyectar la funcionalidad multijugador de forma segura. |
| `Utils` | Funciones de utilidad y ayuda para la configuración y sincronización del juego. |

---

## 🐛 Estado Actual y Problemas Conocidos

- 🔄 **Sincronización en partida**: La sincronización inicial está creada, pero la sincronización *in-game* aún puede presentar errores y está siendo activamente corregida.
- 🤖 **Inteligencia Artificial**: Actualmente **hay soporte** para jugadores controlados por la IA en partidas multijugador.
- 🧪 **Pruebas**: El sistema ha sido probado de forma limitada; se recomienda jugar con amigos para reportar fallos y ayudar a estabilizar la red.

---

## 🙏 Créditos

- **Javierinator3000**: Creador del juego original *[Civilízame esta Jorge](https://javierinatorxd.itch.io/civilzame-esta-jorge)*.
- **Comunidad**: A todos los testers y colaboradores que reportan bugs y sugieren mejoras.

---

## 📜 Licencia

Este proyecto está bajo la Licencia MIT. Consulta el archivo [LICENSE](LICENSE) para más detalles.