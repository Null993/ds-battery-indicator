# 2026-10-06：原装接口追加读取、来源核验与固件密钥假设

日期为 Asia/Shanghai，原始证据时间保留 UTC。用户已说明暂无电池侧测量设备，要求继续研究原装手柄接口。本轮继续使用普通 DualSense USB `054C:0CE6`、固件 `0630`；**实际电池充电 W 和真实 1% SOC 仍未实现**。没有用电压模型、档位插值或额定电流代替真实测量。

## 新增实机实验

研究工具增加显式选项 `--read-extra-features`，只追加 GET_FEATURE `08`、`0C`。执行前检查本机 HIDP ValueCaps：Feature 类型、8 bit、非 range、reportCount 分别为 47/41；逻辑长度含 ID 分别是 48/42。声明不匹配时退出，不执行 Feature/input 查询。Windows `HidD_GetFeature` 使用最大 Feature 缓冲 64 字节；这个缓冲长度不是实际 USB 返回长度。默认查询仍是 05/20；没有通用未知命令扫描。

第一次追加 GET08/0C 均失败。为保留失败原因，工具改用已有的 `HidD_GetFeature` P/Invoke 保存立即取得的 Win32 错误和原始 API 缓冲，再以新目录重复该实验。失败缓冲只是 API 内存，**不是有效手柄响应**。

| 会话（均在 `research/artifacts/captures/`） | 有效输入/失败 | wire[53] | 有效电压查询 | mV 范围 |
|---|---:|---|---:|---:|
| `2026-10-06-soc-followup-endpoint` | 793 / 0 | 全部 19 | 3/3 | 3804 |
| `2026-10-06-extra-features` | 1524 / 0 | 全部 19 | 6/6 | 3808–3809 |
| `2026-10-06-extra-features-errors` | 798 / 0 | 全部 19 | 3/3 | 3802–3815 |

共 3115 条有效输入和 12 条电压查询。19 表示状态 1（充电）、档位 9，不是已充满。电压来自之前验证的固定 `80 04 03 → 81` 查询，单位沿用来源 mV，仍没有独立校准。最后一个短会话出现 3815→3803→3802 mV；只记录变化，不据此推断电流、SOC 或变化原因。

| Feature | 实验前后结果 | 最后会话 Win32 错误 | 说明 |
|---|---|---:|---|
| 05、20 | GET 成功，前后相同 | 0 | 校准/固件信息 |
| 22 | GET 成功，前后相同 | 0 | 包含已匹配的硬件/固件元数据 |
| 08、0C、21 | GET 均失败 | 31 | Windows ERROR_GEN_FAILURE；不能仅由此确定失败发生在哪一层或所有模式都不可读 |

电压响应的第二个 payload word 本轮分别为 1949、1987–1988、1997–1998。继续保留原值，没有将其命名为 mA。虚拟设备提供温度线索，但不能校准原装设备的物理含义和单位。已检查的 Power 命令表仍只明确列出改变充电状态的动作；混合枚举中的 `BATTERY_VOLTAGE=6` 尚无完整设备组/请求实现，不猜测执行。

离线复核三组会话 `hashes.json` 均无不一致，电压请求/响应格式均通过查验。统计、错误码和时间戳见 [分析 JSON](2026-10-06-stock-interface-analysis.json)。

## 新来源逐项核验

固定版本、文件与 SHA-256 见 [本轮来源清单](2026-10-06-stock-source-manifest.json)，本地原始源码保存在 `research/artifacts/sources/`。只作静态阅读，没有运行外部诊断、校准或固件更新代码。

| 来源 | 核验结果 |
|---|---|
| `eddeeh/dualsense-ps5-mitm`，commit `78445e3a9cc61b1840d807dc51cea03bc041e607` | `DualSense.h` 记录 Feature 长度；`InputView.cpp` 的电池显示读取高/低四位，映射三格。电压/温度异常是错误状态枚举，不是测量字段。未发现该部分的精细 SOC/电流接口。 |
| `SpecialKO/SpecialK`，commit `d0672a5499df62f0f23efdc81d87e0a0dc0c35bd` | `playstation.cpp` 的 DualSense 状态结构将 PowerPercent 定为 4 bit、范围 00–0A。DS4 的另一段电量算法不能当作 DualSense 精细数据。 |
| `Sufxx/dualsense-doctor`，commit `0f1d17546d5034b6bec8d74632073855d5cc4175` | MCP `getBattery` 仍按低四位乘 10，没有电流接口。该实现明确输入含 ID，却对 USB 使用索引 54，与本机/Linux 的 wire[53] 不一致。本机首条错误码会话 wire[53]=19、wire[54]=1C，照该代码会显示 100 而非档位 9。没有采用它的偏移或把 MCP 工具名称当作新协议证据。 |
| `ConWan30/QorTroller` 的 sensor-stack 文档 | 二手综合材料宣称没有瞬时电压接口，与本机已成功的 ADC 电压查询冲突；不能以它排除隐藏接口。 |
| `ps-iowned/ps-iowned` README | 对象是 PSIO（PS1 配件），不是 DS4/DualSense。早先将其描述为 DS4 固件解码线索的表述有误，已纠正并排除；未运行该项目算法。 |

搜索与网页结果保存在本地 `interface-search-followup.json`、`power-command-wiki-followup.json`、`firmware-current-search-followup.json`、`stock-new-leads-followup.json` 等。检索为空不证明接口不存在；搜索结果里充电底座额定电流、虚拟设备 BatteryVoltage、游戏油量显示，均不能证明电池侧实时电流。

GitHub raw 下载的代理在本轮前段出现 TLS/超时失败，随后直连成功；新增 doctor 源码通过命令范围代理下的 `gh api` 按固定 commit/blob 获取。临时 HTTP_PROXY/HTTPS_PROXY 在 finally 中恢复，没有修改系统/全局代理或远程地址。

## 公开 DS4 密钥的离线假设

另行检查了 [PS4 Developer Wiki 的 DualShock 4 Keys](https://www.psdevwiki.com/ps4/Keys#Dualshock_4_Keys) 中 Jedi master v1/v2。该来源说的是 DS4 的 AES-128-CBC/零 IV，不是已经验证的 DualSense 解密方法。离线工具 `research/Test-PublicFirmwareKeys.ps1` 对已保存的 0520/0630 两个文件，各以两把公开密钥、四个假设偏移 `80/100/200/1000`（十六进制）测试最多 65536 字节，共 16 个组合。

AES 实现先通过 NIST SP800-38A F.2.2 的首块正例。16 个输出熵为 7.997042–7.997359 bit/byte；预设 battery/charging/voltage/current/Copyright/Sony/Banana/Venom/Betty 标记命中均为 0。全部 `verifiedPlaintext=false`；没有恢复可信代码、向量表、完整性校验或电池算法。

结果见 [密钥假设 JSON](2026-10-06-key-hypotheses.json)。负结果只适用于这些有限组合；不能证明固件一定使用 AES、密钥必然错误，或不存在其他压缩/编码、IV、偏移。没有执行解码输出，也没有发送固件更新命令。

补充静态核对此前保存的 `nowrep/dualsensectl/main.c`：`dualsense_fw_start` 和 `dualsense_fw_write` 直接把固件文件切片 memcpy 到传输缓冲，并不在这两个函数里解密文件。没有运行它的更新流程。这削弱了“用这条主机更新路径直接得到明文”的线索，不能推及所有官方程序或证明设备内部的解码算法。

## 当前缺口与后续判定

实际充电功率需要同一时刻的电池侧 V 和 I；只有约 3.8 V，无法确定 W。电压到 SOC 的关系还取决于充电/放电、温度、负载、老化等；1 mV 数字分辨率不是 1% SOC 精度。十档输入即使改成 91/92/93 的显示也没有增加信息。

后续有意义的新证据应是：原装固件的可验证解码/明文、明确的电池电流或精细 SOC 查询实现、该型号 PMIC 的可靠寄存器/遥测说明。未知动作扫测和重复采集相同档位不能自行补齐语义。暂无仪器时，可以凭可靠厂商/协议说明验证字段；本轮尚未找到这样的电流或 SOC 说明，也没有证明原装设备一定没有这些能力。

本轮完成研究工具与证据记录，生产 `ChargingPowerWatts` 继续为空，浮窗显示 `— W`；不把未实现的两个测量需求标为完成。既有显隐、居中布局和满电环修改不受本轮研究工具改动影响。

复现新增查询（使用新的空目录）：

```powershell
dotnet run --project research/DualSenseProbe -c Release -- research/artifacts/captures/NEW_SESSION 12 --read-extra-features --read-battery-voltage --read-21-22
python research/analyze_voltage_captures.py research/artifacts/captures/NEW_SESSION --output research/artifacts/NEW_ANALYSIS.json
```

验证：Release 研究工具构建 0 警告、0 错误；新增模式在上述两组实机实验中执行；离线哈希与响应验证通过。没有修改生产解析器，未重复无关 UI 回归。
