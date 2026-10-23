#Requires -Version 5.1
# Decrypts the Claude Desktop session in memory, calls the usage endpoint once and prints the HTTP status
# and the response shape. Cookie values are never printed. Close Claude Desktop (tray icon too) before running.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinSqlite
{
    const string Dll = "winsqlite3.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern int sqlite3_open_v2(byte[] utf8Path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern int sqlite3_close(IntPtr db);

    public static int Open(string path, out IntPtr db)
    {
        return sqlite3_open_v2(System.Text.Encoding.UTF8.GetBytes(path + "\0"), out db, 1, IntPtr.Zero);
    }

    public static string Text(IntPtr stmt, int col)
    {
        IntPtr p = sqlite3_column_text(stmt, col);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
    }

    public static byte[] Blob(IntPtr stmt, int col)
    {
        int n = sqlite3_column_bytes(stmt, col);
        byte[] buf = new byte[n];
        if (n > 0) Marshal.Copy(sqlite3_column_blob(stmt, col), buf, 0, n);
        return buf;
    }
}

public static class AesGcmNative
{
    [StructLayout(LayoutKind.Sequential)]
    struct AuthInfo
    {
        public int cbSize;
        public int dwInfoVersion;
        public IntPtr pbNonce;
        public int cbNonce;
        public IntPtr pbAuthData;
        public int cbAuthData;
        public IntPtr pbTag;
        public int cbTag;
        public IntPtr pbMacContext;
        public int cbMacContext;
        public int cbAAD;
        public long cbData;
        public int dwFlags;
    }

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr handle, string prop, byte[] input, int size, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objSize, byte[] secret, int secretSize, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, byte[] iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
    [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipher, byte[] tag)
    {
        IntPtr alg, keyHandle;
        Check(BCryptOpenAlgorithmProvider(out alg, "AES", null, 0));
        try
        {
            byte[] mode = System.Text.Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            Check(BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0));
            Check(BCryptGenerateSymmetricKey(alg, out keyHandle, IntPtr.Zero, 0, key, key.Length, 0));
            GCHandle n = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            GCHandle t = GCHandle.Alloc(tag, GCHandleType.Pinned);
            try
            {
                AuthInfo info = new AuthInfo();
                info.cbSize = Marshal.SizeOf(typeof(AuthInfo));
                info.dwInfoVersion = 1;
                info.pbNonce = n.AddrOfPinnedObject();
                info.cbNonce = nonce.Length;
                info.pbTag = t.AddrOfPinnedObject();
                info.cbTag = tag.Length;
                byte[] plain = new byte[cipher.Length];
                int written;
                Check(BCryptDecrypt(keyHandle, cipher, cipher.Length, ref info, null, 0, plain, plain.Length, out written, 0));
                return plain;
            }
            finally
            {
                n.Free();
                t.Free();
                BCryptDestroyKey(keyHandle);
            }
        }
        finally
        {
            BCryptCloseAlgorithmProvider(alg, 0);
        }
    }

    static void Check(int status)
    {
        if (status != 0) throw new Exception("BCrypt status 0x" + status.ToString("X8"));
    }
}
'@

function Copy-Locked($source, $target) {
    $in = [System.IO.File]::Open($source, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        $out = [System.IO.File]::Create($target)
        try { $in.CopyTo($out) } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}

function Read-Rows($db, $sql) {
    $stmt = [IntPtr]::Zero
    if ([WinSqlite]::sqlite3_prepare_v2($db, $sql, -1, [ref]$stmt, [IntPtr]::Zero) -ne 0) { throw "query failed: $sql" }
    $rows = @()
    while ([WinSqlite]::sqlite3_step($stmt) -eq 100) { $rows += , @([WinSqlite]::Text($stmt, 0), [WinSqlite]::Blob($stmt, 1)) }
    [WinSqlite]::sqlite3_finalize($stmt) | Out-Null
    return , $rows
}

function Hide-Ids($text) {
    $text = $text -replace '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '<uuid>'
    return $text -replace '[\w.+-]+@[\w-]+\.[\w.]+', '<email>'
}

function Invoke-Claude($url, $cookieHeader) {
    $request = [Net.HttpWebRequest]::Create($url)
    $request.Method = 'GET'
    $request.UserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36'
    $request.Accept = 'application/json'
    $request.Headers.Add('Cookie', $cookieHeader)
    $request.Headers.Add('anthropic-client-platform', 'web_claude_ai')
    $request.AutomaticDecompression = [Net.DecompressionMethods]'GZip, Deflate'
    try { $response = $request.GetResponse() } catch [Net.WebException] { $response = $_.Exception.Response; if (-not $response) { throw } }
    try {
        $reader = New-Object IO.StreamReader($response.GetResponseStream(), [Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        return @{ Status = [int]$response.StatusCode; Server = $response.Headers['Server']; CfMitigated = $response.Headers['cf-mitigated']; Body = $body }
    } finally { $response.Close() }
}

$root = Get-ChildItem $env:LOCALAPPDATA, $env:APPDATA -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'claude' } |
    ForEach-Object { Get-ChildItem $_.FullName -Recurse -Depth 2 -File -Force -Filter 'Local State' -ErrorAction SilentlyContinue } |
    Select-Object -First 1 | ForEach-Object { $_.DirectoryName }
if (-not $root) { Write-Host 'Claude data folder not found.' -ForegroundColor Red; exit 1 }

$encrypted = [Convert]::FromBase64String((Get-Content "$root\Local State" -Raw | ConvertFrom-Json).os_crypt.encrypted_key)
$key = [Security.Cryptography.ProtectedData]::Unprotect([byte[]]$encrypted[5..($encrypted.Length - 1)], $null, 'CurrentUser')

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("claude-probe-" + [Guid]::NewGuid())
New-Item -ItemType Directory $tempDir | Out-Null
$cookies = @{}
try {
    $tempDb = Join-Path $tempDir 'Cookies'
    try { Copy-Locked "$root\Network\Cookies" $tempDb } catch { Write-Host 'Cookie file is locked: quit Claude Desktop completely (tray icon -> Quit) and run again.' -ForegroundColor Red; exit 1 }
    if (Test-Path "$root\Network\Cookies-journal") { Copy-Locked "$root\Network\Cookies-journal" "$tempDb-journal" }

    $db = [IntPtr]::Zero
    if ([WinSqlite]::Open($tempDb, [ref]$db) -ne 0) { throw 'Could not open cookie database copy.' }
    try {
        $metaRows = Read-Rows $db "SELECT value, NULL FROM meta WHERE key = 'version'"
        $dbVersion = if ($metaRows.Count) { [int]$metaRows[0][0] } else { 0 }
        Write-Host "cookie db version=$dbVersion"
        foreach ($row in (Read-Rows $db "SELECT name, encrypted_value FROM cookies WHERE host_key IN ('.claude.ai', 'claude.ai')")) {
            $blob = $row[1]
            if ($blob.Length -lt 31 -or [Text.Encoding]::ASCII.GetString($blob, 0, 3) -ne 'v10') { continue }
            $nonce = $blob[3..14]
            $tag = $blob[($blob.Length - 16)..($blob.Length - 1)]
            $cipher = if ($blob.Length -gt 31) { $blob[15..($blob.Length - 17)] } else { @() }
            $plain = [AesGcmNative]::Decrypt($key, [byte[]]$nonce, [byte[]]$cipher, [byte[]]$tag)
            # Cookie db version 24+ prefixes each value with a 32-byte hash of the host.
            if ($dbVersion -ge 24 -and $plain.Length -ge 32) { $plain = $plain[32..($plain.Length - 1)] }
            $cookies[$row[0]] = [Text.Encoding]::UTF8.GetString([byte[]]$plain)
        }
    } finally { [WinSqlite]::sqlite3_close($db) | Out-Null }
} finally { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ("decrypted cookies: {0}" -f $cookies.Count)
$sessionKey = $cookies['sessionKey']
if (-not $sessionKey) { Write-Host 'sessionKey not decrypted.' -ForegroundColor Red; exit 1 }
Write-Host ("sessionKey looks valid: {0} (length {1})" -f ($sessionKey -match '^sk-ant-'), $sessionKey.Length)

$org = $cookies['lastActiveOrg']
Write-Host ("lastActiveOrg is a uuid: {0}" -f ($org -match '^[0-9a-f-]{36}$'))

$full = ($cookies.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; '
$minimal = "sessionKey=$sessionKey"

foreach ($case in @(
    @{ Name = 'usage, all cookies'; Url = "https://claude.ai/api/organizations/$org/usage"; Cookie = $full },
    @{ Name = 'usage, sessionKey only'; Url = "https://claude.ai/api/organizations/$org/usage"; Cookie = $minimal },
    @{ Name = 'organizations list'; Url = 'https://claude.ai/api/organizations'; Cookie = $full }
)) {
    Write-Host "`n== $($case.Name) ==" -ForegroundColor Cyan
    try {
        $r = Invoke-Claude $case.Url $case.Cookie
        Write-Host "status=$($r.Status)  server=$($r.Server)  cf-mitigated=$($r.CfMitigated)"
        if ($case.Name -like 'organizations*') {
            try { Write-Host ("organizations returned: {0}" -f @($r.Body | ConvertFrom-Json).Count) } catch { Write-Host 'body is not JSON' }
        } else {
            $text = Hide-Ids $r.Body
            Write-Host ($text.Substring(0, [Math]::Min(2000, $text.Length)))
        }
    } catch { Write-Host "request failed: $($_.Exception.Message)" -ForegroundColor Red }
}

$cookies.Clear()
Write-Host "`nDone. Send everything above back. No cookie values were printed." -ForegroundColor Green
