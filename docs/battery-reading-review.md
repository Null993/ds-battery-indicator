# DualSense 电量读取检查

## 结论

原实现 `level * 10` 必然只输出整十百分比。这来自 HID 字段的分档精度，并非 UI 把精确值取整。标准输入报告没有提供可直接读取的 1% 精度电量。

普通 DualSense USB 输入报告 ID 为 `0x01`，报告含 ID 长度为 64 字节。完整报告索引 53 的状态字节，对应 HidLibrary `HidReport.Data[52]`（HidLibrary 已剥离 ID）。原实现该索引正确。

低四位是电量档位，0 表示约 0%–9%，1 表示约 10%–19%，依此类推，10 表示 100%。项目保留 `level * 10` 下界显示，保证既有低电量阈值语义不改变。不能靠替换 HID 库获得真实的 23%。

高四位状态：0 为放电；1 为充电；2 为已充满，应显示 100%，不再播放充电动画；0xA、0xB、0xF 等为错误/未知状态。本次对异常状态和保留档位返回无有效电量，不触发虚假的低电量提醒。异常报告会保留上一条有效读数，直到恢复有效报告或断开连接。

## 已修复

- 区分充电和已充满；不再将状态 2 当作仍在充电。
- 校验报告 ID、完整长度、HidLibrary 读取成功状态及报告是否存在。
- 限定 USB 游戏手柄接口，避免误解析蓝牙精简报告、不同头部的蓝牙完整报告和其他 HID 接口。
- 用单个后台轮询任务负责连接与重连，避免启动时并行连接；窗口/托盘订阅完毕后才启动监听。
- 断开后清空旧电量文本和进度，停止低电量重复提醒。

## 其他驱动和库

以下公开实现均使用相同档位，常将区间中值映射为 `min(level * 10 + 5, 100)`。例如档位 2 显示 25%，不是实际测得 25%。蓝牙完整报告也没有提升这个字段的精度。

- [Linux hid-playstation.c](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c)：`dualsense_parse_report`，明确注明每档 10%，充满为 100%。
- [SDL HIDAPI PS5](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c)：同样的中值映射，区分 charging/charged。
- [pydualsense](https://github.com/flok/pydualsense/blob/master/pydualsense/pydualsense.py)：读取状态字节低四位并乘 10 加 5。
- [dualsensectl](https://github.com/nowrep/dualsensectl/blob/master/main.c)：同样的档位读取。
- [HidLibrary HidReport](https://github.com/mikeobrien/HidLibrary/blob/master/src/HidLibrary/HidReport.cs)：`ReportId` 和 `Data` 分离。

按使用时间插值可以产生 23% 这样的估计数字，但需标注“估算”，且会受负载、电池老化、充电等因素影响。目前检查的公开实现没有提供真实 1% 精度的接口；这不等于证明所有未公开协议都不存在。

## 验证

自动回归检查：`dotnet run --project tests/DsBatteryIndicator.RegressionTests -c Release`。使用合成 USB HID 报告验证偏移、档位、充电状态和无效报告拒绝，并在 STA 线程实际创建 WPF 窗口验证自动显隐策略；不连接真实手柄、不写入用户设置。

实机步骤：

1. 不接手柄启动，开启自动显隐并保存：浮窗隐藏，托盘仍存在。
2. 插入 USB 手柄：一个轮询周期左右显示窗口且不抢焦点；拔出后隐藏。
3. 接着手柄重启应用：自动显示；未接手柄重启：保持隐藏。
4. 手动用托盘切换：当次生效；下一次插拔恢复自动规则。
5. 关闭自动显隐并保存：恢复手动显示偏好，插拔不再自动改变窗口显隐。
6. 充电中为绿色旋转环；已充满为 100% 且不旋转；拔出后不会显示旧电量。

硬件插拔与实际报告仍需要实机验证，合成报告和 WPF 回归检查不能替代它。
