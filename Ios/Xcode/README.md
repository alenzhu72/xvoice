# 声线工坊 iPhone 版

1. 在 Mac 上用 Xcode 16 或更新版本打开 `VoiceChange.xcodeproj`，选择 `VoiceChange` 目标。
2. 在 `VoiceChange/SpeechConfig.plist` 的 `aliyunKey`、`miniMaxKey` 字段填入阿里云北京及 MiniMax 中国站的 API Key。阿里云使用北京公共地址。填写后的密钥会随应用一起打包。
3. 在 Signing & Capabilities 中选择 Apple Team。如有需要，将 Bundle Identifier 改为该 Team 可签名的 ID。
4. 选择已连接的 iPhone，点 Run。首次运行时允许麦克风权限。支持 iOS 15 及以上。

项目使用 SwiftUI、AVFoundation 和 URLSession，所需框架由 iOS SDK 提供。包含原图标、实时语音识别、分段变声、麦克风电平和 10 个音色。离开前台时会停止录音。
