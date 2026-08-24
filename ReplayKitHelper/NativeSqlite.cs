using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayKitHelper
{
    // minimal read-only sqlite3 reader over winsqlite3.dll, which ships with windows 10/11 -- no bundled sqlite binary needed, keeping the exe a true single file. used only to read browser cookie stores (chrome/edge sqlite databases), always opened against a private temp copy in mode=ro. ported from obs_replaykit helper modules/30_native.ps1's WinSqlite Add-Type block.
    internal static class NativeSqlite
    {
        private const string DLL = "winsqlite3.dll";
        public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_OPEN_READONLY = 1, SQLITE_OPEN_URI = 0x40;

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);

        // manual null-terminated utf-8 encode -- sqlite3_open_v2/prepare_v2 take raw byte[], not string, to avoid ansi charset mangling on non-ascii paths/sql.
        private static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

        public static string ColumnText(IntPtr stmt, int col)
        {
            IntPtr ptr = sqlite3_column_text(stmt, col);
            if (ptr == IntPtr.Zero) return null;
            int len = sqlite3_column_bytes(stmt, col);
            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        public static byte[] ColumnBlob(IntPtr stmt, int col)
        {
            IntPtr ptr = sqlite3_column_blob(stmt, col);
            int len = sqlite3_column_bytes(stmt, col);
            if (ptr == IntPtr.Zero || len == 0) return new byte[0];
            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return buf;
        }

        public static long ColumnInt64(IntPtr stmt, int col) => sqlite3_column_int64(stmt, col);

        // opens read-only via a file: uri (mode=ro), always against a private temp copy of the browser's live db (never the original, which chrome/edge keep locked while running).
        public static IntPtr OpenReadOnly(string path)
        {
            // percent-encode spaces -- sqlites uri parser otherwise chokes on them, and %TEMP% commonly contains one (a username with a space in it).
            string uri = "file:" + path.Replace("\\", "/").Replace(" ", "%20") + "?mode=ro";
            int rc = sqlite3_open_v2(Utf8Z(uri), out IntPtr db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                string msg = db != IntPtr.Zero ? (ColumnErrorMessage(db) ?? "sqlite3_open_v2 failed") : "sqlite3_open_v2 failed";
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                throw new InvalidOperationException(msg + " (rc=" + rc + ")");
            }
            return db;
        }

        // opens read-write via a file: uri (no mode= restriction) -- used only for the exit-time streamable/google
        // cookie wipe against obs's own live cookies db, once its file lock has been confirmed released.
        public static IntPtr OpenReadWrite(string path)
        {
            string uri = "file:" + path.Replace("\\", "/").Replace(" ", "%20");
            int rc = sqlite3_open_v2(Utf8Z(uri), out IntPtr db, 0x02 | SQLITE_OPEN_URI, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                string msg = db != IntPtr.Zero ? (ColumnErrorMessage(db) ?? "sqlite3_open_v2 failed") : "sqlite3_open_v2 failed";
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                throw new InvalidOperationException(msg + " (rc=" + rc + ")");
            }
            return db;
        }

        private static string ColumnErrorMessage(IntPtr db)
        {
            IntPtr ptr = sqlite3_errmsg(db);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }

        public static void Close(IntPtr db)
        {
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }

        public static IntPtr Prepare(IntPtr db, string sql)
        {
            int rc = sqlite3_prepare_v2(db, Utf8Z(sql), -1, out IntPtr stmt, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException("sqlite3_prepare_v2 failed: " + ColumnErrorMessage(db) + " (rc=" + rc + ")");
            return stmt;
        }

        public static int Step(IntPtr stmt) => sqlite3_step(stmt);

        public static void Finalize(IntPtr stmt)
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
        }
    }
}
