// Compresser - a tiny right-click compressor / extractor for Windows.
//
// All archive work is done by libarchive, which Windows 10 (1803+) / 11 already ships as
// System32\archiveint.dll (it powers tar.exe and Explorer's rar/7z support). That keeps this
// program a few KB with zero dependencies, and full Unicode support via libarchive's _w APIs.
//
//   Compresser.exe                         -> asks to install / uninstall the context menu
//   Compresser.exe /install | /uninstall
//   Compresser.exe compress <path>         -> add file/folder to a .zip (multi-select is merged)
//   Compresser.exe extract-here <archive>
//   Compresser.exe extract-folder <archive>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Compresser")]
[assembly: System.Reflection.AssemblyProduct("Compresser")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

static class Program
{
    const string AppName = "Compresser";

    // Extensions that get the "Extract" entries.
    static readonly string[] ArchiveExts = {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz",
        ".zst", ".tzst", ".lzma", ".cab", ".iso", ".cpio", ".xar", ".lzh", ".lha", ".jar", ".apk"
    };
    static readonly string[] DoubleExts = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lzma" };

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length == 0) return Interactive();

            string cmd = args[0].ToLowerInvariant();
            if (cmd == "/install") { Install(); return 0; }
            if (cmd == "/uninstall") { Uninstall(); return 0; }
            if (args.Length < 2) { Error("Missing path argument."); return 1; }

            if (!File.Exists(Path.Combine(Environment.SystemDirectory, LA.Dll)))
            {
                Error(LA.Dll + " was not found. Windows 10 (version 1803) or newer is required.");
                return 1;
            }

            string path = Path.GetFullPath(args[1].Trim('"'));
            if (cmd == "compress") return CompressQueued(path);
            if (cmd == "extract-here") return Extract(path, false);
            if (cmd == "extract-folder") return Extract(path, true);
            Error("Unknown command: " + args[0]);
            return 1;
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return 1;
        }
    }

    // ================================================================== install

    static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
    }

    static int Interactive()
    {
        var r = MessageBox.Show(
            "Compresser adds right-click menu entries:\n\n" +
            "  - Compress to ZIP (files and folders)\n" +
            "  - Extract here / Extract to folder (zip, rar, 7z, tar, gz, xz, iso, cab...)\n\n" +
            "Yes = Install    No = Uninstall    Cancel = Do nothing",
            AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) Install();
        else if (r == DialogResult.No) Uninstall();
        return 0;
    }

    static void Install()
    {
        // Copy ourselves to a stable location so the menu keeps working if the download is moved.
        Directory.CreateDirectory(InstallDir);
        string exe = Path.Combine(InstallDir, AppName + ".exe");
        if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), exe, StringComparison.OrdinalIgnoreCase))
            File.Copy(Application.ExecutablePath, exe, true);

        string q = "\"" + exe + "\"";
        const string icon = "%SystemRoot%\\System32\\zipfldr.dll,0";

        RemoveKeys();
        AddVerb(@"Software\Classes\*\shell\" + AppName + ".Zip", "Compress to ZIP", icon, q + " compress \"%1\"");
        AddVerb(@"Software\Classes\Directory\shell\" + AppName + ".Zip", "Compress to ZIP", icon, q + " compress \"%1\"");
        foreach (string ext in ArchiveExts)
        {
            string b = @"Software\Classes\SystemFileAssociations\" + ext + @"\shell\" + AppName;
            AddVerb(b + ".ExtractHere", "Extract here", icon, q + " extract-here \"%1\"");
            AddVerb(b + ".ExtractFolder", "Extract to folder", icon, q + " extract-folder \"%1\"");
        }

        MessageBox.Show("Installed.\n\nRight-click any file or folder to use it.\n" +
            "(On Windows 11 the entries are under \"Show more options\", or Shift+Right-click.)",
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void Uninstall()
    {
        RemoveKeys();
        if (Directory.Exists(InstallDir))
        {
            // Delete the installed copy once this process has exited.
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        MessageBox.Show("Uninstalled.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void AddVerb(string keyPath, string text, string icon, string command)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            k.SetValue("MUIVerb", text);
            k.SetValue("Icon", icon, RegistryValueKind.ExpandString);
            k.SetValue("MultiSelectModel", "Document");
            using (var c = k.CreateSubKey("command")) c.SetValue("", command);
        }
    }

    static void RemoveKeys()
    {
        var keys = new List<string> {
            @"Software\Classes\*\shell\" + AppName + ".Zip",
            @"Software\Classes\Directory\shell\" + AppName + ".Zip"
        };
        foreach (string ext in ArchiveExts)
        {
            string b = @"Software\Classes\SystemFileAssociations\" + ext + @"\shell\" + AppName;
            keys.Add(b + ".ExtractHere");
            keys.Add(b + ".ExtractFolder");
        }
        foreach (string k in keys) Registry.CurrentUser.DeleteSubKeyTree(k, false);
    }

    // ================================================================== compress

    // Explorer starts one process per selected item. Each instance appends its path to a queue
    // file; the first becomes the "leader", waits until the queue stops growing, then packs
    // everything into one zip.
    static int CompressQueued(string path)
    {
        string dir = Path.Combine(Path.GetTempPath(), AppName);
        Directory.CreateDirectory(dir);
        string queue = Path.Combine(dir, "queue.txt");

        using (var qLock = new Mutex(false, @"Local\Compresser.Queue"))
        using (var leader = new Mutex(false, @"Local\Compresser.Leader"))
        {
            Acquire(qLock, Timeout.Infinite);
            try { File.AppendAllText(queue, path + "\n", Encoding.UTF8); }
            finally { qLock.ReleaseMutex(); }

            if (!Acquire(leader, 0)) return 0; // the current leader will pack our item

            List<string> items;
            bool leaderHeld = true;
            try
            {
                long len = -1; int stable = 0;
                while (stable < 4)
                {
                    Thread.Sleep(100);
                    long l = File.Exists(queue) ? new FileInfo(queue).Length : 0;
                    if (l == len) stable++; else { len = l; stable = 0; }
                }
                Acquire(qLock, Timeout.Infinite);
                try
                {
                    items = File.Exists(queue) ? File.ReadAllLines(queue, Encoding.UTF8).ToList() : new List<string>();
                    File.Delete(queue);
                }
                finally
                {
                    // Give up leadership while still holding the queue lock: any item queued
                    // after this point is guaranteed to find the leader slot free.
                    leader.ReleaseMutex(); leaderHeld = false;
                    qLock.ReleaseMutex();
                }
            }
            finally { if (leaderHeld) leader.ReleaseMutex(); }

            var valid = items.Select(s => s.Trim().TrimEnd('\\'))
                             .Where(s => s.Length > 0 && (File.Exists(s) || Directory.Exists(s)))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int rc = 0;
            foreach (var g in valid.GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase))
                rc |= Compress(g.Key, g.Select(p => Path.GetFileName(p)).Where(n => n.Length > 0).ToList());
            return rc;
        }
    }

    static bool Acquire(Mutex m, int timeout)
    {
        try { return m.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    class Item { public string Full, Rel; public bool IsDir; public long Size; public DateTime Mtime; }

    static int Compress(string parent, List<string> names)
    {
        if (names.Count == 0) return 0;

        string baseName;
        if (names.Count == 1)
        {
            baseName = Directory.Exists(Path.Combine(parent, names[0])) ? names[0] : Path.GetFileNameWithoutExtension(names[0]);
            if (baseName.Length == 0) baseName = names[0];
        }
        else
        {
            baseName = Path.GetFileName(parent.TrimEnd('\\'));
            if (string.IsNullOrEmpty(baseName)) baseName = "Archive";
        }
        string output = UniquePath(parent, baseName, ".zip");

        var warnings = new List<string>();
        var items = new List<Item>();
        foreach (string n in names) Collect(Path.Combine(parent, n), n, items, warnings);
        long total = Math.Max(1, items.Sum(i => i.Size));

        bool ok = RunWithProgress("Compressing to " + Path.GetFileName(output), job =>
        {
            IntPtr a = LA.archive_write_new();
            try
            {
                Check(a, LA.archive_write_set_format_zip(a));
                // Deflate level 9: the smallest zip every unzip tool (incl. Windows Explorer) can open.
                Check(a, LA.archive_write_set_options(a, "zip:compression=deflate,zip:compression-level=9,zip:hdrcharset=UTF-8"));
                Check(a, LA.archive_write_open_filename_w(a, output));

                var buf = new byte[1 << 18];
                long done = 0;
                foreach (var it in items)
                {
                    if (job.Cancelled) return;
                    job.Status(it.Rel);

                    FileStream fs = null;
                    if (!it.IsDir)
                    {
                        try { fs = new FileStream(it.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
                        catch (Exception ex) { warnings.Add(it.Rel + ": " + ex.Message); done += it.Size; continue; }
                    }
                    using (fs)
                    {
                        IntPtr e = LA.archive_entry_new();
                        try
                        {
                            LA.archive_entry_copy_pathname_w(e, it.IsDir ? it.Rel + "/" : it.Rel);
                            LA.archive_entry_set_filetype(e, it.IsDir ? LA.AE_IFDIR : LA.AE_IFREG);
                            LA.archive_entry_set_perm(e, it.IsDir ? 0x1EDu /*0755*/ : 0x1A4u /*0644*/);
                            LA.archive_entry_set_size(e, it.IsDir ? 0 : fs.Length);
                            LA.archive_entry_set_mtime(e, (long)(it.Mtime.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalSeconds, 0);
                            Check(a, LA.archive_write_header(a, e));
                        }
                        finally { LA.archive_entry_free(e); }

                        if (fs == null) continue;
                        int read;
                        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (job.Cancelled) return;
                            long w = (long)LA.archive_write_data(a, buf, (IntPtr)read);
                            if (w < 0) throw new Exception(LA.Error(a));
                            done += read;
                            job.Progress(done, total);
                        }
                    }
                }
                Check(a, LA.archive_write_close(a));
            }
            finally { LA.archive_write_free(a); }
        });

        if (!ok) { TryDeleteFile(output); return 1; }
        ShowWarnings("Some items could not be added:", warnings);
        return 0;
    }

    static void Collect(string full, string rel, List<Item> items, List<string> warnings)
    {
        try
        {
            var attr = File.GetAttributes(full);
            if ((attr & FileAttributes.Directory) != 0)
            {
                var di = new DirectoryInfo(full);
                items.Add(new Item { Full = full, Rel = rel, IsDir = true, Mtime = di.LastWriteTime });
                if ((attr & FileAttributes.ReparsePoint) != 0) return; // don't follow junctions/symlinks (avoids loops)
                foreach (var c in di.EnumerateFileSystemInfos())
                    Collect(c.FullName, rel + "/" + c.Name, items, warnings);
            }
            else
            {
                var fi = new FileInfo(full);
                items.Add(new Item { Full = full, Rel = rel, Size = fi.Length, Mtime = fi.LastWriteTime });
            }
        }
        catch (Exception ex) { warnings.Add(rel + ": " + ex.Message); }
    }

    // ================================================================== extract

    static int Extract(string archive, bool toFolder)
    {
        if (!File.Exists(archive)) { Error("File not found:\n" + archive); return 1; }
        string parent = Path.GetDirectoryName(archive);
        string dest = toFolder ? UniquePath(parent, StripArchiveExt(Path.GetFileName(archive)), "") : parent;
        bool createdDest = !Directory.Exists(dest);
        Directory.CreateDirectory(dest);

        long archiveSize = Math.Max(1, new FileInfo(archive).Length);
        var warnings = new List<string>();
        int overwrite = 0; // 0 = not asked yet, 1 = overwrite all, 2 = skip existing
        bool askedPassword = false;

        bool ok = RunWithProgress("Extracting " + Path.GetFileName(archive), job =>
        {
            IntPtr a = LA.archive_read_new();
            IntPtr ext = LA.archive_write_disk_new();
            string oldCwd = Environment.CurrentDirectory;
            try
            {
                LA.archive_read_support_format_all(a);
                LA.archive_read_support_filter_all(a);
                // Relative paths only, no "..", no writing through symlinks.
                LA.archive_write_disk_set_options(ext, LA.EXTRACT_TIME | LA.EXTRACT_SECURE_SYMLINKS |
                                                       LA.EXTRACT_SECURE_NODOTDOT | LA.EXTRACT_SECURE_NOABSOLUTEPATHS);
                Check(a, LA.archive_read_open_filename_w(a, archive, (IntPtr)(1 << 16)));
                Environment.CurrentDirectory = dest; // libarchive writes relative to the CWD

                while (!job.Cancelled)
                {
                    IntPtr e;
                    int r = LA.archive_read_next_header(a, out e);
                    if (r == LA.EOF) break;
                    if (r == LA.FATAL) throw new Exception(LA.Error(a));
                    if (r < LA.WARN) { warnings.Add(LA.Error(a)); continue; }

                    string name = LA.Str(LA.archive_entry_pathname_w(e)) ?? Marshal.PtrToStringAnsi(LA.archive_entry_pathname(e));
                    if (string.IsNullOrEmpty(name)) { warnings.Add("Entry with unreadable name skipped."); continue; }
                    job.Status(name);

                    if (LA.archive_entry_is_encrypted(e) != 0 && !askedPassword)
                    {
                        askedPassword = true;
                        string pw = job.Ui(() => PasswordPrompt(Path.GetFileName(archive)));
                        if (pw == null) { job.Cancel(); break; }
                        LA.archive_read_add_passphrase(a, LA.Utf8(pw));
                    }

                    string target = Path.Combine(dest, name.Replace('/', '\\'));
                    if (LA.archive_entry_filetype(e) != LA.AE_IFDIR && File.Exists(target))
                    {
                        if (overwrite == 0)
                        {
                            var res = job.Ui(() => MessageBox.Show(job.Form,
                                "Some files already exist, e.g.:\n" + name + "\n\nYes = Overwrite all    No = Skip existing    Cancel = Stop",
                                AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning));
                            if (res == DialogResult.Cancel) { job.Cancel(); break; }
                            overwrite = res == DialogResult.Yes ? 1 : 2;
                        }
                        if (overwrite == 2) continue; // next_header skips the data
                    }

                    r = LA.archive_write_header(ext, e);
                    if (r < LA.WARN) { warnings.Add(name + ": " + LA.Error(ext)); continue; }
                    if (r == LA.WARN) warnings.Add(name + ": " + LA.Error(ext));

                    while (!job.Cancelled)
                    {
                        IntPtr buf, size; long off;
                        r = LA.archive_read_data_block(a, out buf, out size, out off);
                        if (r == LA.EOF) break;
                        if (r < LA.WARN) throw new Exception(name + ": " + LA.Error(a));
                        if ((long)LA.archive_write_data_block(ext, buf, size, off) < LA.WARN)
                            throw new Exception(name + ": " + LA.Error(ext));
                        job.Progress(LA.archive_filter_bytes(a, -1), archiveSize);
                    }
                    LA.archive_write_finish_entry(ext);
                }
            }
            finally
            {
                Environment.CurrentDirectory = oldCwd;
                LA.archive_write_free(ext);
                LA.archive_read_free(a);
            }
        });

        if (!ok && createdDest) { try { Directory.Delete(dest, true); } catch { } }
        if (ok) ShowWarnings("Extracted with warnings:", warnings);
        return ok ? 0 : 1;
    }

    static string StripArchiveExt(string name)
    {
        foreach (string d in DoubleExts)
            if (name.EndsWith(d, StringComparison.OrdinalIgnoreCase) && name.Length > d.Length)
                return name.Substring(0, name.Length - d.Length);
        string n = Path.GetFileNameWithoutExtension(name);
        return n.Length > 0 ? n : name + "_files";
    }

    static string PasswordPrompt(string archiveName)
    {
        using (var f = new Form { Text = AppName, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                  StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(360, 104),
                                  Font = SystemFonts.MessageBoxFont, TopMost = true })
        {
            var lbl = new Label { Text = "Password for " + archiveName + ":", Left = 12, Top = 12, Width = 336, AutoEllipsis = true };
            var box = new TextBox { Left = 12, Top = 36, Width = 336, UseSystemPasswordChar = true };
            var okB = new Button { Text = "OK", Left = 186, Top = 68, Width = 76, DialogResult = DialogResult.OK };
            var noB = new Button { Text = "Cancel", Left = 272, Top = 68, Width = 76, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { lbl, box, okB, noB });
            f.AcceptButton = okB; f.CancelButton = noB;
            return f.ShowDialog() == DialogResult.OK ? box.Text : null;
        }
    }

    // ================================================================== progress window

    class Job
    {
        public Form Form;
        public volatile bool Cancelled;
        public volatile string CurrentText = "";
        public volatile int Permille;
        public void Cancel() { Cancelled = true; }
        public void Status(string s) { CurrentText = s; }
        public void Progress(long done, long total) { Permille = (int)Math.Min(1000, Math.Max(0, done * 1000 / Math.Max(1, total))); }
        public T Ui<T>(Func<T> f) { return (T)Form.Invoke(f); }
    }

    /// Runs work on a background thread with a small progress window (shown only if the job
    /// takes longer than a moment). Returns true on success, false on error or cancel.
    static bool RunWithProgress(string title, Action<Job> work)
    {
        var form = new Form
        {
            Text = AppName, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(440, 118),
            Font = SystemFonts.MessageBoxFont, ShowInTaskbar = true, Opacity = 0
        };
        var lblTitle = new Label { Text = title, Left = 14, Top = 12, Width = 412, AutoEllipsis = true };
        var lblFile = new Label { Left = 14, Top = 34, Width = 412, AutoEllipsis = true, ForeColor = SystemColors.GrayText, UseMnemonic = false };
        var bar = new ProgressBar { Left = 14, Top = 56, Width = 412, Height = 16, Maximum = 1000 };
        var btn = new Button { Text = "Cancel", Left = 346, Top = 82, Width = 80 };
        form.Controls.AddRange(new Control[] { lblTitle, lblFile, bar, btn });
        form.CancelButton = btn;

        var job = new Job { Form = form };
        Exception error = null;
        bool finished = false;

        btn.Click += (s, e) => job.Cancel();
        form.FormClosing += (s, e) => { if (!finished) { job.Cancel(); e.Cancel = true; } };

        var tick = new System.Windows.Forms.Timer { Interval = 120 };
        int ticks = 0;
        tick.Tick += (s, e) =>
        {
            lblFile.Text = job.CurrentText;
            bar.Value = job.Permille;
            if (++ticks == 3) form.Opacity = 1; // ~360 ms
        };

        form.Shown += (s, e) =>
        {
            tick.Start();
            var t = new Thread(() =>
            {
                try { work(job); }
                catch (Exception ex) { error = ex; }
                form.BeginInvoke((Action)(() => { finished = true; tick.Stop(); form.Close(); }));
            });
            t.IsBackground = true;
            t.Start();
        };

        Application.Run(form);
        form.Dispose();

        if (error != null)
        {
            Error(error is DllNotFoundException || error is EntryPointNotFoundException
                ? "Windows' built-in archive library could not be loaded:\n" + error.Message
                : error.Message);
            return false;
        }
        return !job.Cancelled;
    }

    // ================================================================== helpers

    static void Check(IntPtr a, int r)
    {
        if (r < LA.WARN) throw new Exception(LA.Error(a));
    }

    static string UniquePath(string dir, string name, string ext)
    {
        string p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p) || Directory.Exists(p); i++)
            p = Path.Combine(dir, name + " (" + i + ")" + ext);
        return p;
    }

    static void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    static void ShowWarnings(string header, List<string> warnings)
    {
        if (warnings.Count == 0) return;
        string body = string.Join("\n", warnings.Take(15));
        if (warnings.Count > 15) body += "\n... and " + (warnings.Count - 15) + " more";
        MessageBox.Show(header + "\n\n" + body, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static void Error(string msg)
    {
        MessageBox.Show(msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

// ====================================================================== libarchive interop

static class LA
{
    public const string Dll = "archiveint.dll";
    const CallingConvention CC = CallingConvention.Cdecl;

    public const int EOF = 1, OK = 0, WARN = -20, FAILED = -25, FATAL = -30;
    public const uint AE_IFREG = 0x8000, AE_IFDIR = 0x4000;
    public const int EXTRACT_TIME = 0x4, EXTRACT_SECURE_SYMLINKS = 0x100, EXTRACT_SECURE_NODOTDOT = 0x200,
                     EXTRACT_SECURE_NOABSOLUTEPATHS = 0x10000;

    // read
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_read_new();
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_support_format_all(IntPtr a);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_support_filter_all(IntPtr a);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_open_filename_w(IntPtr a, [MarshalAs(UnmanagedType.LPWStr)] string file, IntPtr blockSize);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_next_header(IntPtr a, out IntPtr entry);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_data_block(IntPtr a, out IntPtr buf, out IntPtr size, out long offset);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_add_passphrase(IntPtr a, byte[] pass);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_read_free(IntPtr a);
    [DllImport(Dll, CallingConvention = CC)] public static extern long archive_filter_bytes(IntPtr a, int n);

    // write to disk
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_write_disk_new();
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_disk_set_options(IntPtr a, int flags);
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_write_data_block(IntPtr a, IntPtr buf, IntPtr size, long offset);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_finish_entry(IntPtr a);

    // write archive
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_write_new();
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_set_format_zip(IntPtr a);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_set_options(IntPtr a, [MarshalAs(UnmanagedType.LPStr)] string opts);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_open_filename_w(IntPtr a, [MarshalAs(UnmanagedType.LPWStr)] string file);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_header(IntPtr a, IntPtr entry);
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_write_data(IntPtr a, byte[] buf, IntPtr size);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_close(IntPtr a);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_write_free(IntPtr a);

    // entries
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_entry_new();
    [DllImport(Dll, CallingConvention = CC)] public static extern void archive_entry_free(IntPtr e);
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_entry_pathname_w(IntPtr e);
    [DllImport(Dll, CallingConvention = CC)] public static extern IntPtr archive_entry_pathname(IntPtr e);
    [DllImport(Dll, CallingConvention = CC)] public static extern void archive_entry_copy_pathname_w(IntPtr e, [MarshalAs(UnmanagedType.LPWStr)] string p);
    [DllImport(Dll, CallingConvention = CC)] public static extern ushort archive_entry_filetype(IntPtr e);
    [DllImport(Dll, CallingConvention = CC)] public static extern void archive_entry_set_filetype(IntPtr e, uint type);
    [DllImport(Dll, CallingConvention = CC)] public static extern void archive_entry_set_perm(IntPtr e, uint perm);
    [DllImport(Dll, CallingConvention = CC)] public static extern void archive_entry_set_size(IntPtr e, long size);
    [DllImport(Dll, CallingConvention = CC)] public static extern void archive_entry_set_mtime(IntPtr e, long sec, int nsec);
    [DllImport(Dll, CallingConvention = CC)] public static extern int archive_entry_is_encrypted(IntPtr e);

    [DllImport(Dll, CallingConvention = CC)] static extern IntPtr archive_error_string(IntPtr a);

    public static string Error(IntPtr a)
    {
        string s = Marshal.PtrToStringAnsi(archive_error_string(a));
        return string.IsNullOrEmpty(s) ? "Unknown archive error." : s;
    }
    public static string Str(IntPtr p) { return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p); }
    public static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
}
