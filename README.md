# VCClient-launcher

给 **官方 VCClient 包** 加一层外壳的启动器。

不修改官方任何文件，只负责「启动前的准备工作」——换镜像、补模型、填代理。

**界面仍然是官方客户端窗口**，和你直接用官方包完全一样。

---

## 为什么需要它

官方 VCClient 开箱可用，但在**国内受限网络**下有三个坎：

| 问题 | 官方行为 | 本启动器 |
|---|---|---|
| 缺模型组件 | 用裸 requests 自己下（无镜像/代理/续传），卡在 0% | 用多镜像下载器补齐（断点续传 + SHA256 + 自动换源） |
| model_dir 为空 | 下载十几个示例模型（很慢） | 代下 **1 个** 即可规避 |
| 网络不通 | 没有任何配置入口 | **先出配置页**，可换镜像、填代理、一键测速 |

---

## 快速开始

### 1. 下载官方包

从 w-okada/voice-changer 的 Release 下载：

| 你的电脑 | 下哪个 |
|---|---|
| 有 NVIDIA 显卡 | vcclient_win_cuda_*.zip（约 3.6 GB，含 CUDA 运行时） |
| 没有独显 | vcclient_win_std_*.zip（约 400 MB） |

解压到一个**空间充足**的目录（CUDA 版解压后约 9.5 GB）。

### 2. 放入本启动器

把 VCClient-launcher.exe 放到**和 main.exe 同一个目录**：

```
你的目录\
    main.exe                  <- 官方服务
    VCClient-launcher.exe     <- 本启动器（放这里）
    _internal\                <- 官方运行时
    web_front\
    ...
```

### 3. 双击运行

1. 弹出**配置页**（默认已选好 hf-mirror.com）
2. 按需改镜像 / 填代理，或点「一键测速」让它自己选最快的
3. 点「**启动 VCClient**」
4. 首次会检查模型组件，缺的自动下载（约 3.24 GB），进度和速度都显示在界面上
5. 下完自动启动服务并打开**官方界面**

---

## 配置页说明

| 项 | 说明 |
|---|---|
| **常用镜像** | 5 个预设，选中自动填入并显示适用说明 |
| **一键测速** | 测当前填的那个源 |
| **全部测速** | 依次测完所有预设，**自动选中最快的** |
| **自定义镜像** | 留空 = 用官方源；填了会整体替换 https://huggingface.co |
| **HTTP 代理** | 如 http://127.0.0.1:7890，留空 = 不用 |
| **服务端口** | 默认 18000，一般不用改 |
| **界面窗口** | native（官方客户端，推荐）/ browser（Edge/Chrome App 模式） |
| **改用浏览器窗口** | 默认**不勾**。勾了会禁用官方客户端 —— 除非它无法启动，否则别动 |

配置会存到 settings\launcher.json，也可以把该文件**直接拖进窗口**导入。

---

## 镜像可用性（实测）

测试环境：中国大陆家宽，**无代理纯直连**。

| 镜像 | 结果 |
|---|---|
| **https://hf-mirror.com** | 可用。200 / SHA256 匹配 / 支持断点续传 / 实测 33+ MB/s |
| https://huggingface.co | DNS 被污染 + TCP 超时 |
| https://hf-mirror.net | 间歇性失败，成功率约 1/10，且失败要等 40 秒 |
| https://aifasthub.com | TLS 握手被重置 |
| https://www.modelscope.cn | 404（该站没有这些仓库） |
| 清华 / 阿里 / 腾讯 / 南大 / 北外 / 中科院 | 不代理 HuggingFace 的 resolve 路径 |

> **为什么没有「清华源 / 阿里源」**：那些是 PyPI 或通用 CDN 镜像，
> 不代理 huggingface.co 的模型下载路径。本项目模型全在 HuggingFace 上，
> 只能用 **HF 镜像**或**代理**。

**默认已设为 hf-mirror.com** —— 因为官方源在受限网络下根本连不上，
让你一上手就失败是很差的体验。

---

## 命令行参数

给脚本 / 自动化用：

```bat
VCClient-launcher.exe --no-config                 跳过配置页直接启动
VCClient-launcher.exe --mirror https://xxx.com    指定镜像
VCClient-launcher.exe --port 18000                指定端口
VCClient-launcher.exe --window native             界面模式
VCClient-launcher.exe --test-download             只测下载器，不开界面
```

---

## 配置文件

settings\launcher.json（key=value 纯文本，可直接编辑）

```ini
Port=18000
Mirror=https://hf-mirror.com
Proxy=
DisableOfficialClient=0
WindowMode=native
```

| 键 | 说明 |
|---|---|
| Mirror | 留空 = 官方源 |
| Proxy | HTTP 代理 |
| DisableOfficialClient | 默认 0（保留官方客户端）。**除非官方客户端无法启动，否则不要改** |
| WindowMode | native / browser |

---

## 日志

| 文件 | 内容 |
|---|---|
| launcher.log | 本启动器的日志（下载、启动、错误） |
| vcclient.log | 官方服务的日志 |

---

## 常见问题

**Q: 下载很慢 / 失败？**
点「全部测速」，它会自动选最快的源。若全部不可用，说明需要代理 ——
在「HTTP 代理」里填上（如 http://127.0.0.1:7890）。

**Q: 提示「无效的 URI」？**
镜像地址格式不对，要以 http:// 或 https:// 开头。

**Q: 提示「未能创建 SSL/TLS 安全通道」？**
该镜像不支持 TLS 1.2+，换一个源。

**Q: 界面没出来？**
看 launcher.log。若显示「服务未能就绪」，看 vcclient.log。

**Q: 官方 VCClient.exe 还能用吗？**
能。本启动器不动任何官方文件（除了下载模型到 modules\ 和
model_dir\，那是官方本来就会做的事）。两者可以共存。

**Q: 它怎么知道我是 CPU 版还是 CUDA 版？**
**不需要知道**。运行时差异在官方包里就决定了 —— 你下的是哪个包，
就自带哪个 torch。本启动器只负责启动它。
（官方 main.exe 是 Python/PyTorch，它自己会检测 CUDA。）

**Q: 提示找不到 main.exe？**
把 VCClient-launcher.exe 放到和 main.exe 同一个目录。

---

## 自己编译

需要 Windows 自带的 .NET Framework 编译器（无需装 SDK）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1

# 或编译并直接部署到官方包目录
powershell -ExecutionPolicy Bypass -File build.ps1 -DeployTo "C:\path\to\official\package"
```

产物是单文件 VCClient-launcher.exe（约 45 KB）。

---

## 实现说明

用 **C# / .NET Framework WinForms**，与官方 VCClient.exe 同一技术栈
（官方那个也是 C#，mscoree.dll + V4.0.30319）。

选它的原因：体积 45 KB（对比 Electron 的 200 MB）、启动快、
无额外运行时（Windows 自带 .NET Framework 4.x）、编译零依赖。

源码是单文件 VcClientApp.cs。
更详细的设计说明与踩坑记录见 DEVELOPMENT.md。

启动流程：

```
双击 -> 单实例检查 -> 找 main.exe -> [先配置] -> 检查 10 个组件
     -> 缺的下载 -> 检查 model_dir（空的就代下 1 个）
     -> 启动 main.exe -> 等端口 -> 官方派生客户端窗口 -> 托盘
```

---

## 许可

- 本启动器的代码：跟随本仓库许可
- **VCClient 本体**：版权归 w-okada 所有，遵循其原始许可