using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OldPuckLauncher
{
    static class Program
    {
        const string DepotCommand = "download_depot 2994020 2994021 5369161611927115251";
        const string ManifestId = "5369161611927115251";
        const string DepotId = "2994021";

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072;
            }
            catch (NotSupportedException)
            {
            }

            if (args.Length > 0 && args[0] == "--check")
            {
                string manifestPath = args.Length > 1 ? args[1] : "";
                Environment.ExitCode = SelfCheck(manifestPath);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }

        static string AppDir()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        static int SelfCheck(string manifestPath)
        {
            StringBuilder report = new StringBuilder();
            int failed = 0;
            Action<string> ok = delegate (string line) { report.AppendLine("OK " + line); };
            Action<string> bad = delegate (string line)
            {
                failed++;
                report.AppendLine("FAIL " + line);
            };

            if (KeyTyper.InputBytes == 40) ok("keyboard input size");
            else bad("keyboard input size " + KeyTyper.InputBytes);
            if (Updater.Allowed("Plugins/MenuGate/000000MenuGate.dll")) ok("dll path");
            else bad("dll path");
            if (Updater.Allowed("direct-server.cfg")) ok("cfg path");
            else bad("cfg path");
            if (!Updater.Allowed("Puck.exe")) ok("reject puck");
            else bad("reject puck");
            if (!Updater.Allowed("UnityPlayer.dll")) ok("reject unity");
            else bad("reject unity");
            if (!Updater.Allowed("steam_api64.dll")) ok("reject steam api");
            else bad("reject steam api");
            if (!Updater.Allowed(@"Plugins\MenuGate\..\..\Puck.exe")) ok("reject traversal");
            else bad("reject traversal");
            if (!Updater.Allowed("Plugins/MenuGate/profile.txt")) ok("reject profile");
            else bad("reject profile");
            if (!Updater.Allowed("identity.txt")) ok("reject identity");
            else bad("reject identity");
            if (!Updater.Allowed("menugate-profile.txt")) ok("reject menugate profile");
            else bad("reject menugate profile");
            if (!Updater.Allowed("menugate-identity.txt")) ok("reject menugate identity");
            else bad("reject menugate identity");
            if (!Updater.Allowed("Puck_Data/data.unity3d")) ok("reject data");
            else bad("reject data");
            if (!Updater.Allowed("Plugins/Other/Mod.dll")) ok("reject other plugin");
            else bad("reject other plugin");

            string seedDir = Path.Combine(AppDir(), "files");
            string tempGame = Path.Combine(Path.GetTempPath(), "OldPuckLauncher-seed-" + Process.GetCurrentProcess().Id);
            try
            {
                if (Directory.Exists(tempGame)) Directory.Delete(tempGame, true);
                Directory.CreateDirectory(tempGame);
                string seedError = Updater.CopySeed(seedDir, tempGame, delegate (string line) { report.AppendLine(line); });
                if (seedError != null) bad(seedError);
                else ok("seed copy");
                string dll = Path.Combine(tempGame, "Plugins", "MenuGate", "000000MenuGate.dll");
                string cfg = Path.Combine(tempGame, "direct-server.cfg");
                if (!File.Exists(dll) || !File.Exists(cfg)) bad("seed files missing after copy");
                else ok("seed files exist");
                if (File.Exists(Path.Combine(tempGame, "Puck.exe"))) bad("seed wrote Puck.exe");
                else ok("seed left the game binary alone");
            }
            finally
            {
                try { if (Directory.Exists(tempGame)) Directory.Delete(tempGame, true); }
                catch (IOException) { }
            }

            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
            {
                bad("manifest path");
            }
            else
            {
                string json = File.ReadAllText(manifestPath);
                Manifest manifest = Manifest.Parse(json);
                if (manifest.Files.Count < 2) bad("manifest file count " + manifest.Files.Count);
                else ok("manifest lists " + manifest.Files.Count + " files, version " + manifest.Version);
                string root = Path.GetDirectoryName(manifestPath);
                foreach (ManifestFile file in manifest.Files)
                {
                    if (!Updater.Allowed(file.Path))
                    {
                        bad("manifest listed a blocked path " + file.Path);
                        continue;
                    }
                    string full = Path.Combine(root, file.Path.Replace('/', '\\'));
                    if (!File.Exists(full))
                    {
                        bad("missing " + file.Path);
                        continue;
                    }
                    string hash = Updater.Sha256(full);
                    if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        bad("hash " + file.Path);
                    else
                        ok("hash " + file.Path);
                }

                string serveError = ServeAndDownload(root, report, bad, ok);
                if (serveError != null) bad(serveError);
            }

            string badJson = "{\"version\":\"x\",\"files\":[{\"path\":\"Puck.exe\",\"sha256\":\"" + new string('a', 64) + "\"}]}";
            Manifest evil = Manifest.Parse(badJson);
            if (evil.Files.Count == 1 && !Updater.Allowed(evil.Files[0].Path)) ok("blocked manifest entry");
            else bad("blocked manifest entry");

            string reportPath = Path.Combine(Path.GetTempPath(), "OldPuckLauncher-check.txt");
            File.WriteAllText(reportPath, report.ToString());
            return failed == 0 ? 0 : 1;
        }

        static string ServeAndDownload(string root, StringBuilder report, Action<string> bad, Action<string> ok)
        {
            int port = 49321;
            HttpListener listener = null;
            for (int i = 0; i < 20; i++)
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + (port + i) + "/");
                try
                {
                    listener.Start();
                    port = port + i;
                    break;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    listener = null;
                }
            }
            if (listener == null) return "local update server did not start";

            string dest = Path.Combine(Path.GetTempPath(), "OldPuckLauncher-http-" + Process.GetCurrentProcess().Id);
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext context = listener.GetContext();
                        string rel = context.Request.Url.AbsolutePath.TrimStart('/').Replace('/', '\\');
                        string full = Path.Combine(root, rel);
                        if (rel.IndexOf("..") >= 0 || !File.Exists(full))
                        {
                            context.Response.StatusCode = 404;
                            context.Response.Close();
                            continue;
                        }
                        byte[] bytes = File.ReadAllBytes(full);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/octet-stream";
                        context.Response.ContentLength64 = bytes.Length;
                        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        context.Response.OutputStream.Close();
                    }
                }
                catch (HttpListenerException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            });
            worker.IsBackground = true;
            worker.Start();
            try
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.CreateDirectory(dest);
                string error = Updater.DownloadUpdates("http://127.0.0.1:" + port + "/manifest.json", dest, delegate (string line)
                {
                    report.AppendLine(line);
                });
                if (error != null) return error;
                string dll = Path.Combine(dest, "Plugins", "MenuGate", "000000MenuGate.dll");
                string cfg = Path.Combine(dest, "direct-server.cfg");
                if (!File.Exists(dll) || !File.Exists(cfg)) return "download did not write both files";
                if (File.Exists(Path.Combine(dest, "Puck.exe"))) return "download wrote Puck.exe";
                ok("localhost update");
                return null;
            }
            finally
            {
                try { listener.Stop(); }
                catch (ObjectDisposedException) { }
                listener.Close();
                try { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
                catch (IOException) { }
            }
        }
    }

    static class Updater
    {
        public static bool Allowed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Trim().Replace('\\', '/');
            if (p.Length == 0 || p.Length > 180) return false;
            if (p.IndexOf(':') >= 0 || p.IndexOf('%') >= 0) return false;
            if (p.StartsWith("/") || p.StartsWith("./")) return false;
            string[] parts = p.Split('/');
            if (parts.Length == 0) return false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || parts[i] == "." || parts[i] == "..") return false;
            }
            string lower = p.ToLowerInvariant();
            if (lower == "profile.txt" || lower == "identity.txt") return false;
            if (lower == "menugate-profile.txt" || lower == "menugate-identity.txt") return false;
            if (lower == "puck.exe" || lower == "unityplayer.dll" || lower == "steam_api64.dll") return false;
            if (lower.StartsWith("puck_data/") || lower.StartsWith("monobleedingedge/")) return false;
            if (lower == "direct-server.cfg") return true;
            if (parts.Length == 3
                && string.Equals(parts[0], "Plugins", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[1], "MenuGate", StringComparison.OrdinalIgnoreCase)
                && parts[2].ToLowerInvariant().EndsWith(".dll"))
            {
                string name = parts[2];
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    bool digit = c >= '0' && c <= '9';
                    bool upper = c >= 'A' && c <= 'Z';
                    bool lowerChar = c >= 'a' && c <= 'z';
                    if (!digit && !upper && !lowerChar && c != '.' && c != '_' && c != '-') return false;
                }
                return true;
            }
            return false;
        }

        public static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        public static string CopySeed(string seedDir, string gameDir, Action<string> log)
        {
            string[] rels = new string[] {
                "direct-server.cfg",
                "Plugins/MenuGate/000000MenuGate.dll"
            };
            for (int i = 0; i < rels.Length; i++)
            {
                string error = CopyOne(seedDir, gameDir, rels[i], log);
                if (error != null) return error;
            }
            return null;
        }

        static string CopyOne(string seedDir, string gameDir, string rel, Action<string> log)
        {
            if (!Allowed(rel)) return "Blocked file: " + rel;
            string src = Path.Combine(seedDir, rel.Replace('/', '\\'));
            if (!File.Exists(src)) return "The launcher is missing " + rel + " in its files folder.";
            string dest = Path.Combine(gameDir, rel.Replace('/', '\\'));
            if (SameFile(src, dest))
            {
                log("Already in place: " + rel);
                return null;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(src, dest, true);
            }
            catch (IOException)
            {
                return "Close Puck, then try again. A file is in use: " + rel;
            }
            catch (UnauthorizedAccessException)
            {
                return "Close Puck, then try again. A file is in use: " + rel;
            }
            log("Installed " + rel);
            return null;
        }

        public static string DownloadUpdates(string manifestUrl, string gameDir, Action<string> log)
        {
            if (string.IsNullOrEmpty(manifestUrl)) return "No update host is set in launcher.cfg.";
            string url = manifestUrl.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "manifestUrl has to be an http or https address.";
            int slash = url.LastIndexOf('/');
            if (slash <= "https://".Length) return "manifestUrl has to point at manifest.json.";
            string root = url.Substring(0, slash + 1);

            string json;
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "OldPuckLauncher";
                    json = client.DownloadString(url);
                }
            }
            catch (WebException ex)
            {
                return "Could not read the update manifest. " + ex.Message;
            }

            Manifest manifest = Manifest.Parse(json);
            if (manifest.Files.Count == 0) return "The update manifest did not list any files.";
            for (int i = 0; i < manifest.Files.Count; i++)
            {
                if (!Allowed(manifest.Files[i].Path))
                    return "The update manifest asked for a file this launcher will not write: " + manifest.Files[i].Path;
            }
            if (!string.IsNullOrEmpty(manifest.Version))
                log("Update version is " + manifest.Version);

            for (int i = 0; i < manifest.Files.Count; i++)
            {
                ManifestFile file = manifest.Files[i];
                string dest = Path.Combine(gameDir, file.Path.Replace('/', '\\'));
                if (File.Exists(dest))
                {
                    string have = Sha256(dest);
                    if (string.Equals(have, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        log("Already current: " + file.Path);
                        continue;
                    }
                }
                string fileUrl = root + file.Path.Replace('\\', '/');
                string error = DownloadFile(fileUrl, dest, file.Sha256, file.Path);
                if (error != null) return error;
                if (string.Equals(file.Path, "direct-server.cfg", StringComparison.OrdinalIgnoreCase))
                    log("Updated direct-server.cfg. That file sets the server list host and the tick rate.");
                else
                    log("Updated " + file.Path);
            }
            return null;
        }

        static string DownloadFile(string fileUrl, string dest, string expected, string rel)
        {
            string partial = dest + ".partial";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "OldPuckLauncher";
                    client.DownloadFile(fileUrl, partial);
                }
                string got = Sha256(partial);
                if (!string.Equals(got, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partial);
                    return "Downloaded file did not match the update manifest: " + rel;
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(partial, dest);
                return null;
            }
            catch (IOException)
            {
                TryDelete(partial);
                return "Close Puck, then try again. A file is in use: " + rel;
            }
            catch (UnauthorizedAccessException)
            {
                TryDelete(partial);
                return "Close Puck, then try again. A file is in use: " + rel;
            }
            catch (WebException ex)
            {
                TryDelete(partial);
                return "Could not download " + rel + ". " + ex.Message;
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        static bool SameFile(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static string Apply(string gameDir, string manifestUrl, string seedDir, bool seedIfHostMissing, Action<string> log)
        {
            if (!string.IsNullOrEmpty(manifestUrl))
            {
                string error = DownloadUpdates(manifestUrl, gameDir, log);
                if (error == null) return null;
                if (!seedIfHostMissing) return error;
                log(error);
                log("Using the Puck 202 files shipped with the launcher.");
            }
            else if (!seedIfHostMissing)
            {
                log("No update host is set in launcher.cfg. The game will use the files already in the folder.");
                return null;
            }
            else
            {
                log("No update host is set in launcher.cfg. Installing the Puck 202 files shipped with the launcher.");
            }
            return CopySeed(seedDir, gameDir, log);
        }
    }

    class ManifestFile
    {
        public string Path;
        public string Sha256;
    }

    class Manifest
    {
        public string Version;
        public List<ManifestFile> Files = new List<ManifestFile>();

        public static Manifest Parse(string json)
        {
            Manifest manifest = new Manifest();
            if (string.IsNullOrEmpty(json)) return manifest;
            Match version = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]*)\"");
            if (version.Success) manifest.Version = version.Groups[1].Value;
            MatchCollection files = Regex.Matches(
                json,
                "\"path\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"sha256\"\\s*:\\s*\"([0-9a-fA-F]{64})\"");
            foreach (Match match in files)
            {
                ManifestFile file = new ManifestFile();
                file.Path = match.Groups[1].Value;
                file.Sha256 = match.Groups[2].Value.ToLowerInvariant();
                manifest.Files.Add(file);
            }
            return manifest;
        }
    }

    static class ConfigFile
    {
        public static string Get(string key)
        {
            string path = CfgPath();
            if (!File.Exists(path)) return "";
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string found = line.Substring(0, eq).Trim();
                if (!string.Equals(found, key, StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring(eq + 1).Trim();
            }
            return "";
        }

        public static string GetInstallDir()
        {
            string path = InstallPath();
            if (!File.Exists(path)) return "";
            string dir = File.ReadAllText(path).Trim();
            return dir;
        }

        public static void SetInstallDir(string dir)
        {
            string path = InstallPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, dir);
        }

        static string InstallPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OldPuckLauncher", "install.txt");
        }

        static string CfgPath()
        {
            return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "launcher.cfg");
        }
    }

    static class SteamDepot
    {
        public const string Command = "download_depot 2994020 2994021 5369161611927115251";
        public const string ConsoleUrl = "steam://open/console";
        public const string ManifestId = "5369161611927115251";
        public const string DepotId = "2994021";

        public static void OpenConsole()
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = ConsoleUrl;
            info.UseShellExecute = true;
            Process.Start(info);
        }

        public static string FindExe(out string steamDir)
        {
            steamDir = null;
            string exe = FirstExisting(
                ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
                ExeBeside(ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")),
                ExeBeside(ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")),
                ExeBeside(ReadReg(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")),
                ExeFromProtocol());
            if (string.IsNullOrEmpty(exe)) return null;
            steamDir = Path.GetDirectoryName(exe);
            return exe;
        }

        static string FirstExisting(params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string path = Clean(paths[i]);
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            return "";
        }

        static string ExeBeside(string dir)
        {
            dir = Clean(dir);
            if (string.IsNullOrEmpty(dir)) return "";
            return Path.Combine(dir, "steam.exe");
        }

        static string ExeFromProtocol()
        {
            string command = ReadReg(Registry.ClassesRoot, @"steam\shell\open\command", "");
            if (string.IsNullOrEmpty(command)) return "";
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                if (end > 1) return command.Substring(1, end - 1);
            }
            int space = command.IndexOf(' ');
            if (space > 0) return command.Substring(0, space);
            return command;
        }

        static string ReadReg(RegistryKey hive, string sub, string name)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(sub))
                {
                    if (key == null) return "";
                    object value = key.GetValue(name);
                    return value == null ? "" : value.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        static string Clean(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace('/', '\\');
        }

        public static string CopyTree(string src, string dst)
        {
            if (SameDir(src, dst)) return null;
            if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            info.Arguments = "\"" + src + "\" \"" + dst + "\" /E /R:2 /W:2 /NFL /NDL /NJH /NJS /nc /ns /np /XF profile.txt identity.txt menugate-profile.txt menugate-identity.txt";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            StringBuilder output = new StringBuilder();
            Process proc = Process.Start(info);
            proc.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) output.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) output.AppendLine(e.Data);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            if (proc.ExitCode >= 8)
                return "Could not copy the depot into the folder you chose. " + output.ToString();
            return null;
        }

        public static bool SameDir(string a, string b)
        {
            string left = Path.GetFullPath(a).TrimEnd('\\', '/');
            string right = Path.GetFullPath(b).TrimEnd('\\', '/');
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

    }

    class DepotWatch
    {
        public bool SawDownload;
        public bool Failed;
        public string Failure = "";
        public string CompletePath = "";

        public bool Pull(LogTail tail)
        {
            string text = tail.ReadNew();
            if (!string.IsNullOrEmpty(text))
            {
                if (text.IndexOf("Downloading depot " + SteamDepot.DepotId, StringComparison.Ordinal) >= 0)
                    SawDownload = true;
                Match match = Regex.Match(
                    text,
                    "Depot download complete : \"([^\"]+)\" \\(manifest " + SteamDepot.ManifestId + "\\)");
                if (match.Success) CompletePath = match.Groups[1].Value;
                if (text.IndexOf("Depot download failed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Failed = true;
                    string[] lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf("Depot download failed", StringComparison.OrdinalIgnoreCase) >= 0)
                            Failure = lines[i].Trim();
                    }
                }
            }
            return SawDownload || Failed || !string.IsNullOrEmpty(CompletePath);
        }
    }

    class LogTail
    {
        string path;
        long offset;

        public LogTail(string path)
        {
            this.path = path;
            if (File.Exists(path)) offset = new FileInfo(path).Length;
        }

        public string ReadNew()
        {
            if (!File.Exists(path)) return "";
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length < offset) offset = 0;
                if (stream.Length == offset) return "";
                stream.Seek(offset, SeekOrigin.Begin);
                int count = (int)Math.Min(stream.Length - offset, 200000);
                byte[] buf = new byte[count];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = stream.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                offset = stream.Position;
                return Encoding.UTF8.GetString(buf, 0, read);
            }
        }
    }

    class LauncherForm : Form
    {
        static readonly Color Navy = Color.FromArgb(8, 22, 48);
        static readonly Color NavyButton = Color.FromArgb(12, 36, 72);
        Label titleLabel;
        Panel accentLine;
        Label folderLabel;
        TextBox logBox;
        Button setupButton;
        Button playButton;
        Button checkButton;
        Button cancelButton;
        BackgroundWorker worker;
        volatile bool stopWaiting;
        string chosenDir;
        Image lockerRoom;
        Size backdropSize;
        MemoryStream iconSource;
        Icon windowIcon;

        public LauncherForm()
        {
            Text = "Puck Legacy";
            windowIcon = LoadWindowIcon();
            if (windowIcon != null) Icon = windowIcon;
            Font = new Font("Segoe UI", 9f);
            BackColor = Navy;
            ForeColor = Color.White;
            ClientSize = new Size(880, 540);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            lockerRoom = LoadLockerRoom();
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, true, null);

            titleLabel = new Label();
            titleLabel.Text = "Puck Legacy";
            titleLabel.Font = new Font("Segoe UI", 26f, FontStyle.Bold);
            titleLabel.ForeColor = Color.White;
            titleLabel.BackColor = Color.Transparent;
            titleLabel.Left = 28;
            titleLabel.Top = 16;
            titleLabel.Width = 824;
            titleLabel.Height = 48;
            titleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            accentLine = new Panel();
            accentLine.BackColor = Color.White;
            accentLine.Left = 28;
            accentLine.Top = 68;
            accentLine.Width = 72;
            accentLine.Height = 3;
            accentLine.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            folderLabel = new Label();
            folderLabel.Left = 28;
            folderLabel.Top = 80;
            folderLabel.Width = 824;
            folderLabel.Height = 22;
            folderLabel.ForeColor = Color.White;
            folderLabel.BackColor = Color.Transparent;
            folderLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            logBox = new TextBox();
            logBox.Left = 28;
            logBox.Top = 112;
            logBox.Width = 824;
            logBox.Height = 348;
            logBox.Multiline = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.ReadOnly = true;
            logBox.BackColor = Color.FromArgb(6, 16, 36);
            logBox.ForeColor = Color.White;
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.Font = new Font("Segoe UI", 9f);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            setupButton = MakeButton("setup Puck202", 28, 478, 156, false);
            setupButton.Click += OnSetup;
            checkButton = MakeButton("Check for updates", 196, 478, 168, false);
            checkButton.Click += OnCheck;
            playButton = MakeButton("Play", 376, 478, 120, true);
            playButton.Click += OnPlay;
            cancelButton = MakeButton("Cancel", 508, 478, 120, false);
            cancelButton.Enabled = false;
            cancelButton.Click += delegate { stopWaiting = true; Log("Stopping the wait. Steam may keep downloading."); };

            Controls.Add(titleLabel);
            Controls.Add(accentLine);
            Controls.Add(folderLabel);
            Controls.Add(logBox);
            Controls.Add(setupButton);
            Controls.Add(checkButton);
            Controls.Add(playButton);
            Controls.Add(cancelButton);

            Resize += delegate { RefreshBackdrop(); };
            RefreshBackdrop();

            Load += delegate
            {
                ShowFolder();
                if (string.IsNullOrEmpty(ResolveInstallDir()))
                    Log("setup Puck202 asks where to save the game, downloads the depot through the Steam console, then puts the Puck 202 files into that folder.");
                string url = ConfigFile.Get("manifestUrl");
                if (string.IsNullOrEmpty(url))
                    Log("Update checks stay off until manifestUrl is set in launcher.cfg.");
                else
                    Log("Updates come from " + url);
            };
            FormClosing += delegate
            {
                stopWaiting = true;
                Image background = BackgroundImage;
                BackgroundImage = null;
                if (background != null) background.Dispose();
                if (lockerRoom != null)
                {
                    lockerRoom.Dispose();
                    lockerRoom = null;
                }
                if (windowIcon != null)
                {
                    windowIcon.Dispose();
                    windowIcon = null;
                }
                if (iconSource != null)
                {
                    iconSource.Dispose();
                    iconSource = null;
                }
            };
        }

        static Button MakeButton(string text, int left, int top, int width, bool accent)
        {
            Button button = new Button();
            button.Text = text;
            button.Left = left;
            button.Top = top;
            button.Width = width;
            button.Height = 36;
            button.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.White;
            button.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            button.Tag = accent ? "accent" : "navy";
            if (accent)
            {
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(226, 234, 244);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(198, 214, 232);
            }
            else
            {
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(24, 58, 110);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(8, 22, 48);
            }
            EventHandler paintState = delegate
            {
                if (!button.Enabled)
                {
                    button.BackColor = Color.FromArgb(16, 28, 46);
                    button.ForeColor = Color.FromArgb(130, 146, 166);
                    button.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 112);
                    return;
                }
                button.FlatAppearance.BorderColor = Color.White;
                if ((string)button.Tag == "accent")
                {
                    button.BackColor = Color.White;
                    button.ForeColor = Navy;
                }
                else
                {
                    button.BackColor = NavyButton;
                    button.ForeColor = Color.White;
                }
            };
            button.EnabledChanged += paintState;
            paintState(button, EventArgs.Empty);
            return button;
        }

        Icon LoadWindowIcon()
        {
            try
            {
                byte[] bytes = null;
                string beside = Path.Combine(AppDir(), "puck-legacy.ico");
                if (File.Exists(beside))
                    bytes = File.ReadAllBytes(beside);
                else
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OldPuckLauncher.icon.ico"))
                    {
                        if (stream == null) return null;
                        bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);
                    }
                }
                iconSource = new MemoryStream(bytes);
                return new Icon(iconSource);
            }
            catch (Exception)
            {
                return null;
            }
        }

        Image LoadLockerRoom()
        {
            try
            {
                string beside = Path.Combine(AppDir(), "puck-legacy-locker-room.png");
                if (File.Exists(beside))
                {
                    using (FileStream stream = File.OpenRead(beside))
                    using (Image image = Image.FromStream(stream))
                        return new Bitmap(image);
                }
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OldPuckLauncher.locker.png"))
                {
                    if (stream == null) return null;
                    using (Image image = Image.FromStream(stream))
                        return new Bitmap(image);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        void RefreshBackdrop()
        {
            if (lockerRoom == null || ClientSize.Width < 2 || ClientSize.Height < 2) return;
            if (backdropSize == ClientSize && BackgroundImage != null) return;
            Bitmap frame = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                float scale = Math.Max(frame.Width / (float)lockerRoom.Width, frame.Height / (float)lockerRoom.Height);
                int width = (int)(lockerRoom.Width * scale);
                int height = (int)(lockerRoom.Height * scale);
                graphics.DrawImage(lockerRoom, (frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
                using (SolidBrush wash = new SolidBrush(Color.FromArgb(176, 8, 22, 48)))
                    graphics.FillRectangle(wash, 0, 0, frame.Width, frame.Height);
            }
            Image previous = BackgroundImage;
            BackgroundImageLayout = ImageLayout.None;
            BackgroundImage = frame;
            backdropSize = ClientSize;
            if (previous != null) previous.Dispose();
        }

        void ShowFolder()
        {
            string dir = ResolveInstallDir();
            if (string.IsNullOrEmpty(dir))
                folderLabel.Text = "Game folder: run setup Puck202";
            else
                folderLabel.Text = "Game folder: " + dir;
        }

        string ResolveInstallDir()
        {
            string configured = ConfigFile.GetInstallDir();
            if (!string.IsNullOrEmpty(configured) && File.Exists(Path.Combine(configured, "Puck.exe")))
                return Path.GetFullPath(configured);
            string beside = Path.Combine(AppDir(), "Puck.exe");
            if (File.Exists(beside)) return AppDir();
            return "";
        }

        string AppDir()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        string SeedDir()
        {
            return Path.Combine(AppDir(), "files");
        }

        void OnSetup(object sender, EventArgs e)
        {
            if (worker != null) return;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where to save Puck 202. Puck.exe will go in this folder.";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                chosenDir = dialog.SelectedPath;
            }
            if (string.IsNullOrEmpty(chosenDir)) return;
            StartJob(delegate (object s, DoWorkEventArgs args) { RunSetup(); });
        }

        void OnCheck(object sender, EventArgs e)
        {
            if (worker != null) return;
            StartJob(delegate (object s, DoWorkEventArgs args) { RunUpdate(false); });
        }

        void OnPlay(object sender, EventArgs e)
        {
            if (worker != null) return;
            StartJob(delegate (object s, DoWorkEventArgs args) { RunUpdate(true); });
        }

        void StartJob(DoWorkEventHandler work)
        {
            stopWaiting = false;
            setupButton.Enabled = false;
            playButton.Enabled = false;
            checkButton.Enabled = false;
            cancelButton.Enabled = true;
            worker = new BackgroundWorker();
            worker.DoWork += work;
            worker.RunWorkerCompleted += delegate
            {
                worker = null;
                if (IsDisposed) return;
                setupButton.Enabled = true;
                playButton.Enabled = true;
                checkButton.Enabled = true;
                cancelButton.Enabled = false;
                ShowFolder();
            };
            worker.RunWorkerAsync();
        }

        void RunUpdate(bool startGame)
        {
            string dir = ResolveInstallDir();
            if (string.IsNullOrEmpty(dir))
            {
                Log("Run setup Puck202 before playing. That downloads the depot and installs the Puck 202 files.");
                return;
            }
            string error = Updater.Apply(dir, ConfigFile.Get("manifestUrl"), SeedDir(), false, Log);
            if (error != null)
            {
                Log(error);
                return;
            }
            if (!startGame)
            {
                Log("Update check finished.");
                return;
            }
            StartGame(dir);
        }

        void StartGame(string dir)
        {
            string puck = Path.Combine(dir, "Puck.exe");
            if (!File.Exists(puck))
            {
                Log("Puck.exe is not in the game folder yet.");
                return;
            }
            if (Process.GetProcessesByName("steam").Length == 0)
            {
                Log("Starting Steam.");
                try
                {
                    ProcessStartInfo steamInfo = new ProcessStartInfo();
                    steamInfo.FileName = "steam://open/main";
                    steamInfo.UseShellExecute = true;
                    Process.Start(steamInfo);
                    Thread.Sleep(2000);
                }
                catch (Win32Exception)
                {
                    Log("Steam is not installed.");
                    return;
                }
            }
            Log("Starting Puck.");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = puck;
            info.WorkingDirectory = dir;
            info.UseShellExecute = true;
            Process.Start(info);
        }

        void RunSetup()
        {
            string dest = chosenDir;
            Log("Save location: " + dest);
            Log("Steam downloads this depot into its own steamapps\\content folder. After that, the launcher copies it into the folder you chose and adds the Puck 202 files.");
            Log("Opening the Steam console with steam://open/console.");
            try
            {
                SteamDepot.OpenConsole();
            }
            catch (Win32Exception)
            {
                Log("Steam is not installed, so the depot download cannot start.");
                return;
            }

            if (!Wait(60000, delegate { return FindSteamWindow() != IntPtr.Zero; }))
            {
                if (stopWaiting) return;
                Log("Steam did not open a window. Start Steam, then press setup Puck202 again.");
                return;
            }
            string steamDir;
            SteamDepot.FindExe(out steamDir);
            if (string.IsNullOrEmpty(steamDir))
            {
                Log("The Steam console is open. This PC's Steam folder could not be found, so paste this command there:");
                Log(SteamDepot.Command);
                return;
            }
            string logPath = Path.Combine(steamDir, "logs", "console_log.txt");
            LogTail tail = new LogTail(logPath);
            DepotWatch watch = new DepotWatch();
            Thread.Sleep(1500);
            if (stopWaiting) return;
            Log("Typing the depot command into the Steam window.");
            SendCommand();
            Log("Running the Steam console command: " + SteamDepot.Command);

            if (!Wait(20000, delegate { return watch.Pull(tail); }))
            {
                if (stopWaiting || watch.Failed)
                {
                    ReportFailure(watch.Failed, watch.Failure);
                    return;
                }
                Log("Sending the depot command again.");
                SendCommand();
                Wait(15000, delegate { return watch.Pull(tail); });
            }

            if (watch.Failed)
            {
                ReportFailure(true, watch.Failure);
                return;
            }
            if (watch.SawDownload && string.IsNullOrEmpty(watch.CompletePath))
                Log("Steam is downloading depot 2994021.");
            if (string.IsNullOrEmpty(watch.CompletePath) && !watch.SawDownload)
            {
                Log("The Steam console did not start the download. Paste this command into the Steam console and leave this window open:");
                Log(SteamDepot.Command);
            }

            bool finished = Wait(6 * 60 * 60 * 1000, delegate
            {
                bool started = watch.SawDownload;
                watch.Pull(tail);
                if (!started && watch.SawDownload) Log("Steam is downloading depot 2994021.");
                if (watch.Failed) return true;
                if (!string.IsNullOrEmpty(watch.CompletePath)) return true;
                return false;
            });
            if (stopWaiting) return;
            if (watch.Failed)
            {
                ReportFailure(true, watch.Failure);
                return;
            }
            if (!finished || string.IsNullOrEmpty(watch.CompletePath))
            {
                Log("The depot download did not finish. When the Steam console says Depot download complete, press setup Puck202 again and choose the same folder.");
                return;
            }

            string completePath = watch.CompletePath;
            Log("Steam finished the depot download.");
            if (!File.Exists(Path.Combine(completePath, "Puck.exe")))
            {
                Log("The downloaded folder does not contain Puck.exe: " + completePath);
                return;
            }

            string manifestUrl = ConfigFile.Get("manifestUrl");
            string seed = SeedDir();
            string modsError = Updater.Apply(completePath, manifestUrl, seed, true, Log);
            if (modsError != null)
            {
                Log(modsError);
                return;
            }

            if (!SteamDepot.SameDir(completePath, dest))
            {
                Log("Copying the depot into the folder you chose.");
                string copyError = SteamDepot.CopyTree(completePath, dest);
                if (copyError != null)
                {
                    Log(copyError);
                    return;
                }
                modsError = Updater.Apply(dest, manifestUrl, seed, true, Log);
                if (modsError != null)
                {
                    Log(modsError);
                    return;
                }
            }

            if (!File.Exists(Path.Combine(dest, "Puck.exe")))
            {
                Log("Puck.exe is not in the folder you chose.");
                return;
            }
            ConfigFile.SetInstallDir(dest);
            Log("The Puck 202 files are in that depot. Play checks for newer copies of those files and downloads them before the game starts.");
        }

        void ReportFailure(bool failed, string failure)
        {
            if (!failed) return;
            if (!string.IsNullOrEmpty(failure)
                && failure.Length < 180
                && failure.IndexOf("ticket", StringComparison.OrdinalIgnoreCase) < 0
                && failure.IndexOf("token", StringComparison.OrdinalIgnoreCase) < 0)
                Log("Steam reported: " + failure);
            else
                Log("Steam reported the depot download failed.");
        }

        void SendCommand()
        {
            if (IsDisposed) return;
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    try { Clipboard.SetText(SteamDepot.Command); }
                    catch (ExternalException) { }
                    IntPtr hwnd = FindSteamWindow();
                    if (hwnd == IntPtr.Zero) return;
                    KeyTyper.Focus(hwnd);
                    Thread.Sleep(400);
                    KeyTyper.TypeLine(SteamDepot.Command);
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        static IntPtr FindSteamWindow()
        {
            SteamWindowSearch search = new SteamWindowSearch();
            EnumWindowsProc proc = search.OnWindow;
            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return search.Handle;
        }

        bool Wait(int timeoutMs, Func<bool> done)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (stopWaiting) return false;
                if (done()) return true;
                Thread.Sleep(500);
                waited += 500;
            }
            return done();
        }

        void Log(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), message); }
                catch (InvalidOperationException) { }
                return;
            }
            logBox.AppendText(message + Environment.NewLine);
        }

        internal static bool IsVisible(IntPtr hwnd)
        {
            return IsWindowVisible(hwnd);
        }

        internal static void ReadTitle(IntPtr hwnd, StringBuilder title)
        {
            GetWindowText(hwnd, title, title.Capacity);
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);
    }

    class SteamWindowSearch
    {
        public IntPtr Handle;

        public bool OnWindow(IntPtr hwnd, IntPtr lParam)
        {
            if (!LauncherForm.IsVisible(hwnd)) return true;
            StringBuilder title = new StringBuilder(512);
            LauncherForm.ReadTitle(hwnd, title);
            if (title.ToString() != "Steam") return true;
            Handle = hwnd;
            return false;
        }
    }

    static class KeyTyper
    {
        const uint InputKeyboard = 1;
        const uint KeyUp = 2;
        const uint Unicode = 4;

        public static int InputBytes
        {
            get { return Marshal.SizeOf(typeof(INPUT)); }
        }

        public static void Focus(IntPtr hwnd)
        {
            uint processId;
            uint target = GetWindowThreadProcessId(hwnd, out processId);
            uint current = GetCurrentThreadId();
            bool attached = false;
            if (target != current) attached = AttachThreadInput(current, target, true);
            ShowWindow(hwnd, 9);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(current, target, false);
        }

        public static int TypeLine(string text)
        {
            INPUT[] keys = new INPUT[(text.Length * 2) + 2];
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                keys[n++] = Character(text[i], true);
                keys[n++] = Character(text[i], false);
            }
            keys[n++] = Enter(true);
            keys[n++] = Enter(false);
            return (int)SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT)));
        }

        static INPUT Character(char c, bool down)
        {
            INPUT input = new INPUT();
            input.type = InputKeyboard;
            input.wScan = c;
            input.dwFlags = Unicode;
            if (!down) input.dwFlags = Unicode | KeyUp;
            return input;
        }

        static INPUT Enter(bool down)
        {
            INPUT input = new INPUT();
            input.type = InputKeyboard;
            input.wVk = 0x0D;
            if (!down) input.dwFlags = KeyUp;
            return input;
        }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public ushort wVk;
            [FieldOffset(10)] public ushort wScan;
            [FieldOffset(12)] public uint dwFlags;
            [FieldOffset(16)] public uint time;
            [FieldOffset(20)] public IntPtr extra;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint from, uint to, bool attach);
    }
}
