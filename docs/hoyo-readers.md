# 原神、崩铁、绝区零：构建与运行

这是基于 [BlueHeisenberg/SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) 的实验性源码分支，
整合原神 BLB3、崩铁 ENCR、绝区零 mhy1 离线着色器读取器。同步基线为上游 v1.4.5：
`02a91be56f1c32c9b21a5aac6cad5e86d6e1b75a`（2026-11-04）。
本分支的构建包和源码入口见 [README](../README.md)；上游发行版不包含这些新增适配。
当前三款游戏保持实验性状态，分发流程见 [发布验证要求](releasing.md)。

## 支持范围

| 游戏 | 本机验证版本 | 读取方式 | 限制 |
|---|---|---|---|
| 原神 | 7.2 | BLB3 串接容器，LZ4HC / Oodle | 已验证该版本布局，不承诺未来更新兼容 |
| 崩坏：星穹铁道 | 4.6.0 | ENCR / mr0k，Oodle，ShaderPlatformInfos 外部 LZ4 字典 | 含该分支不做头部对齐的 UnityFS v7 |
| 绝区零 | 3.3.0 | mhy1、Unity Shader blob（平台 4 和 25）、RayTracingShader 对象中的 DXIL | BBL 格式尚未实现，会明确提示并跳过 |

仅对安装文件做离线读取，准备编译输入时重新核对字节码哈希。基础资源、Persistent 更新与已验证的无扩展名 PC SDK 包均纳入索引，
没有推断游戏实际挂载的优先级。这些改动不启用游戏录制器，也不修改游戏注入规则。

HoYo 资源索引统计 DirectX 字节码。原神默认资源中发现的 SPIR-V、GLSL／GLES 源码和安装目录中的原生模块字节码
不计入这些资源索引数字。绝区零 SM6 和光追库已可索引和回读，DX12 管线预热仍需完整管线状态及根签名。

读取成功不等于已验证真实驱动缓存命中。DX11 沿用现有供应商能力限制；AMD 的顶点输入布局要求、
DX12 的完整根签名/PSO 要求仍然适用。最初的规划验证使用模拟 NVIDIA 能力；后续本机 GPU 验证单独列在下文。

## 构建机需要什么

以下流程适用于 Windows x64；这次三游戏适配未在 Linux/macOS 上验证。

- **Git**：克隆源码和同步上游。
- **[.NET 10 SDK x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**：`global.json` 的最低 SDK 为
  10.0.100，允许较新版本。本次使用 10.0.403。SDK 已含 .NET 运行时，构建机不用再单独安装运行时。
- **Visual Studio 2022 或对应 Build Tools**，安装“使用 C++ 的桌面开发”、MSVC v143、MASM、CMake 工具和
  **Windows 11 SDK 10.0.26100.0**。Avalonia Windows 目标框架引用此 SDK；原生项目使用 C++20 和 CMake 3.20+。
  在 VS 开发者 PowerShell 中运行原生构建，确保 `cmake` 可用。
- 还原依赖时需要访问 **NuGet** 与 **GitHub**。CMake 会获取锁定版本的第三方源码及 AMD AGS；
  具体来源与校验规则保持上游构建脚本不变。

不需要 Unity Editor、REA、Ghidra、AnimeStudio、ImHex 或其他逆向工具。产品也不加载调查用 AnimeStudio DLL，
适配没有新增产品 NuGet 依赖。算法来源与许可证见 [第三方声明](../THIRD-PARTY-NOTICES.md#l44)。

## 从干净源码构建

先克隆仓库，后续命令在仓库根目录运行：

```powershell
git clone https://github.com/BedrockLian/SCSKiller.git
cd SCSKiller
dotnet --info
dotnet build SCSKiller.slnx -c Release
dotnet test tests/SCSKiller.Tests/SCSKiller.Tests.csproj -c Release --filter 'Needs!=Game'
```

上面的托管构建可以验证读取器和界面；要实际运行预热，还需要构建原生宿主及其依赖：

```powershell
cmake -S proxy -B proxy/build -G 'Visual Studio 17 2022' -A x64
cmake --build proxy/build --config Release --target scskiller_warm scskiller_warm_segheap --parallel 2
```

该命令构建 `scskiller_warm.exe`、segment-heap 变体、`d3d12.dll` 和所需的 Fossilize 依赖，
适用于本次离线读取与预热路线；完整上游录制器/发行包使用 [CONTRIBUTING.md](../CONTRIBUTING.md) 和
`build/publish.ps1`，不要求为本次适配额外启用它们。
原生输出在 `proxy/build/Release/`，开发构建使用现有 NativeTools 规则向上查找该目录。

如 AMD AGS 下载失败，检查日志中的 URL、代理或证书错误后重新配置；不要用来源不明或校验不符的 DLL 替代。

## 怎么启动，需要哪些运行时

**构建成功后，双击根目录的 `启动SCSKiller.cmd`。** 它使用通用的仓库相对路径，不依赖作者机器上的分析目录。

- Avalonia 界面：`src/SCSKiller.Ui/bin/Release/net10.0-windows10.0.26100.0/win-x64/SCSKiller.exe`。
- CLI：`src/SCSKiller.Cli/bin/Release/net10.0-windows/win-x64/scskiller.exe`。
- 原生组件：`proxy/build/Release/`，包括 `segheap/scskiller_warm.exe`。

普通 `dotnet build` 的 Avalonia 界面是框架依赖构建，运行机器需要 **.NET 10 x64 Runtime**；SDK 已包含它。
Windows CLI 项目默认 `SelfContained=true`，构建时也会复制 .NET 运行时，与界面输出不同。
没有 SDK、只运行拷贝来的构建目录时，从微软 [.NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
安装 x64 运行时。也可安装包含它的 Desktop Runtime。
Windows App SDK 由项目按自包含方式复制到 Windows 输出目录；不要只拷贝单个 exe，须保留整个输出目录。
完整自包含发行包会包含 .NET 运行时，不需要目标机器另装 .NET，区别见
[微软发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。本仓库仅上传源码，不附带这些运行时二进制。

裸开发版 exe 直接双击会拒绝使用正式安装版数据目录，并显示开发构建提示。
启动脚本为子进程设置独立的 `out/app-data/` 数据目录，同时关闭测试录制器开关；不修改系统环境变量。
如需自己从 PowerShell 运行 CLI：

```powershell
$env:SCSKILLER_DATA_DIR = Join-Path (Get-Location) 'out/app-data'
$env:SCSKILLER_TEST_RECORDER = '0'
& .\src\SCSKiller.Cli\bin\Release\net10.0-windows\win-x64\scskiller.exe --help
```

这是独立的开发版数据区，不会自动导入正式安装版的设置或扫描记录。更新后退出旧窗口再打开新版。

### Oodle 与其他原生依赖

含 Oodle 数据块的资源需要项目锁定的 `oodle-data-shared.dll`。读取器复用现有 Codecs 下载与 SHA-256 校验，
首次需要时从构建脚本指定的来源下载到应用数据目录下的 `codecs/`；使用上述启动脚本时即
`out/app-data/codecs/`。如果离线，准备与源码固定 SHA-256 完全一致的本地副本放入此目录。
不要替换为游戏 DLL，未匹配校验值的文件不会加载。

本次 HoYo 原生 Oodle 路线需要 CPU 支持 **AVX2 和 BMI2**；缺少指令集或无法取得锁定 DLL 时，会报告原因。
源码 fork 不上传或再分发 Oodle。`build/publish-hoyo.ps1` 固定调用上游 `-NoOodle` 打包，
输出完整自包含 Windows 包及独立启动脚本；许可证限制见第三方声明。
zlib-ng 等其他引擎依赖仍使用上游固定来源及校验流程。预热宿主采用静态 MSVC CRT，见 `proxy/CMakeLists.txt`。

## 验证结果与边界

2026-11-04 的只读本机验证：

| 检查 | 结果 |
|---|---|
| 合成回归测试 | 最初 28 项通过，覆盖格式识别、跨段读取、校验和、取消、源文件变化、字典解压和普通 Unity 回归；后续新增 2 项首个资源不支持时的识别回归，共 30 项 |
| 原神完整索引 | 235,543 个唯一着色器；64 容器副本 15,874 项全部回读并核对 SHA-1，准备 6,924 项 DX11 输入 |
| 崩铁完整索引 | 17,923 项；参考探针与旧读取器哈希并集全部包含，准备 17,916 项 DX11 输入且零缺失 |
| 绝区零完整索引 | 51,371 项；与参考哈希并集完全一致，准备 50,780 项 DX11 输入且零缺失；明确跳过 1 个 BBL 文件 |
| 字节码反汇编抽查 | 崩铁/绝区零共 65 份，全部通过微软 FXC/DXC |

HS/DS 配对后算一个规划项，因此规划数量与唯一字节码数量不同。绝区零另有 588 个 SM6 字节码未纳入此 DX11 规划。
没有在这轮 2026-11-04 验证中启动游戏、执行 GPU 预热、验证实际缓存命中或帧时间改善。仅保证上述已检查层次，
不把本机数字作为未来版本的固定测试期望。

进一步分析确认 BBL 属于 Silence 补丁清单，在元数据中找到了格式标记和候选常量，但尚未恢复有效目录和内容。
因此不能判断该文件是否能增加着色器覆盖，也没有把未验证的解密猜测接入产品。

### 着色器覆盖修复

绝区零 3.3.0 的当前读取器在可读取的 Unity 资源中索引并回读了 **104,333 个唯一 DirectX 字节码**，
与独立资源探针的 SHA-1 集合完全相同。相比此前的 51,371 项，新增 52,954 个平台 25 的 SM6 DXIL
和 8 个仅从 RayTracingShader 对象读取的光追库。BBL 继续记录并跳过，这些统计也不包括安装目录中原生模块的字节码。

同轮原神和崩铁分别索引并回读 **235,543** 和 **17,923** 个唯一字节码，与复核集合完全相同。三款游戏均逐项核对回读哈希。

### 2026-11-06 分发候选验证

以下是覆盖修复之前的预发布包验证。

从本分支重新构建完整 x64/x86 原生组件、Avalonia 界面和 CLI；整份解决方案 Release 构建零错误，
保留上游现有的 64 项编译警告。30 项合成测试全部通过；三款游戏重新执行完整只读索引及哈希回读，
数量与上表一致，崩铁/绝区零没有丢失参考哈希。原神全量安装另外准备了 103,864 项 DX11 输入，
不再仅使用前述 64 容器副本。

使用自包含包的原生宿主，在 **NVIDIA GeForce RTX 5060 Laptop GPU、驱动 617.42** 上逐批执行：

| 游戏 | 实际执行的完整 DX11 规划项 | 失败 | 驱动跳过 | 不支持 / 未构建 |
|---|---:|---:|---:|---:|
| 原神 | 103,864 | 0 | 0 | 0 / 0 |
| 崩铁 | 17,916 | 0 | 0 | 0 / 0 |
| 绝区零 | 50,780 | 0 | 0 | 0 / 0 |

合计 172,560 项，三批正常结束，无崩溃和中断。宿主在工作区暂存目录运行，没有启动游戏本体或安装录制器。
这些结果验证 DX11 输入能被这台机器的驱动接受，不证明游戏使用同一缓存键、缓存命中或帧时间改善；
测试前没有清空用户驱动缓存，也没有把执行时间作为冷缓存性能指标。SM6、DX12 和其他 GPU/驱动不在此验证中。

完整目录复制到源码树以外后，包内 CLI 在 `DOTNET_ROOT` 指向不存在目录的条件下仍正常输出版本，
界面通过独立脚本进入真实 Library 并识别本机 GPU。包内数据写在自身 `data/`，不借用源码树原生输出。
这仍不是未安装 SDK/.NET 的干净 Windows 系统测试，后者保留为稳定版发布前的验收项。

## 仓库保留内容

保留上游程序源码、构建文件、UI 必需资源、许可证、新增读取器与合成/可选集成测试。
不上传游戏安装文件、着色器字节码、真实资源副本、用户设置、数据库、分析探针/报告、反编译结果、
第三方工具、SDK、缓存、运行时 DLL 或本机构建输出。
可选真实资源测试需要开发者自行提供安装目录和参考哈希清单，默认测试不依赖它们；见
[测试说明](../tests/SCSKiller.Tests/README.md)。

## 同步上游

首次设置上游，然后合并更新，重新构建并运行回归测试：

```powershell
git remote add upstream https://github.com/BlueHeisenberg/SCSKiller.git
git fetch upstream
git switch main
git merge upstream/main
dotnet build SCSKiller.slnx -c Release
dotnet test tests/SCSKiller.Tests/SCSKiller.Tests.csproj -c Release --filter 'Needs!=Game'
```

如果已经存在 `upstream`，跳过添加 remote 的那一行。遇到冲突需保留经过验证的读取路线并重新测试，
不要直接覆盖整个 Core 目录。源许可证保持 GPL-3.0-or-later 及上游附加许可，移植算法保留 MIT 署名。
