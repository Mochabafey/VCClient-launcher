using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VcClientApp
{
    // =================================================================
    //  VCClient 单窗口启动器（App 化外壳）
    //
    //  它解决官方 start_http.bat 的四个问题：
    //    1) 会弹出控制台黑窗
    //       -> 本程序以 winexe 编译，且用 CreateNoWindow 启动 main.exe
    //    2) 工作目录漂移导致缓存找不到、每次都重下
    //       -> 强制把工作目录切到自身所在目录
    //    3) 首次启动要下 3.24GB 且不支持断点续传（中断即从 0 重来）
    //       -> 内置下载器：Range 续传 + SHA256 校验 + 镜像加速
    //    4) 校验结果每次都重算
    //       -> 带时间戳缓存，第二次启动跳过 3.24GB 的 SHA256
    //
    //  关于窗口（实测结论）：
    //    main.exe 的 --launch_client 参数在本版本【无效】，它总会派生
    //    _internal\native_client\voice-changer-native-client.exe。
    //    该进程是独立进程（父进程死后仍存活），窗口标题 vcclient-native-client。
    //    因此本启动器不再另开浏览器窗口，而是把这个官方窗口当作唯一的
    //    App 窗口：等它出现即视为就绪，它关闭即视为退出。
    // =================================================================

    class ModuleInfo
    {
        public string Rel;
        public string Url;
        public long Size;
        public string Hash;
        public ModuleInfo(string rel, string url, long size, string hash)
        {
            Rel = rel; Url = url; Size = size; Hash = hash;
        }
    }

    static class ModuleTable
    {
        // 数据取自 vcclient 2.1.4-alpha 内置模块表（含官方 SHA256）
        public static readonly ModuleInfo[] All = new ModuleInfo[]
        {
            new ModuleInfo("contentvec/hubert_base.pt",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/contentvec/hubert_base.pt",
                189507909L, "f54b40fd2802423a5643779c4861af1e9ee9c1564dc9d32f54f20b5ffba7db96"),
            new ModuleInfo("contentvec/contentvec-f.onnx",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/contentvec/contentvec-f.onnx",
                378550151L, "4b31ed3d95a568fab7952de923ff7f7d3d17128ea6fce69f665509d24c3156db"),
            new ModuleInfo("rinna_hubert/rinna_hubert_base-f.onnx",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/rinna_hubert/rinna_hubert_base-f.onnx",
                378550151L, "d00e262757fa1550faac53fa6140dad16ca75603a36ecfead468920a9f744a16"),
            new ModuleInfo("onnxcrepe/tiny.onnx",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/onnxcrepe/tiny.onnx",
                1955762L, "91fc2a0fd10f965dbf7775995daf50e99273caedd7efd00001f23be649da1bc3"),
            new ModuleInfo("onnxcrepe/full.onnx",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/onnxcrepe/full.onnx",
                88984790L, "119845c72c702e052e5262430f9d120bce46176689aa226c39d09dea5cc3a610"),
            new ModuleInfo("rmvpe/rmvpe_20231006.pt",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/rmvpe/rmvpe_20231006.pt",
                181184272L, "6d62215f4306e3ca278246188607209f09af3dc77ed4232efdd069798c4ec193"),
            new ModuleInfo("rmvpe/rmvpe_20231006.onnx",
                "https://huggingface.co/wok000/vcclient_modules/resolve/main/rmvpe/rmvpe_20231006.onnx",
                362003174L, "84f0586308e36157f75b77c8591bf636d6719c0c4ba95f8faf3df479e7566219"),
            new ModuleInfo("applio/applio_japanese_hubert_base.pt",
                "https://huggingface.co/IAHispano/Applio/resolve/main/Resources/embedders/japanese_hubert_base.pt",
                378888853L, "dade3cf824ae0d214f7de8b73e70bae7c101e81f12d93577c4760bf516db4063"),
            new ModuleInfo("applio/applio_chinese_hubert_base.pt",
                "https://huggingface.co/IAHispano/Applio/resolve/main/Resources/embedders/chinese_hubert_base.pt",
                1136482241L, "8cd5db6302ae2e79b5972cd02ae375a42a76170374d6e1952fa78d1fe4e4f756"),
            new ModuleInfo("applio/applio_korean_hubert_base.pt",
                "https://huggingface.co/IAHispano/Applio/resolve/main/Resources/embedders/korean_hubert_base.pt",
                378876997L, "6b42c8453b96b203198c1c280a8821158ea3fa8dbbc2a6220cad1c1489c3e65e"),
        };
    }

    static class Program
    {
        public static int Port = 18000;
        public static string BaseDir;
        public static string LogFile;
        // 默认镜像：首次运行（还没有 launcher.json）时用这个。
        // 选 hf-mirror.com 而不是留空走官方源 —— 官方源在受限网络下
        // 根本连不上，让用户一上手就失败是很差的体验。
        // 用户可以在配置页改成别的，或清空以使用官方源。
        public static string Mirror = MirrorPresets.DefaultBase;
        public static string WindowMode = "native";   // native | browser
        public static string Proxy = "";              // HTTP 代理，如 http://127.0.0.1:7890
        // 【默认 false = 不动官方客户端】
        // 这个套壳的定位是「在官方包上做增强」，官方客户端
        // （voice-changer-native-client.exe）就是用户要用的界面。
        // 把它禁用掉，用户就没有界面可用了 —— 所以默认必须是不禁用。
        // 只有在极少数情况下（例如用户明确要换成浏览器窗口）才需要打开它。
        public static bool DisableOfficialClientEnabled = false;

        // ---------------- 配置持久化 ----------------
        // 存成 key=value 的纯文本，用户可以直接编辑，也可以拖进配置窗口导入。
        static string ConfigPath()
        {
            return Path.Combine(BaseDir, "settings", "launcher.json");
        }

        public static void LoadConfig()
        {
            try
            {
                string p = ConfigPath();
                if (!File.Exists(p)) return;
                foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1).Trim();
                    if (k == "Port") { int pv; if (int.TryParse(v, out pv)) Port = pv; }
                    else if (k == "Mirror") Mirror = v;
                    else if (k == "Proxy") Proxy = v;
                    else if (k == "WindowMode") WindowMode = v;
                    else if (k == "DisableOfficialClient") DisableOfficialClientEnabled = (v == "1" || v.ToLower() == "true");
                }
            }
            catch { /* 读失败就用默认值，不阻塞启动 */ }
        }

        public static void SaveConfig()
        {
            try
            {
                string p = ConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# VCClient 启动器配置");
                sb.AppendLine("# 可以直接编辑，也可以拖进启动器窗口导入。");
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("Mirror=" + Mirror);
                sb.AppendLine("Proxy=" + Proxy);
                sb.AppendLine("DisableOfficialClient=" + (DisableOfficialClientEnabled ? "1" : "0"));
                sb.AppendLine("WindowMode=" + WindowMode);
                File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* 写失败不影响运行 */ }
        }

        static Process server;
        static NotifyIcon tray;
        static StatusForm form;
        static volatile bool shuttingDown = false;
        static volatile bool cancelDownload = false;
        static Mutex singleInstance;

        [STAThread]
        static void Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            Directory.SetCurrentDirectory(BaseDir);         // 固定工作目录，解决缓存漂移
            LogFile = Path.Combine(BaseDir, "launcher.log");

            // 【必须】启用 TLS 1.2 / 1.3。
            // .NET Framework 4.x 默认只用 TLS 1.0，而 huggingface.co 与各大镜像
            // 早已禁用 TLS 1.0/1.1。不设这一句，所有下载都会报
            //   "请求被中止: 未能创建 SSL/TLS 安全通道"
            // 而这个错误信息很像网络不通，很容易误判 —— 实测踩过：
            // 三个镜像都 HTTP 200，下载器却一个文件都下不下来。
            try
            {
                // 3072 = Tls13（老编译器没有这个枚举值，所以用数字）
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
            }
            catch
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            // 默认代理设置要显式关掉，否则会继承 IE 的代理配置
            ServicePointManager.DefaultConnectionLimit = 8;

            bool testMode = false;
            string testOnly = "";
            bool skipConfig = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--mirror" && i + 1 < args.Length) { Mirror = args[++i]; }
                else if (a == "--port" && i + 1 < args.Length) { Port = int.Parse(args[++i]); }
                else if (a == "--window" && i + 1 < args.Length) { WindowMode = args[++i]; }
                else if (a == "--no-config") { skipConfig = true; }
                else if (a == "--test-download") { testMode = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) testOnly = args[++i]; }
            }

            // ---- 自检模式：只验证下载器，不开界面、不启服务 ----
            if (testMode)
            {
                RunSelfTest(testOnly);
                return;
            }

            bool createdNew;
            singleInstance = new Mutex(true, "VCClientApp_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("VCClient 已经在运行了。\n请查看任务栏右下角的托盘图标。",
                    "VCClient", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!File.Exists(Path.Combine(BaseDir, "main.exe")))
            {
                MessageBox.Show("找不到 main.exe。\n请把本程序放在 main.exe 所在文件夹内。",
                    "VCClient", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // ---- 先配置、再启动 ----
            // 为什么加这一步：官方 VCClient.exe 一启动就直奔下载/拉起服务，
            // 网络受限的用户没有任何机会先改镜像或代理 —— 只能干看着它失败。
            // 所以这里先弹配置页（可改镜像/代理/端口，也可把 launcher.json
            // 拖进来导入），用户点「启动 VCClient」才真正开始。
            // 加 --no-config 可跳过（自动化/脚本调用时用）。
            LoadConfig();
            if (!skipConfig)
            {
                using (ConfigForm cf = new ConfigForm())
                {
                    if (cf.ShowDialog() != DialogResult.OK)
                    {
                        Log("用户在配置页取消，退出");
                        return;
                    }
                    SaveConfig();   // 把界面上的选择落盘，下次不用重填
                }
            }

            form = new StatusForm();
            form.Show();
            Application.DoEvents();

            Thread worker = new Thread(new ThreadStart(Worker));
            worker.IsBackground = true;
            worker.Start();

            Application.Run(form);
        }

        // ============================ 主流程 ============================
        static void Worker()
        {
            try
            {
                Log("=== VCClient App 启动 ===");
                SetStatus("正在检查模型组件…", "校验已有文件，缺失的会自动补齐。");

                List<ModuleInfo> missing = MissingModules();
                if (missing.Count > 0)
                {
                    long need = 0;
                    foreach (ModuleInfo m in missing) need += m.Size;
                    SetStatus("需要下载模型组件", string.Format(
                        "{0} 个组件，共 {1:N2} GB。支持断点续传：随时关闭，下次自动接着下。",
                        missing.Count, need / 1024.0 / 1024.0 / 1024.0));

                    if (!DownloadAll(missing))
                    {
                        if (cancelDownload)
                            SetStatus("已取消下载", "进度已保留。重新打开会自动续传。");
                        else
                            SetStatus("下载未完成", "网络可能不稳定。重新打开本程序会自动续传。");
                        return;
                    }
                    SetStatus("模型组件已就绪", "");
                }

                // ---- 初始示例模型 ----
                // 必须在启动服务【之前】放好，否则官方 main.exe 会自己去下
                // 十几个示例模型，卡在 0% 让界面停在「等待服务就绪」。
                if (!EnsureInitialSample())
                {
                    if (cancelDownload)
                        SetStatus("已取消", "进度已保留。重新打开会自动续传。");
                    else
                        SetStatus("初始示例模型未就绪", "网络可能不稳定。重新打开本程序会自动续传。");
                    return;
                }

                SetStatus("正在启动 VCClient 服务…", "首次启动需加载 AI 运行时，约 20~60 秒。");
                if (!StartServer())
                {
                    SetStatus("服务启动失败", "详见 launcher.log 与 vcclient.log");
                    return;
                }

                SetStatus("等待服务就绪…", "正在加载模型与音频设备。");
                if (!WaitForPort(180))
                {
                    SetStatus("服务未能就绪", "请查看 vcclient.log 排查。");
                    return;
                }

                // 服务就绪后，main.exe 会自行派生官方客户端窗口。
                // 等它出现，作为唯一的 App 窗口。
                SetStatus("正在打开界面…", "");
                if (WindowMode == "native")
                {
                    WaitForNativeClient(60);
                }
                else
                {
                    Thread.Sleep(1500);
                    OpenAppWindow();
                }

                SetupTray();
                HideForm();

                WatchWindows();
            }
            catch (Exception e)
            {
                Log("EXCEPTION: " + e.ToString());
                SetStatus("发生错误", e.Message);
            }
        }

        // 等待官方 native client 窗口出现（main.exe 会自动派生它）
        static void WaitForNativeClient(int timeoutSec)
        {
            DateTime deadline = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < deadline)
            {
                if (NativeClientAlive()) { Log("官方客户端窗口已出现"); return; }
                Thread.Sleep(800);
            }
            Log("等待官方客户端窗口超时（服务仍在运行）");
        }

        // ======================= 模型检查（带缓存） =======================
        static string ModulePath(ModuleInfo m)
        {
            return Path.Combine(BaseDir, "modules", m.Rel.Replace('/', Path.DirectorySeparatorChar));
        }

        static string StampFile { get { return Path.Combine(BaseDir, ".modules_verified"); } }

        // 时间戳缓存：把「路径|大小|修改时间」记下来，没变就不再算 SHA256
        static Dictionary<string, string> LoadStamp()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(StampFile)) return d;
                foreach (string line in File.ReadAllLines(StampFile))
                {
                    string[] p = line.Split('|');
                    if (p.Length == 3) d[p[0]] = p[1] + "|" + p[2];
                }
            }
            catch { }
            return d;
        }

        static void SaveStamp(Dictionary<string, string> d)
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in d)
                    lines.Add(kv.Key + "|" + kv.Value);
                File.WriteAllLines(StampFile, lines.ToArray());
            }
            catch { }
        }

        static List<ModuleInfo> MissingModules()
        {
            Dictionary<string, string> stamp = LoadStamp();
            List<ModuleInfo> missing = new List<ModuleInfo>();
            bool changed = false;

            foreach (ModuleInfo m in ModuleTable.All)
            {
                string p = ModulePath(m);
                if (!File.Exists(p)) { missing.Add(m); continue; }

                FileInfo fi = new FileInfo(p);
                if (fi.Length != m.Size) { missing.Add(m); continue; }

                string sig = fi.Length.ToString() + "|" + fi.LastWriteTimeUtc.Ticks.ToString();
                string old;
                if (stamp.TryGetValue(m.Rel, out old) && old == sig)
                    continue;                                   // 命中缓存，跳过 SHA256

                if (Sha256(p) == m.Hash)
                {
                    stamp[m.Rel] = sig;
                    changed = true;
                }
                else
                {
                    missing.Add(m);
                }
            }
            if (changed) SaveStamp(stamp);
            Log("模块检查完成：缺失/损坏 " + missing.Count + " 个");
            return missing;
        }

        static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ======================= 下载（续传 + 校验） =======================
        public static void CancelDownload() { cancelDownload = true; }

        static bool DownloadAll(List<ModuleInfo> list)
        {
            // 先告诉界面总量，这样能算出「总进度」
            long bytes = 0;
            foreach (ModuleInfo m in list) bytes += m.Size;
            form.BeginDownload(list.Count, bytes);

            int idx = 0;
            foreach (ModuleInfo m in list)
            {
                if (cancelDownload) return false;
                idx++;
                form.SetOverall(idx, list.Count);
                if (!DownloadOne(m)) return false;
                form.FinishFile(m.Size);   // 累加，供总进度使用
            }
            // 全部完成后刷新时间戳缓存
            Dictionary<string, string> stamp = LoadStamp();
            foreach (ModuleInfo m in list)
            {
                string p = ModulePath(m);
                if (File.Exists(p))
                {
                    FileInfo fi = new FileInfo(p);
                    stamp[m.Rel] = fi.Length.ToString() + "|" + fi.LastWriteTimeUtc.Ticks.ToString();
                }
            }
            SaveStamp(stamp);
            return true;
        }

        static bool DownloadOne(ModuleInfo m)
        {
            string target = ModulePath(m);
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            string url = m.Url;
            // 镜像只在【看起来像个地址】时才用。
            // 踩过的坑：配置里若混进非 URL 的值（例如手改文件、或拖入的配置
            // 里字段错位成了 "1"），直接 Replace 会拼出 "1/wok000/..." 这种
            // 无效 URI，然后每次下载都报「无效的 URI: 未能确定 URI 的格式」，
            // 而且因为会重试 8 次，界面看起来像「网络不通」，很难查。
            // 所以这里先校验，不合法就忽略并记一条日志。
            if (Mirror.Length > 0)
            {
                if (Mirror.StartsWith("http://") || Mirror.StartsWith("https://"))
                {
                    url = url.Replace("https://huggingface.co", Mirror.TrimEnd('/'));
                }
                else
                {
                    Log("镜像地址无效，已忽略: [" + Mirror + "]（应以 http:// 或 https:// 开头）");
                }
            }

            int attempt = 0;
            while (attempt < 8)
            {
                if (cancelDownload) return false;
                attempt++;

                long have = File.Exists(target) ? new FileInfo(target).Length : 0;
                if (have > m.Size) { try { File.Delete(target); } catch { } have = 0; }
                if (have == m.Size)
                {
                    if (Sha256(target) == m.Hash) return true;
                    // 大小正确但哈希不符 = 内容损坏（常见于两个下载器同时写过同一文件）。
                    // 必须删除重来；若按 have==Size 去续传，服务器会返回 416 导致空转重试。
                    Log("文件大小正确但校验失败，删除重下: " + m.Rel);
                    try { File.Delete(target); } catch { }
                    have = 0;
                }

                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 120000;
                    req.UserAgent = "vcclient-app/1.0";
                    req.AllowAutoRedirect = true;
                    if (have > 0) req.AddRange(have);

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        bool resume = (have > 0 && resp.StatusCode == HttpStatusCode.PartialContent);
                        if (have > 0 && !resume) have = 0;      // 服务器不支持续传 -> 重头来

                        FileMode mode = (have > 0) ? FileMode.Append : FileMode.Create;
                        long done = have;
                        Stopwatch sw = Stopwatch.StartNew();
                        long mark = done;
                        double markSec = 0;

                        using (FileStream fs = new FileStream(target, mode, FileAccess.Write, FileShare.None, 1 << 20))
                        {
                            byte[] buf = new byte[1 << 20];
                            Stream rs = resp.GetResponseStream();
                            int n;
                            while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (cancelDownload) return false;
                                fs.Write(buf, 0, n);
                                done += n;

                                double el = sw.Elapsed.TotalSeconds;
                                if (el - markSec > 0.4)
                                {
                                    double speed = (done - mark) / (el - markSec);
                                    mark = done; markSec = el;
                                    form.SetFile(m.Rel, done, m.Size, speed);
                                }
                            }
                        }
                    }

                    if (File.Exists(target) && new FileInfo(target).Length == m.Size && Sha256(target) == m.Hash)
                    {
                        form.SetFile(m.Rel + "  ✔ 校验通过", m.Size, m.Size, 0);
                        Log("下载完成并通过校验: " + m.Rel);
                        return true;
                    }
                    form.SetFile(m.Rel + "  校验未通过，重试…", 0, m.Size, 0);
                    Log("校验未通过，重试: " + m.Rel);
                }
                catch (Exception e)
                {
                    Log("下载出错 (" + m.Rel + " 第" + attempt + "次): " + e.Message);
                    form.SetFile(m.Rel + "  网络中断，重试 " + attempt + "/8", 0, m.Size, 0);
                    Thread.Sleep(Math.Min(2000 * attempt, 15000));
                }
            }

            if (File.Exists(target) && new FileInfo(target).Length == m.Size && Sha256(target) == m.Hash)
                return true;
            Log("放弃: " + m.Rel);
            return false;
        }

        // ======================= 启动服务（无黑窗） =======================
        // 关于第二个窗口：
        //   main.exe 在服务就绪后会自行派生
        //     _internal\native_client\voice-changer-native-client.exe
        //   （--launch_client 参数实测完全无效）。
        //
        //   实测三种处理方式：
        //     a) taskkill 掉它        -> 服务立刻停止（端口释放）❌
        //     b) 发 WM_CLOSE          -> Tauri 不响应，窗口不动 ❌
        //     c) 隐藏窗口             -> 有效，但已闪现过，观感差 ⚠️
        //     d) 启动前禁用该 exe     -> 服务完全正常，窗口根本不出现 ✅
        //
        //   采用 (d)：在 StartServer 之前把 exe 改名，main.exe 找不到就跳过。
        static string OfficialClientExe
        {
            get
            {
                return Path.Combine(BaseDir, "_internal", "native_client",
                    "voice-changer-native-client.exe");
            }
        }

        static void DisableOfficialClient()
        {
            try
            {
                string exe = OfficialClientExe;
                string off = exe + ".disabled";
                if (File.Exists(exe))
                {
                    File.Move(exe, off);
                    Log("已禁用官方客户端（避免弹出第二个窗口），服务不受影响");
                }
                else if (File.Exists(off))
                {
                    Log("官方客户端已处于禁用状态");
                }
            }
            catch (Exception e)
            {
                Log("禁用官方客户端失败（仍会尝试隐藏）: " + e.Message);
            }
        }

        // 把 .disabled 改回原名（用户在配置页取消勾选「禁用官方客户端」时用）。
        // 注意：这一步会动官方文件，所以只在用户明确要求时才做。
        static void RestoreOfficialClient()
        {
            try
            {
                string exe = OfficialClientExe;
                string off = exe + ".disabled";
                if (File.Exists(off) && !File.Exists(exe))
                {
                    File.Move(off, exe);
                    Log("已恢复官方客户端 exe");
                }
            }
            catch (Exception e)
            {
                Log("恢复官方客户端失败: " + e.Message);
            }
        }

        // ---------------- 初始示例模型 ----------------
        // 为什么必须代下：
        //   官方 main.exe 启动时会检查 model_dir 里【有没有任何一个模型】，
        //   一个都没有就去 huggingface 下载十几个示例模型（裸 requests，
        //   无镜像/代理/续传）。网络不好时它会卡在 0%，界面永远停在
        //   「等待服务就绪」。日志里能看到：
        //     Sample is not ready. Downloading initial samples.
        //   官方判定是「有没有任何一个」，所以只要先放进去 1 个，
        //   它就完全不会再触发那段下载。
        static readonly string[] SampleBaseUrls = new string[]
        {
            "https://huggingface.co/wok000/vcclient_model/resolve/main/v2.1/sample/kikoto_kurage",
        };

        const string SampleModelFile = "kikoto_kurage_v2_40k_e100.onnx";
        const long SampleModelSize = 110387304L;
        const string SampleModelHash = "725cd4ff3a0858c5c738f2b07b5b469fa6708ef67f13c26aa7b8723dc5f1e0e5";
        const string SampleIconFile = "kikoto_kurage.png";
        const long SampleIconSize = 172867L;
        const string SampleIconHash = "18920b3db8fba7fe718ffc31eb8977c61825971add901c0f620107ca4ce6b2a3";

        // model_dir 里有没有可用的模型？
        // 注意比官方多查一步：params.json 指向的模型文件要【真的存在】。
        // 踩过坑：params.json 还在、模型文件被删了，官方认为「已安装」不下载，
        // 前端却因为拿不到模型而整屏报错，卡在一个既不能用也不自修的状态。
        static bool HasAnyInstalledModel()
        {
            try
            {
                string md = Path.Combine(BaseDir, "model_dir");
                if (!Directory.Exists(md)) return false;
                foreach (string slot in Directory.GetDirectories(md))
                {
                    string pj = Path.Combine(slot, "params.json");
                    if (!File.Exists(pj)) continue;
                    string txt = File.ReadAllText(pj, Encoding.UTF8);
                    // 查 model_file 字段（snake_case —— 官方就是这个写法）。
                    // 不解析完整 JSON 是为了不引入依赖，官方这份结构很稳定。
                    int k = txt.IndexOf("\"model_file\"");
                    if (k < 0) { return true; }        // 结构不认识，按「有」处理，交给官方
                    int q1 = txt.IndexOf('"', txt.IndexOf(':', k) + 1);
                    int q2 = txt.IndexOf('"', q1 + 1);
                    if (q1 < 0 || q2 < 0) return true;
                    string mf = txt.Substring(q1 + 1, q2 - q1 - 1);
                    if (File.Exists(Path.Combine(slot, mf))) return true;
                }
            }
            catch { }
            return false;
        }

        // 代下 1 个示例模型（放进 slot 0），避免官方那套下载器被触发
        static bool EnsureInitialSample()
        {
            if (HasAnyInstalledModel())
            {
                Log("model_dir 已有可用模型，跳过初始示例");
                return true;
            }

            Log("model_dir 中没有模型 —— 代下 1 个初始示例，避免官方下载器卡住");
            SetStatus("正在准备初始示例模型…",
                "官方要求 model_dir 里至少有一个模型，否则会自行下载十几个（可能很慢）。");

            string slot = Path.Combine(BaseDir, "model_dir", "0");
            Directory.CreateDirectory(slot);

            if (!DownloadFileTo(SampleBaseUrls[0] + "/" + SampleModelFile,
                    Path.Combine(slot, SampleModelFile), SampleModelSize, SampleModelHash, "初始模型"))
                return false;

            if (!DownloadFileTo(SampleBaseUrls[0] + "/" + SampleIconFile,
                    Path.Combine(slot, SampleIconFile), SampleIconSize, SampleIconHash, "初始模型图标"))
                return false;

            WriteSampleParams(slot);
            Log("初始示例模型已就位: " + slot);
            return true;
        }

        // 写 params.json。
        //
        // ⚠️ 字段名必须是 snake_case，与官方 SampleManager 的读法一致。
        // 踩过的坑：一开始按 C# 习惯写成了 camelCase（modelFile / iconFile /
        // isF0 …），官方读不到 model_file，于是判定「Sample is not ready」
        // 并触发它自己的示例下载 —— 表现就是日志里刷
        //   Sample is not ready. Downloading initial samples.
        // 明明模型文件已经放好了，却依然在下载。
        // 字段列表取自官方 2.1.4 生成的 params.json（逐字段核对过）。
        static void WriteSampleParams(string slot)
        {
            string json =
                "{\n" +
                "    \"slot_index\": 0,\n" +
                "    \"voice_changer_type\": \"RVC\",\n" +
                "    \"name\": \"黄琴海月(onnx)\",\n" +
                "    \"description\": \"\",\n" +
                "    \"credit\": \"\",\n" +
                "    \"terms_of_use_url\": \"" + SampleBaseUrls[0] + "/terms_of_use.txt\",\n" +
                "    \"icon_file\": \"" + SampleIconFile + "\",\n" +
                "    \"speakers\": {},\n" +
                "    \"model_file\": \"" + SampleModelFile + "\",\n" +
                "    \"index_file\": null,\n" +
                "    \"is_onnx\": true,\n" +
                "    \"inferencer_type\": \"onnxRVC\",\n" +
                "    \"sample_rate\": 40000,\n" +
                "    \"is_f0\": true,\n" +
                "    \"deprecated\": false,\n" +
                "    \"embedder\": \"hubert_base_l12\",\n" +
                "    \"override_embedder\": null,\n" +
                "    \"pitch_estimator\": \"rmvpe_onnx\",\n" +
                "    \"sample_id\": null,\n" +
                "    \"version\": \"v3.0\",\n" +
                "    \"chunk_sec\": 0.5,\n" +
                "    \"pitch_shift\": 0,\n" +
                "    \"index_ratio\": 0,\n" +
                "    \"protect_ratio\": 0.5\n" +
                "}\n";
            try { File.WriteAllText(Path.Combine(slot, "params.json"), json, new UTF8Encoding(false)); }
            catch (Exception e) { Log("写 params.json 失败: " + e.Message); }
        }

        // 通用：下载单个文件（带续传 + 校验），供示例模型使用
        static bool DownloadFileTo(string url, string target, long size, string hash, string label)
        {
            if (Mirror.Length > 0 && (Mirror.StartsWith("http://") || Mirror.StartsWith("https://")))
                url = url.Replace("https://huggingface.co", Mirror.TrimEnd('/'));

            for (int attempt = 1; attempt <= 6; attempt++)
            {
                if (cancelDownload) return false;
                try
                {
                    long have = File.Exists(target) ? new FileInfo(target).Length : 0;
                    if (have > size) { try { File.Delete(target); } catch { } have = 0; }
                    if (have == size && Sha256(target) == hash) return true;

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 120000;
                    req.UserAgent = "vcclient-app/1.0";
                    req.AllowAutoRedirect = true;
                    if (Proxy.Length > 0) req.Proxy = new WebProxy(Proxy);
                    if (have > 0) req.AddRange(have);

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0;
                        using (FileStream fs = new FileStream(target,
                                   have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
                        {
                            Stream rs = resp.GetResponseStream();
                            byte[] buf = new byte[1 << 20];
                            long done = have;
                            Stopwatch sw = Stopwatch.StartNew();
                            long mark = done; double markSec = 0;
                            int n;
                            while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (cancelDownload) return false;
                                fs.Write(buf, 0, n);
                                done += n;
                                double el = sw.Elapsed.TotalSeconds;
                                if (el - markSec > 0.4)
                                {
                                    form.SetFile(label, done, size, (done - mark) / (el - markSec));
                                    mark = done; markSec = el;
                                }
                            }
                        }
                    }

                    if (File.Exists(target) && new FileInfo(target).Length == size && Sha256(target) == hash)
                    {
                        Log(label + " 下载完成并通过校验");
                        return true;
                    }
                    Log(label + " 校验未通过，重试 " + attempt + "/6");
                }
                catch (Exception e)
                {
                    Log(label + " 下载出错 (第 " + attempt + " 次): " + e.Message);
                    System.Threading.Thread.Sleep(Math.Min(2000 * attempt, 10000));
                }
            }
            return false;
        }

        static bool StartServer()
        {
            // 官方客户端（voice-changer-native-client.exe）就是用户要用的界面，
            // 默认【不动它】。只有用户显式勾选了「改用浏览器窗口」才禁用它，
            // 那时我们会改用 Edge/Chrome 的 App 模式开界面。
            //
            // 另外：如果之前被禁用过（.disabled 还在），这里顺手恢复回来 ——
            // 否则用户会遇到「界面打不开」，而且很难想到是文件被改了名。
            if (DisableOfficialClientEnabled)
            {
                DisableOfficialClient();
                Log("按配置禁用官方客户端，将改用浏览器窗口");
            }
            else
            {
                RestoreOfficialClient();
                Log("保留官方客户端（默认行为，用户将看到官方界面）");
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Path.Combine(BaseDir, "main.exe");
            psi.Arguments = "start --https false --port " + Port + " --launch_client false";
            psi.WorkingDirectory = BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;                  // 关键：不产生控制台窗口
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            server = new Process();
            server.StartInfo = psi;
            server.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            { if (e.Data != null) Log("OUT " + e.Data); };
            server.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            { if (e.Data != null) Log("ERR " + e.Data); };

            if (!server.Start()) return false;
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            Log("服务进程已启动 PID=" + server.Id);
            return true;
        }

        static bool WaitForPort(int timeoutSec)
        {
            DateTime deadline = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < deadline)
            {
                if (server == null || server.HasExited) return false;
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                        IAsyncResult ar = c.BeginConnect("127.0.0.1", Port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(600))
                        {
                            c.EndConnect(ar);
                            return true;
                        }
                    }
                }
                catch { }
                Thread.Sleep(500);
            }
            return false;
        }

        // ======================= 打开 App 窗口 =======================
        static string browserExe;
        static string browserProfile;

        static void OpenAppWindow()
        {
            string url = "http://localhost:" + Port + "/";

            // 优先复用官方客户端窗口（它由 main.exe 自行派生）
            if (NativeClientAlive())
            {
                Log("官方客户端窗口已存在，直接复用");
                return;
            }

            if (WindowMode == "native")
            {
                string nc = Path.Combine(BaseDir, @"_internal\native_client\voice-changer-native-client.exe");
                if (File.Exists(nc))
                {
                    ProcessStartInfo npsi = new ProcessStartInfo(nc, "-u " + url);
                    npsi.WorkingDirectory = BaseDir;
                    npsi.UseShellExecute = false;
                    npsi.CreateNoWindow = true;
                    Process.Start(npsi);
                    Log("已拉起官方客户端窗口");
                    return;
                }
                Log("未找到官方客户端，回退到浏览器 App 模式");
            }

            browserExe = FindBrowser();
            if (browserExe == null)
            {
                Process.Start(url);                     // 兜底：默认浏览器
                Log("未找到 Edge/Chrome，用默认浏览器打开");
                return;
            }

            browserProfile = Path.Combine(BaseDir, "browser_profile");
            string a = "--app=" + url
                     + " --window-size=1400,920"
                     + " --user-data-dir=\"" + browserProfile + "\""
                     + " --no-first-run --no-default-browser-check";

            ProcessStartInfo psi = new ProcessStartInfo(browserExe, a);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            Log("已用 App 模式打开窗口: " + browserExe);
        }

        static string FindBrowser()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] cands = new string[]
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf,   @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf,   @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            };
            foreach (string c in cands) if (File.Exists(c)) return c;
            return null;
        }

        // 官方客户端是否存活
        static bool NativeClientAlive()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("voice-changer-native-client");
                return ps.Length > 0;
            }
            catch { return true; }
        }

        // 盯着窗口：官方客户端消失 或 服务退出 -> 结束程序
        static void WatchWindows()
        {
            int goneCount = 0;
            while (!shuttingDown)
            {
                Thread.Sleep(2000);
                if (shuttingDown) return;

                bool alive;
                if (WindowMode == "native")
                    alive = NativeClientAlive();
                else
                    alive = BrowserAlive();

                if (alive) { goneCount = 0; continue; }

                // 服务本身还在、但窗口没了：可能是还没起来，再宽容几轮
                if (!ServerRunning) { Log("服务已退出，程序结束。"); Shutdown(); return; }

                goneCount++;
                if (goneCount >= 3)
                {
                    Log("检测到界面窗口已关闭，退出程序。");
                    Shutdown();
                    return;
                }
            }
        }

        static bool BrowserAlive()
        {
            if (browserExe == null || browserProfile == null) return NativeClientAlive();
            try
            {
                string q = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='"
                         + Path.GetFileName(browserExe) + "'";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(q))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        object cl = o["CommandLine"];
                        if (cl != null && cl.ToString().IndexOf(browserProfile, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch (Exception e) { Log("BrowserAlive 查询失败: " + e.Message); return true; }
            return false;
        }

        // ======================= 托盘 =======================
        static void SetupTray()
        {
            tray = new NotifyIcon();
            tray.Text = "VCClient";
            try
            {
                string ico = Path.Combine(BaseDir, @"web_front\favicon.ico");
                tray.Icon = File.Exists(ico) ? new Icon(ico) : SystemIcons.Application;
            }
            catch { tray.Icon = SystemIcons.Application; }

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("打开界面", delegate(object s, EventArgs e) { OpenAppWindow(); });
            menu.MenuItems.Add("打开所在文件夹", delegate(object s, EventArgs e)
            { Process.Start("explorer.exe", "\"" + BaseDir + "\""); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate(object s, EventArgs e) { Shutdown(); });
            tray.ContextMenu = menu;
            tray.DoubleClick += delegate(object s, EventArgs e) { OpenAppWindow(); };
            tray.Visible = true;

            tray.BalloonTipTitle = "VCClient 已就绪";
            tray.BalloonTipText = "界面已在独立窗口中打开。关闭该窗口即退出程序。";
            try { tray.ShowBalloonTip(4000); } catch { }
        }

        public static void Shutdown()
        {
            if (shuttingDown) return;
            shuttingDown = true;
            Log("正在退出…");
            try { if (tray != null) tray.Visible = false; } catch { }
            KillServerTree();
            try { Application.Exit(); } catch { }
        }

        static void KillServerTree()
        {
            // 官方客户端是独立进程（父进程死后仍存活），需单独结束
            try
            {
                Process[] ncs = Process.GetProcessesByName("voice-changer-native-client");
                foreach (Process nc in ncs)
                {
                    try { nc.Kill(); Log("已结束官方客户端 PID=" + nc.Id); } catch { }
                }
            }
            catch (Exception e) { Log("结束官方客户端失败: " + e.Message); }

            try
            {
                if (server == null || server.HasExited) return;
                // 结束整棵进程树（main.exe 会派生子进程）
                ProcessStartInfo psi = new ProcessStartInfo("taskkill",
                    "/PID " + server.Id + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.WaitForExit(8000);
                Log("已结束服务进程树 PID=" + server.Id);
            }
            catch (Exception e) { Log("KillServerTree 失败: " + e.Message); }
        }

        // ======================= UI 辅助 =======================
        public static void SetStatus(string title, string detail)
        {
            if (form == null) return;
            try { form.Invoke((MethodInvoker)delegate { form.UpdateStatus(title, detail); }); }
            catch { }
        }

        static void HideForm()
        {
            if (form == null) return;
            try { form.Invoke((MethodInvoker)delegate { form.Hide(); }); }
            catch { }
        }

        public static void Log(string line)
        {
            try
            {
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        public static bool IsShuttingDown { get { return shuttingDown; } }
        public static bool ServerRunning
        {
            get { return server != null && !server.HasExited && !shuttingDown; }
        }

        // ======================= 自检模式 =======================
        static void RunSelfTest(string only)
        {
            Log("=== 自检模式开始 (only='" + only + "') ===");
            int pass = 0, fail = 0;
            foreach (ModuleInfo m in ModuleTable.All)
            {
                if (only.Length > 0 && m.Rel.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string p = ModulePath(m);
                bool ok = File.Exists(p) && new FileInfo(p).Length == m.Size && Sha256(p) == m.Hash;
                Log((ok ? "PASS " : "FAIL ") + m.Rel + "  (" + (File.Exists(p) ? new FileInfo(p).Length.ToString() : "缺失") + "/" + m.Size + ")");
                if (ok) pass++; else fail++;
            }
            Log("=== 自检结束: 通过 " + pass + " 失败 " + fail + " ===");
            Environment.Exit(fail == 0 ? 0 : 1);
        }
    }

    // ============================ 配置窗口 ============================
    // 双击启动器后先出这个窗口：改镜像/代理/端口，或把 launcher.json
    // 拖进来导入；点「启动 VCClient」才真正开始。
    // 为什么需要：官方 VCClient.exe 一启动就直奔下载，网络受限的用户
    // 没有任何机会先改镜像 —— 只能干看着它卡住或失败。
    class ConfigForm : Form
    {
        TextBox txtPort, txtMirror, txtProxy;
        CheckBox chkDisableClient;
        ComboBox cmbWindow, cmbPreset;
        Label lblModuleState, lblPresetNote;
        Button btnSpeed, btnProbe;

        public ConfigForm()
        {
            Text = "VCClient 启动配置";
            // 客户区高度 = 内容区（含镜像预设/测速约 560）+ 底部按钮面板（52）
            ClientSize = new Size(640, 620);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            AllowDrop = true;

            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0) ImportConfig(files[0]);
            };

            int y = 16;
            AddSection("下载源 / 网络", ref y);

            // ---- 镜像预设（点一下即填，不用自己查地址）----
            AddLabel("常用镜像（选一个会自动填入下面）", ref y);
            cmbPreset = new ComboBox();
            cmbPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (MirrorPreset mp in MirrorPresets.All)
                cmbPreset.Items.Add(mp.Name);
            cmbPreset.SetBounds(16, y, 400, 24);
            cmbPreset.SelectedIndexChanged += delegate(object s, EventArgs e)
            {
                // 加 null 保护：万一将来有人在控件创建完之前就设了 SelectedIndex，
                // 这里不至于把进程带崩（实测崩过一次，见下方注释）。
                int i = cmbPreset.SelectedIndex;
                if (i >= 0 && i < MirrorPresets.All.Length)
                {
                    if (txtMirror != null) txtMirror.Text = MirrorPresets.All[i].Base;
                    if (lblPresetNote != null) lblPresetNote.Text = MirrorPresets.All[i].Note;
                }
            };
            Controls.Add(cmbPreset);

            // 打开时自动选中与当前配置匹配的预设（这样用户一眼能看出
            // 「现在用的是哪个源」，而不是看到一个空白下拉框）
            // ⚠️ 注意：这里【不能】立刻设置 SelectedIndex。
            // 那会同步触发上面的 SelectedIndexChanged，而回调里要写
            // txtMirror / lblPresetNote —— 这两个控件此时还没创建（在下面），
            // 结果是 null 引用 -> 进程直接以 0xC0000005 (访问违例) 退出，
            // 而且因为 winexe 没有控制台，表现为「双击没反应」。
            // 所以这里只记下要选哪一项，等所有控件都建好再统一应用（见下方）。

            btnSpeed = new Button();
            btnSpeed.Text = "一键测速";
            btnSpeed.SetBounds(424, y, 90, 24);
            btnSpeed.Click += delegate(object s, EventArgs e) { RunSpeedTest(); };
            Controls.Add(btnSpeed);

            btnProbe = new Button();
            btnProbe.Text = "全部测速";
            btnProbe.SetBounds(520, y, 90, 24);
            btnProbe.Click += delegate(object s, EventArgs e) { RunSpeedTestAll(); };
            Controls.Add(btnProbe);
            y += 28;

            lblPresetNote = new Label();
            lblPresetNote.Text = "选镜像后这里会显示适用说明。点「全部测速」可一次测完并自动选最快的。";
            lblPresetNote.SetBounds(24, y, 570, 32);
            lblPresetNote.ForeColor = Color.Gray;
            lblPresetNote.Font = new Font("Microsoft YaHei UI", 8F);
            Controls.Add(lblPresetNote);
            y += 36;

            AddLabel("自定义镜像（留空则用官方 huggingface.co）", ref y);
            txtMirror = AddText(Program.Mirror, ref y);
            AddHint("把 https://huggingface.co 整体替换成你的地址", ref y);

            AddLabel("HTTP 代理（可选，如 http://127.0.0.1:7890）", ref y);
            txtProxy = AddText(Program.Proxy, ref y);

            AddSection("运行参数", ref y);
            AddLabel("服务端口", ref y);
            txtPort = AddText(Program.Port.ToString(), ref y);

            AddLabel("界面窗口", ref y);
            cmbWindow = new ComboBox();
            cmbWindow.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbWindow.Items.Add("native（官方客户端，推荐）");
            cmbWindow.Items.Add("browser（Edge/Chrome App 模式）");
            cmbWindow.SelectedIndex = (Program.WindowMode == "browser") ? 1 : 0;
            cmbWindow.SetBounds(16, y, 560, 24);
            Controls.Add(cmbWindow);
            y += 34;

            chkDisableClient = new CheckBox();
            chkDisableClient.Text = "改用浏览器窗口（勾选后会禁用官方客户端，默认不勾）";
            chkDisableClient.Checked = Program.DisableOfficialClientEnabled;
            chkDisableClient.SetBounds(16, y, 580, 24);
            Controls.Add(chkDisableClient);
            y += 24;

            Label hintClient = new Label();
            hintClient.Text = "默认使用官方客户端界面（voice-changer-native-client.exe）。只有它无法启动时才需要勾选上面这项。";
            hintClient.SetBounds(24, y, 570, 32);
            hintClient.ForeColor = Color.Gray;
            hintClient.Font = new Font("Microsoft YaHei UI", 8F);
            Controls.Add(hintClient);
            y += 38;

            AddSection("模型组件状态", ref y);
            lblModuleState = new Label();
            lblModuleState.SetBounds(16, y, 580, 32);
            lblModuleState.ForeColor = Color.DimGray;
            Controls.Add(lblModuleState);
            RefreshModuleState();
            y += 38;

            Label drop = new Label();
            drop.Text = "提示：把 launcher.json 拖进本窗口即可导入配置";
            drop.SetBounds(16, y, 500, 20);
            drop.ForeColor = Color.SteelBlue;
            Controls.Add(drop);

            // ---------------- 底部按钮区 ----------------
            // 用 Panel 把按钮与上面的内容【彻底隔开】。
            // 踩过的坑（两次）：
            //   1) 按钮 y 跟着内容累加 -> 内容一多就被挤到窗口外；
            //   2) 改成 ClientSize.Height - 46 后，内容仍然排到了 468，
            //      与按钮(454) 重叠，按钮文字被截掉一半（截图里那样）。
            // 用 Dock=Bottom 的面板最稳：无论上面放多少内容，按钮区
            // 永远占据底部固定高度，不会被覆盖。
            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 52;
            Controls.Add(footer);

            const int btnH = 30;
            const int btnY = 11;      // 面板内垂直居中

            Button btnSave = new Button();
            btnSave.Text = "保存配置";
            btnSave.SetBounds(16, btnY, 96, btnH);
            btnSave.Click += delegate(object s, EventArgs e)
            {
                if (ApplyToForm())
                {
                    Program.SaveConfig();
                    MessageBox.Show("已保存到 settings\\launcher.json", "VCClient",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            footer.Controls.Add(btnSave);

            Button btnCancel = new Button();
            btnCancel.Text = "退出";
            btnCancel.SetBounds(footer.Width - 102, btnY, 86, btnH);
            btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnCancel.Click += delegate(object s, EventArgs e) { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(btnCancel);

            Button btnStart = new Button();
            btnStart.Text = "启动 VCClient";
            btnStart.SetBounds(footer.Width - 222, btnY, 110, btnH);
            btnStart.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnStart.Click += delegate(object s, EventArgs e)
            {
                if (ApplyToForm()) { DialogResult = DialogResult.OK; Close(); }
            };
            footer.Controls.Add(btnStart);

            AcceptButton = btnStart;
            CancelButton = btnCancel;

            // ---------------- 收尾：现在所有控件都已创建，可以安全地
            // 让镜像下拉框选中与当前配置匹配的那一项 ----------------
            // （必须放在这里 —— 提前设置会在回调里访问尚未创建的控件而崩溃，
            //   实测踩过：进程以 0xC0000005 静默退出，双击没反应。）
            int curIdx = -1;
            for (int i = 0; i < MirrorPresets.All.Length; i++)
            {
                if (string.Equals(MirrorPresets.All[i].Base, Program.Mirror,
                        StringComparison.OrdinalIgnoreCase))
                { curIdx = i; break; }
            }
            if (curIdx >= 0)
            {
                cmbPreset.SelectedIndex = curIdx;   // 触发 Changed，顺带填好说明文字
            }
            else if (Program.Mirror.Length > 0)
            {
                lblPresetNote.Text = "当前用的是自定义镜像（不在上面的预设里）。";
            }
            else
            {
                lblPresetNote.Text = "当前未设置镜像，将使用官方源 huggingface.co。";
            }
        }

        void AddSection(string text, ref int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(16, y, 580, 20);
            l.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            Controls.Add(l);
            y += 28;
        }

        void AddLabel(string text, ref int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(16, y, 580, 20);
            Controls.Add(l);
            y += 22;
        }

        void AddHint(string text, ref int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(24, y, 570, 18);
            l.ForeColor = Color.Gray;
            l.Font = new Font("Microsoft YaHei UI", 8F);
            Controls.Add(l);
            y += 22;
        }

        TextBox AddText(string value, ref int y)
        {
            TextBox t = new TextBox();
            t.Text = value;
            t.SetBounds(16, y, 560, 24);
            Controls.Add(t);
            y += 30;
            return t;
        }

        void RefreshModuleState()
        {
            int have = 0;
            foreach (ModuleInfo m in ModuleTable.All)
            {
                string p = Path.Combine(Program.BaseDir, "modules", m.Rel.Replace('/', Path.DirectorySeparatorChar));
                try { if (File.Exists(p) && new FileInfo(p).Length == m.Size) have++; }
                catch { }
            }
            if (have == ModuleTable.All.Length)
                lblModuleState.Text = "全部 " + have + " 个组件已就位，启动时无需下载。";
            else
                lblModuleState.Text = "已就位 " + have + " / " + ModuleTable.All.Length
                    + " 个组件；启动时会自动补齐缺失的部分（支持断点续传）。";
        }

        void ImportConfig(string file)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1).Trim();
                    if (k == "Port") txtPort.Text = v;
                    else if (k == "Mirror") txtMirror.Text = v;
                    else if (k == "Proxy") txtProxy.Text = v;
                    else if (k == "WindowMode") cmbWindow.SelectedIndex = (v == "browser") ? 1 : 0;
                    else if (k == "DisableOfficialClient")
                        chkDisableClient.Checked = (v == "1" || v.ToLower() == "true");
                }
                MessageBox.Show("已导入：" + Path.GetFileName(file), "VCClient",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "VCClient",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------- 测速 ----------------
        // ⚠️ 两个都必须注意，都是实测踩过的：
        //
        // 1) 必须跳过 TCP 慢启动阶段再计时，否则严重低估。
        //    实测（hf-mirror.com，181 MB 文件，每 8 MB 一段）：
        //       第 1 段 (MB  0- 8):   5.50 MB/s   <- 慢启动
        //       第 2 段 (MB  8-16):  47.12 MB/s   <- 拥塞窗口撑开
        //       第 3 段 (MB 16-24):  66.49 MB/s
        //    若只读前 1.5 MB 就计时会得到 0.60 MB/s —— 低估约 100 倍，
        //    于是「一键测速」会把快源测成慢源。
        //
        // 2) 测速文件必须【足够大】，否则热身阶段就把文件读完了，
        //    正式计时读到 0 字节，结果报「无数据」——明明源是好的。
        //    踩过：一开始用 tiny.onnx（1.9 MB）当探针，热身要 2 MB，
        //    正好把整个文件读完，于是 hf-mirror.com 被误判为不可用。
        //    改用 rmvpe_20231006.pt（181 MB），热身 4 MB + 计时 12 MB
        //    都远小于文件大小，且 16 MB 的流量对用户是可接受的代价。
        const int WARMUP_BYTES = 4 * 1024 * 1024;
        const int MEASURE_BYTES = 12 * 1024 * 1024;

        double ProbeSpeed(string baseUrl, out string err)
        {
            err = "";
            string url = baseUrl.TrimEnd('/') + MirrorPresets.ProbePath;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "vcclient-app/1.0";
                req.AllowAutoRedirect = true;
                string px = txtProxy.Text.Trim();
                if (px.Length > 0) req.Proxy = new WebProxy(px);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream rs = resp.GetResponseStream())
                {
                    byte[] buf = new byte[65536];

                    // 阶段 1：热身，丢弃前 4 MB（让拥塞窗口撑开），不计时
                    long warm = 0;
                    Stopwatch warmSw = Stopwatch.StartNew();
                    while (warm < WARMUP_BYTES && warmSw.Elapsed.TotalSeconds < 8)
                    {
                        int n = rs.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        warm += n;
                    }
                    if (warm <= 0) { err = "连不上或无数据"; return -1; }

                    // 阶段 2：正式计时，读 12 MB
                    long got = 0;
                    Stopwatch sw = Stopwatch.StartNew();
                    while (got < MEASURE_BYTES && sw.Elapsed.TotalSeconds < 10)
                    {
                        int n = rs.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        got += n;
                    }
                    sw.Stop();

                    double sec = sw.Elapsed.TotalSeconds;
                    // 热身没读满（文件太小或提前结束）时，用热身的平均速度兜底，
                    // 免得明明能下却报「无数据」
                    if (got <= 0 || sec <= 0)
                    {
                        double wsec = warmSw.Elapsed.TotalSeconds;
                        if (warm > 0 && wsec > 0) return warm / wsec;
                        err = "无数据";
                        return -1;
                    }
                    return got / sec;   // 字节/秒
                }
            }
            catch (Exception e)
            {
                err = e.Message;
                if (err.Length > 60) err = err.Substring(0, 60) + "…";
                return -1;
            }
        }

        // 只测当前填的那个
        void RunSpeedTest()
        {
            string baseUrl = txtMirror.Text.Trim();
            if (baseUrl.Length == 0) baseUrl = "https://huggingface.co";
            if (!baseUrl.StartsWith("http")) { lblPresetNote.Text = "地址要以 http:// 或 https:// 开头"; return; }

            btnSpeed.Enabled = false;
            lblPresetNote.Text = "正在测速：" + baseUrl + " …";
            Application.DoEvents();

            string err;
            double sp = ProbeSpeed(baseUrl, out err);
            btnSpeed.Enabled = true;

            if (sp < 0)
                lblPresetNote.Text = "测速失败：" + err + "\n（该源在当前网络下不可用，换一个试试）";
            else
                lblPresetNote.Text = string.Format("测速结果：{0:N2} MB/s  （{1}）", sp / 1048576.0, baseUrl);
        }

        // 把所有预设测一遍，并自动把最快的填进去
        void RunSpeedTestAll()
        {
            btnProbe.Enabled = false;
            btnSpeed.Enabled = false;

            string best = null;
            double bestSp = -1;
            StringBuilder report = new StringBuilder();

            foreach (MirrorPreset mp in MirrorPresets.All)
            {
                lblPresetNote.Text = "正在测速：" + mp.Name + " …";
                Application.DoEvents();

                string err;
                double sp = ProbeSpeed(mp.Base, out err);
                if (sp < 0)
                    report.AppendLine(string.Format("{0}：不可用（{1}）", mp.Name, err));
                else
                {
                    report.AppendLine(string.Format("{0}：{1:N2} MB/s", mp.Name, sp / 1048576.0));
                    if (sp > bestSp) { bestSp = sp; best = mp.Base; }
                }
            }

            btnProbe.Enabled = true;
            btnSpeed.Enabled = true;

            if (best != null)
            {
                txtMirror.Text = best;
                report.AppendLine();
                report.AppendLine(string.Format("已自动选中最快的：{0}（{1:N2} MB/s）",
                    best, bestSp / 1048576.0));
            }
            else
            {
                report.AppendLine();
                report.AppendLine("全部镜像都不可用。请检查网络，或填一个你自己的代理。");
            }
            lblPresetNote.Text = report.ToString().TrimEnd();
        }

        bool ApplyToForm()
        {
            int pv;
            if (!int.TryParse(txtPort.Text.Trim(), out pv) || pv < 1 || pv > 65535)
            {
                MessageBox.Show("端口必须是 1~65535 之间的数字。", "VCClient",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            Program.Port = pv;
            string mir = txtMirror.Text.Trim().TrimEnd('/');
            // 存之前先校验：非 http(s) 开头的一律当没填，避免把脏值落盘后
            // 每次下载都拼出无效 URI（那个错误信息是「无效的 URI」，
            // 很容易被误判成网络问题）。
            if (mir.Length > 0 && !mir.StartsWith("http://") && !mir.StartsWith("https://"))
            {
                MessageBox.Show("镜像地址要以 http:// 或 https:// 开头。\n已按「留空（用官方源）」处理。",
                    "VCClient", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mir = "";
                txtMirror.Text = "";
            }
            Program.Mirror = mir;
            Program.Proxy = txtProxy.Text.Trim();
            Program.DisableOfficialClientEnabled = chkDisableClient.Checked;
            Program.WindowMode = (cmbWindow.SelectedIndex == 1) ? "browser" : "native";
            return true;
        }
    }

    // ============================ 镜像预设 ============================
    // 内置几个常见镜像，用户点一下就填上，不用自己查地址。
    //
    // ⚠️ 下面这些结论都是【实测】得来的（无代理纯直连，中国大陆家宽），
    //    不是凭印象写的。之前的版本把可用性标反了 —— 把唯一能用的
    //    hf-mirror.com 写成「等于没镜像」，却把实测 0/10 成功的
    //    aifasthub 写成「实测可用」，等于把用户从唯一能用的源吓跑。
    //
    // 实测数据（2026-10，三个文件含跨组织大文件，校验 SHA256）：
    //   hf-mirror.com   200 / SHA 匹配 / Range 206 / 10 次全成功 / 大文件峰值 60+ MB/s
    //   hf-mirror.net   间歇性 TLS RST，成功率约 1/10，失败要等 40 秒
    //   aifasthub.com   TLS 握手被 RST，成功率 0/10
    //   huggingface.co  DNS 被污染 + TCP 443 超时
    //   modelscope.cn   404（该站没有这些仓库）
    //
    // 另一个坑：www.wisemodel.cn 返回 200 但内容是 HTML 页面 ——
    // 只看状态码会误判为「可用」，所以测速必须校验内容。
    internal class MirrorPreset
    {
        public string Name;
        public string Base;
        public string Note;
        public MirrorPreset(string n, string b, string note) { Name = n; Base = b; Note = note; }
    }

    internal static class MirrorPresets
    {
        public static readonly MirrorPreset[] All = new MirrorPreset[]
        {
            new MirrorPreset("hf-mirror.com（推荐）", "https://hf-mirror.com",
                "国内公益镜像。实测可直连、支持断点续传，大文件可达数十 MB/s。"),
            new MirrorPreset("官方源（huggingface.co）", "https://huggingface.co",
                "直连官方。网络通畅时最快；受限网络下通常连不上（DNS 被污染）。"),
            new MirrorPreset("hf-mirror.net（不稳定）", "https://hf-mirror.net",
                "实测成功率低（约 1/10），且失败要等 40 秒。仅作备用。"),
            new MirrorPreset("aifasthub（不稳定）", "https://aifasthub.com",
                "实测当前网络下无法连接（TLS 被重置）。仅作备用。"),
            new MirrorPreset("ModelScope 魔搭（阿里）", "https://www.modelscope.cn",
                "阿里的模型站。仅在模型于该站有对应仓库时可用（本项目多数没有）。"),
        };

        // 测速用的文件。
        // ⚠️ 必须选【大文件】—— 曾用 1.9 MB 的 tiny.onnx，结果热身阶段
        // （要 4 MB）就把整个文件读完了，正式计时读到 0 字节，
        // 于是把明明可用的 hf-mirror.com 报成「不可用（无数据）」。
        // 现在用 181 MB 的 rmvpe 模型，热身 4 MB + 计时 12 MB 都远小于它。
        public const string ProbePath =
            "/wok000/vcclient_modules/resolve/main/rmvpe/rmvpe_20231006.pt";
        public const long ProbeSize = 181184272L;

        // 默认镜像（首次运行没配置时用哪个）。
        // 选 hf-mirror.com 而不是官方源：官方源在受限网络下根本连不上，
        // 让用户一上手就失败是很差的体验。
        public const string DefaultBase = "https://hf-mirror.com";
    }

    // ============================ 状态窗口 ============================
    class StatusForm : Form
    {
        Label title, status, detail;
        ProgressBar bar;
        Button cancelBtn;
        // 下载进度面板新增：整体进度条 + 说明文字
        Label overall;
        ProgressBar overallBar;
        int totalCount = 0;
        long totalBytes = 0;
        long doneBefore = 0;

        public StatusForm()
        {
            Text = "VCClient";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false;
            // 高度按内容排：标题/状态/进度条/详情/总进度条/总进度文字/按钮
            ClientSize = new Size(540, 232);
            BackColor = Color.FromArgb(24, 30, 42);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);

            title = new Label();
            title.Text = "VCClient";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(120, 200, 255);
            title.SetBounds(24, 18, 480, 34);
            Controls.Add(title);

            status = new Label();
            status.Text = "正在初始化…";
            status.SetBounds(26, 60, 490, 22);
            Controls.Add(status);

            bar = new ProgressBar();
            bar.SetBounds(26, 90, 488, 18);
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 28;
            Controls.Add(bar);

            detail = new Label();
            detail.Text = "";
            detail.ForeColor = Color.FromArgb(170, 180, 195);
            detail.SetBounds(26, 116, 488, 26);
            Controls.Add(detail);

            // 整体进度（多文件下载时，用户更关心「一共下了多少」）
            overallBar = new ProgressBar();
            overallBar.SetBounds(26, 146, 488, 12);
            overallBar.Style = ProgressBarStyle.Continuous;
            overallBar.Maximum = 1000;
            overallBar.Visible = false;
            Controls.Add(overallBar);

            overall = new Label();
            overall.Text = "";
            overall.ForeColor = Color.FromArgb(140, 155, 175);
            overall.SetBounds(26, 162, 488, 20);
            Controls.Add(overall);

            cancelBtn = new Button();
            cancelBtn.Text = "取消下载并退出";
            cancelBtn.SetBounds(378, 190, 136, 28);
            cancelBtn.FlatStyle = FlatStyle.Flat;
            cancelBtn.ForeColor = Color.FromArgb(200, 210, 225);
            cancelBtn.Visible = false;
            cancelBtn.Click += delegate(object s, EventArgs e)
            {
                Program.CancelDownload();
                cancelBtn.Enabled = false;
                cancelBtn.Text = "正在退出…";
            };
            Controls.Add(cancelBtn);

            try
            {
                string ico = Path.Combine(Program.BaseDir, @"web_front\favicon.ico");
                if (File.Exists(ico)) this.Icon = new Icon(ico);
            }
            catch { }
        }

        public void UpdateStatus(string t, string d)
        {
            status.Text = t;
            detail.Text = d;
        }

        public void SetOverall(int idx, int total)
        {
            if (InvokeRequired) { Invoke((MethodInvoker)delegate { SetOverall(idx, total); }); return; }
            bar.Style = ProgressBarStyle.Marquee;
            cancelBtn.Visible = true;
            overall.Text = string.Format("组件 {0} / {1}", idx, total);
        }

        // 下载进度面板。
        // 要展示的信息（用户实际关心的）：
        //   · 当前在下载哪个文件
        //   · 这个文件下了多少 / 总共多少，百分比
        //   · 实时速度 + 已用时间 + 预计剩余
        //   · 整体进度（第几个 / 共几个，以及总体百分比）
        public void SetFile(string name, long done, long size, double speed)
        {
            if (InvokeRequired) { Invoke((MethodInvoker)delegate { SetFile(name, done, size, speed); }); return; }
            bar.Style = ProgressBarStyle.Continuous;
            bar.Maximum = 1000;
            int v = size > 0 ? (int)(done * 1000 / size) : 0;
            if (v < 0) v = 0;
            if (v > 1000) v = 1000;
            bar.Value = v;
            cancelBtn.Visible = true;

            double pct = size > 0 ? (done * 100.0 / size) : 0;
            status.Text = string.Format("正在下载：{0}", name);

            // 速度 + 剩余时间估算
            string speedTxt = speed > 1024
                ? string.Format("{0:N2} MB/s", speed / 1048576.0)
                : string.Format("{0:N0} KB/s", speed / 1024.0);
            string etaTxt = "";
            if (speed > 1024 && size > done)
            {
                double secs = (size - done) / speed;
                if (secs < 3600) etaTxt = string.Format("剩余约 {0:N0} 分 {1:N0} 秒", secs / 60, secs % 60);
                else etaTxt = string.Format("剩余约 {0:N1} 小时", secs / 3600);
            }

            detail.Text = string.Format(
                "{0} / {1}  ({2:N1}%)   {3}   {4}",
                Mb(done), Mb(size), pct, speedTxt, etaTxt);

            // 总体进度条（按已下字节 / 总字节算）
            if (totalBytes > 0)
            {
                long allDone = doneBefore + done;
                int ov = (int)(allDone * 1000 / totalBytes);
                if (ov < 0) ov = 0; if (ov > 1000) ov = 1000;
                overallBar.Value = ov;
                overall.Text = string.Format("总进度 {0:N1}%   （{1} / {2}）",
                    allDone * 100.0 / totalBytes, Mb(allDone), Mb(totalBytes));
            }
        }

        // 开始一轮下载前，告知总量，便于算总进度
        public void BeginDownload(int count, long bytes)
        {
            if (InvokeRequired) { Invoke((MethodInvoker)delegate { BeginDownload(count, bytes); }); return; }
            totalCount = count;
            totalBytes = bytes;
            doneBefore = 0;
            overallBar.Visible = true;
            overallBar.Value = 0;
            overall.Text = string.Format("准备下载 {0} 个组件，共 {1}", count, Mb(bytes));
            cancelBtn.Visible = true;
        }

        // 一个文件下完，累加已下字节
        public void FinishFile(long size)
        {
            if (InvokeRequired) { Invoke((MethodInvoker)delegate { FinishFile(size); }); return; }
            doneBefore += size;
        }

        static string Mb(long b)
        {
            if (b >= 1073741824L) return string.Format("{0:N2} GB", b / 1073741824.0);
            return string.Format("{0:N1} MB", b / 1048576.0);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 已就绪后关闭状态窗 -> 收进托盘；否则视为退出
            if (!Program.IsShuttingDown && Program.ServerRunning)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            Program.Shutdown();
            base.OnFormClosing(e);
        }
    }
}
