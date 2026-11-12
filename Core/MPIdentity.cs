using System;

namespace CivilizameMP.Core
{
    /// <summary>
    /// Identidad por INSTANCIA de juego (no por máquina):
    ///
    /// Cada proceso de juego genera un GUID propio que se usa como UserId de
    /// Photon. Así host y cliente pueden convivir en la misma PC (el viejo
    /// UserId compartido por PlayerPrefs hacía que Photon rechazara el segundo
    /// join con "UserId already joined").
    ///
    /// Se expone como estático porque el UserId debe ser estable durante toda la
    /// sesión: Phantom lo usa para re-entrar con el MISMO ActorNumber
    /// (OpRejoinRoom) tras las desconexiones por carga de mapa.
    /// Regenerate() se usa al volver al menú para no arrastrar identidad vieja.
    /// </summary>
    public static class MPIdentity
    {
        private static string _id = Guid.NewGuid().ToString();

        public static string InstanceId => _id;

        public static void Regenerate()
        {
            _id = Guid.NewGuid().ToString();
            CivilizameMPPlugin.Log.LogInfo($"[Identity] Nueva identidad de instancia: {_id}");
        }

        public static void ResetIdentity()
        {
            Regenerate();
        }
    }
}
