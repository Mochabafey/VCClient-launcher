# VCClient drop-in 启动器

> 给**官方 VCClient 包**加一层外壳。不修改官方任何文件。
>
> 面向维护者；使用者看 [使用说明.txt](../dist/VCClient-dropin-launcher/使用说明.txt)。

---

## 一、它解决什么问题

官方 VCClient 开箱可用，但在**受限网络**下有三个坎：

| 问题 | 官方行为 | 本启动器的处理 |
|---|---|---|
| 缺模型组件 | 用裸 `requests` 自己下，无镜像/代理/续传，卡在 0% | 先用自己的多镜像下载器补齐（断点续传 + SHA256 + 自动换源） |
| `model_dir` 为空 | 下载十几个示例模型（很慢） | 代下 **1 个**即可规避（官方判定是「有没有任何一个」） |
| 网络不通 | 没有任何配置入口 | **先出配置页**，可换镜像、填代理、一键测速 |

**界面仍然是官方客户端窗口** —— 本启动器不替换它，只是把「启动前的准备工作」做好。

---

## 二、设计原则

### 1. 绝不修改官方文件（除了它自己也会改的）

唯一会动的是 `voice-changer-native-client.exe` 的重命名，**而且默认不启用**。

> 早期版本把「禁用官方客户端」当成默认行为，还标成「套壳模式需要」——
> 这是**方向性错误**：本项目的定位是在官方包上做增强，官方客户端
> 就是用户要用的界面，禁用它用户就没界面了。现已改为默认保留。

### 2. 硬件版本不由本程序判断

官方包分 `std`（CPU）与 `cuda` 两种，**运行时差异在官方包里就决定了**：

| 官方包 | `_internal\` 内容 |
|---|---|
| `vcclient_win_std_*.zip` | CPU 版 torch |
| `vcclient_win_cuda_*.zip` | CUDA 版 torch（约 5.9 GB） |

用户下载哪个包就自带哪个运行时，本启动器只负责「启动它」。
**不做显卡检测** —— 官方 `main.exe`（Python/PyTorch）自己会找 CUDA，
外层去猜反而容易出错。

### 3. 先配置、再启动

官方 `VCClient.exe` 一启动就直奔下载，网络受限的用户**没有任何机会**
先改镜像 —— 只能干看着它失败。所以本程序先弹配置页。

加 `--no-config` 可跳过（脚本/自动化调用时用）。

---

## 三、代码结构

```
tools/launcher/
    VcClientApp.cs     源码（单文件 C#，约 1800 行）
    build.ps1          编译脚本（可选 -DeployTo <官方包目录>）
    VCClient.exe       编译产物（提交进仓库，用户可直接取用）
```

**为什么用 C# / .NET Framework WinForms**：
官方 `VCClient.exe` 就是 C#/.NET（`mscoree.dll` + `V4.0.30319`），
用同一套技术栈可以：
- 体积小（45 KB，对比 Electron 外壳 200 MB）
- 启动快，无额外运行时（Windows 自带 .NET Framework 4.x）
- 编译环境零依赖（`csc.exe` 系统自带，不需要装 SDK）

### 主要类

| 类 | 职责 |
|---|---|
| `Program` | 入口、配置读写、下载器、服务启动、窗口管理、托盘 |
| `ModuleTable` | 10 个模型组件的清单（URL / 大小 / SHA256） |
| `ModuleInfo` | 单个组件 |
| `ConfigForm` | 配置页（镜像、代理、端口、测速） |
| `StatusForm` | 启动进度页（进度条、速度、ETA） |
| `MirrorPresets` | 内置镜像预设 |

---

## 四、构建

```powershell
# 只编译
powershell -ExecutionPolicy Bypass -File tools/launcher/build.ps1

# 编译并部署到官方包目录（会自动改名成 VCClient-launcher.exe）
powershell -ExecutionPolicy Bypass -File tools/launcher/build.ps1 -DeployTo "C:\path\to\official\package"
```

> `build.ps1` **保持纯 ASCII**，因此不需要 UTF-8 BOM
> （见 [check-encoding.ps1](check-encoding.ps1) 的判据）。

---

## 五、踩过的坑（都是实测发现的）

### 5.1 TLS 1.2 必须显式启用

.NET Framework 4.x 默认只用 TLS 1.0，而 huggingface.co 与各镜像早已禁用它。
不设 `ServicePointManager.SecurityProtocol` 时所有下载都会报：

```
请求被中止: 未能创建 SSL/TLS 安全通道。
```

**这个错误信息很像「网络不通」，极易误判** —— 实测时三个镜像都 HTTP 200，
下载器却一个文件都下不下来。

### 5.2 测速必须跳过 TCP 慢启动

实测同一个文件（hf-mirror.com，181 MB，每 8 MB 一段）：

```
第 1 段 (MB  0- 8):   5.50 MB/s   <- 慢启动
第 2 段 (MB  8-16):  47.12 MB/s   <- 拥塞窗口撑开
第 3 段 (MB 16-24):  66.49 MB/s
第 4 段 (MB 24-32):  62.69 MB/s
```

若只读前 1.5 MB 就计时，会得到 **0.60 MB/s** —— **低估约 100 倍**，
于是「一键测速」会把快源测成慢源、自动选中最差的那个。

现在：先丢 4 MB 热身不计时，再读 12 MB 计时。

### 5.3 测速探针文件不能太小

一开始用 `tiny.onnx`（1.9 MB）当探针，而热身要 4 MB ——
**正好把整个文件读完**，正式计时读到 0 字节，于是把明明可用的
`hf-mirror.com` 报成「不可用（无数据）」。

改用 `rmvpe_20231006.pt`（181 MB）作探针。

### 5.4 `params.json` 字段名是 snake_case

官方读的是 `model_file` / `icon_file` / `is_f0` / `pitch_estimator`…
写成 camelCase（`modelFile` / `iconFile` / `isF0`）时官方读不到，
判定 `Sample is not ready` 并**触发它自己的示例下载** ——
明明模型文件已经放好了，日志里却还在刷下载进度。

### 5.5 WinForms 控件创建顺序导致的崩溃

在 `cmbPreset.SelectedIndex = n` 时，会**同步**触发
`SelectedIndexChanged` 回调，而回调里要写 `txtMirror` / `lblPresetNote`。
若这两个控件还没创建（在代码后面），就是 null 引用 →
进程以 **`0xC0000005`（访问违例）静默退出**。
因为 `winexe` 没有控制台，表现为「双击没反应」，极难查。

**修法**：把「设置选中项」移到所有控件创建完之后；并给回调加 null 保护。

### 5.6 按钮被内容挤出窗口 / 与文字重叠

配置页的按钮最初跟着内容 y 累加，内容一多就被挤出窗口；
改成 `ClientSize.Height - 46` 后又与提示文字重叠（内容排到 468、按钮在 454），
按钮文字被截掉一半。

**修法**：用 `Dock = Bottom` 的 `Panel` 装按钮，与内容彻底隔离。

### 5.7 镜像可用性与注释相反

早期注释把唯一可用的 `hf-mirror.com` 写成「308 重定向回官方，等于没镜像」，
却把实测 0/10 成功的 `aifasthub` 写成「实测可用」——
**会把用户从唯一能用的源吓跑**。

实测结论见下节。

---

## 六、镜像可用性（实测）

**测试环境**：中国大陆家宽，无代理纯直连。

| 镜像 | 结果 |
|---|---|
| `https://hf-mirror.com` | **可用**。200 / SHA256 匹配 / Range 206 / 10 次全成功 / 33+ MB/s |
| `https://hf-mirror.net` | 间歇性 TLS RST，成功率约 **1/10**，失败要等 40 秒 |
| `https://aifasthub.com` | TLS 握手被重置，成功率 **0/10** |
| `https://huggingface.co` | DNS 被污染 + TCP 443 超时 |
| `https://www.modelscope.cn` | 404（该站没有这些仓库） |
| 清华 / 阿里 / 腾讯 / 南大 / 北外 / 中科院 | 403 或 404（不代理 HF 的 resolve 路径） |

### 关于 hf-mirror.com 的 302

它会 302 到 `cas-bridge.xethub.hf.co`（HF 的 Xet 对象存储），
**不是**跳回 `huggingface.co` —— 这个 302 对程序无害，
`AllowAutoRedirect = true` 自动跟随即可（实测跨主机 302 后
`Range` 仍返回 206、`Content-Range` 正确、字节数一致）。

### 一个陷阱

`www.wisemodel.cn` 返回 **200 但内容是 HTML 页面** ——
只检查状态码会误判为「可用」。所以测速必须校验内容，不能只看 HTTP 200。

---

## 七、命令行参数

| 参数 | 说明 |
|---|---|
| `--no-config` | 跳过配置页，直接启动 |
| `--mirror <URL>` | 指定镜像 |
| `--port <端口>` | 指定端口 |
| `--window native\|browser` | 界面模式 |
| `--test-download [名字]` | 只测下载器，不开界面、不启服务 |

---

## 八、配置文件

位置：`settings\launcher.json`（key=value 纯文本，可直接编辑或拖进配置页导入）

```ini
Port=18000
Mirror=https://hf-mirror.com
Proxy=
DisableOfficialClient=0
WindowMode=native
```

| 键 | 说明 |
|---|---|
| `Mirror` | 留空 = 官方源；填镜像会整体替换 `https://huggingface.co` |
| `Proxy` | HTTP 代理，如 `http://127.0.0.1:7890` |
| `DisableOfficialClient` | 默认 `0`（保留官方客户端）。改成 `1` 会禁用它并改用浏览器窗口 —— **除非官方客户端无法启动，否则不要改** |
| `WindowMode` | `native`（官方客户端，推荐）/ `browser`（Edge/Chrome App 模式） |

> 默认镜像设为 `hf-mirror.com` 而非留空走官方源：官方源在受限网络下
> 根本连不上，让用户一上手就失败是很差的体验。

---

## 九、启动流程

```
双击 VCClient-launcher.exe
  ↓
单实例锁检查
  ↓
找 main.exe（BaseDirectory 起向上最多 3 层，容忍放在子目录）
  ↓
[先配置] 弹配置页 → 用户点「启动 VCClient」才继续
  ↓
检查 10 个模型组件 → 缺的用多镜像下载器补齐
  ↓
检查 model_dir → 空的就代下 1 个示例模型
  ↓
启动 main.exe（start --https false --port N --launch_client false）
  ↓
等端口就绪（最多 240 秒）
  ↓
官方 main.exe 自行派生客户端窗口（或回退浏览器 App 模式）
  ↓
托盘常驻；关窗口即退出
```
