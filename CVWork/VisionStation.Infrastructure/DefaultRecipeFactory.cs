using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public static class DefaultRecipeFactory
{
    public static Recipe Create()
    {
        var rois = new RoiDefinition[]
        {
            new()
            {
                Id = "roi-measure",
                Name = "尺寸测量ROI",
                Shape = RoiShapeKind.RotatedRectangle,
                X = 450,
                Y = 265,
                Width = 380,
                Height = 190,
                Angle = 0
            },
            new()
            {
                Id = "roi-code",
                Name = "读码ROI",
                Shape = RoiShapeKind.Rectangle,
                X = 900,
                Y = 210,
                Width = 250,
                Height = 120
            },
            new()
            {
                Id = "roi-defect",
                Name = "外观缺陷ROI",
                Shape = RoiShapeKind.Polygon,
                Points =
                [
                    new Point2D(390, 230),
                    new Point2D(890, 230),
                    new Point2D(930, 515),
                    new Point2D(350, 515)
                ]
            }
        };

        var tools = new VisionToolDefinition[]
        {
            new()
            {
                Id = "tool-acquire",
                Name = "采图",
                Kind = VisionToolKind.AcquireImage
            },
            new()
            {
                Id = "tool-locate",
                Name = "模板定位",
                Kind = VisionToolKind.TemplateLocate,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["engine"] = "OpenCv",
                    ["matchMode"] = "Shape",
                    ["autoLearnTemplate"] = "False",
                    ["minScore"] = "0.85",
                    ["angleStart"] = "-45",
                    ["angleExtent"] = "90",
                    ["angleStep"] = "2",
                    ["cannyLow"] = "60",
                    ["cannyHigh"] = "160",
                    ["shapeScoreVersion"] = "2",
                    ["shapeCoverageDistance"] = "3"
                }
            },
            new()
            {
                Id = "tool-roi",
                Name = "ROI映射",
                Kind = VisionToolKind.RoiMap
            },
            new()
            {
                Id = "tool-measure-width",
                Name = "宽度测量",
                Kind = VisionToolKind.MeasureDistance,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nominal"] = "50.0",
                    ["lower"] = "49.6",
                    ["upper"] = "50.4",
                    ["unit"] = "mm",
                    ["measurementMode"] = "Simulated",
                    ["enabledOutputs"] = "MeasureValueOutput,ResultOutput"
                }
            },
            new()
            {
                Id = "tool-code",
                Name = "条码识别",
                Kind = VisionToolKind.CodeRead
            },
            new()
            {
                Id = "tool-defect",
                Name = "斑点分析",
                Kind = VisionToolKind.DefectDetect,
                Parameters = VisionToolDefaults.CreateBlobAnalysisParameters()
            },
            new()
            {
                Id = "tool-judge",
                Name = "综合判定",
                Kind = VisionToolKind.Judge
            }
        };

        var productParameters = new ProductParameterDefinition[]
        {
            new()
            {
                Id = "param-width",
                Name = "产品宽度",
                Value = "50.0",
                Unit = "mm",
                Description = "默认测量目标"
            },
            new()
            {
                Id = "param-height",
                Name = "产品高度",
                Value = "20.0",
                Unit = "mm",
                Description = "可供外部二次开发读取"
            },
            new()
            {
                Id = "param-model",
                Name = "视觉模板版本",
                Value = "v1.0",
                Description = "当前产品使用的模板标识"
            }
        };

        var motionSequences = new MotionSequenceDefinition[]
        {
            new()
            {
                Id = "motion-main",
                Name = "上料检测位",
                ControllerProfile = "Reserved",
                Description = "预留给不同轴卡的完整动作序列配置",
                Steps =
                [
                    new MotionStepDefinition
                    {
                        Id = "motion-main-home",
                        Name = "回原点",
                        CommandType = "Home",
                        AxisKey = "AxisX",
                        TimeoutMs = 10000
                    },
                    new MotionStepDefinition
                    {
                        Id = "motion-main-move",
                        Name = "移动到检测位",
                        CommandType = "MoveAbsolute",
                        AxisKey = "AxisX",
                        Position = 120,
                        Speed = 80,
                        Acceleration = 120,
                        WaitSignalId = "plc-ready",
                        TimeoutMs = 5000
                    }
                ]
            }
        };

        var processSteps = new ProcessStepDefinition[]
        {
            new()
            {
                Id = "proc-001",
                StepNo = 1,
                Name = "轴运动到拍照位",
                StepType = ProcessStepType.AxisMove,
                DeviceKey = "motion-main",
                AxisKey = "AxisX",
                Position = 120,
                Speed = 80,
                Acceleration = 120,
                TimeoutMs = 5000,
                Description = "移动产品到主相机拍照位置"
            },
            new()
            {
                Id = "proc-002",
                StepNo = 2,
                Name = "等待检测信号",
                StepType = ProcessStepType.WaitPlcSignal,
                DeviceKey = "plc-main",
                SignalId = "PlcReady",
                TimeoutMs = 3000,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["expected"] = "1",
                    ["match"] = "Equals",
                    ["pollIntervalMs"] = "50",
                    ["debounceMs"] = "100",
                    ["onTimeout"] = "AlarmStop"
                },
                Description = "等待 PLC 地址读值满足条件"
            },
            new()
            {
                Id = "proc-003",
                StepNo = 3,
                Name = "运行视觉主流程",
                StepType = ProcessStepType.RunVisionFlow,
                FlowId = "main",
                Description = "执行当前配方绑定的主检测流程"
            },
            new()
            {
                Id = "proc-004",
                StepNo = 4,
                Name = "读取宽度结果",
                StepType = ProcessStepType.ReadVisionResult,
                ResultKey = "MeasuredWidth",
                Description = "从视觉结果中提取宽度值"
            },
            new()
            {
                Id = "proc-005",
                StepNo = 5,
                Name = "写入结果表",
                StepType = ProcessStepType.WriteResultTable,
                ResultKey = "MeasuredWidth",
                OutputTarget = "ResultTable.Width",
                Description = "把宽度值写入结果表字段"
            },
            new()
            {
                Id = "proc-006",
                StepNo = 6,
                Name = "回写总判定",
                StepType = ProcessStepType.WritePlc,
                DeviceKey = "plc-main",
                ResultKey = "OverallResult",
                OutputTarget = "D200",
                TimeoutMs = 1000,
                Description = "把整站 OK/NG 回写给 PLC"
            },
            new()
            {
                Id = "proc-007",
                StepNo = 7,
                Name = "结束",
                StepType = ProcessStepType.End,
                Description = "流程结束"
            }
        };

        var visionResults = new VisionResultDefinition[]
        {
            new()
            {
                Id = "result-overall",
                Name = "总判定",
                SourceToolId = "tool-judge",
                SourceKey = "Outcome",
                DataType = "enum",
                ParticipateInJudge = true,
                ExternalAlias = "OverallResult",
                PlcAddress = "D200",
                Description = "整站 OK/NG 结果"
            },
            new()
            {
                Id = "result-width",
                Name = "宽度值",
                SourceToolId = "tool-measure-width",
                SourceKey = "distance",
                DataType = "double",
                Unit = "mm",
                ExternalAlias = "MeasuredWidth",
                PlcAddress = "D210",
                Description = "供 PLC/MES 或二次开发调用"
            },
            new()
            {
                Id = "result-code",
                Name = "条码内容",
                SourceToolId = "tool-code",
                SourceKey = "code",
                DataType = "string",
                ExternalAlias = "Barcode",
                PlcAddress = "D220",
                Description = "读码结果"
            }
        };

        var plcSignals = new PlcSignalDefinition[]
        {
            new()
            {
                Id = "plc-ready",
                Name = "允许检测",
                Address = "M100",
                Direction = "Read",
                TriggerValue = "1",
                TimeoutMs = 3000,
                Blocking = true,
                Description = "等待 PLC 允许信号后再执行检测"
            },
            new()
            {
                Id = "plc-busy",
                Name = "检测忙",
                Address = "M110",
                Direction = "Write",
                TriggerValue = "1",
                TimeoutMs = 1000,
                Blocking = false,
                Description = "运行时通知 PLC 当前工位忙碌"
            }
        };

        var signalMappings = new SignalMappingDefinition[]
        {
            new()
            {
                Id = "signal-allow-inspect",
                Key = "AllowInspect",
                Name = "允许检测",
                DataType = "bool",
                SourceType = "PLC",
                DeviceKey = "plc-main",
                Address = "plc-ready",
                Enabled = true,
                Description = "PLC 允许检测信号"
            }
        };

        var variables = new RecipeVariableDefinition[]
        {
            new()
            {
                Id = "var-product-width",
                Key = "ProductWidth",
                Name = "产品宽度",
                Direction = "Input",
                DataType = "double",
                DefaultValue = "50.0",
                CurrentValue = "50.0",
                Unit = "mm",
                Source = "配方参数:param-width",
                Target = "RuntimeValues.ProductWidth",
                Required = true,
                Description = "检测前可由手动、PLC 或 MES 覆盖的产品目标宽度"
            },
            new()
            {
                Id = "var-template-version",
                Key = "TemplateVersion",
                Name = "模板版本",
                Direction = "Input",
                DataType = "string",
                DefaultValue = "v1.0",
                CurrentValue = "v1.0",
                Source = "配方参数:param-model",
                Target = "RuntimeValues.TemplateVersion",
                Description = "当前产品视觉模板或算法资源版本"
            },
            new()
            {
                Id = "var-measured-width",
                Key = "MeasuredWidth",
                Name = "宽度测量值",
                Direction = "Output",
                DataType = "double",
                Unit = "mm",
                Source = "tool-measure-width:distance",
                Target = "PLC:D210",
                Description = "视觉流程输出的宽度结果，可写入 PLC/MES/历史记录"
            },
            new()
            {
                Id = "var-width-deviation",
                Key = "WidthDeviation",
                Name = "宽度偏差",
                Direction = "Internal",
                DataType = "double",
                Unit = "mm",
                Source = "Expression:${MeasuredWidth}-${ProductWidth}",
                Target = "RuntimeValues.WidthDeviation",
                Expression = "${MeasuredWidth}-${ProductWidth}",
                Description = "示例手动计算变量：测量宽度减去目标宽度"
            },
            new()
            {
                Id = "var-barcode",
                Key = "Barcode",
                Name = "条码内容",
                Direction = "Output",
                DataType = "string",
                Source = "tool-code:code",
                Target = "PLC:D220",
                Description = "读码工具输出的条码文本"
            },
            new()
            {
                Id = "var-overall-result",
                Key = "OverallResult",
                Name = "总判定",
                Direction = "Output",
                DataType = "enum",
                Source = "tool-judge:Outcome",
                Target = "PLC:D200",
                Required = true,
                Description = "整站最终 OK/NG/Error 判定"
            },
            new()
            {
                Id = "var-plc-ready",
                Key = "PlcReady",
                Name = "PLC允许检测",
                Direction = "Input",
                DataType = "bool",
                DefaultValue = "1",
                CurrentValue = "1",
                Source = "PLC:M100",
                Target = "plc-ready",
                Required = true,
                Description = "外部设备允许检测的触发变量"
            }
        };

        return new Recipe
        {
            Id = "default",
            Name = "定位测量检测流程",
            ProductCode = "VS-DEMO-001",
            Description = "采图 -> 模板定位 -> ROI映射 -> 尺寸测量 -> 条码 -> 缺陷检测 -> OK/NG判断",
            Camera = new CameraSettings
            {
                CameraId = "SIM-CAM-01",
                DisplayName = "模拟相机 01",
                ExposureTimeUs = 8000,
                Gain = 1.2,
                HardwareTrigger = false
            },
            ProductParameters = productParameters,
            Rois = rois,
            Tools = tools,
            CurrentFlowId = "main",
            Flows =
            [
                new VisionFlowDefinition
                {
                    Id = "main",
                    Name = "主检测流程",
                    Description = "采图 -> 模板定位 -> ROI映射 -> 尺寸测量 -> 条码 -> 缺陷检测 -> OK/NG判断",
                    Rois = rois,
                    Tools = tools,
                    UpdatedAt = DateTimeOffset.Now
                }
            ],
            MotionSequences = motionSequences,
            ProcessSteps = processSteps,
            VisionResults = visionResults,
            PlcSignals = plcSignals,
            SignalMappings = signalMappings,
            Variables = variables,
            TracePolicy = new TracePolicy
            {
                SaveOkImages = true,
                SaveNgImages = true,
                ImageFormat = "Png",
                RetentionDays = 30,
                MaxStorageMegabytes = 20_480
            },
            UpdatedAt = DateTimeOffset.Now
        };
    }
}
