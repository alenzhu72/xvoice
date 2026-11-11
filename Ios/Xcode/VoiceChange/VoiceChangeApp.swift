import AVFoundation
import SwiftUI

@main
struct VoiceChangeApp: App {
    var body: some Scene { WindowGroup { VoiceView() } }
}

@MainActor
struct VoiceView: View {
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var model = VoiceModel()
    @State private var voiceID = Voice.presets[0].id
    private let accent = Color(red: 0.47, green: 0.90, blue: 0.80)

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Image("VoiceLogo").resizable().scaledToFit()
                    .frame(width: 76, height: 76).clipShape(RoundedRectangle(cornerRadius: 16))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 5) {
                    Text("声线工坊").font(.largeTitle.bold())
                    Text("普通话实时变声").foregroundColor(.secondary)
                }
                Picker("音色", selection: $voiceID) {
                    ForEach(Voice.presets) { voice in Text(voice.name).tag(voice.id) }
                }
                .pickerStyle(.menu)
                .disabled(model.running)
                Button {
                    if model.running { model.stop() }
                    else if let voice = Voice.presets.first(where: { $0.id == voiceID }) { model.start(voice) }
                } label: {
                    Text(model.running ? "结束变声" : "开始变声")
                        .font(.headline).frame(maxWidth: .infinity, minHeight: 54)
                        .background(accent, in: RoundedRectangle(cornerRadius: 14))
                        .foregroundColor(.black)
                }
                .disabled(model.stopping)
                Text(model.status).foregroundColor(accent)
                VStack(alignment: .leading, spacing: 6) {
                    Text(model.receivingAudio ? "麦克风正在采集" : "麦克风")
                    ProgressView(value: model.level).tint(accent)
                        .accessibilityLabel("麦克风电平")
                }
                Text("最近识别").foregroundColor(.secondary)
                Text(model.transcript.isEmpty ? "说话后，识别结果会显示在这里。" : model.transcript)
                    .frame(maxWidth: .infinity, minHeight: 64, alignment: .topLeading)
                    .padding(14).background(Color.white.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
            }.padding(24)
        }
        .background(Color(red: 0.063, green: 0.086, blue: 0.125).ignoresSafeArea())
        .preferredColorScheme(.dark)
        .onChange(of: scenePhase) { phase in if phase == .background { model.stop() } }
    }
}

@MainActor
final class VoiceModel: ObservableObject {
    @Published private(set) var running = false
    @Published private(set) var stopping = false
    @Published private(set) var status = "就绪"
    @Published private(set) var transcript = ""
    @Published private(set) var level = 0.0
    @Published private(set) var receivingAudio = false
    private var session: Task<Void, Never>?
    private var phrases = PhraseStream()

    func start(_ voice: Voice) {
        guard session == nil else { return }
        running = true
        status = "正在启动…"
        transcript = ""
        phrases = PhraseStream()
        session = Task { await run(voice) }
    }

    func stop() {
        guard let session = session else { return }
        stopping = true
        status = "正在结束…"
        session.cancel()
    }

    private func run(_ voice: Voice) async {
        defer {
            session = nil
            running = false
            stopping = false
            receivingAudio = false
            level = 0
        }
        do {
            let config = try SpeechConfig.load()
            let permission = await withCheckedContinuation { continuation in
                AVAudioSession.sharedInstance().requestRecordPermission { continuation.resume(returning: $0) }
            }
            try Task.checkCancellation()
            guard permission else { throw SpeechError("请在系统设置中允许麦克风访问。") }
            let audio = try VoiceAudio()
            defer { audio.stop() }
            let frames: (stream: AsyncStream<Data>, continuation: AsyncStream<Data>.Continuation) = boundedStream(50)
            let fragments: (stream: AsyncStream<String>, continuation: AsyncStream<String>.Continuation) = boundedStream(3)
            defer {
                frames.continuation.finish()
                fragments.continuation.finish()
            }
            try audio.start(frames.continuation) { [weak self] peak in
                Task { @MainActor in
                    guard let self = self, self.running, !self.stopping else { return }
                    self.level = peak
                    self.receivingAudio = true
                }
            }
            status = "正在连接阿里云…"
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask {
                    try await Speech.recognize(config: config, audio: frames.stream, ready: {
                        await self.showReady(voice)
                    }, result: { text, final in
                        if let fragment = await self.handleResult(text, final: final) {
                            fragments.continuation.yield(fragment)
                        }
                    })
                }
                group.addTask {
                    for await fragment in fragments.stream {
                        try await Speech.synthesize(config: config, voice: voice, text: fragment) { data in
                            try await audio.play(data)
                        }
                    }
                }
                defer { group.cancelAll() }
                _ = try await group.next()
            }
            status = "已结束"
        } catch {
            status = Task.isCancelled ? "已结束" : error.localizedDescription
        }
    }

    private func showReady(_ voice: Voice) {
        if !stopping { status = "实时变声中 · \(voice.name)" }
    }

    private func handleResult(_ text: String, final: Bool) -> String? {
        guard !stopping else { return nil }
        if !text.isEmpty { transcript = text }
        return phrases.update(text, final: final)
    }
}

private func boundedStream<Element>(_ capacity: Int) -> (AsyncStream<Element>, AsyncStream<Element>.Continuation) {
    var continuation: AsyncStream<Element>.Continuation!
    let stream = AsyncStream<Element>(bufferingPolicy: .bufferingNewest(capacity)) { continuation = $0 }
    return (stream, continuation)
}

@MainActor
final class VoiceAudio {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let outputFormat = AVAudioFormat(standardFormatWithSampleRate: 24_000, channels: 1)!
    private var tapInstalled = false
    private var pendingBuffers = 0

    init() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playAndRecord, mode: .default, options: [.defaultToSpeaker])
        try session.setActive(true)
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: outputFormat)
    }

    func start(_ frames: AsyncStream<Data>.Continuation, level: @escaping (Double) -> Void) throws {
        let input = engine.inputNode
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0,
              let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true),
              let converter = AVAudioConverter(from: inputFormat, to: target) else {
            throw SpeechError("无法打开麦克风或转换录音格式。")
        }
        Self.installTap(input, format: inputFormat, target: target, converter: converter, frames: frames, level: level)
        tapInstalled = true
        engine.prepare()
        try engine.start()
    }

    private nonisolated static func installTap(_ input: AVAudioInputNode, format: AVAudioFormat,
        target: AVAudioFormat, converter: AVAudioConverter, frames: AsyncStream<Data>.Continuation,
        level: @escaping (Double) -> Void) {
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in
            let capacity = AVAudioFrameCount(ceil(Double(buffer.frameLength) * 16_000 / format.sampleRate)) + 32
            guard let converted = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
            var consumed = false
            var error: NSError?
            let result = converter.convert(to: converted, error: &error) { _, state in
                state.pointee = consumed ? .noDataNow : .haveData
                if consumed { return nil }
                consumed = true
                return buffer
            }
            if result == .error {
                frames.finish()
                return
            }
            guard let samples = converted.int16ChannelData?[0], converted.frameLength > 0 else { return }
            let count = Int(converted.frameLength)
            frames.yield(Data(bytes: samples, count: count * MemoryLayout<Int16>.size))
            var peak = 0
            for index in 0..<count { peak = max(peak, abs(Int(samples[index]))) }
            level(sqrt(Double(peak) / 32_768))
        }
    }

    func play(_ data: Data) async throws {
        let bytes = [UInt8](data)
        var offset = 0
        while offset < bytes.count {
            try Task.checkCancellation()
            while pendingBuffers >= 3 { try await Task.sleep(nanoseconds: 10_000_000) }
            let count = min(960, (bytes.count - offset) / 2)
            guard count > 0, let buffer = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: AVAudioFrameCount(count)),
                  let samples = buffer.floatChannelData?[0] else {
                throw SpeechError("无法播放合成音频。")
            }
            buffer.frameLength = AVAudioFrameCount(count)
            for index in 0..<count {
                let bits = UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
                samples[index] = Float(Int16(bitPattern: bits)) / 32_768
                offset += 2
            }
            pendingBuffers += 1
            player.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { [weak self] _ in
                Task { @MainActor in
                    guard let self = self else { return }
                    self.pendingBuffers -= 1
                }
            }
            if !player.isPlaying { player.play() }
        }
    }

    func stop() {
        if tapInstalled {
            engine.inputNode.removeTap(onBus: 0)
            tapInstalled = false
        }
        player.stop()
        engine.stop()
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}
