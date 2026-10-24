# 跨电脑使用

## 准备

- 目标电脑运行 Windows x64。
- 安装并登录雷神加速器；应用不会安装、登录或修改雷神客户端。若雷神以管理员权限运行，本应用也须以管理员身份启动，才能核对进程身份与当前会话。
- 解压最新的 LeiGodAutoStop ZIP，将解压后的整个文件夹放在目标电脑的稳定位置。EXE 是 self-contained 发布，不要求目标电脑单独安装 .NET。

## 校验与启动

运行 LeiGodAutoStop.exe，点“一键检查并开始”。软件会自动校验本包 EXE 与 SHA-256 清单、读取本机雷神环境并设置随雷神启动。Windows 出现权限提示时点“是”。换电脑或移动应用目录后，在目标电脑重新点一键设置。包内 `tools\verify-package.ps1` 仍可供需要手动核验的人使用，不是正常使用的必需步骤。

一键操作会逐项检查雷神客户端、日志权限与格式、当前登录账号和账户接口，并在检查通过后开始监控。雷神版本只显示供排查，不作为版本号门槛。检查失败时窗口显示原因；修复后再次点“一键检查并开始”。需要单独查看报告时，可运行 `LeiGodAutoStop.exe --diagnostics`。

程序支持括号前缀日志、JSON 行日志，并会尝试 `resources\\llogs`、`resources\\logs`、`logs`、`log` 四种目录。检查报告显示“日志格式不支持”时，不能安全启用自动暂停，应保留报告并等待适配该雷神版本；程序不会猜测游戏或发送暂停请求。

## 暂停能力如何在不同电脑上建立

自动暂停需要两组东西：当前登录会话的账号参数（`account_token` 等）和暂停请求正文。新版不要求“本机日志里必须出现过暂停请求”，而是在当前雷神客户端日志里按优先级取材：

1. 出现 `/client/pause` 或 `/client/pause/status` 请求行时，复用该行的主机、查询参数和正文；括号前缀、JSON 行、客户端 axios 错误对象三种写法都认。
2. 本机日志从未出现暂停请求时，改从同一客户端的最新 `*.leigod.com` 请求行取查询参数，再按客户端协议构造 `/client/pause` 调用。程序按日志时间优先采用较新的账号会话数据，以降低登录切换后复用旧 token 的风险。
3. 只有 JSON `params` 对象的日志行同样可以取参数。

第 2 条让客户端从未记录暂停端点时仍可尝试构造请求。本机历史样本里，普通请求与暂停请求的账号参数逐字段一致；这不是所有未来客户端版本的保证。请求正文回退值来自当前开发电脑的日志样本，尚不能单凭这份样本证明所有客户端版本都使用相同正文；首次真实调用还必须由目标电脑当前账号的暂停状态与稳定余额读回来验证。检查中的“暂停请求参数通过”只表示可构造，不等于当前电脑已实测成功。已暂停账号没有新暂停动作，是一种可能情况，不能仅凭截图断定日志缺失原因。

请求正文优先使用目标电脑当前客户端日志实测到的内容；没有观测到时使用开发机样本回退值 `tQFyh13f7AYsHg==`。样本里 177 条请求正文相同，但这不是跨所有客户端版本的保证。若客户端响应超时或网络中断，程序不会立即切换主机重复发送，而是先读取目标电脑当前账号状态。最终成功必须由账户暂停状态和连续 3 次、间隔 15 秒（总计至少 45 秒）的真实余额复核确认；任意明显下降都会显示“暂停未确认”。

暂停接口主机白名单为 `api1/api2/api3.leigod.com`：本机历史日志确认雷神会在这三个主机之间切换，其中 `api3` 只出现在部分会话里，旧版本曾因白名单只有 api1/api2 而误判。发送时按“日志里出现的主机 → api2 → api1 → api3”顺序尝试，某一主机返回成功即停止；全部失败会记录每个主机的失败原因。

日志行来源：接受本次客户端进程，以及同一安装目录内正在运行的其他雷神进程写入的行，仍要求该行时间不早于本次客户端启动时间。

## 检查报告里“暂停能力”不是通过时

- 提醒“还没有读到本客户端日志行”：雷神刚启动时的正常状态，保持雷神运行即可，应用每 30 秒自动复查。
- 提醒“已读 N 条本客户端日志，但还没有带 account_token 的请求行”：确认在目标电脑登录了正确账号，重启雷神后重跑检查；也可在雷神中操作一次暂停/恢复促使客户端记录请求。不要把切换账号前的旧日志当作当前会话证据。
- 失败：只在日志目录读不到或账号接口不可用时出现，这种状态不会发送暂停请求。

报告同目录的 `%LOCALAPPDATA%\LeiGodAutoStop\pause-log-shape.txt` 会写出脱敏结构指纹（只有主机名、字段名、分隔符和正文长度，不含令牌和正文）。把它发回即可定位差异，不需要提供账号令牌。

## 游戏映射

游戏映射存储在 %LOCALAPPDATA%\LeiGodAutoStop\aliases.json。应用优先使用雷神当前加速会话日志中的 game_process，并合并已核实的内置进程名。目前内置覆盖 ID 231（黎明杀机）、1727（Apex，r5apex.exe / r5apex_dx12.exe）、917（Left 4 Dead 2，left4dead2.exe）和 Terraria 的 502/921/2246（Terraria.exe）；不代表支持所有游戏或未来雷神日志格式。已确认的其他游戏映射只作为该 ID 的受限回退候选，不会枚举全部系统进程。日志没有可用进程名、ID 又没有内置或本机确认映射时，应用保持等待；若有可核实候选，点击“确认当前游戏进程”并核对游戏 ID、进程名与本机完整路径。被隔离的旧映射不会从 aliases.json 删除；可通过“管理本机游戏映射”查看或删除。

如果要复制已确认的映射，可使用包内工具：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\game-mappings.ps1 -Action Export -Path .\game-mappings.json
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\game-mappings.ps1 -Action Import -Path .\game-mappings.json
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\game-mappings.ps1 -Action Check -Path local
```

映射含游戏 ID、可执行文件名和路径。目标电脑上的路径不存在时，运行该游戏并用“确认当前游戏进程”重新确认本机实际路径。不要复制整个 %LOCALAPPDATA%\LeiGodAutoStop 目录；其中包含机器本地偏好和日志。

## 不同游戏同时运行

应用只等待当前雷神加速会话实际映射的进程。目标游戏仍在运行时不暂停；目标游戏全部退出后开始 120 秒连续倒计时。未被雷神加速的其他游戏不属于该会话，不会阻止倒计时。若日志无法确定某个活动会话的进程，应用会停表，避免误暂停。

## 两款游戏和跨电脑验收

必须在本机和目标 Windows 电脑各做一次现场验收；本机结果不能替代目标电脑。两台电脑分别使用各自已安装并登录的雷神、各自的账号和本机游戏路径。只转移 ZIP，不复制 `%LOCALAPPDATA%\LeiGodAutoStop`；目标电脑首次运行时应重新检查环境。程序优先锁定雷神当前会话日志中的 `game_process`，因此通常不需要手动映射；只有目标客户端没有提供进程名、进程名重复或无法唯一确认时，才提示在该电脑确认一次本机进程映射。验收脚本会在 JSON 的 `PackageIdentity` 自动记录 EXE SHA-256 和整个解压包内容指纹 `PackageTreeSha256`；两台电脑的两项必须一致，才可比较运行结果。这样不要求手工定位 ZIP 文件或复制本机偏好目录。

本机雷神游戏目录和 Steam 清单列有 Apex（LeiGod 游戏 ID 1727，进程 `r5apex_dx12.exe`）、Terraria（LeiGod 目录 ID 502/921/2246，Steam app 105600，进程 `Terraria.exe`）及 Left 4 Dead 2（ID 917，进程 `left4dead2.exe`）。当前优先用 Apex + Terraria；清单和安装文件只说明它们是候选，必须现场确认雷神确实能启动加速，不能替代真实验收。目标电脑也有这两款时，两边使用同一对游戏；若没有，则选两边都已安装且能在雷神里实际加速的另外两款。每款游戏分别运行以下验收；进程名填该游戏实际运行的 `.exe` 文件名：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\cross-computer-acceptance.ps1 -GameProcessName r5apex_dx12.exe -UnacceleratedProcessNames ACCStd.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\cross-computer-acceptance.ps1 -GameProcessName Terraria.exe -UnacceleratedProcessNames ACCStd.exe
```

按提示完成首次检查、游戏运行、退出、倒计时中重开、再次退出并等待完整 120 秒、关闭雷神和重新打开雷神。`-UnacceleratedProcessNames` 应填写实际未加速运行的游戏或后台程序；指定进程必须持续运行到暂停确认。还需测试“同时加速多个游戏”：账户恢复计时后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\cross-computer-concurrent-acceptance.ps1 -AcceleratedGameProcessNames 'r5apex_dx12.exe','Terraria.exe' -UnacceleratedProcessNames UnacceleratedGame.exe,PCManager.exe
```

按提示先让两款游戏同时加速运行，再只退出第一款、保持第二款运行，最后退出第二款；未加速进程持续运行到暂停读回。脚本检查任一加速游戏仍运行时不暂停、全部退出后完整等待 120 秒再暂停。每台电脑至少两款游戏分别完成单游戏流程，并完成一次双加速并行流程。若雷神客户端不允许这两款同时加速，记为未验证，不能用单游戏结果替代。每轮倒计时前确认账户处于计时状态；如果已暂停，先在雷神客户端手动恢复。验收脚本只读进程和应用日志，不改变账户状态或结束进程，结果写入 JSON。缺少目标电脑真实记录时，整体仍为 NOT VERIFIED。

为避免在错误目录执行 PowerShell，目标电脑也可以直接双击包根目录的 `开始跨电脑验收.cmd`。它会从自身目录校验包、启动应用，并依次提示输入两款游戏进程名和一个持续运行的未加速后台进程，再调用三套验收脚本。它不会自动关闭游戏、雷神或后台程序；每个阶段仍必须由使用者按提示完成真实操作。

两台电脑各自完成两款单游戏验收和一次双游戏并行验收后，可在任一台电脑把四份 JSON 放到同一目录，用包内对比工具做最终判定。它会要求两台电脑名称不同、每台至少两款单游戏均为 PASS、双游戏并行为 PASS，并比较 EXE SHA-256 与包内容指纹：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\compare-transfer-acceptance.ps1 `
  -SourceSingleGameReports .\source-apex.json,.\source-terraria.json `
  -SourceConcurrentReport .\source-concurrent.json `
  -TargetSingleGameReports .\target-apex.json,.\target-terraria.json `
  -TargetConcurrentReport .\target-concurrent.json
```

对比工具只汇总已生成的验收 JSON，不会修改账号、进程或任务计划；它不能把缺失的目标电脑现场记录替换成通过。

## 网络与数据

应用从当前雷神客户端日志提取账号参数（优先暂停请求行，其次同一客户端的其他请求行），在内存中读取令牌，并使用 HTTPS 读取账户暂停状态。必要时向雷神同源 API 发送暂停请求；之后以账户状态和余额读回为准。令牌不写入应用日志或配置文件。网络不可用或账户响应不完整时，应用记录失败并等待重试，不显示为成功。


