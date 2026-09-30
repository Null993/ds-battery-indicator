# DS Battery Indicator

[English](README.en.md)

Windows 10/11 桌面应用，通过浮动窗口显示 DualSense 手柄电量。USB 连接时读取 HID 报告获取实时电量，支持低电量三通道提醒。

## 功能

- **浮动电量窗口**：暗色圆角卡片，环形进度条 + 百分比数字，可拖拽、置顶、调整透明度
- **低电量提醒**：托盘气泡 + 提示音 + 手柄震动/灯带变色（三通道并行，不打断游戏）
- **充电动画**：蓝色静电环切换为绿色旋转虚线，充电时不触发低电量提醒
- **系统托盘**：鼠标悬停显示电量状态，双击切换窗口显隐
- **RTSS 叠加**：通过 RivaTuner Statistics Server 在游戏内显示电量
- **中英双语**：自动检测系统语言，右键菜单一键切换
- **开机自启**：支持注册表写入
- **窗口位置/显隐状态持久化**：重启恢复
- **随连接自动显隐**：在“其它设置”勾选“随手柄连接自动显示/隐藏浮窗”并保存，USB 手柄连接时显示，断开时隐藏；默认关闭

## 电量精度与自动显隐

DualSense 的标准 HID 电量字段只提供约 10% 一档的数据：档位 2 表示约 20%–29%，本应用显示档位下界 20%，不能读取真实的 23%。Linux、SDL 和 pydualsense 常显示区间中值 25%，这仍是估计值，不代表 1% 精度。已充满状态单独显示为 100%，不播放充电动画。

当前支持普通 DualSense 的 USB 完整输入报告（ID `0x01`，含 ID 共 64 字节）。HidLibrary 的 `Data` 不含报告 ID，因此电量字段位于 `Data[52]`。读取失败、错误报告类型、保留电量档位和异常充电状态不会被误当成有效电量。

开启自动显隐后，启动时也会按连接状态决定是否显示，不覆盖手动显隐偏好。托盘仍可手动显示/隐藏，下一次连接状态变化会重新应用自动规则。关闭自动显隐并保存后恢复手动显隐偏好；响应延迟取决于设置的轮询时间（默认约 1 秒）。自动显示不会抢占前台焦点。

电量协议分析、参考代码及实机验证步骤见 [电量读取检查](docs/battery-reading-review.md)。

## 运行要求

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- DualSense 手柄（USB 连接，VID 0x054C / PID 0x0CE6）
- RTSS 叠加功能需要安装 RivaTuner Statistics Server

## 构建

```bash
cd DsBatteryIndicator
dotnet build
dotnet run
```

## 发布

双击 `publish.bat` 或手动执行：

```bash
cd DsBatteryIndicator
dotnet publish -c Release -o ../publish
```

## 项目结构

```
DsBatteryIndicator/
├── App.xaml/.cs              入口 + 系统托盘
├── Models/
│   └── DualSenseDevice.cs    设备状态模型
├── Services/
│   ├── HidService.cs         HID 通信（设备枚举/读取/输出报告）
│   ├── BatteryParser.cs      报告解析（电量/充电状态）
│   ├── NotificationService.cs 低电量通知
│   └── AppSettings.cs        JSON 设置持久化
├── Resources/
│   └── Strings.cs            中英双语字符串
├── Plugins/
│   ├── IPlugin.cs            插件接口
│   └── RtssPlugin/
│       └── RtssService.cs    RTSS 共享内存写入
├── ViewModels/
│   └── MainViewModel.cs      MVVM ViewModel
└── Views/
    ├── MainWindow.xaml/.cs   浮动窗口
    ├── BatteryRing.xaml/.cs  环形进度条控件
    └── HapticSettingsWindow  设置窗口
```

## 技术参考

- DualSense USB HID 报告格式：Linux 内核 `hid-playstation.c`
- RTSS 共享内存：RTSS SDK `RTSSSharedMemory.h`
- 输出报告格式：`daidr/dualsense-tester`
