# StartMenuSlowDrag 工具包

用 Win 键替代原生开始菜单动画：改为**手指甩动风格**的顺滑开场。
目标机器：Surface Book 2（i5-7300U / 8GB / Win11 25H2 build 26200）

---

## 一、它是做什么的

原生按 Win 键，开始菜单用一段约 250–300ms 的动画弹出。这台机器的 GPU 全程空闲、
CPU 却是满速，也就是说卡顿来自 **CPU 工作量**：`StartMenuExperienceHost` 每次需要约
170ms 的**前置** CPU 成本（布局 + 资源），压在整段动画时长上，帧就掉。

而**用手指从屏幕最底边往上划**时，动画由触点位置驱动，时长由手指决定，
每帧位移极小，所以看起来顺滑。

本工具就是把那条手势路径**程序化复现**：注入一次触摸，
从屏幕最底边快速上划约 200px，让外壳自己的惯性动画接管。

---

## 二、热键与行为

| 操作 | 行为 |
|---|---|
| **Win 单击**（菜单关着） | 甩动划开开始菜单 |
| **Win 单击**（菜单开着） | 关闭开始菜单 |
| **Win + 字母** | 组合键完整保留（工具内部重放） |
| **CapsLock** | 完全原生，工具已彻底放行 |
| `Ctrl + Alt + F` | 时长 −25ms（写入 config.txt） |
| `Ctrl + Alt + D` | 时长 +25ms（写入 config.txt） |
| `Ctrl + Alt + Shift + S` | 退出工具（键盘钩子立即卸载） |
| 托盘图标双击 | 直接打开开始菜单 |
| 托盘图标右键 | 菜单：打开 / 加速 / 减速 / 打开目录 / 退出 |

---

## 三、配置（`1-程序\config.txt`）

```
duration=120      移动时长 ms       实测典型 110~170
distance=200      上划距离 px       实测典型 170~250
ease=2.0          >1 = 加速曲线（ease-in）
hold=30           按下后的静止 ms   实测典型 20~40
winkey=on         设为 off 则完全不碰 Win 和 CapsLock
```

改完**重启工具**生效。热键 `Ctrl+Alt+F/D` 会自动写回这个文件，无需重启。

> **重要：故意没有「到达顶部后停留再松手」这个选项。**
> 在顶部停住会让最后几帧速度归零，外壳的惯性动画拿到 **0 初速度**，
> 菜单只能硬邦邦到位。**趁还在高速运动时立刻松手，才是这套效果成立的关键。**

---

## 四、核心发现（调试过程中验证出来的机制）

**Windows 外壳的开场动画由「松手瞬间的速度」驱动。**

| 做法 | 松手速度 | 观感 |
|---|---|---|
| 匀速拖拽 | 约 2667 px/s | 硬，像"啪"一下到位 ❌ |
| 顶部停留 40ms 再松 | **0** | 更硬 ❌ |
| 加速曲线 + 到顶立刻松手 | **5000+ px/s** | 惯性接管，丝滑 ✅ |

这个结论是**从你自己手指的实测轨迹里推出来的**，不是推测：

- 第 1 轮采集（20ms 采样）指向 *ease-in、180ms、270px*
- 第 2 轮采集（全分辨率 CSV）修正为 ***120ms、200px**、按下静止 30ms*
- 并证明「顶部停留」是反效果

---

## 五、文件清单

### 1-程序
| 文件 | 说明 |
|---|---|
| `StartMenuSlowDrag.exe` | ⭐ **这就是实现功能的程序**，唯一需要运行的（16.5 KB） |
| `config.txt` | 配置文件 |

### 2-源码
| 文件 | 说明 |
|---|---|
| `StartMenuSlowDrag.cs` | 主程序源码，用 csc 编译成上面的 exe |
| `SwipeRecorder.cs` / `.exe` | **触摸轨迹采集器**。用 Raw Input 直接读 Intel Precise Touch 的 HID 原始报告，绕过"外壳吞掉手势"的限制 |
| `SwipeFlick.ps1` | 甩动模型试验台，支持 ease-in/out + 按下静止 + 顶部停留三个相位 |

### 3-实测数据
| 文件 | 说明 |
|---|---|
| `swipe-report.txt` | 第 1 轮采集（含设备能力描述） |
| `swipe2.txt` | 第 2 轮采集报告 |
| `swipe2.txt.csv` | 第 2 轮 **151 个全分辨率采样点** |

### 4-其它
| 文件 | 说明 |
|---|---|
| `Load-Reduction-Checklist.md` | 这台机器的降负载清单与实测数据汇总 |
| `disable-startmenu-websearch.reg` | 关闭开始菜单联网搜索（需提权导入） |
| `undo-startmenu-websearch.reg` | 上者的撤销 |
| `search-settings-backup\` | 搜索设置改动前的注册表备份 |

---

## 六、重新安装（换机或重装系统时）

1. 把 `1-程序\StartMenuSlowDrag.exe` 和 `config.txt` 复制到
   `%LOCALAPPDATA%\StartMenuSlowDrag\`
2. 为 exe 建一个快捷方式，放进
   `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\`
3. 双击运行（托盘出现图标即成功）

**重新编译源码：**

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe `
  /target:winexe /optimize+ `
  /out:StartMenuSlowDrag.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  StartMenuSlowDrag.cs
```

> ⚠️ 源码必须保持 **纯 ASCII**。PowerShell 5.1 读取无 BOM 的 `.ps1`/`.cs`
> 会按 ANSI 解码，中文注释会变乱码并冲垮语法。

---

## 七、完全卸载

```
1. Ctrl+Alt+Shift+S 退出（或任务管理器结束 StartMenuSlowDrag.exe）
2. 删除文件夹 %LOCALAPPDATA%\StartMenuSlowDrag
3. 删除启动文件夹里的 StartMenuSlowDrag.lnk
```

工具**不写注册表、不装驱动、不改系统文件** —— 全部改动只在内存里。
删掉文件就彻底没了。

---

## 八、工作原理（技术备注）

1. **键盘钩子**（`WH_KEYBOARD_LL`）
   - `RegisterHotKey` **无法**注册单独的 Win 键（系统保留），必须用低级键盘钩子
   - 实测确认：开始菜单是在 Win 键**抬起**时打开的
   - 但**只吞抬起事件会导致系统认为 Win 键一直被按住**（实测 `GetAsyncKeyState` 返回
     `0x8001`，之后打字按 E 会变成 Win+E）——所以按下和抬起**都**吞掉，按键状态始终干净
   - 组合键通过 `keybd_event` 重放完整的「Win按下+键按下+键抬起+Win抬起」序列
   - 所有合成事件带 `dwExtraInfo = 0x44534800` 魔数，钩子据此放行，避免自噬

2. **触摸注入**（`InitializeTouchInjection` + `InjectTouchInput`）
   - 配置照搬 FlaUI 的 Touch 实现（唯一验证可用的组合）：
     `InitializeTouchInjection(256, TOUCH_FEEDBACK_DEFAULT)`、
     `touchFlags=0`、`touchMask=0`、`rcContact` 设为触点处的零宽高矩形
   - 状态机：`DOWN|INRANGE|INCONTACT` → `UPDATE|INRANGE|INCONTACT` × N → `UP`
   - **起点必须在屏幕最底边**（y = 屏高−2）。从开始按钮中心起手**不会触发**手势

3. **开关判定**：检查前台窗口 / 可见顶层窗口是否属于
   `StartMenuExperienceHost`、`SearchHost`、`SearchApp`。
   关闭直接发 `Esc`，不走手势。
