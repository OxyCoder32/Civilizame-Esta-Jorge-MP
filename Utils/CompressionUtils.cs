using System.IO;
using System.IO.Compression;

namespace CivilizameMP.Utils
{
    public static class CompressionUtils
    {
        public static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(data, 0, data.Length);
                gzip.Close();
                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] compressed)
        {
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}