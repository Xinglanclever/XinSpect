using System.Globalization;
using System.IO;
using System.Text;

namespace XinSpect;

internal static class SensorCsvFile
{
    internal static (string Path, TextWriter Writer) Create(string folder, DateTimeOffset now)
    {
        Directory.CreateDirectory(folder);
        string prefix = "XinSpect_感測記錄_" + now.ToLocalTime().ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        for (int suffix = 0; suffix < 1000; suffix++)
        {
            string path = System.IO.Path.Combine(folder, prefix + (suffix == 0 ? "" : "_" + suffix.ToString(CultureInfo.InvariantCulture)) + ".csv");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
            catch (IOException) when (File.Exists(path)) { continue; }
            try { return (path, new StreamWriter(stream, new UTF8Encoding(true))); }
            catch { stream.Dispose(); throw; }
        }
        throw new IOException("Unable to reserve a unique sensor log filename after 1,000 attempts.");
    }
}
