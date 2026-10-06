# 2026-10-06：电压接口实机验证与电流线索核查

日期为 Asia/Shanghai。原始时间戳保留 UTC。**本轮成功读取了第三方硬件测试项目定义的电池电压接口；实际电池充电电流、功率与精确 SOC 仍未实现。** 这里的电压单位沿用来源代码，尚未用独立电压表校准，不能把毫伏读数直接变成真实电量百分比。

## 已执行的查询

设备仍为普通 DualSense USB `054C:0CE6`，固件 `0x0630`，主版本 `0x0110002A`，硬件信息 `0x00001107`。没有刷写、改变充电模式或校准数据。

新来源是 [daidr/dualsense-tester](https://github.com/daidr/dualsense-tester/blob/62dce1703a468a20af901db5e31e476ce5ec24b3/src/utils/dualsense/ds.util.ts#L516)，固定 commit `62dce1703a468a20af901db5e31e476ce5ec24b3`。来源枚举和实现共同给出：

| 项目 | 内容 |
|---|---|
| 请求 | 含 Report ID 的 64 字节 `80 04 03`，后续补零 |
| 设备组 | `ANALOG_DATA = 4` |
| 动作 | `BATTERY = 3` |
| 响应 | GET_FEATURE `0x81` |
| 完成校验 | wire[0..3] 必须为 `81 04 03 02` |
| 电压 | wire[4..5]，无符号 16 位、小端，来源标为 mV |
| 第二个数值 | wire[6..7]，暂保留原始值，不解释为电流 |

`sendTestCommandPure` 的第四个参数 4 是期待结果长度，不是另一个请求参数。工厂通道通过 SET_FEATURE 提交**查询**，所以“只读取信息”不等于“完全没有 SET_FEATURE”。新增工具只允许这条固定查询，不提供任意 device/action 扫测参数；每两秒提交一次，保存全部请求、响应、时间戳、API 成功与 Win32 错误。

每条查询最多轮询 25 次，检查单调计时约 1 秒；Windows 同步 HID 调用本身的阻塞时间由系统决定，这不是强制的总执行时限。严格要求匹配设备/动作、完成状态、64 字节 API 缓冲以及 2000–5000 mV 的保守范围。范围限制只是研究工具的异常过滤，不能证明读数的物理准确度；超范围保留原始响应但不发布测量值。

首次原始响应的前八字节：`81 04 03 02 88 0E 38 07`。电压数值为 `0x0E88 = 3720`。其余字节均为零。第二个数值为 `0x0738 = 1848`，**不是已验证的 1848 mA**。

## 实机证据

| 本地会话名 | 有效输入报告 | 读取失败 | 电池状态 | 电压查询结果 |
|---|---:|---:|---|---|
| `2026-10-06-read-21-22` | 1334 | 0 | 全部 `17`，充电档位 7 | 未查询电压 |
| `2026-10-06-battery-voltage` | 1610 | 0 | 全部 `18`，充电档位 8 | 6/6 成功，3719–3721 mV |
| `2026-10-06-battery-voltage-long` | 23269 | 0 | `18` 20534 条，`19` 2735 条 | 90/90 成功，3745–3775 mV，31 种值 |

三分钟会话：本地 20:45:40–20:48:40，首次 3745 mV、最后 3774 mV。约 158.855 秒处，`wire[53]` 从 `18` 跳为 `19`（20:48:19）。生产应用按低四位乘 10 显示 80%→90%；Linux/Sony 应用以档位中点显示 85%→95%，同样没有增加真实测量精度。

这次记录支持“已知输入字段仍按十个百分点分档，另一个查询返回更细的电压数值”。没有独立 SOC、电压/电流参考仪器；没有采集放电、拔线或本轮充满状态，不能据此声称已校准电压、恢复精确 SOC，或推断完整充电曲线。用电压变化速度乘额定容量，仍然是模型估算，不能满足实际功率要求。

所有采样文件均通过各自 `hashes.json` 的 SHA-256 复核，未发现不一致。脱敏统计见 [分析 JSON](2026-10-06-voltage-analysis.json)。复现命令：

```powershell
dotnet run --project research/DualSenseProbe -c Release -- research/artifacts/captures/NEW_SESSION 180 --read-battery-voltage --read-21-22
python research/analyze_voltage_captures.py research/artifacts/captures/NEW_SESSION --output research/artifacts/NEW_ANALYSIS.json
```

## Feature 21/22

GET `0x21` 在两次含该查询的会话中均失败。它在描述符中出现不能证明支持 GET；[报告摘要页面](https://controllers.fandom.com/wiki/Sony_DualSense/Report_Summaries)的搜索摘要将其称为音频控制 SET。完整页面访问受限，该名称只作未完全核验的线索，不作为写命令依据。

GET `0x22` 成功，包含硬件/固件元数据：wire[1]=3 与 fwType 匹配，wire[3..6]=`07 11 00 00` 与硬件信息匹配，wire[7..10]=`2A 00 10 01` 与主固件匹配。新来源 `getBtPatchInfo` 从 wire[31..34] 读 BT patch 版本。各会话前后响应相同，跨电量 7/8/9 也没有变化。未从该报告识别到瞬时 V/I；只说“包含元数据”，不宣称已经解析全部未知字段。

## 电流线索逐项查验

以下源码均已按固定 commit 保存并计算哈希，见 [本轮来源清单](2026-10-06-source-manifest.json)。

| 线索 | 实际核查结果 | 对需求的影响 |
|---|---|---|
| `dualsense-tester` 的 ADC Battery 查询 | 实机成功；已验证响应格式，来源定义第一字为电压 | 可继续验证电压精度，不能计算 W |
| `DualSenseClient/VIIPER` 虚拟设备 | `device.go` 的 sensor 组模拟电压和温度；默认数据是合成值 | 第二字有温度线索，虚拟实现不是实机单位校准证据 |
| `dualsense-tester` 诊断 telemetry | 实现充电累计时间、次数、异常计数；没有瞬时电流字段 | 累计时间不能变成实际充电功率 |
| `BATTERY_VOLTAGE=6` 枚举名称 | 在全局混合 action 枚举里，未发现对应 POWER 调用实现；设备组和完整语义尚未证实 | 未猜测参数执行；名称本身也不是电流接口 |
| [everything-imu](https://github.com/matiaspalmac/everything-imu/blob/8af3a268f618d646f6e280bcd16226854b6bda76/DEVICES.md) 的 `0x22 Battery current` | 属于 Joy-Con 2/Pro Controller 2 的 BLE 输入字段 | 不是 DualSense Feature 22，不移植该偏移 |
| [DualSense-Padhack](https://github.com/Squibs/DualSense-Padhack/blob/49188fbfd56ff3d8c324ff9ab2724943c34a572b/README.md) 的电压电量显示 | 作者额外接线至电池、加电阻分压与 Pico ADC，并更换电池；由电压估算电量 | 不是原装手柄精确 SOC 或电流接口 |
| [RogueByte PS5 Controller Lab](https://github.com/RogueByteOfficial/RogueByte-PS5-Controller-Lab/blob/5f4ecc7ee7de18854cefc99ecb1ea97b01f3dd22/src/triggers/trigger_adaptive.c#L110) 宣称 stall current sensing | 对应函数注释为模拟，直接设置 passed=true、stall=false、温度 27.2；非 PS4 分支初始化电量也有固定 85 | 不作为真实电流读取依据，也没有电池侧电流 |
| `RogueByte DualSense Pro Repair Suite` 协议文档 | 将状态高位写为互相独立的连接/充电标志，与 Linux 定义及实机状态枚举不一致 | 未采用这份位定义 |

目前没有找到可验证的 DualSense 电池充电电流读取库/驱动。这个结论限于已核查材料；没有证明固件内部或其他未公开通道一定不存在。

## 固件与硬件方向

[Bond 固件结构页](https://www.psdevwiki.com/ps5/index.php?title=Bond&oldid=2837)补充了 Banana/Venom/Betty 的历史头部说明。已知 Banana 的日期、PID、版本偏移与先前样本分析相符，但没有给出解码、解密密钥或电流协议。保留此前高熵/压缩流验证结论，不能把这些历史头部当作已恢复固件代码。

[iFixit 拆解](https://www.ifixit.com/Teardown/PlayStation+5+Teardown/138280)标识其拆解型号的 PMIC 为 Dialog DA9087。本机尚未拆机确认相同芯片。搜索命中标题为 DA9087 的数据表聚合页面时，正文实际展示 DA9080 等其他型号；未找到可验证的 DA9087 厂商寄存器表。不能套用其他 PMIC 的 I2C 地址或 ADC 比例。

[DualSense HID 命令表](https://www.psdevwiki.com/ps5/index.php?title=DualSense_HID_Commands&oldid=2785)中的 Power 动作 2/3 用于改变充电状态，并非测量。没有执行这些动作或任何 NVS/DFU/刷写流程。网页工具能读取上述 wiki；用 curl 获取固定 revision 的 HTML 返回 403，保留失败记录而没有伪造本地 HTML 哈希。Fandom 完整页面访问受限也一并记录。

## 当前实现和验收条件

自动显隐、功率开关、开启/关闭布局调整、功率与百分比共用中轴线、100% 绿色完整环已在既有分支实现。本轮新增了可复用的固定电压研究查询与离线查验工具，**没有把它当成功率接入生产应用**。浮窗依然显示 `— W`。

63 项回归检查通过，包括错误设备/动作、未完成响应、截断报告、异常电压、第二字不冒充电流，以及既有浮窗行为。实机查询成功不替代物理仪器校准。

完成实时 W 尚需：找到可验证的电池电流查询及单位、方向、测量位置、刷新率，再与同时间的电池电压相乘并验证断连/超时/充满处理。若没有主机可访问的电流遥测，后续只能通过电池侧测量硬件或内部总线分析获取新证据；USB 功率计仍不能替代电池侧读数。软件可以正确发送已知查询，不能仅靠增加驱动制造设备没有上报的测量值。
