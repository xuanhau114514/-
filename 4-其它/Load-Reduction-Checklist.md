# 降负载清单 · Surface Book 2 (i5-7300U / 8GB / Win11 25H2)

> 生成时间：2026-10-10
> 所有数字均为本机实测，非估算。

---

## 一、实测的后台占用（这就是要砍的对象）

| 项目 | 进程数 | 内存 | CPU 累计 | 备注 |
|---|---|---|---|---|
| Microsoft Edge | 3 | ~750 MB | 高 | 启动项里还有 `MicrosoftEdgeAutoLaunch_...` |
| DeepSeek Harness | 4 | ~987 MB | 1720 秒 | 就是那个 AI 界面 |
| **Windows 外壳 WebView2** | 6~7 | **~370–500 MB** | 每开一次开始菜单 110–140 ms | 包名 `MicrosoftWindows.Client.CBS`，Chromium 内核 |
| TextInputHost（中日双 IME） | 1 | 214 MB | 167 秒 | 中+日两套输入法 |
| 火绒安全软件 | 3 | ~290 MB | — | **正在卸载** |
| msedge (Edge 本体) | 3 | ~750 MB | — | 与第一行同一项 |

**合计约 2.6 GB / 7.92 GB 被上面这些吃掉，而物理内存只有 7.92 GB。**

关键结论：这台机器**唯一抱怨的症状是"开始菜单呼出动画帧率低"**，而实测显示
GPU 占用 0.1%、DWM CPU 0ms、CPU 满速 2611 MHz —— 瓶颈是**纯 CPU 工作量**：
每次开+关开始菜单需要 450~660 ms 的 CPU 时间，而动画窗口只有约 300 ms。
2 核 4 线程的机器上，后台每多占一点，UI 线程就晚一点拿到 CPU。

---

## 二、A 档：立刻做，零风险，不需要重启

### A1. 掐掉 Edge 的后台常驻与自启动 ★收益最大
1. 打开 Edge，地址栏输入 `edge://settings/system`
2. 关闭 **「启动增强」**（Startup boost）
3. 关闭 **「关闭 Microsoft Edge 后继续运行后台扩展和应用」**
4. 打开 **任务管理器** → **启动应用** → 找到 `MicrosoftEdgeAutoLaunch_39FC6EE296BC2BDA5D3FCAF201C6DC4D` → 右键 **禁用**

> 效果：Edge 不再随开机启动，关闭窗口后进程真正退出（当前 3 个进程约 750 MB）。

### A2. 删掉不用的日文输入法
1. 设置 → 时间和语言 → 语言和区域
2. 找到 **日本语** → 右侧 `⋯` → **删除**
3. （如仍需中日切换，可只保留日文但移除微软拼音之一，不要两套都留）

> 效果：降低 `TextInputHost` 的负载（当前累计 167 CPU 秒，对于输入法宿主来说偏高）。

### A3. 看视频 / 玩 galgame 时不要开 Edge 和 DSH
> 实测：这两个关掉可释放约 **1.7 GB 内存** 和一大块 CPU。
> 这是**唯一在你现有硬件上能立刻感受到差别**的操作。

---

## 三、B 档：一次性配置，改完长期有效

### B1. 关闭传递优化
设置 → Windows 更新 → 高级选项 → 传递优化 → 关闭
**「允许从其他电脑下载」**

### B2. 检查后台应用
设置 → 应用 → 已安装的应用 → 高级应用设置 → 后台应用
→ 把不需要的逐个关掉

### B3. 启动应用复核
任务管理器 → 启动应用。目前只有 5 项，已经比较干净：
```
MicrosoftEdgeAutoLaunch_...   ← A1 里禁用它
SecurityHealth                ← 保留（安全中心）
SurfaceDTX.exe                ← 保留（Surface 键盘底座驱动）
RtkAudUService                ← 保留（Realtek 音频）
sysdiag                       ← 火绒，卸载后应消失
```

---

## 四、C 档：可选（此前你选择"先不动"）

### C1. 前台优先级加权
把前台进程的时间片加权从 2:1 提到 3:1。这个键存在的唯一目的
就是让前台 UI 线程优先拿到 CPU，正好打在这个痛点上，且**不损失任何动画**。

```powershell
# 应用（需要管理员权限）
reg add "HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl" /v Win32PrioritySeparation /t REG_DWORD /d 0x26 /f

# 撤销（恢复默认）
reg add "HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl" /v Win32PrioritySeparation /t REG_DWORD /d 0x02 /f
```
需重启生效。

### C2. 菜单延迟归零
```powershell
reg add "HKCU\Control Panel\Desktop" /v MenuShowDelay /t REG_SZ /d 0 /f
# 撤销
reg add "HKCU\Control Panel\Desktop" /v MenuShowDelay /t REG_SZ /d 400 /f
```

### C3. 终极方案：关闭动画效果
**唯一能彻底消除掉帧的办法** —— 没有动画就没有帧预算可掉。
设置 → 辅助功能 → 视觉效果 → **动画效果 = 关**
代价：放弃所有过渡动画（开始菜单、任务视图、窗口最大化都会瞬间切换）。

---

## 五、D 档：磁盘（实测结果和你预期不同）

C 盘现状：**剩余 34.73 GB / 237.33 GB（14.6%）**

实际扫描结果 —— **常规清理项几乎全是空的**，这台机器已经被清理过了：

| 目标 | 大小 |
|---|---|
| Windows 更新缓存 `SoftwareDistribution\Download` | 0 MB |
| 系统临时目录 `C:\Windows\Temp` | 0 MB |
| 用户临时目录 | 11 MB |
| 预读文件 `Prefetch` | 0 MB |
| IE/Edge 缓存 | 0 MB |
| UWP 应用数据 | 117 MB |
| **MSI 安装缓存 `C:\Windows\Installer`** | **1,028 MB** |
| 休眠文件 | 不存在（休眠已关） |

⚠️ `C:\Windows\Installer` **不要手动删除** —— 那是 MSI 的卸载/修复缓存，
删了会导致一批程序无法卸载或修复。要清理只能用它自带的机制：

```powershell
# 管理员运行：清理组件存储（回收被取代的更新）
DISM /Online /Cleanup-Image /StartComponentCleanup
```

**结论：那 34.7 GB 的占用不在常规位置，需要一次完整目录扫描才能找出来。**
如果要做，说一声，我扫一遍。

---

## 六、重启回来后的待办

火绒卸载后需要**重启**才能卸载 Boot/System 级驱动（`hrdevmon.sys` / `sysdiag.sys`）。
Windows Defender 会**自动重新接管**（它之前显示 `AMServiceEnabled=False` 只是因为被火绒顶替）。

重启后：

### 待办 1：写入搜索设置
火绒的 `sysdiag.sys` 之前拦截了对 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search`
的写入。卸载重启后应该就能写了。可以直接双击导入（同目录下）：

```
disable-startmenu-websearch.reg
```

内容：
- `BingSearchEnabled = 0`（移除搜索框里的网页结果）
- `CortanaConsent = 0`
- `IsDynamicSearchBoxEnabled = 0`
- `IsMSACloudSearchEnabled = 0`
- `IsAADCloudSearchEnabled = 0`
- `DisableSearchBoxSuggestions = 1`（此项在 Policies 路径，可能需要管理员）

撤销用 `undo-startmenu-websearch.reg`。

### 待办 2：重测开始菜单开销（验证因果关系）
```powershell
powershell -ExecutionPolicy Bypass -File ".\StartMenu-Lag-Test.ps1"
```
**基线对照（火绒还在时测得）：**

| 组件 | 打开阶段 | 关闭阶段 |
|---|---|---|
| StartMenuExperienceHost | 94 / 203 ms | 344 / 312 ms |
| SearchHost | 110 / 141 ms | 62 / 78 ms |
| msedgewebview2 | 16 / 46 ms | 16 / 79 ms |
| explorer | 47 / 63 ms | 62 / 62 ms |
| **脚本平均合计** | **662 ms** | |

### 待办 3：根据重测结果决定
- **明显下降** → 火绒的 HIPS 逐操作拦截被坐实，配合 A 档即可
- **没有变化** → 说明 660 ms 是 Win11 外壳 + Chromium 的硬成本，
  那么只有 C3（关闭动画效果）能根治

---

## 附：本机已确认的事实（避免重复排查）

| 检查项 | 结果 |
|---|---|
| GPU 占用（开菜单时） | 0.1% —— 显卡不是瓶颈 |
| DWM CPU 增量 | 0 ms —— 合成器不是瓶颈 |
| CPU 频率 | 2611 MHz，性能占比 104.8% —— 无热降频 |
| 电源 | 交流供电，最小/最大处理器状态均 100% |
| 显示器可用 3:2 模式 | **只有 3000×2000 一个**（已用 CDS_TEST 逐个验证） |
| 面板 EDID | 29×19 cm (1.526)，单个详细时序 3000×2000@59.99Hz，416 MHz |
| 开始菜单是否冷启动 | 否，进程自开机常驻且未被挂起 |
| 开始菜单缓存是否被重写 | 否，只有 8KB 的 `start2.bin` 被写 |
| 推荐/最近项 | 早已关闭 |
| SysMain / WSearch / DiagTrack | 已禁用 |
| 透明效果 / Widgets | 已关闭 |
| Intel 控制面板 | 未安装（且 Intel 已移除自定义分辨率功能） |
