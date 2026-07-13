# OpenCV Shape V2 整产品模板匹配设计

**状态：** 已批准方案 1，等待书面设计复核

**日期：** 2026-07-13

**适用方案：** `CVWork.sln`

## 1. 背景

当前 `TemplateLocate` 的 OpenCV Shape 模式可以学习完整产品，但长条模板旋转时仍把边缘写回原始固定尺寸画布。产品在大角度旋转后超出画布的部分被裁掉，剩余中段仍可获得接近满分的匹配结果。

现场模板中的产品有效轮廓约为 `985×143 px`。受控复现将完整产品旋转 90° 后，当前实现返回 `0.9999`，但实际评分轮廓只有约 `395×185 px`，保留旋转边界后应约为 `950×369 px`。这证明“高分、中心正确、角度正确”不能代表完整产品参与了评分。

当前 Shape 分数还是单向 Chamfer 类分数：它计算模板边缘到搜索边缘的平均距离，不检查模板边缘覆盖率，也不惩罚候选区域内多余的搜索边缘。在散乱、密集和堆叠场景中，其他产品边缘可以替代目标边缘，产生局部高分。

结果显示还把“实际评分边缘”和“完整模板 ROI”合并到同一 `ShapeContours`，并用相同橙色绘制。完整橙色外圈因此会掩盖评分轮廓已经被裁剪的事实。

## 2. 目标

1. 完整产品在任意配置角度旋转后，所有模板边缘都保留在实际匹配画布中。
2. 粗搜索、精搜索、最终位姿和结果轮廓使用同一套旋转后尺寸与中心语义。
3. Shape V2 同时衡量前向距离、反向距离和模板边缘覆盖率，局部碎片不能再获得整件高分。
4. 评分边缘、完整模板 ROI 和定位中心在结果中具有独立、稳定的语义。
5. 保持现有 `TemplateMatcher.Learn/Match` 外部 seam，不要求工具调用方理解旋转矩阵、Chamfer 或粗精搜索细节。
6. 新评分行为可版本化，旧配方和旧历史结果可以继续读取。
7. 默认自动化测试不提交现场工业原图；真实图片仅在本地验收。

## 3. 非目标

本阶段不实现：

- 多目标匹配、NMS 和约 18 件产品的逐件计数。
- 正面/反面双模板分类。
- HALCON adapter。
- 尺度搜索、镜像匹配或遮挡恢复。
- 真正的亚像素、极性和可配置金字塔层数。
- 对密集粘连组中不可见产品作强制定位。

这些能力在 Shape V2 单目标结果稳定后进入独立阶段。

## 4. 方案比较

### 4.1 只修旋转画布

将 `RotateShapeEdges` 改为保留边界。改动最小，但粗搜索尺寸映射、精搜索区域和局部高分仍可能出错，不能独立满足现场要求。

### 4.2 OpenCV Shape V2（采用）

在现有深 module 内同时修复旋转几何、粗精搜索映射、候选质量验证和结果叠加语义。外部仍通过 `TemplateMatcher.Learn/Match` 使用，调用方只接收最终结果和诊断信息。

该方案把复杂度集中在 OpenCV matcher implementation，保持调用方 leverage 和维护 locality，也为以后增加 HALCON adapter 保留稳定上层语义。

### 4.3 立即切换 HALCON

可能缩短复杂遮挡算法的研发时间，但会立即引入授权、部署、模型文件和运行时依赖。本阶段尚未建立可信的 OpenCV 基线，不采用该方案。

## 5. Module 与 seam

### 5.1 外部 seam

保留现有入口：

```csharp
TemplateMatcher.Learn(frame, searchRoi, parameters);
TemplateMatcher.Match(frame, searchRoi, parameters, cancellationToken);
```

测试和生产调用方均通过该 seam 验证行为，不暴露或直接测试私有旋转函数。

`TemplateMatchResult` 末尾增加可选诊断语义：

- `MatchedTemplateRoiContours`：完整训练 ROI 变换到匹配位姿后的轮廓。
- `ShapeCoverage`：实际评分模板边缘中，被搜索边缘支持的比例，范围为 `0..1`。
- `ShapeReverseScore`：候选支持区域内，搜索边缘到模板边缘的反向得分，范围为 `0..1`。

现有 `ShapeContours` 的语义收紧为“真正参与评分的模板边缘”。新增字段放在位置记录末尾并提供默认值；仓库内不存在记录解构用法。

### 5.2 内部 implementation

OpenCV matcher implementation 隐藏：

- 逐角度旋转边界计算。
- 粗分辨率与原分辨率之间的中心、宽高映射。
- 精搜索区域扩展和边界裁剪。
- 前向 Chamfer 候选生成。
- 覆盖率和反向距离质量验证。
- v1/v2 分数策略选择。

本阶段不新增只有一个实现的公共 matcher interface。等 OpenCV 与 HALCON 两个真实 adapter 同时存在时，再提取后端 seam。

## 6. 旋转与定位几何

1. 每个角度根据原模板宽高、正弦和余弦计算旋转后外接尺寸。
2. 调整仿射矩阵平移量，使原模板中心映射到新画布中心。
3. Shape 边缘和模板支持掩膜使用同一个变换；边缘插值使用 `Nearest`。
4. `MatchCandidate.Width/Height` 始终表示当前角度的实际画布尺寸。
5. 粗搜索结果还原到原图时，缩放候选中心和实际宽高，再由中心计算左上角；禁止恢复成未旋转模板尺寸。
6. 精搜索区域基于旋转后候选范围和配置 margin 扩展，并限制在搜索 ROI 内。
7. 最终 `Pose`、评分边缘和模板 ROI 都以同一候选中心为变换原点。
8. 旋转模板大于搜索区域时跳过该角度，不允许通过二次裁剪继续评分。

## 7. Shape V2 评分

现有距离变换和 `MatchTemplate(CCorr)` 保留为快速候选生成。每个角度的最佳位置再执行一次质量验证：

1. **前向得分**：模板边缘到搜索边缘的平均距离，继续使用现有指数映射。
2. **覆盖率**：模板边缘位置的搜索距离低于容差的像素数除以模板边缘总数。容差随当前粗精搜索尺度同步缩放，最终结果以原分辨率计算。
3. **反向得分**：在旋转后的模板支持域内，搜索边缘到模板边缘的平均距离，再使用相同指数映射。
4. **最终得分**：`min(forwardScore, reverseScore) * coverage`。

V2 增加参数 `shapeCoverageDistance`，表示原模板分辨率下允许的边缘距离，默认 `3.0 px`，有效范围为 `0.5..20 px`。粗搜索使用 `max(0.75, shapeCoverageDistance * passScale)`，精搜索使用原值。前向和反向距离使用同一个 `shapeScoreScale` 指数映射，避免两个分数采用不同量纲。

模板存在 Polygon/Circle/RotatedRectangle 掩膜时，反向统计只使用旋转后的模板掩膜；没有模板掩膜时使用旋转后的模板矩形支持域。支持域外的其他产品边缘不参与反向惩罚。

以下情况不产生有效候选：

- 旋转后模板边缘少于现有最小数量。
- 支持域内没有可计算的有效边缘。
- 旋转画布超过搜索区域。
- 计算过程中收到取消请求。

## 8. 评分版本和兼容性

- 新学习的 OpenCV Shape 模型写入 `shapeScoreVersion=2`。
- 新建工具和参数对话框默认使用 V2。
- 已有配方缺少该参数时继续使用 v1 评分公式，但旋转 keep-bounds 作为几何缺陷修复始终生效；因此大角度结果的实际分数仍可能下降，这是从“残缺轮廓分数”纠正为“完整轮廓分数”的预期变化。
- 已有工具在参数对话框确认保存后写入 V2；现场配方需重新验证 `minScore`，不能静默沿用旧分数经验。
- v1 和 v2 都返回分离后的评分轮廓和模板 ROI；这是显示语义修复，不改变检测判定。
- v1 的 `ShapeCoverage` 和 `ShapeReverseScore` 保持空值；V2 必须返回二者。UI 只在值存在时显示覆盖率。
- 旧历史结果只有混合 `shapeContours` 时继续按旧格式显示，不能根据“最后一条轮廓”猜测拆分。

## 9. 结果数据与显示

`TemplateLocateTool` 输出：

- `shapeContours`：评分边缘。
- `matchedTemplateRoiContours`：完整模板 ROI。
- `shapeCoverage`：覆盖率。
- `shapeReverseScore`：反向得分。
- `overlaySchemaVersion=2`：标识新格式。

`ToolResult.Data` 已按字典 JSON 持久化，不修改领域模型、SQLite 表结构或迁移。

生产结果页和模板调参页采用相同显示规则：

- 评分边缘：橙色。
- 完整模板 ROI：青色。
- 定位中心：按最终结果显示绿色或红色。
- 定位标签显示最终分数和覆盖率。

旧结果没有新字段时，原 `shapeContours` 继续以橙色显示。

## 10. 测试策略

实施严格采用 Red-Green-Refactor，每项生产行为先观察对应测试因缺少行为而失败。

### 10.1 默认快速测试

在 `VisionStation.Vision.Tests` 中使用纯内存合成的不对称长条产品，通过公共 `TemplateMatcher` seam 测试：

1. 90° 旋转后返回宽高随旋转交换，完整边缘未被固定画布裁剪。
2. `0°/35°/90°/-135°` 的中心误差不超过 2 px，角度误差不超过配置角度步长。
3. 大于 720 px 的搜索图强制经过粗到精路径，90° 仍保持正确尺寸和中心。
4. 只有 30%–40% 中段并叠加杂乱线的负例低于 `0.85`。
5. 完整产品叠加有限杂边仍匹配成功。
6. 完整产品完全位于搜索 ROI 内但靠近其边缘时仍可匹配，不发生二次裁剪。
7. `ShapeContours` 和 `MatchedTemplateRoiContours` 分离返回。

在 `VisionStation.Vision.UI.Tests` 中验证：

1. 新结果分别生成橙色评分边缘和青色模板 ROI。
2. 旧混合格式仍可显示。
3. 模板调参页与生产结果页使用一致语义。

### 10.2 本地现场验收

现场 BMP 不提交 Git。实施完成后从本地目录运行当前六张图片并记录：

- 最终分数、覆盖率和反向得分。
- 中心误差和模 180° 角度误差。
- 实际评分轮廓范围与完整产品范围之比。
- 单张 `5472×3648` 图耗时。

`正.bmp`、`反.bmp` 用于单件检查；`散乱.bmp` 的 8 个独立完整目标用于后续多目标基线；粘连和边界截断目标在本阶段标记为 ignore。

## 11. 性能约束

- 保留现有粗搜索和精搜索两阶段。
- 每个角度先用前向 Chamfer 产生一个最佳位置，再进行质量验证。
- 反向距离只在候选支持域内计算，不扫描完整搜索图。
- 所有逐角度 `Mat` 在本轮结束时释放，不跨角度累积。
- 本地六图基准中，Shape V2 单图总耗时不超过修复前同参数基线的 1.3 倍；若超过，先优化临时矩阵和候选验证，再考虑牺牲质量公式。

## 12. 预计修改范围

- `VisionStation.Vision/OpenCvTemplateMatcher.cs`
- `VisionStation.Vision/TemplateMatcher.cs`
- `VisionStation.Vision/Tools/TemplateLocateTool.cs`
- `VisionStation.Vision.UI/Services/VisionOverlayBuilder.cs`
- `VisionStation.Vision.UI/ViewModels/TemplateLocateToolDialogViewModel.cs`
- `VisionStation.Vision.UI/ViewModels/VisionToolCatalog.cs`
- `VisionStation.Vision.Tests/OpenCvTemplateMatcherTests.cs`（新增）
- `VisionStation.Vision.Tests/TemplateLocateToolTests.cs`（新增或并入 matcher 测试）
- `VisionStation.Vision.UI.Tests` 中对应叠加和调参测试
- 二次开发说明文档（实施阶段新增）

不进行无关格式化、目录重组或公共后端抽象。

## 13. 完成标准

1. 合成长条产品在所有指定角度下完整旋转，90° 回归测试由红转绿。
2. 粗到精路径不再把候选恢复为未旋转尺寸。
3. 局部中段负例的 V2 分数低于 `0.85`。
4. 完整目标正例满足中心、角度和覆盖率要求。
5. 评分边缘与模板 ROI 在数据和显示中均完全分离。
6. 旧配方和旧历史结果保持可读。
7. 默认测试不依赖本机绝对路径或工业图片。
8. 本地现场六图验收结果被记录，性能不超过基线 1.3 倍。
9. `VisionStation.Vision.Tests`、`VisionStation.Vision.UI.Tests` 和 Release 构建通过。
10. 实施过程中每项新行为都有明确的 Red-Green 证据。
