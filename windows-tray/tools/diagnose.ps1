#Requires -Version 5.1
# Prints only metadata about the Claude Desktop cookie store. Cookie values are never printed.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

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
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)] public static extern IntPtr sqlite3_errmsg(IntPtr db);

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
'@

function Write-Section($title) { Write-Host "`n== $title ==" -ForegroundColor Cyan }

function Copy-Locked($source, $target) {
    $in = [System.IO.File]::Open($source, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        $out = [System.IO.File]::Create($target)
        try { $in.CopyTo($out) } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}

Write-Section 'Claude data folders'
$candidates = @("$env:APPDATA\Claude")
$candidates += Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter 'Claude*' -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude' }

$root = $null
foreach ($dir in $candidates) {
    $exists = Test-Path $dir
    Write-Host ("{0}  exists={1}" -f $dir, $exists)
    if ($exists -and -not $root) { $root = $dir }
}
if (-not $root) { Write-Host 'Claude Desktop data folder not found.' -ForegroundColor Red; exit 1 }

Write-Section 'Cookie stores'
$cookieFiles = @(Get-ChildItem $root -Recurse -Depth 5 -File -Filter 'Cookies' -ErrorAction SilentlyContinue)
if ($cookieFiles.Count -eq 0) {
    Write-Host 'No Cookies file anywhere under the data folder. Top-level contents:' -ForegroundColor Red
    Get-ChildItem $root -Force | ForEach-Object { Write-Host "  $($_.Name)$(if ($_.PSIsContainer) { '\' })" }
    exit 1
}
foreach ($file in $cookieFiles) {
    Write-Host ("{0}  size={1}" -f $file.FullName.Substring($root.Length), $file.Length)
    foreach ($suffix in '-wal', '-journal') { if (Test-Path "$($file.FullName)$suffix") { Write-Host "  has $suffix file" } }
}
Write-Host ("Local State files: {0}" -f ((Get-ChildItem $root -Recurse -Depth 3 -File -Filter 'Local State' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($root.Length) }) -join ', '))

Write-Section 'Local State encryption key'
$keyBytes = $null
$crypt = $null
if (Test-Path "$root\Local State") { $crypt = (Get-Content "$root\Local State" -Raw | ConvertFrom-Json).os_crypt }
else { Write-Host 'Local State not found at data folder root.' -ForegroundColor Red }
if (-not $crypt) { Write-Host 'os_crypt section missing or unreadable.' -ForegroundColor Red }
else {
    Write-Host ("os_crypt keys: {0}" -f (($crypt.PSObject.Properties.Name) -join ', '))
    if ($crypt.encrypted_key) {
        $raw = [Convert]::FromBase64String($crypt.encrypted_key)
        $prefix = [Text.Encoding]::ASCII.GetString($raw, 0, 5)
        Write-Host "encrypted_key prefix=$prefix length=$($raw.Length)"
        if ($prefix -eq 'DPAPI') {
            try {
                $protected = $raw[5..($raw.Length - 1)]
                $keyBytes = [Security.Cryptography.ProtectedData]::Unprotect([byte[]]$protected, $null, 'CurrentUser')
                Write-Host "DPAPI unprotect: OK, key length=$($keyBytes.Length)" -ForegroundColor Green
            } catch { Write-Host "DPAPI unprotect: FAILED ($($_.Exception.Message))" -ForegroundColor Red }
        }
    }
}

Write-Section 'claude.ai cookies (names and formats only)'
foreach ($file in $cookieFiles) {
    Write-Host "-- $($file.FullName.Substring($root.Length))"
    $tempDir = Join-Path ([IO.Path]::GetTempPath()) ("claude-diag-" + [Guid]::NewGuid())
    New-Item -ItemType Directory $tempDir | Out-Null
    try {
        $tempDb = Join-Path $tempDir 'Cookies'
        Copy-Locked $file.FullName $tempDb
        foreach ($suffix in '-wal', '-journal') { if (Test-Path "$($file.FullName)$suffix") { Copy-Locked "$($file.FullName)$suffix" "$tempDb$suffix" } }

        $db = [IntPtr]::Zero
        if ([WinSqlite]::Open($tempDb, [ref]$db) -ne 0) { Write-Host '  could not open database copy' -ForegroundColor Red; continue }
        try {
            $stmt = [IntPtr]::Zero
            $sql = "SELECT host_key, name, encrypted_value, length(value), expires_utc FROM cookies WHERE host_key LIKE '%claude.ai%' OR host_key LIKE '%anthropic.com%' ORDER BY host_key, name"
            if ([WinSqlite]::sqlite3_prepare_v2($db, $sql, -1, [ref]$stmt, [IntPtr]::Zero) -ne 0) {
                Write-Host ("  query failed: {0}" -f [Runtime.InteropServices.Marshal]::PtrToStringAnsi([WinSqlite]::sqlite3_errmsg($db))) -ForegroundColor Red
                continue
            }
            $rows = 0
            while ([WinSqlite]::sqlite3_step($stmt) -eq 100) {
                $rows++
                $enc = [WinSqlite]::Blob($stmt, 2)
                $format = if ($enc.Length -ge 3) { [Text.Encoding]::ASCII.GetString($enc, 0, 3) } else { 'none' }
                Write-Host ("  {0,-22} {1,-28} format={2} encLen={3} plainLen={4}" -f [WinSqlite]::Text($stmt, 0), [WinSqlite]::Text($stmt, 1), $format, $enc.Length, [WinSqlite]::Text($stmt, 3))
            }
            [WinSqlite]::sqlite3_finalize($stmt) | Out-Null
            Write-Host "  rows=$rows"
        } finally { [WinSqlite]::sqlite3_close($db) | Out-Null }
    } catch {
        Write-Host "  error: $($_.Exception.Message)" -ForegroundColor Red
    } finally { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Section 'Environment'
Write-Host "PowerShell=$($PSVersionTable.PSVersion)  OS=$([Environment]::OSVersion.VersionString)"
$exe = Get-Process -Name 'claude' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($exe) { Write-Host "Claude running: yes  version=$($exe.MainModule.FileVersionInfo.ProductVersion)" } else { Write-Host 'Claude running: no' }

Write-Host "`nDone. Copy everything above and send it back. No cookie values were printed." -ForegroundColor Green
