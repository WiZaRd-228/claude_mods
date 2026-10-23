using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeLimitsTray;

internal enum ImportStatus
{
    Ok,
    NotFound,
    Locked,
    Failed,
}

internal sealed record ImportResult(ImportStatus Status, string? SessionKey = null, string? Organization = null, string? Error = null);

internal static class DesktopSession
{
    private const int SqliteRow = 100;
    private const int HostHashLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static ImportResult Import()
    {
        try
        {
            var root = FindDataFolder();
            if (root is null) return new ImportResult(ImportStatus.NotFound, Error: "Claude Desktop data folder not found");

            var cookieFile = Path.Combine(root, "Network", "Cookies");
            if (!File.Exists(cookieFile)) return new ImportResult(ImportStatus.NotFound, Error: "Cookie store not found");

            var key = ReadMasterKey(root);
            var cookies = ReadClaudeCookies(cookieFile, key);
            CryptographicOperations.ZeroMemory(key);

            if (!cookies.TryGetValue("sessionKey", out var sessionKey) || string.IsNullOrEmpty(sessionKey))
            {
                return new ImportResult(ImportStatus.NotFound, Error: "No Claude session in Claude Desktop");
            }

            cookies.TryGetValue("lastActiveOrg", out var organization);
            return new ImportResult(ImportStatus.Ok, sessionKey, organization);
        }
        catch (IOException)
        {
            return new ImportResult(ImportStatus.Locked, Error: "Claude Desktop is running and holds its cookie store");
        }
        catch (Exception ex)
        {
            Log.Write($"Session import failed: {ex.GetType().Name}: {ex.Message}");
            return new ImportResult(ImportStatus.Failed, Error: ex.Message);
        }
    }

    private static string? FindDataFolder()
    {
        var bases = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        foreach (var baseDir in bases)
        {
            if (!Directory.Exists(baseDir)) continue;
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
            {
                if (!Path.GetFileName(dir).Contains("claude", StringComparison.OrdinalIgnoreCase)) continue;
                var found = FindLocalState(dir, depth: 2);
                if (found is not null) return found;
            }
        }

        return null;
    }

    private static string? FindLocalState(string dir, int depth)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "Local State")) && File.Exists(Path.Combine(dir, "Network", "Cookies"))) return dir;
            if (depth == 0) return null;
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                var found = FindLocalState(child, depth - 1);
                if (found is not null) return found;
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static byte[] ReadMasterKey(string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Local State")));
        var encoded = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
            ?? throw new InvalidDataException("encrypted_key missing");
        var raw = Convert.FromBase64String(encoded);
        if (Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI") throw new InvalidDataException("Unsupported key protection");
        return ProtectedData.Unprotect(raw[5..], null, DataProtectionScope.CurrentUser);
    }

    private static Dictionary<string, string> ReadClaudeCookies(string cookieFile, byte[] key)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ClaudeLimitsTray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempDb = Path.Combine(tempDir, "Cookies");
            CopyShared(cookieFile, tempDb);
            if (File.Exists(cookieFile + "-journal")) CopyShared(cookieFile + "-journal", tempDb + "-journal");
            return Sqlite.ReadCookies(tempDb, key);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(target);
        input.CopyTo(output);
    }

    private static string Decrypt(byte[] blob, byte[] key, int dbVersion)
    {
        if (blob.Length < 3 + NonceLength + TagLength || Encoding.ASCII.GetString(blob, 0, 3) != "v10") return "";
        var nonce = blob.AsSpan(3, NonceLength);
        var cipher = blob.AsSpan(3 + NonceLength, blob.Length - 3 - NonceLength - TagLength);
        var tag = blob.AsSpan(blob.Length - TagLength, TagLength);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagLength)) aes.Decrypt(nonce, cipher, tag, plain);
        var offset = dbVersion >= 24 && plain.Length >= HostHashLength ? HostHashLength : 0;
        var value = Encoding.UTF8.GetString(plain, offset, plain.Length - offset);
        CryptographicOperations.ZeroMemory(plain);
        return value;
    }

    private static class Sqlite
    {
        private const string Dll = "winsqlite3.dll";
        private const int ReadOnly = 1;

        [DllImport(Dll)] private static extern int sqlite3_open_v2(byte[] utf8Path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] utf8Sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport(Dll)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
        [DllImport(Dll)] private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
        [DllImport(Dll)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll)] private static extern int sqlite3_close(IntPtr db);

        public static Dictionary<string, string> ReadCookies(string path, byte[] key)
        {
            if (sqlite3_open_v2(Utf8(path), out var db, ReadOnly, IntPtr.Zero) != 0) throw new InvalidDataException("Cannot open cookie store copy");
            try
            {
                var version = 0;
                Query(db, "SELECT value FROM meta WHERE key = 'version'", stmt => int.TryParse(Text(stmt, 0), out version));

                var cookies = new Dictionary<string, string>();
                Query(db, "SELECT name, encrypted_value FROM cookies WHERE host_key IN ('.claude.ai', 'claude.ai')",
                    stmt => cookies[Text(stmt, 0)] = Decrypt(Blob(stmt, 1), key, version));
                return cookies;
            }
            finally
            {
                sqlite3_close(db);
            }
        }

        private static void Query(IntPtr db, string sql, Action<IntPtr> onRow)
        {
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero) != 0) throw new InvalidDataException("Cookie store query failed");
            try
            {
                while (sqlite3_step(stmt) == SqliteRow) onRow(stmt);
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

        private static string Text(IntPtr stmt, int column)
        {
            var pointer = sqlite3_column_text(stmt, column);
            return pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";
        }

        private static byte[] Blob(IntPtr stmt, int column)
        {
            var length = sqlite3_column_bytes(stmt, column);
            var buffer = new byte[length];
            if (length > 0) Marshal.Copy(sqlite3_column_blob(stmt, column), buffer, 0, length);
            return buffer;
        }
    }
}
