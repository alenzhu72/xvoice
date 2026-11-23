"""One-time offline export. The shipped application does not use Python."""
import os
from pathlib import Path
import sys

root, output, dependencies = (Path(arg).resolve() for arg in sys.argv[1:4])
names = sys.argv[4:] or ["guanguanV1"]
os.chdir(root)
sys.path[:0] = [str(dependencies), str(root)]
import torch
from torch import nn
from infer.hubert import load_hubert_model
from infer.rtrvc import get_synthesizer
from onnxruntime.quantization import quantize_dynamic, QuantType

torch.set_num_threads(4)

class ContentEncoder(nn.Module):
    def __init__(self):
        super().__init__()
        self.encoder = load_hubert_model("cpu")
        self.encoder.encoder.layers = self.encoder.encoder.layers[:9]

    def forward(self, audio):
        return self.encoder.final_proj(self.encoder(audio).last_hidden_state)

class VoiceGenerator(nn.Module):
    def __init__(self, name):
        super().__init__()
        self.model, checkpoint = get_synthesizer(f"assets/weights/{name}.pth")
        assert checkpoint.get("version", "v1") == "v1" and checkpoint.get("f0", 1) == 1
        self.sample_rate = checkpoint["config"][-1]

    def forward(self, features, pitch, frequency):
        return self.model.infer(features, torch.tensor([80]), pitch, frequency, torch.tensor([0]), 62, 18)[0]

with torch.inference_mode():
    if not (output / "contentvec.onnx").is_file():
        encoder = ContentEncoder()
        audio = torch.zeros(1, 12800)
        assert encoder(audio).shape == (1, 39, 256)
        full = output / "contentvec-fp32.onnx"
        torch.onnx.export(encoder, (audio,), str(full), input_names=["audio"], output_names=["features"], opset_version=17, dynamo=False)
        quantize_dynamic(str(full), str(output / "contentvec.onnx"), op_types_to_quantize=["MatMul"], weight_type=QuantType.QInt8, per_channel=True, extra_options={"MatMulConstBOnly": True})
    for name in names:
        voice = VoiceGenerator(name)
        pitch = torch.full((1, 80), 55, dtype=torch.long)
        freq = torch.full((1, 80), 150.0)
        feat = torch.zeros(1, 80, 256)
        assert voice(feat, pitch, freq).numel() == voice.sample_rate * 18 // 100
        torch.onnx.export(voice, (feat, pitch, freq), str(output / f"{name}.onnx"), input_names=["features", "pitch", "frequency"], output_names=["audio"], opset_version=17, dynamo=False)
        print("Voice sample rate:", voice.sample_rate, flush=True)
