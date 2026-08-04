using System.IO;
using UnityEngine;

namespace CivilizameMP.Utils
{
    public static class FileUtils
    {
        public static byte[] ReadSaveFile(int slot)
        {
            string path = Application.persistentDataPath + "/save_" + slot;
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public static void WriteSaveFile(int slot, byte[] data)
        {
            string path = Application.persistentDataPath + "/save_" + slot;
            File.WriteAllBytes(path, data);
        }

        public static void WriteMetadata(int slot, string name)
        {
            string path = Application.persistentDataPath + "/Mapa" + slot + ".txt";
            File.WriteAllText(path, name);
        }

        public static bool SaveExists(int slot)
        {
            return File.Exists(Application.persistentDataPath + "/save_" + slot);
        }
    }
}