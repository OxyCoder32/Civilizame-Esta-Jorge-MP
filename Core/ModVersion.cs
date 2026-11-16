using System;
using System.IO;
using BepInEx;

namespace CivilizameMP.Core
{
    /// <summary>Versión del mod en UN solo sitio: la constante VERSION la usa
    /// el atributo [BepInPlugin] de CivilizameMPPlugin y viaja por Photon
    /// (propiedad "mpv") y por el actualizador. IMPORTANTE: cada vez que se
    /// publique una DLL nueva en el repo hay que SUBIR este número, o el
    /// actualizador y el chequeo de versiones no detectarán el cambio.</summary>
    public static class ModVersion
    {
        public const string VERSION = "1.1.0";

        public static string Current { get; } = Resolve();

        private static string Resolve()
        {
            try
            {
                var attr = Attribute.GetCustomAttribute(
                    typeof(CivilizameMPPlugin), typeof(BepInPlugin)) as BepInPlugin;
                if (attr == null || attr.Version == null) return "0.0.0";
                return attr.Version.Major + "." + attr.Version.Minor + "." + attr.Version.Build;
            }
            catch
            {
                return VERSION;
            }
        }

        /// <summary>Lee la versión de una DLL del mod (los BYTES del archivo,
        /// sin cargar ni ejecutar nada) con Mono.Cecil, que BepInEx ya trae.
        /// Busca el atributo [BepInPlugin] de la clase plugin y devuelve su
        /// campo Version: la MISMA fuente que usa la copia local.</summary>
        public static string ReadFromDllBytes(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var mod = Mono.Cecil.ModuleDefinition.ReadModule(ms))
                {
                    foreach (var t in mod.Types)
                    {
                        foreach (var ca in t.CustomAttributes)
                        {
                            if (ca.AttributeType == null ||
                                ca.AttributeType.FullName != "BepInEx.BepInPlugin")
                                continue;
                            // ctor (string GUID, string Name, string Version)
                            if (ca.ConstructorArguments.Count >= 3)
                            {
                                var v = ca.ConstructorArguments[2].Value as string;
                                if (!string.IsNullOrEmpty(v)) return v;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
