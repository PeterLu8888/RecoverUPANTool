# RecoverUPANTool

盲扫坏盘的**块级整卷备份**小工具（Windows / .NET Framework 4.x，`csc.exe` 直接编译，无依赖）。

当 U 盘/移动盘拷贝文件报错、文件系统已经不可靠时，最稳的抢救方式不是再用资源管理器去"读文件"，
而是把整卷当成一串扇区原样镜像下来，事后再对镜像做文件级操作。

这个工具就是干这个的：**全程不解析文件系统**——不列目录、不读文件名、不看文件内容，
只以 4 MiB 为单位读 `\\.\E:` 并写入镜像文件。

## 特性

- **不碰文件系统**：没有 exFAT/NTFS 解析代码，文件名和内容都不会被读取或解释。
- **无需管理员即可读卷**：用 `\\.\E:` 卷句柄（`\\.\PhysicalDriveN` 需要管理员，本工具绕开）。
- **稀疏镜像**：全 0 的 4 MiB 块不写盘，NTFS 上留洞，逻辑 1.86 TiB 的镜像实际占用小得多。
- **断点续跑**：每 256 MiB 写状态文件，作业中断后重跑同一条命令即可继续，不重头来。
- **坏块容忍**：读失败的块不跳过不猜，逐条记录进 `bad-sectors.txt`。
- **死区快进**：连续失败时用指数探测 + 二分找边界，一整段几百 MiB 的死区只需几次探测。
- **多轮重试 + 冷却**：针对"读到一半罢工、歇一会儿又恢复"的间歇性 CRC 故障；
  轮次之间静置，盘恢复后能抢回大量数据。
- **二分拆块**：对顽固坏块向下拆分到 64 KiB，尽量把坏块旁边的可读数据救出来。
- **抽样校验**：`Verify.exe` 随机抽 1 MiB 块逐字节比对镜像与源盘。

## 编译

```powershell
.\build.ps1
```

只用系统自带的 `csc.exe`，产物 `RawImager.exe` / `Verify.exe` 在仓库根目录。

## 用法

```powershell
# 首次整卷镜像（默认源 \\.\E:，默认目标 D:\PlayGround\backup）
RawImager.exe --fresh

# 中断后续跑（不要加 --fresh）
RawImager.exe

# 自定义路径
RawImager.exe --src '\\.\E:' --dst 'D:\img\disk.raw' --map 'D:\img\bad.txt' --state 'D:\img\disk.state' --log 'D:\img\progress.log' --fresh

# 抽样校验
Verify.exe --img 'D:\img\disk.raw' --src '\\.\E:' --samples 300
```

### 参数

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--src` | `\\.\E:` | 源卷 |
| `--dst` | `D:\PlayGround\backup\E-volume.raw` | 目标镜像（稀疏） |
| `--map` | `D:\PlayGround\backup\bad-sectors.txt` | 坏区清单 |
| `--state` | `D:\PlayGround\backup\.E-volume.state` | 断点状态 |
| `--log` | `D:\PlayGround\backup\image-progress.log` | 进度日志 |
| `--fresh` | 关 | 清空状态/清单，从头开始 |
| `--maxbytes` | 全卷 | 只处理前 N 字节（调试用） |
| `--prewait` | 0 | 第二遍开始前先空转 N 秒，让盘降温 |
| `--rounds` | 3 | 第二遍重试轮数 |
| `--cooldown` | 90 | 轮次之间的冷却秒数 |
| `--p2budget` | 3600 | 拆块抢救阶段的总预算秒数 |
| `--p2min` | 65536 | 拆块的最小粒度（字节） |
| `--pace` | 0 | 重试时每次尝试后暂停 N 毫秒，给盘喘息 |
| `--batch` | 0 | 每 N 次尝试后做一次较长休息（配合 `--rest`） |
| `--rest` | 0 | 每 `--batch` 次尝试后的休息秒数 |

`--p2budget 0` 会跳过拆块阶段，只做重试轮次。

运行时可以创建一个 `STOP.flag`（与镜像同目录）让它在当前块结束后安全停机；
删掉这个文件再运行同一条命令即可续跑。

## 工作流程

1. **第一遍（顺序扫描）**：4 MiB 一块读源卷 → 写镜像；全 0 块不写；读失败则记 `BAD4M`，
   连续失败时用指数探测 + 二分找死区边界，整段记 `BADRUN` 后快进。
2. **第二遍（重试）**：对失败块做多轮重试，轮次间静置。间歇性故障常见于 USB 桥/主控过热，
   冷却后大批块能重新读出。
3. **第三遍（拆块）**：仍失败的块二分拆分到 64 KiB，尽量救出坏点旁边的数据；
   最终仍不可读的记录为 `BAD`。

## `bad-sectors.txt` 格式

```
BAD4M     <卷内偏移> <4194304> err=23     第一遍 4 MiB 块读失败（ERROR_CRC）
BADRUN    <偏移> <长度>                    连续失效区（已被快进，内部块靠第二遍重试）
RECOVERED <偏移> <长度> round=<轮次>        重试/拆块成功
BAD       <偏移> <长度>                    最终仍不可读
SUMMARY   total=.. written=.. zero=.. badbytes=.. badchunks=..
```

## 恢复数据

1. 准备一块 ≥ 镜像逻辑大小的空盘/分区（**不要写回原盘**）；
2. 原样写回（`dd` 用 PortableGit 自带的即可，需要管理员权限）：

```powershell
dd.exe if=D:\PlayGround\backup\E-volume.raw of=\\?\X: bs=4M conv=noerror,sync
```

3. 挂载后取文件；`bad-sectors.txt` 里记录的区间就是已经丢失的扇区。

> 镜像是稀疏文件，复制它要用支持稀疏的工具（如 `robocopy /b`），否则会按逻辑大小实占空间。

## 已知限制

- 仅 Windows（依赖 `\\.\Volume` 句柄、`DeviceIoControl`）。
- 读整盘物理设备需要管理员；本工具用卷句柄规避，因此镜像是**卷级**的（不含分区表/MBR）。
- 稀疏镜像在非 NTFS 目标上不节省空间。
- 对已经彻底损坏（持续报 CRC）的介质，重试只能抢回间歇性失效的部分，物理坏区不可恢复。

## License

MIT
