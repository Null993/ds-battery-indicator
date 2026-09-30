# DualSense 研究工具

结论、实验日志和下一步见 [研究记录](../docs/research/dualsense/README.md)。`artifacts/` 是本地原始证据目录，Git 忽略它；公开来源 URL、固定 commit、SHA-256 和脱敏分析摘要进入版本控制。任何新实验都应新建采样目录，不覆盖旧样本。

## 实机只读采样

```powershell
dotnet run --project research/DualSenseProbe -c Release -- research/artifacts/captures/NEW_SESSION_NAME 10
```

只选择一个普通 DualSense USB 游戏手柄接口（VID 054C，PID 0CE6，输入长度 64）；零个或多个设备会退出，不任意选取。仅枚举描述符能力、读取输入报告、发送已知 USB `GET_FEATURE 0x05/0x20`。工具未实现输出报告、SET_FEATURE、未知 ID 扫描、校准写入、配对和固件更新。允许采样时长 1–3600 秒；长采样会占用设备读句柄，应在确认实验条件后手动运行。

每条记录包含 UTC 与单调时间、Report ID、含 ID 的长度和十六进制原始字节。`hashes.json` 记录文件 SHA-256。HidLibrary 的 Feature 读取成功只返回固定最大缓冲区长度，不能据此确认真实 USB 传输长度；日志明确标注逻辑期望长度与缓冲区长度。

`descriptor-capabilities.json` 来自 Windows `HidP_GetValueCaps`，不是原始 USB report descriptor 转储；保存每个结构的 72 字节原始值以便重新解释。当前设备按钮能力不含额外 Feature ID（`NumberFeatureButtonCaps=0`）；工具只遍历 ValueCaps，不宣称覆盖任意设备的全部描述符元素。

## 资料快照

```powershell
./research/Fetch-Sources.ps1
```

此命令抓取当前指定文件的最近 commit，并按固定 commit 下载、计算 SHA-256；结果写入本地 `artifacts/sources/manifest.json`。本次研究使用的固定清单另存于 `docs/research/dualsense/source-manifest.json`，以后运行不会改写该已提交清单。GitHub 下载使用命令级 127.0.0.1:7890 代理；脚本临时设置的 gh 环境变量在 finally 中恢复。

## 离线分析

```powershell
python research/analyze_artifacts.py research/artifacts
```

分析现有固件文件、采样字节变化、时间戳候选、少量 CRC32 假设、Feature 前后差异；不向手柄发送任何请求。输出 `offline-analysis.json`，保留原始输入。脚本没有把高熵字节自动解释成电量/电流，也没有假设固件一定加密。

## UI 回归与预览

```powershell
dotnet run --project tests/DsBatteryIndicator.RegressionTests -c Release -- research/artifacts/previews
```

测试使用合成数据，不写用户设置、不连接硬件。预览的 100% 是测试夹具，不是实时仪表读数；功率显示“— W”。

## 官方程序静态分析

本轮发现本机官方应用2.2.1.2后，使用 ILSpy CLI 9.1.0.7988 反编译托管库，并用 pefile 2024.8.26 / Capstone 5.0.9 静态读取原生 PE。工具均局部安装于 `artifacts/`，输出不进入生产应用。

```powershell
dotnet tool install ilspycmd --tool-path research/artifacts/tools --version 9.1.0.7988
python -m pip install --target research/artifacts/python-libs pefile==2024.8.26 capstone==5.0.9
research/artifacts/tools/ilspycmd.exe -p -o research/artifacts/sony-managed "C:/Program Files/Sony/PlayStationAccessories/PlayStationAccessories.dll"
python research/inspect_sony_native.py "C:/Program Files/Sony/PlayStationAccessories/libFWupdater.dll" research/artifacts/sony-native --rva 3ce0 --rva 8600 --rva 3400 --range 3400:3640
```

`inspect_sony_native.py` 不加载或执行目标 DLL。每次分析应先记录哈希和版本；这里的 RVA 只对应本轮哈希，不应盲用于另一版 DLL。详细解读见 [官方程序分析](../docs/research/dualsense/2026-09-30-sony-app.md)。
