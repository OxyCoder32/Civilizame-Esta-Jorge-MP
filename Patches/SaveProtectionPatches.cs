using HarmonyLib;
using CivilizameMP.Network;

namespace CivilizameMP.Patches
{
    /// <summary>
    /// Evita que el guardado de seguridad automático del juego (cada 10 turnos)
    /// sobrescriba GuardadoSeguridad.jue / MapaGuardadoSeguridad.txt mientras el
    /// mod los está usando para sincronizar turnos. Nuestro código pide permiso
    /// con StateHandoff.VanillaAutosaveAllowed justo antes de guardar.
    /// </summary>
    [HarmonyPatch(typeof(InformationToFile), "GuardadoSeguridad")]
    public static class BlockVanillaAutosavePatch
    {
        public static bool Prefix() => !StateHandoff.BlockVanillaAutosave;
    }

    [HarmonyPatch(typeof(Tablero), "GuardadoSeg")]
    public static class BlockVanillaMapAutosavePatch
    {
        public static bool Prefix() => !StateHandoff.BlockVanillaAutosave;
    }

    /// <summary>
    /// Etiqueta el GameSave con el turno que va a ARRANCAR mientras el mod guarda
    /// un estado de paso de turno. Al cargar, el juego hace TurnOrder = save - 1 y
    /// NexTurn lo avanza al etiquetado: el ingreso se aplica exactamente una vez.
    /// </summary>
    [HarmonyPatch(typeof(GameSave), MethodType.Constructor,
        new[] { typeof(GameManager), typeof(Converter), typeof(string) })]
    public static class GameSaveLabelPatch
    {
        public static void Postfix(GameSave __instance)
        {
            if (StateHandoff.PendingSaveLabel >= 0)
                __instance.TurnOrder = StateHandoff.PendingSaveLabel;
        }
    }
}
