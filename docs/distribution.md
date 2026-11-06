# SCSKiller HoYo 包使用说明

这是 [BedrockLian/SCSKiller](https://github.com/BedrockLian/SCSKiller) 的 Windows x64 社区发行包。
原神 BLB3、崩铁 ENCR、绝区零 mhy1 的读取、编译输入准备及本机 NVIDIA DX11 原生预热已验证；
三款游戏实际缓存命中和帧时间收益尚未验证，其他供应商/驱动没有本次适配的 GPU 验证结果。
绝区零 BBL 不支持，会记录并跳过。完整支持说明见
[读取器文档](https://github.com/BedrockLian/SCSKiller/blob/main/docs/hoyo-readers.md)。

## 启动

1. 在发行页面取得 ZIP 和同名 `.sha256`；用 `Get-FileHash <ZIP路径> -Algorithm SHA256` 比较哈希。
2. 完整解压到当前用户可写目录。不要在压缩包预览中运行，也不要只复制 EXE。
3. 双击 `SCSKiller.exe` 或 `Start-SCSKiller.cmd`。请保留包内的 `.scskiller-hoyo-portable` 标记。
4. 在界面扫描或手动添加本机游戏，核对引擎识别与日志，再进行索引和规划。当前读取器只读游戏资源。

需要 Windows 10 2004（19041）或更新版本的 x64 系统，以及支持 AVX2/BMI2 的 CPU。
包内已包含 .NET 10 和 Windows App SDK；不需要安装 SDK、Unity 或逆向工具。
首次解码 Oodle 资源需要联网下载固定版本的解码器并校验 SHA-256，文件位于 `data/codecs/`。
解码器缺失或 CPU 不支持时会报告原因。Oodle DLL 不随此包分发。

命令行从包目录运行，例如：

```powershell
./SCSKiller-CLI.cmd --help
./SCSKiller-CLI.cmd --version
```

GUI 和 CLI 默认使用同一个包内 `data/`，两个启动脚本还会关闭测试录制器开关。保持这个数据目录设置时，
程序会按上游规则拒绝录制器安装和依赖正式安装的系统集成功能。不需要管理员权限。

## 更新与卸载

本包未安装为 Velopack 应用，不使用上游自动更新源。请从本仓库下载新包，退出旧版后再更新。
保留 `data/` 可以延续设置和索引；请勿用旧 DLL 覆盖新文件。程序不自动导入上游安装版数据。
要删除此副本，退出程序后删除解压目录；GPU 驱动自行管理的缓存不在这个目录里。

包未签名；校验和只能验证取得的文件与发布文件是否相同，不能代替发布者签名。
不要关闭系统防护以运行来源不明的副本。发生启动错误时保留错误信息和 `data/` 中的日志，
在[本分支 Issues](https://github.com/BedrockLian/SCSKiller/issues)报告版本、系统、GPU 和游戏版本，
不要上传账号凭据、游戏文件或着色器内容。

## 来源与许可

`build-info.json` 记录仓库、版本、源提交及构建时工作区是否有未提交变更；`modified: true` 的包
不能仅凭该提交还原，应重新从干净提交构建后再分发。
准确对应的源码应随发行说明提供；源码和许可见
[本仓库](https://github.com/BedrockLian/SCSKiller)、包内 `LICENSE`、`LICENSE-EXCEPTION.txt`、
`THIRD-PARTY-NOTICES.md` 以及 `notices/`。这不是游戏厂商或上游的官方发行版。
