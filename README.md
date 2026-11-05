# SCSKiller HoYo 分支

基于 [BlueHeisenberg/SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) 的社区分支，增加原神、
崩坏：星穹铁道、绝区零的离线着色器资源读取。保留上游的界面、游戏扫描、编译规划和原生预热宿主。

**当前定位：Windows x64 预发布版本。** 已验证资源索引、字节码回读、编译输入准备和本机 NVIDIA DX11 预热；尚未验证三款游戏
实际运行时的驱动缓存命中或卡顿改善。绝区零 BBL 补丁清单仍不支持。请勿将这些游戏标记为已完成稳定适配。

- [本分支源码](https://github.com/BedrockLian/SCSKiller) · [发行页面](https://github.com/BedrockLian/SCSKiller/releases)
- [构建、运行依赖与读取器说明](docs/hoyo-readers.md)
- [分发包使用说明](docs/distribution.md) · [发布验证要求](docs/releasing.md)
- [问题反馈](https://github.com/BedrockLian/SCSKiller/issues) · [更新记录](CHANGELOG.md)

本分支由社区维护，与上游及游戏厂商均无隶属关系。上游发行版不含本分支的新增读取器；
本分支的问题请提交到本仓库。上游产品介绍和原有游戏验证结果见
[上游 README](https://github.com/BlueHeisenberg/SCSKiller/blob/02a91be56f1c32c9b21a5aac6cad5e86d6e1b75a/README.md)，
不能用作三款 HoYo 游戏的效果证明。

## 支持到哪一步

同步基线：上游 **v1.4.5**，提交 `02a91be56f1c32c9b21a5aac6cad5e86d6e1b75a`。

| 游戏 | 验证过的资源版本 | 已实现 | 尚未验证或实现 |
|---|---|---|---|
| 原神 | 7.2 | BLB3 串接容器、LZ4HC/Oodle、103,864 项 DX11 预热 | 游戏内缓存命中及性能收益 |
| 崩坏：星穹铁道 | 4.6.0 | ENCR/mr0k、外部 LZ4 字典、17,916 项 DX11 预热 | 游戏内缓存命中及性能收益 |
| 绝区零 | 3.3.0 | mhy1、Unity shader blob、50,780 项 DX11 预热 | BBL、SM6 的 DX11 规划、游戏内性能收益 |

资源文件只读。读取器同时索引基础资源和 Persistent 更新，不推断游戏实际挂载的覆盖顺序。
字节码在准备编译输入时再次核对哈希。游戏更新可能改变布局，旧版本验证结果不代表新版本兼容。
DX11/DX12 的硬件、供应商和管线信息限制沿用上游，详见[支持边界](docs/hoyo-readers.md#支持范围)。
上述预热在 RTX 5060 Laptop GPU / 驱动 617.42 上完成，三批均为零失败、零驱动跳过；
这证明原生宿主接受了这些输入，不能代替游戏内效果测试，也不能外推到 AMD、其他驱动或 DX12。

## 使用分发包

如果[发行页面](https://github.com/BedrockLian/SCSKiller/releases)已有本分支包，下载
`SCSKiller-HoYo-<版本>-win-x64.zip` 及同名 `.sha256` 文件。源码 ZIP 不含可执行程序；
如果页面没有附件，请按下文自行构建。

1. 核对 SHA-256 后，将整个压缩包解压到可写目录。
2. 双击 **`Start-SCSKiller.cmd`**。数据保存在包内 `data/`，不使用上游安装版的数据区。
3. 在界面中扫描或添加游戏目录，再检查识别结果和日志。首次读取 Oodle 资源需要联网下载项目锁定并校验的解码器。
4. 更新时退出旧进程，将旧包的 `data/` 保留到新包；重新扫描游戏，不复制旧程序 DLL。

包内包含 .NET 和 Windows App SDK 运行时，不需要另装 .NET。请保留全部文件。
Oodle DLL 不随包分发；当前 HoYo 路线需要 AVX2/BMI2 CPU。
包没有使用上游的安装器、自动更新源或签名身份；更新从本仓库手动下载。
未签名程序可能触发 Windows 的发布者提示，校验和用于检查文件一致性，不能代替代码签名。

启动脚本保持录制器关闭，不修改游戏文件、系统环境变量或上游安装数据。
直接双击内部 `SCSKiller.exe` 会显示开发构建提示，请使用包内启动脚本。

## 从源码构建

需要 .NET 10 SDK x64、Visual Studio 2022 / Build Tools 的 C++ 桌面开发工具、v143、MASM、
CMake 3.20+ 和 Windows SDK 10.0.26100.0；还原和原生配置需要联网。

```powershell
git clone https://github.com/BedrockLian/SCSKiller.git
cd SCSKiller
dotnet build SCSKiller.slnx -c Release
dotnet test tests/SCSKiller.Tests/SCSKiller.Tests.csproj -c Release --filter 'Needs!=Game'
cmake -S proxy -B proxy/build -G 'Visual Studio 17 2022' -A x64
cmake --build proxy/build --config Release --target scskiller_warm scskiller_warm_segheap --parallel 2
```

然后双击仓库根目录 **`启动SCSKiller.cmd`**，开发数据保存在 `out/app-data/`。
开发版界面需要 .NET 10 x64 Runtime；Windows CLI 默认自包含。SDK 已包含对应运行时。

构建完整自包含分发包：

```powershell
./build/publish-hoyo.ps1 -Version 1.4.5-hoyo.1
```

该命令先运行合成回归测试，复用上游的 x64/x86 原生组件和自包含发布检查，加入独立启动脚本、
使用说明、来源记录及 ZIP 校验和。产物位于 `dist/`，不会自动上传或创建 GitHub Release。
具体依赖、联网与离线使用方式见[构建指南](docs/hoyo-readers.md)。

## 验证与贡献

默认测试不依赖游戏安装、逆向工具或 GPU。可选只读资源测试见[测试说明](tests/SCSKiller.Tests/README.md)。
提交前应运行回归测试；新格式或识别问题需要能够复现的合成测试。工作流检查托管构建和测试，
手动运行时可选择生成完整 Windows 包；成功状态与具体提交绑定。

不要提交游戏资源、着色器字节码、用户数据库、运行时 DLL、SDK 或分析工具。分发稳定版还需要
完成[发布验证要求](docs/releasing.md)中的实际 GPU 和游戏效果验证。

## 许可证与致谢

遵循上游 **GPL-3.0-or-later** 及 [附加许可](LICENSE-EXCEPTION.txt)。二进制分发时应提供准确对应的源码，
保留 [LICENSE](LICENSE)、[第三方声明](THIRD-PARTY-NOTICES.md) 和依赖随附的许可文件。
移植的 AnimeStudio 格式、密码算法与数据表保留 MIT 署名，见
[原始许可](src/SCSKiller.Core/Unity/ThirdParty/AnimeStudio-LICENSE.txt)。

感谢 SCSKiller 上游作者及其依赖项目。上游的账号、赞助和社区服务仍由上游独立维护，
本分支不提供其会员权益或服务兼容保证。
