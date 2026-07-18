# HALCON 26.05 部署、许可与现场排障

本项目的 HALCON backend 是“完整安装的外部 runtime + 精确版本的 managed NuGet + 有效许可”模式，不是把一个 `halcon.dll` 放到发布目录的自包含模式。

## 1. 不可放宽的版本矩阵

| 项目 | 锁定值 | 代码验证方式 |
| --- | --- | --- |
| 操作系统/进程 | Windows x64 | 非 Windows 或 32 位进程返回 `RUNTIME_ARCH_MISMATCH` |
| HALCON edition | HALCON 26.05 Progress | runtime 和许可算子真实探测 |
| Native file version | `26.05.0.0` | 读取 `bin\x64-win64\halcon.dll` 的 file version，必须精确相等 |
| Native architecture | AMD64 PE32+ / `x64-win64` | 启动前解析 PE header |
| NuGet | `MVTec.HalconDotNet` `26050.0.0` | `VisionStation.Vision.csproj` 精确 PackageReference |
| Managed assembly | `26050.0.0.0` | runtime probe 读取 assembly version |
| .NET | .NET 8 x64 framework-dependent runtime | Client 和 TestHost 均按 `win-x64` 发布 |
| 许可 | 能执行 scaled-shape 算子的有效 Progress 许可 | `license-smoke` 真实创建并持久化 shape model |

MVTec 的 [HALCON 26.05.0.0 Progress release notes](https://www.mvtec.com/products/halcon/documentation/release-notes-2605-0) 明确说明 26.05 Progress 不与 25.11 及更早版本二进制兼容，且需要有效 Progress 许可。不得用“大版本看起来接近”代替上表的精确校验。

## 2. 为什么禁止单独复制 `halcon.dll`

`halcon.dll` 不是完整的 runtime。它依赖安装目录中的其他 native 组件、HALCON 资源、许可基础设施，并在 Windows floating license 场景使用 `hlwd`。单拷 DLL 还会绕过安装器的 64 位注册表信息，使现场修复无法追溯。

正确做法是：

1. 使用 MVTec 安装器在目标机安装完整 HALCON 26.05 Progress x64 runtime。
2. 由现场许可管理流程安装/激活正确许可，不把许可文件放进 Git 或应用发布包。
3. 应用发布目录保留 NuGet 提供的 `MVTec.HalconDotNet.dll`，但不携带从某台开发机拷贝的 native `halcon.dll`。
4. 发布后用 TestHost 在目标机运行真实 probe、license、model roundtrip、timeout 和 benchmark 验收。

## 3. Runtime 候选发现顺序

`HalconRuntimeLocator` 按以下顺序检查，第一个通过完整校验的候选生效：

1. **进程条件**：必须是 Windows 64 位进程。
2. **进程环境变量**：只有 `HALCONROOT` 和 `HALCONARCH` 同时非空时才成为一个候选，且 `HALCONARCH` 必须精确为 `x64-win64`。环境变量候选优先级高于 devices 配置。
3. **`devices.json` 配置**：`SystemSettings.Halcon.RuntimeRoot` 的非空值必须是绝对安装根目录。空值表示不提供该候选，仍允许环境变量和 Registry64 自动发现。
4. **Registry64 卸载项**：读取 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` 的 64 位视图，仅选 `DisplayName` 精确匹配 `MVTec HALCON 26.05 Progress` 的 `InstallLocation`。多个匹配项按规范化路径稳定排序。

每个根目录都要通过同一组检查：绝对路径、`bin\x64-win64\halcon.dll` 存在、AMD64 PE32+、file version 精确为 `26.05.0.0`。某一个早期候选失败不会隐藏后面的有效候选；重复根目录只检查一次，失败记录会进入 technical details。

`devices.json` 使用 Web JSON 命名。保留自动发现的安全写法是：

```json
{
  "systemSettings": {
    "halcon": {
      "runtimeRoot": ""
    }
  }
}
```

需要显式根目录时，应用的系统设置页写入当前目标机的完整绝对安装根目录；不要手工填写 `bin\x64-win64` 子目录，也不要把开发机路径固化到默认配置。

runtime 第一次绑定时，bootstrapper 会把选定根目录写入当前进程的 `HALCONROOT`、写入 `HALCONARCH=x64-win64`，使用 DLL 所在目录 + System32 依赖搜索加载 native module，再为 `MVTec.HalconDotNet` 注册 resolver。同一进程绑定后不允许切换第二个 runtime root；更改安装或配置后必须完整重启应用。

## 4. 许可验收

runtime 文件存在不代表 shape matching 许可可用。应用会关闭 HALCON 的“许可错误终止进程”行为，然后执行真实 matching-license operator；许可错误稳定映射为 `LICENSE_UNAVAILABLE`，不应导致应用突然退出。

现场需要确认：

- 许可 edition 与 HALCON 26.05 Progress 匹配，不根据许可文件名或某个环境变量猜测许可类型。
- 在目标机真实运行 `license-smoke`；它不是只读版本，而是创建 scaled-shape model 并持久化一对模型文件。
- 使用 floating license 时，核对许可服务器的 DNS/网络/安全策略和供应商许可管理状态；不在应用文档中猜测端口或修改许可内容。

### Windows floating license 与 `hlwd`

`hlwd` 是 **MVTec License Watchdog**，不是 floating license server。根据 MVTec [HALCON Programmer's Guide](https://www.mvtec.com/fileadmin/Redaktion/mvtec.com/products/halcon/documentation/manuals/programmers_guide.pdf)，Windows 进程在终止阶段无法自行用 socket 及时归还 floating license；HALCON 取得 floating license 时会启动独立 `hlwd`，如主进程来不及归还，watchdog 代为归还后退出。

因此 floating 部署必须额外验证：

1. 完整 runtime 的 `bin\x64-win64` 中存在安装器提供的 `hlwd.exe`。
2. AppLocker、防病毒软件和账户权限允许 HALCON 在取许可时启动它。用 `license-smoke` 观察它随许可获取出现，并在 TestHost 退出/许可归还后结束。
3. 不要把 `hlwd.exe` 单独拷贝或设为长驻应用；它的生命周期由 HALCON 管理。
4. 当前 VisionStation 没有实现 `FinalizeHALCONLibrary` 或 `set_system('return_license')` 的备用路径，因此 floating 环境中 `hlwd` 被阻止时必须判定部署不合格，不能带病上线。

### 许可失败负测的真实边界

在已经具有 Progress/SOM/硬件许可的机器上，伪造无效 `HALCON_LICENSE_FILE` 不一定能夺走真实许可，因而不能证明 `LICENSE_UNAVAILABLE` 的端到端路径。本项目已用单元测试锁定许可 error range 到 `LICENSE_UNAVAILABLE` 的分类；真实负测必须在经授权的无许可验收机/VM，或按现场许可管理流程隔离 floating server 的机器上完成。不得为了负测删除、改名或损坏生产机许可。

## 5. x64 发布

先为 `win-x64` 还原，再做 framework-dependent 发布：

```powershell
dotnet restore .\VisionStation.Client\VisionStation.Client.csproj `
  -r win-x64 -p:Platform=x64

dotnet publish .\VisionStation.Client\VisionStation.Client.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:Platform=x64 --no-restore
```

发布检查：

- 目标是 `net8.0-windows/win-x64`，不存在 Any CPU/x86 替代包。
- 输出含 `MVTec.HalconDotNet.dll`，但不把 native `halcon.dll` 当作应用私有依赖。
- 目标机已安装 x64 .NET 8 Desktop Runtime 和本文锁定的完整 HALCON runtime。
- 配方 JSON 与 `Resources\Templates` 在同一停机点备份/恢复，不单独备份 `.shm` 或单独回滚 recipe reference。

TestHost 建议作为独立的现场验收包发布，不与 Client 进程共享已绑定的 runtime：

```powershell
dotnet restore .\VisionStation.Vision.Halcon.TestHost\VisionStation.Vision.Halcon.TestHost.csproj `
  -r win-x64 -p:Platform=x64

dotnet publish .\VisionStation.Vision.Halcon.TestHost\VisionStation.Vision.Halcon.TestHost.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:Platform=x64 --no-restore
```

## 6. 五个现有 TestHost 命令

TestHost 在 stdout 只输出一个 JSON object，字段固定为 `success`、`code`、`stage`、`runtimeVersion`、`technicalSummary`。已处理失败不往 stderr 打 stack trace。退出码为：`0` 成功、`1` 命令已执行但验收失败、`2` 参数无效、`3` TestHost 未处理异常。

下列命令假定现场 shell 的 `HALCONROOT` 已指向要验收的安装根，`$testHost` 指向已发布的 TestHost exe：

### 6.1 `probe`

```powershell
& $testHost probe `
  --root $env:HALCONROOT `
  --expected-version 26.05.0.0
```

它在全新 x64 进程中验证 root、AMD64 PE32+、native/managed/system 版本、DLL resolver 绑定和真实许可算子。成功时为 `success=true`、`code=OK`、`stage=probe`、`runtimeVersion=26.05.0.0`。可选 `--second-root` 仅用来证明同一进程拒绝二次绑定；它不是生产双 runtime 切换功能。

### 6.2 `license-smoke`

```powershell
& $testHost license-smoke --root $env:HALCONROOT
```

该命令学习一个合成产品，调用真实 scaled-shape 算子，并持久化模型。成功时为 `code=OK`、`stage=license-smoke`。这是每台目标机和每次许可调整后的必做项。

### 6.3 `model-roundtrip`

```powershell
$roundtripRoot = Join-Path `
  ([IO.Path]::GetTempPath()) `
  ('VisionStation-Halcon-Roundtrip-' + [Guid]::NewGuid().ToString('N'))

& $testHost model-roundtrip `
  --root $env:HALCONROOT `
  --working-directory $roundtripRoot
```

它执行“学习 → `.shm/.json` commit → Dispose service/cache → 创建全新 runtime → 校验并加载 → 匹配”。成功时 `code=OK`、`stage=model-roundtrip`。要验证损坏模型能否 fail closed，在独立临时目录加 `--corrupt-model true`；预期退出码为 `1`、`code=MODEL_CHECKSUM_MISMATCH`、`stage=model`。

TestHost 不删除调用方提供的 roundtrip 目录。保存报告后，只对上述代码生成的专用临时目录做带前缀校验的清理：

```powershell
$ownedPrefix = [IO.Path]::GetFullPath(
  (Join-Path ([IO.Path]::GetTempPath()) 'VisionStation-Halcon-Roundtrip-'))
$resolvedRoundtrip = [IO.Path]::GetFullPath($roundtripRoot)

if (-not $resolvedRoundtrip.StartsWith(
    $ownedPrefix,
    [StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to delete non-owned path '$resolvedRoundtrip'."
}

if (Test-Path -LiteralPath $resolvedRoundtrip) {
  Remove-Item -LiteralPath $resolvedRoundtrip -Recurse -Force
}
```

### 6.4 `timeout`

先证明长 budget 能正常完成：

```powershell
& $testHost timeout `
  --root $env:HALCONROOT `
  --milliseconds 5000
```

再证明 HALCON error 9400 能映射到稳定码：

```powershell
& $testHost timeout `
  --root $env:HALCONROOT `
  --milliseconds 100
```

第二条是有意执行的负测，预期退出码为 `1`、`success=false`、`code=MATCH_TIMEOUT`、`stage=match`，`technicalSummary` 含 `ErrorCode=9400`。现场脚本不能把这个预期非零退出码误报为 TestHost 崩溃。

要验证“进入 native 后取消仍等安全返回”：

```powershell
& $testHost timeout `
  --root $env:HALCONROOT `
  --milliseconds 5000 `
  --cancel-after-milliseconds 150
```

成功证据为 `success=true`、`code=OPERATION_CANCELLED`、`stage=cancel`，且 summary 中 `nativeEntered=true`、`nativeReturned=true`、`postCancelWaitMs` 至少为 `50`。该 code 是 TestHost 验收码，不是业务诊断码；业务层仍抛 `OperationCanceledException`，不产生 `MATCH_CANCELLED`。

### 6.5 `benchmark`

在目标工控机上用固定迭代数采集 cold-load、warm-single 和 1/3/5 targets 五组数据：

```powershell
$benchmarkOutput = Join-Path `
  (Get-Location) `
  'artifacts\halcon-benchmark.json'

& $testHost benchmark `
  --root $env:HALCONROOT `
  --iterations 50 `
  --output $benchmarkOutput
```

命令的 stdout 仍然只是本节开头约定的五字段 `HalconTestHostReport`；包含机器指纹、耗时分位数和资源计数的详细文档原子写入 `$benchmarkOutput`。成功时 stdout 为 `success=true`、`code=OK`、`stage=benchmark`。详细 JSON 的 `iterations` 必须为 `50`，并且 `coldLoad`、`warmSingle`、`targets1`、`targets3`、`targets5` 每组都必须达到 `validSamples=50`、`operatorFailures=0`，即每组 50/50 有效且零算子失败。可以用以下脚本把该条件变成明确的上线门：

```powershell
$benchmark = Get-Content $benchmarkOutput -Raw | ConvertFrom-Json
$groups = 'coldLoad', 'warmSingle', 'targets1', 'targets3', 'targets5'

if ($benchmark.iterations -ne 50) {
  throw "Unexpected benchmark iteration count: $($benchmark.iterations)"
}

foreach ($group in $groups) {
  $result = $benchmark.$group
  if ($result.validSamples -ne 50 -or $result.operatorFailures -ne 0) {
    throw "HALCON benchmark gate failed for '$group'."
  }
}
```

详细 JSON 已生成但某组不是 50/50 或存在算子失败时，TestHost 返回退出码 `1`、`code=BENCHMARK_INCOMPLETE`；若某组连一个有效样本都没有，则直接返回最后一个业务诊断码且不生成详细 JSON。输出路径无效或无法原子提交时返回 `code=BENCHMARK_OUTPUT_INVALID`。性能数值只与同一目标机、同一版本的历史基线比较，不拿另一台机器的绝对毫秒数直接判定。

## 7. 本地真实图验收数据集

真实产品图不进入仓库。把数据集根目录放在目标机受控位置，并在根目录创建 `halcon-dataset.json`。清单属性名区分大小写，不接受注释、尾随逗号、重复属性或未知属性；图像路径必须是数据集根下的安全相对路径。以下是覆盖全部标签的最小完整示例：

```json
{
  "schemaVersion": 1,
  "template": {
    "image": "template/product-front.png",
    "roi": {
      "x": 40,
      "y": 30,
      "width": 620,
      "height": 1180
    }
  },
  "cases": [
    {
      "id": "front-01",
      "image": "cases/front-01.png",
      "label": "positive/front"
    },
    {
      "id": "back-01",
      "image": "cases/back-01.png",
      "label": "back"
    },
    {
      "id": "similar-01",
      "image": "cases/similar-01.png",
      "label": "similar"
    },
    {
      "id": "partial-01",
      "image": "cases/partial-01.png",
      "label": "partial"
    },
    {
      "id": "boundary-01",
      "image": "cases/boundary-01.png",
      "label": "boundary"
    },
    {
      "id": "polarity-01",
      "image": "cases/polarity-01.png",
      "label": "polarity"
    }
  ]
}
```

允许的 canonical label **恰好只有六个精确值**：`positive/front`、`back`、`similar`、`partial`、`boundary`、`polarity`，没有大小写别名。清单至少包含一个 `positive/front` 和一个负样本；建议像上例一样为五类负场景各保留样本。`template.roi` 的 `x/y` 必须有限且不小于 `0`，`width/height` 必须有限且大于 `0`；每个 case 的 `id` 和图像路径必须唯一，case 不能复用学习模板图，所有引用图像都必须存在且不能通过路径或链接逃出数据集根。

在具有有效 HALCON 许可的验收机运行：

```powershell
$env:VISIONSTATION_HALCON_DATASET = `
  (Resolve-Path '.\halcon-acceptance-data').Path

dotnet test `
  .\VisionStation.Vision.Halcon.Tests\VisionStation.Vision.Halcon.Tests.csproj `
  -c Release `
  --filter 'Category=LocalDataset'
```

验收固定使用 `ExactCount`、`ExpectedCount=1`，不能用只投影最佳候选的 `Single` 冒充唯一性证明。每个 `positive/front` case 必须得到 `HasMatch=true`、`Outcome=Ok`、`Matches.Count=1`，也就是恰好接受一个匹配；其余五类每个 case 都必须 `HasMatch=false` 且 `Matches.Count=0`，即零接受。只有 `VISIONSTATION_HALCON_DATASET` 在进程环境中根本未定义时才 skipped；只要变量存在，包括空字符串或纯空白，目录不存在、清单错误、图像缺失或匹配结果不合格都会使测试失败，不能用错误值制造“跳过”。

## 8. 上线前验收顺序

1. 在发布机构建 Client 和 TestHost 的 Release/win-x64 包，记录 commit、NuGet lock/还原日志和包 hash。
2. 在目标机安装 .NET 8 x64 Desktop Runtime 与完整 HALCON 26.05 Progress x64 runtime，安装合法许可。
3. 确认 `HALCONROOT/HALCONARCH` 与 devices 配置不冲突；有多版本 HALCON 时不依赖偶然的 registry 顺序。
4. 按第 6 节运行 `probe`、`license-smoke`、`model-roundtrip`、timeout 5000/100、cancellation smoke 和 `benchmark --iterations 50`；保存每个 stdout JSON，并保存 benchmark 详细 JSON。
5. 按第 7 节设置 `VISIONSTATION_HALCON_DATASET` 并运行真实图验收；确认所有正样本恰好一个接受、所有负样本零接受。
6. floating license 另行确认 `hlwd` 可启动与归还许可；无许可负测只在授权的无许可验收机完成。
7. 恢复一份真实配方 + Templates 成对备份，运行一次生产图匹配，确认 pose/scale、exact-count、NG 清端口和操作员中文提示。
8. 按性能基线文档在目标工控机比较 benchmark；不用另一台机器的绝对毫秒值作上线门槛。

## 9. 按稳定错误码排障

首先保存 `code`、`stage`、`runtimeVersion`、`technicalSummary` 和当前 recipe/tool owner。不要只截图一条中文消息。

### 9.1 配置阶段

| 错误码 | 检查 | 处理 |
| --- | --- | --- |
| `CONFIG_UNKNOWN_ENGINE` | 配方 `engine` 文本 | 改为受支持的 `Halcon`/`OpenCv`/`ManagedNcc`；不加默认回退 |
| `CONFIG_SERVICE_REQUIRED` | Client 是否用了 full factory | 修复 composition/deployment；这不是重学习能解决的错误 |
| `CONFIG_UNSUPPORTED_MODE` | `matchMode`、`multiMatchMode`、cardinality | HALCON 改用 `Shape`；Managed NCC 不能用 ExactCount |
| `CONFIG_INVALID_PARAMETER` | technical details 中的键、owner、ROI 或结果契约 | 在配方/UI 修复该值，不盲目重装 runtime |

### 9.2 Runtime 和许可

| 错误码 | 操作员检查 | 安全处理 |
| --- | --- | --- |
| `RUNTIME_NOT_FOUND` | `HALCONROOT/HALCONARCH`、devices root、Registry64；根下是否真存在 `bin\x64-win64\halcon.dll`；summary 的 Win32 loader error | 修正候选根或用 MVTec 安装器修复完整 runtime；禁止从其他机器单拷 DLL |
| `RUNTIME_ARCH_MISMATCH` | Client/TestHost 是否 x64，`HALCONARCH` 是否精确 `x64-win64`，native PE 是否 AMD64 PE32+ | 重发 win-x64 包或重装 x64 runtime；不用 Any CPU/x86 迁就 |
| `RUNTIME_VERSION_MISMATCH` | JSON 中的实际 native/system/managed/model 版本 | 使 native `26.05.0.0`、NuGet `26050.0.0`、assembly `26050.0.0.0` 成套；升级后按新 schema 重学习，不覆盖 metadata 版本 |
| `LICENSE_UNAVAILABLE` | `license-smoke`、Progress edition、硬件/SOM 状态；floating server 连通和 `hlwd` | 交由授权的许可管理人员修复；不更改系统时钟、不篡改许可、不反复重试占用 floating seat |

### 9.3 Model 阶段

| 错误码 | 检查 | 处理 |
| --- | --- | --- |
| `MODEL_PATH_INVALID` | recipe reference 是否为 store 生成的相对路径，Templates 根权限/reparse point | 恢复成对备份或重学习；不把绝对路径手工写进 recipe |
| `MODEL_NOT_FOUND` | owner 下的 `.shm/.json` generation 是否成对存在 | 恢复与当前 recipe 同一时点的 Templates 备份，或重新学习并保存 |
| `MODEL_CHECKSUM_MISMATCH` | 是否被手工修改、部分复制、防病毒软件改写或磁盘损坏 | 立即停用该 generation；恢复同一 generation 的原始 pair 或重学习；不重算 checksum 绕过告警 |
| `MODEL_METADATA_INVALID` | owner/generation/几何/metadata checksum 是否同一组 | 把 recipe + Templates 作为整体恢复；不编辑 JSON |
| `MODEL_VERSION_MISMATCH` | schema/model format 是否由另一应用版本生成 | 回滚成套应用/runtime/资源，或在当前版本重学习 |
| `MODEL_RELEARN_REQUIRED` | generation 参数是否修改，配方是否丢失 `halcon.*` model state | 在完整产品 ROI 上重新学习，将 `TemplateLearningResult.Parameters` 完整保存 |
| `MODEL_LOAD_FAILED` | Templates 根读写权限、磁盘空间、native model 可读性，再运行 `model-roundtrip` | 保留 technical summary 和磁盘事件；修复存储/runtime 后重学习，不只点“重试” |
| `MODEL_TEMPLATE_INCOMPLETE` | 产品/学习 ROI 是否贴图像边界 | 重新采集完整产品或修正 ROI，再学习 |
| `MODEL_CONTRAST_WEAK` | 打光、曝光、暗产品/亮背景极性 | 先修复成像，不用降低匹配硬门代替 |
| `MODEL_INTERNAL_FEATURES_WEAK` | 整个产品 ROI 是否包含稳定孔/缺口，是否模糊/过曝 | 修正 ROI/焦距/打光，确保至少 3 组稳定内特征后重学习 |

### 9.4 Match 阶段

| 错误码 | 现场含义 | 处理 |
| --- | --- | --- |
| `MATCH_INVALID_POSE` | 候选几何、Scale 或搜索域无效 | 检查 ROI 映射与缩放范围；不使用该位姿驱动下游 |
| `MATCH_INCOMPLETE_AT_BOUNDARY` | 整体产品支持域越出图像/搜索 ROI | 改变取景或扩大搜索 ROI；不关闭边界门 |
| `MATCH_POLARITY_MISMATCH` | 候选明暗极性不一致 | 检查反面、反光和曝光；如产品工艺真改变则分配新配方并重学习 |
| `MATCH_OUTER_CONTOUR_WEAK` | 外轮廓覆盖/P95 不合格 | 先看遮挡、模糊、打光和边缘，样本证明后才调单项阈值 |
| `MATCH_INNER_FEATURES_WEAK` | 内特征覆盖或 quorum 不合格 | 检查反面/相似品/孔位遮挡；不只提高 native score |
| `MATCH_DUPLICATE_OVERLAP` | 与高质量已接受候选的支持域重复 | 保持作为去重 NG 证据；如真是密集独立产品，用现场标注重新评估 `maxOverlap` |
| `MATCH_TIMEOUT` | HALCON error 9400，本次搜索未在原生 budget 内完成 | 保持 NG 和无操作端口；先收紧搜索 ROI/角度/Scale、调整 level/candidate limit，采样后才在 `100..60000` 内增大 timeout |
| `MATCH_COUNT_MISMATCH` | 实际通过验证数量不等于配方期望 | 检查上料数与 `expectedCount`；候选只用于调试，不发布位姿端口 |
| `MATCH_CANDIDATE_LIMIT_REACHED` | 原生候选已截顶，数量结论不完整 | 先缩小搜索空间和排除背景假候选；需增大 limit 时同时重测 timeout/内存 |
| `MATCH_OPERATOR_FAILED` | 未归类的 native/backend 失败 | 保留原始 JSON、应用日志、输入图和配方版本；停止自动重试，交给开发排查 |

## 10. 升级、备份与回滚

- HALCON 模型引用包含 runtime/model version 和 checksum，因此升级 native runtime 不是拷贝新 DLL。必须同时评估 NuGet、probe、model schema、重学习与真实图性能。
- 备份单位是“配方存储 + `Resources\Templates` 整树”，而不是单个 model file。备份前停止新学习/复制/删除操作，确保 JSON commit point 和 generation pair 处于同一时点。
- 回滚必须成套回滚 Client、HALCON runtime/NuGet 版本和对应配方资源。回滚后重新执行五个 TestHost 验收，不用旧机器上一次的成功 JSON 代替。
- 任何 checksum、owner 或 version 失配都是阻止生产的完整性事件。不允许通过手改 recipe/metadata 把它“修成绿色”。
