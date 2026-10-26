# animeface 模型目录（ACG 方向2）

本目录为"动漫脸检测"模型资产，由 saperm 任务 2026-10-23 采集并验证。
全部为 MIT/Apache 兼容许可，未修改模型权重。

## 1) 主模型：YOLOv8n 动漫脸检测（ONNX）

- 来源：HuggingFace 仓库 `deepghs/anime_face_detection`，子目录 `face_detect_v1.4_n`
  - 镜像下载（GitHub/HF 直连不可达时可用）：https://hf-mirror.com/deepghs/anime_face_detection/resolve/main/face_detect_v1.4_n/model.onnx
- 文件：`model.onnx`（12,102,558 B，sha256 FD860B650A4377046842C3CD80D01B0B408BDFBDB4ACEE5759630F82C6EF04A9）
- 架构：YOLOv8n，单类 `face`（labels.json = ["face"]）
- 指标（上游 README）：F1 0.94，推荐阈值 0.278（threshold.json）
- 许可：MIT（仓库 tag `license:mit`）
- 输入：`images` float32 [batch,3,H,W]，动态尺寸，建议 640×640 letterbox（114 灰边）
- 输出：`output0` float32 [batch,5,8400] → 每列 `cx,cy,w,h,score`（先转置为 [N,5]）
- 参考推理：onnxruntime CPU，162 张 1280×720/900×506 截图共 5.09s（≈31ms/张）

## 2) 兜底/对照：lbpcascade_animeface（OpenCV LBP 级联）

- 来源：nagadomi/lbpcascade_animeface（MIT），经 SourceForge 镜像取得仓库 zip：
  https://sourceforge.net/projects/lbpcascade-animeface.mirror/files/latest/download
- 文件：`lbpcascade_animeface.xml`（246,945 B，sha256 9376D30AC38DB6BDA2A68B88B3B76BBD7E6AA33AF47F7F5C76BC88CA75F1CE30）
- 用 `cv2.CascadeClassifier` 直接加载；无需 GPU、无需 onnxruntime
- 实测比 ONNX 更容易在 UI 图标/文字上误检（如 DeepSeek 鲸鱼 logo）

## 3) 验证脚本

`tests/animface_test.py`（本仓库根目录运行）：

```
python tests/animface_test.py                      # 默认 onnx，扫描 out/sa-view/glance-*.jpg + task-*.jpg
python tests/animface_test.py --backend cascade    # 级联对照
python tests/animface_test.py --image x.jpg --conf 0.3 --top 4
```

JSON 报告：`tests/animface_report_onnx.json` / `tests/animface_report_cascade.json`，
标注图：`tests/animface_previews/`。

## 4) 命名约定

- 若后续换成 s 版（11.1M 参数，F1 0.95）：把 `face_detect_v1.4_s/model.onnx` 命名为同目录 `model.onnx` 即可，脚本/建议接口无需改。
