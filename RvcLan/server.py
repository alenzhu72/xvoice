import asyncio
import os
from pathlib import Path
import sys
import time
from concurrent.futures import ThreadPoolExecutor

root = Path(sys.argv[1]).resolve()
os.chdir(root)
sys.path.insert(0, str(root))
sys.argv = sys.argv[:1]
os.environ["OMP_NUM_THREADS"] = "4"
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
import numpy as np
import torch
import websockets
from configs.config import Config
from infer.rtrvc import RVC

NAMES = {"kikiV1", "guanguanV1", "keruanV1"}
BLOCK = 4000
CONTEXT = 16000
OVERLAP = 320
MODELS = {}
EXECUTOR = ThreadPoolExecutor(max_workers=1)
busy = False

class Stream:
    def __init__(self, name):
        if name not in MODELS:
            c = Config()
            rvc = RVC(0, 0, f"assets/weights/{name}.pth", f"assets/indices/{name}.index", .35, c, None)
            MODELS.clear()
            MODELS[name] = (rvc, c.device)
        self.rvc, device = MODELS[name]
        self.history = torch.zeros(CONTEXT + BLOCK + OVERLAP, device=device)
        self.previous = None
        self.fade = np.linspace(0, 1, OVERLAP, dtype=np.float32)
        self.rvc.cache_pitch.zero_()
        self.rvc.cache_pitchf.zero_()
        self.convert(bytes(BLOCK * 2))
        self.history.zero_()
        self.previous = None

    def convert(self, packet):
        history = self.history
        history[:-BLOCK] = history[BLOCK:].clone()
        history[-BLOCK:] = torch.from_numpy(np.frombuffer(packet, dtype="<i2").astype(np.float32) / 32768).to(history.device)
        with torch.inference_mode():
            result = self.rvc.infer(history, BLOCK, CONTEXT // 160, (BLOCK + OVERLAP) // 160, "rmvpe")
            # Resample to the fixed wire format, independently of checkpoint sample rate.
            result = torch.nn.functional.interpolate(
                result.reshape(1, 1, -1), size=BLOCK + OVERLAP,
                mode="linear", align_corners=False).flatten().float().cpu().numpy()
        if self.previous is not None:
            result[:OVERLAP] = self.previous * (1 - self.fade) + result[:OVERLAP] * self.fade
        self.previous = result[BLOCK:].copy()
        return (result[:BLOCK] * 32768).clip(-32768, 32767).astype("<i2").tobytes()

async def handle(ws, path):
    global busy
    name = path.removeprefix("/rvc/")
    if path != f"/rvc/{name}" or name not in NAMES:
        await ws.close(1008, "Unknown model")
        return
    if busy:
        await ws.close(1013, "One client at a time")
        return
    busy = True
    loop = asyncio.get_running_loop()
    try:
        stream = await loop.run_in_executor(EXECUTOR, Stream, name)
        await ws.send("ready")
        async for packet in ws:
            if not isinstance(packet, bytes) or len(packet) != BLOCK * 2:
                await ws.close(1003, "Expected 8000-byte PCM block")
                return
            started = time.monotonic()
            await ws.send(await loop.run_in_executor(EXECUTOR, stream.convert, packet))
            if getattr(stream, "logged", False) is False:
                print(f"{name}: first live block {(time.monotonic()-started)*1000:.0f} ms", flush=True)
                stream.logged = True
    except websockets.ConnectionClosed:
        pass
    except Exception:
        import traceback
        traceback.print_exc()
        await ws.close(1011, "RVC inference failed; see PC console")
    finally:
        busy = False

async def existing_rvc_server():
    try:
        async with websockets.connect("ws://127.0.0.1:8766/rvc/probe", open_timeout=2, close_timeout=1) as ws:
            try:
                await asyncio.wait_for(ws.recv(), timeout=2)
            except websockets.ConnectionClosed as error:
                return error.code == 1008 and error.reason == "Unknown model"
    except (OSError, TimeoutError, websockets.WebSocketException):
        pass
    return False


async def main():
    try:
        async with websockets.serve(handle, "0.0.0.0", 8766, max_size=8000, max_queue=1, compression=None):
            print("RVC LAN ready on port 8766", flush=True)
            await asyncio.Future()
    except OSError as error:
        if getattr(error, "winerror", None) != 10048 and error.errno != 10048:
            raise
        if await existing_rvc_server():
            print("RVC LAN is already running on port 8766. Connect from the Android app.")
            return 0
        print("Port 8766 is in use by another service. Close that service and retry.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
