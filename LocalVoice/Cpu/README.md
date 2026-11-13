# 声线工坊 RVC CPU 版

解压后运行 VoiceChange.RvcCpu.exe。音色菜单内置 guanguanV1、kikiV1 和 keruanV1。
要换音色，结束当前变声，选新音色，再点击开始。

## 添加音色

1. 已用本项目导出的音色 ONNX，复制到 EXE 同级的 models 目录。
2. 点击「刷新设备和音色」。菜单名称来自 ONNX 文件名。
3. 保留 models/contentvec.onnx。它是所有音色共用的人声特征模型。

网上的 .pth 需要先转换。本版导出脚本支持带 F0 的 RVC v1、256 维模型。
普通的 .onnx 也可能采用不同输入格式。请使用本项目脚本导出。
CPU 版暂不使用 .index。MP3、WAV 等录音需要先训练成 RVC 模型。

## 在当前开发电脑导出 .pth

把来源可信的模型放到 RVC 的 assets/weights 下，例如 my_voice.pth。
从项目根目录运行以下 PowerShell 命令，最后一个参数是文件名去掉 .pth。

    $r = (Resolve-Path bin/rvc-runtime/RVC20260718Nvidia).Path
    & "$r/runtime/python.exe" -u LocalVoice/Cpu/export_models.py $r obj/rvc-cpu-models obj/rvc-export-deps my_voice
    Copy-Item obj/rvc-cpu-models/my_voice.onnx bin/windows-rvc-cpu-voices/models/

干净环境需先准备 RVC 开发环境，并将 onnx、protobuf、ml_dtypes 和 onnxruntime 安装到第三个参数指定的依赖目录。目标目录需已存在。

用于商品或公开分享的音色，请核对模型权重的商用和再分发许可，以及录音和声音本人的授权。内置音色仅用于本次原型试听。
