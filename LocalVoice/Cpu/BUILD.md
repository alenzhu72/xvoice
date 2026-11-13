# XVoice Windows CPU 多音色版源码

在 Windows x64 上安装 .NET 10 SDK，然后从本目录运行：

    dotnet publish LocalVoice/Cpu/VoiceChange.RvcCpu.csproj -c Release -r win-x64 --self-contained true

从已经下载的 Windows CPU 多音色成品 ZIP 中复制整个 models 目录到发布目录，和 VoiceChange.RvcCpu.exe 并排。应用需要 contentvec.onnx、guanguanV1.onnx、kikiV1.onnx 和 keruanV1.onnx。模型权重未打进源码包。默认编译 CPU 版本。

新增音色与模型导出见 LocalVoice/Cpu/README.md。
