using System;
using System.IO;
using UnityEngine;

namespace CivilizameMP.Network
{
    /// <summary>
    /// Mensaje de paso de turno: contiene el estado completo del juego
    /// (GuardadoSeguridad.jue + MapaGuardadoSeguridad.txt) más los datos de control.
    /// </summary>
    public class HandoffMessage
    {
        public int FromTurn;    // turno que se TERMINÓ (índice de jugador)
        public int LabelTurn;   // TurnOrder grabado en el save: turno que va a ARRANCAR
        public int BaseSeq;     // secuencia del estado en que se basa (control de estados)
        public byte[] JueBytes;
        public byte[] MapBytes;

        public byte[] Serialize()
        {
            int jueLen = JueBytes == null ? 0 : JueBytes.Length;
            int mapLen = MapBytes == null ? 0 : MapBytes.Length;

            using (var ms = new MemoryStream(28 + jueLen + mapLen))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(StateHandoff.PAYLOAD_MAGIC);
                w.Write(StateHandoff.PAYLOAD_VERSION);
                w.Write(FromTurn);
                w.Write(LabelTurn);
                w.Write(BaseSeq);
                w.Write(jueLen);
                w.Write(mapLen);
                if (jueLen > 0) w.Write(JueBytes);
                if (mapLen > 0) w.Write(MapBytes);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static HandoffMessage Deserialize(byte[] data)
        {
            if (data == null || data.Length < 28)
            {
                CivilizameMPPlugin.Log.LogWarning("[Handoff] Payload inválido (tamaño)");
                return null;
            }

            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                {
                    if (r.ReadInt32() != StateHandoff.PAYLOAD_MAGIC)
                    {
                        CivilizameMPPlugin.Log.LogWarning("[Handoff] Payload inválido (magic)");
                        return null;
                    }
                    if (r.ReadInt32() != StateHandoff.PAYLOAD_VERSION)
                    {
                        CivilizameMPPlugin.Log.LogWarning("[Handoff] Versión de payload incompatible");
                        return null;
                    }

                    var msg = new HandoffMessage();
                    msg.FromTurn = r.ReadInt32();
                    msg.LabelTurn = r.ReadInt32();
                    msg.BaseSeq = r.ReadInt32();
                    int jueLen = r.ReadInt32();
                    int mapLen = r.ReadInt32();
                    if (jueLen > 0) msg.JueBytes = r.ReadBytes(jueLen);
                    if (mapLen > 0) msg.MapBytes = r.ReadBytes(mapLen);
                    return msg;
                }
            }
            catch (Exception ex)
            {
                CivilizameMPPlugin.Log.LogError($"[Handoff] Error deserializando payload: {ex.Message}");
                return null;
            }
        }
    }
}
